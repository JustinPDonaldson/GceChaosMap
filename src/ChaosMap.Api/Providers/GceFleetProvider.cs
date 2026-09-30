using ChaosMap.Api.Domain;
using Google.Cloud.Compute.V1;
using Microsoft.Extensions.Options;

namespace ChaosMap.Api.Providers;

/// <summary>
/// Real Compute Engine backend. Reads one regional MIG per region and breaks the fleet by deleting VMs;
/// the MIG's own autohealing / target-size reconciliation is what "recovers" - this class never recreates anything.
/// Only VMs that appear in a configured MIG's managed-instance list can be deleted, so a caller cannot
/// use the API to delete arbitrary instances in the project.
/// </summary>
public sealed class GceFleetProvider : IFleetProvider
{
    private readonly FleetOptions _fleet;
    private readonly GceOptions _gce;
    private readonly TimeProvider _time;
    private readonly ILogger<GceFleetProvider> _log;
    private readonly RegionInstanceGroupManagersClient _migs;
    private readonly InstancesClient _instances;
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private FleetSnapshot _last;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;

    public GceFleetProvider(
        IOptions<FleetOptions> fleet,
        IOptions<GceOptions> gce,
        TimeProvider time,
        ILogger<GceFleetProvider> log)
    {
        _fleet = fleet.Value;
        _gce = gce.Value;
        _time = time;
        _log = log;

        if (string.IsNullOrWhiteSpace(_gce.ProjectId))
            throw new InvalidOperationException("Gce:ProjectId must be set when Fleet:Provider is 'Gce'.");

        _migs = RegionInstanceGroupManagersClient.Create();
        _instances = InstancesClient.Create();
        _last = new FleetSnapshot(time.GetUtcNow(), Mode, _fleet.Regions.Select(EmptyRegion).ToList());
    }

    public string Mode => "gce";

    public async Task<FleetSnapshot> RefreshAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if ((now - _lastPoll).TotalSeconds < _gce.PollSeconds) return _last;

            var regions = await Task.WhenAll(_fleet.Regions.Select(r => ReadRegionAsync(r, now, ct)));
            _last = new FleetSnapshot(now, Mode, regions);
            _lastPoll = now;
            return _last;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep serving the last good picture; the next refresh retries.
            _log.LogWarning(ex, "Compute Engine refresh failed");
            _lastPoll = _time.GetUtcNow();
            return _last;
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public async Task<ChaosResult> KillInstanceAsync(string instanceName, CancellationToken ct)
    {
        var vm = _last.Regions.SelectMany(r => r.Zones).SelectMany(z => z.Instances)
            .FirstOrDefault(i => i.Name == instanceName);
        if (vm is null) return new ChaosResult(false, $"{instanceName} is not part of a managed fleet.");
        if (vm.State == InstanceState.Stopping) return new ChaosResult(false, $"{instanceName} is already stopping.");

        await DeleteAsync(vm, ct);
        return new ChaosResult(true, $"Deleted {vm.Name} in {vm.Zone}.", 1);
    }

    // The following method deletes all live instances in a specified zone. It first filters the instances in the last known fleet snapshot to find those that are in the specified zone and are not already stopping. If there are no such instances, it returns a result indicating that there are no live instances to delete. Otherwise, it asynchronously deletes each of the identified instances and returns a result indicating how many instances were deleted.
    public async Task<ChaosResult> KillZoneAsync(string zoneId, CancellationToken ct)
    {
        var victims = _last.Regions.SelectMany(r => r.Zones)
            .Where(z => z.Id == zoneId)
            .SelectMany(z => z.Instances)
            .Where(i => i.State != InstanceState.Stopping)
            .ToList();
        if (victims.Count == 0) return new ChaosResult(false, $"No live instances in {zoneId}.");

        await Task.WhenAll(victims.Select(v => DeleteAsync(v, ct)));
        return new ChaosResult(true, $"Deleted {victims.Count} instances in {zoneId}.", victims.Count);
    }

    // The following method deletes all live instances in a specified region. It first filters the instances in the last known fleet snapshot to find those that are in the specified region and are not already stopping. If there are no such instances, it returns a result indicating that there are no live instances to delete. Otherwise, it asynchronously deletes each of the identified instances and returns a result indicating how many instances were deleted.
    public async Task<ChaosResult> KillRegionAsync(string regionId, CancellationToken ct)
    {
        var victims = _last.Regions.Where(r => r.Id == regionId)
            .SelectMany(r => r.Zones).SelectMany(z => z.Instances)
            .Where(i => i.State != InstanceState.Stopping)
            .ToList();
        if (victims.Count == 0) return new ChaosResult(false, $"No live instances in {regionId}.");

        await Task.WhenAll(victims.Select(v => DeleteAsync(v, ct)));
        return new ChaosResult(true, $"Deleted {victims.Count} instances in {regionId}.", victims.Count);
    }

    // The following method resets the fleet to its initial state. It returns a result indicating that there is nothing to reset, as the MIGs restore their target size on their own.
    public Task<ChaosResult> ResetAsync(CancellationToken ct) =>
        Task.FromResult(new ChaosResult(true, "Nothing to reset: the MIGs restore their target size on their own."));

    // The following method deletes a specified instance. It logs a warning indicating that the instance is being deleted and then calls the GCE API to delete the instance. The deletion operation is not awaited to completion, as the poll loop observes the result.
    private async Task DeleteAsync(InstanceInfo vm, CancellationToken ct)
    {
        _log.LogWarning("Chaos: deleting {Instance} in {Zone}", vm.Name, vm.Zone);
        // The returned operation is intentionally not awaited to completion; the poll loop observes the result.
        await _instances.DeleteAsync(_gce.ProjectId, vm.Zone, vm.Name, ct);
    }

    /// The following method reads the state of a specified region. It first creates a dictionary to group instances by zone. It then sends a request to the GCE API to list the managed instances in the specified region and iterates through the results. For each instance, it extracts the zone from the instance URL and adds the instance information to the corresponding zone in the dictionary. Finally, it creates a list of ZoneInfo objects and returns a RegionInfo object containing the region's information and the list of zones.
    private async Task<RegionInfo> ReadRegionAsync(RegionOptions def, DateTimeOffset now, CancellationToken ct)
    {
        var byZone = def.Zones.ToDictionary(z => $"{def.Id}-{z}", _ => new List<InstanceInfo>());

        var request = new ListManagedInstancesRegionInstanceGroupManagersRequest
        {
            Project = _gce.ProjectId,
            Region = def.Id,
            InstanceGroupManager = def.MigName,
        };

        await foreach (var mi in _migs.ListManagedInstancesAsync(request).WithCancellation(ct))
        {
            var zone = ZoneFromUrl(mi.Instance);
            if (zone is null || !byZone.TryGetValue(zone, out var list)) continue;

            var health = mi.InstanceHealth.Select(h => h.DetailedHealthState).FirstOrDefault();
            var state = MapState(mi.CurrentAction, mi.InstanceStatus, health);
            var name = mi.Instance[(mi.Instance.LastIndexOf('/') + 1)..];
            list.Add(new InstanceInfo(name, def.Id, zone, state, now));
        }

        var zones = byZone
            .Select(kv => new ZoneInfo(kv.Key, false, kv.Value.OrderBy(i => i.Name, StringComparer.Ordinal).ToList()))
            .ToList();

        return new RegionInfo(
            def.Id, def.Name, def.Lat, def.Lon,
            _fleet.TargetPerRegion,
            zones.Sum(z => z.Instances.Count(i => i.State == InstanceState.Healthy)),
            zones);
    }

    private static RegionInfo EmptyRegion(RegionOptions def) => new(
        def.Id, def.Name, def.Lat, def.Lon, 0, 0,
        def.Zones.Select(z => new ZoneInfo($"{def.Id}-{z}", false, [])).ToList());

    /// <summary>Extracts "us-central1-a" from ".../zones/us-central1-a/instances/web-x".</summary>
    internal static string? ZoneFromUrl(string? instanceUrl)
    {
        if (string.IsNullOrEmpty(instanceUrl)) return null;
        const string marker = "/zones/";
        var start = instanceUrl.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = instanceUrl.IndexOf('/', start);
        return end < 0 ? null : instanceUrl[start..end];
    }

    /// <summary>
    /// Collapses the MIG's currentAction, the VM's status and the autohealing health check into the five states the map draws.
    /// </summary>
    internal static InstanceState MapState(string? currentAction, string? instanceStatus, string? health)
    {
        if (currentAction is "DELETING" or "ABANDONING") return InstanceState.Stopping;

        switch (instanceStatus)
        {
            case "STOPPING" or "STOPPED" or "SUSPENDING" or "SUSPENDED" or "TERMINATED":
                return InstanceState.Stopping;
            case "PROVISIONING":
                return InstanceState.Provisioning;
            case "STAGING":
                return InstanceState.Staging;
        }

        if (string.IsNullOrEmpty(instanceStatus) && currentAction is "CREATING" or "RECREATING")
            return InstanceState.Provisioning;

        // RUNNING from here on: a VM is only "healthy" once its settled and, if a health check exists, it passes it.
        if (currentAction is not (null or "" or "NONE")) return InstanceState.Starting;
        return string.IsNullOrEmpty(health) || health == "HEALTHY" ? InstanceState.Healthy : InstanceState.Starting;
    }
}
