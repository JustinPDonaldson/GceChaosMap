using ChaosMap.Api.Domain;
using Google.Cloud.Compute.V1;
using Microsoft.Extensions.Options;

namespace ChaosMap.Api.Providers;

/// <summary>
/// Real Compute Engine backend. Reads one regional MIG per region and breaks the fleet through the Compute API;
/// the MIG's own reconciliation and autohealing do the recovering.
///
/// A VM kill is a plain delete. A zone or region "outage" is a real evacuation: the zone is removed from the MIG's
/// distribution policy (or the MIG is resized to zero) so replacements cannot land there, and after a timeout the
/// original configuration is restored. "Down" is always derived from what Compute Engine reports, not from memory,
/// so an outage that outlives this process is still noticed and restored.
///
/// Only VMs that appear in a configured MIG's managed-instance list can be deleted, so a caller cannot use the API
/// to delete arbitrary instances in the project.
/// </summary>
public sealed class GceFleetProvider : IFleetProvider
{
    private readonly FleetOptions _fleet;
    private readonly GceOptions _gce;
    private readonly TimeProvider _time;
    private readonly ILogger<GceFleetProvider> _log;
    private readonly RegionInstanceGroupManagersClient _migs;
    private readonly InstancesClient _instances;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RegionState> _state = [];

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
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if ((now - _lastPoll).TotalSeconds < _gce.PollSeconds) return _last;

            var reads = await Task.WhenAll(_fleet.Regions.Select(r => ReadRegionAsync(r, now, ct)));
            foreach (var read in reads) _state[read.Def.Id] = read.State with { RestoreAt = _state.GetValueOrDefault(read.Def.Id)?.RestoreAt };

            foreach (var def in _fleet.Regions) await RestoreWhenDueAsync(def, now, ct);

            _last = new FleetSnapshot(now, Mode, reads.Select(r => Overlay(r, _state[r.Def.Id])).ToList());
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
            _gate.Release();
        }
    }

    public async Task<ChaosResult> KillInstanceAsync(string instanceName, CancellationToken ct)
    {
        var vm = _last.Regions.SelectMany(r => r.Zones).SelectMany(z => z.Instances)
            .FirstOrDefault(i => i.Name == instanceName);
        if (vm is null) return new ChaosResult(false, $"{instanceName} is not part of a managed fleet.");
        if (vm.State == InstanceState.Stopping) return new ChaosResult(false, $"{instanceName} is already stopping.");

        return await GuardedAsync(async () =>
        {
            await DeleteAsync(vm, ct);
            return new ChaosResult(true, $"Deleted {vm.Name} in {vm.Zone}.", 1);
        });
    }

    public async Task<ChaosResult> KillZoneAsync(string zoneId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var def = _fleet.Regions.FirstOrDefault(r => r.Zones.Any(z => $"{r.Id}-{z}" == zoneId));
            if (def is null || !_state.TryGetValue(def.Id, out var state)) return new ChaosResult(false, $"No zone named {zoneId}.");
            if (!state.ActiveZones.Contains(zoneId)) return new ChaosResult(false, $"{zoneId} is already down.");
            if (state.ActiveZones.Count == 1) return new ChaosResult(false, $"{zoneId} is the last zone serving {def.Id}. Use Fail region instead.");

            return await GuardedAsync(async () =>
            {
                var remaining = state.ActiveZones.Where(z => z != zoneId).ToList();
                await PatchZonesAsync(def, remaining, ct);

                state.ActiveZones.Remove(zoneId);
                state.RestoreAt = _time.GetUtcNow().AddSeconds(_gce.OutageSeconds);

                var victims = LiveInstances(z => z.Id == zoneId);
                var deleted = await DeleteAllAsync(victims, ct);
                return new ChaosResult(true, $"Zone {zoneId} evacuated; {deleted} instances deleted.", deleted);
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChaosResult> KillRegionAsync(string regionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var def = _fleet.Regions.FirstOrDefault(r => r.Id == regionId);
            if (def is null || !_state.TryGetValue(def.Id, out var state)) return new ChaosResult(false, $"No region named {regionId}.");
            if (state.TargetSize == 0) return new ChaosResult(false, $"{regionId} is already down.");

            return await GuardedAsync(async () =>
            {
                var count = LiveInstances(z => z.Id.StartsWith(regionId + "-", StringComparison.Ordinal)).Count;

                // Resizing to zero makes the MIG delete everything and stops it rebuilding until we restore the size.
                await _migs.ResizeAsync(_gce.ProjectId, def.Id, def.MigName, 0, ct);
                _log.LogWarning("Chaos: resized {Mig} to 0", def.MigName);

                state.TargetSize = 0;
                state.RestoreAt = _time.GetUtcNow().AddSeconds(_gce.OutageSeconds);
                return new ChaosResult(true, $"Region {regionId} evacuated; {count} instances going away.", count);
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChaosResult> ResetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var restored = 0;
            foreach (var def in _fleet.Regions)
            {
                if (!_state.TryGetValue(def.Id, out var state) || !IsDegraded(def, state)) continue;
                await RestoreAsync(def, state, ct);
                restored++;
            }
            _lastPoll = DateTimeOffset.MinValue; // show the restored configuration on the next tick
            return new ChaosResult(true, restored == 0
                ? "Nothing to restore: the MIGs already have their full configuration."
                : $"Restored {restored} region(s). Replacement VMs take a couple of minutes to boot.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Reset failed");
            return new ChaosResult(false, "Compute Engine refused the restore: " + FirstLine(ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreWhenDueAsync(RegionOptions def, DateTimeOffset now, CancellationToken ct)
    {
        var state = _state[def.Id];
        if (!IsDegraded(def, state))
        {
            state.RestoreAt = null;
            return;
        }

        // Also covers an outage that started before this process did: it gets a timer the first time it is seen.
        state.RestoreAt ??= now.AddSeconds(_gce.OutageSeconds);
        if (now < state.RestoreAt) return;

        _log.LogWarning("Chaos: restoring {Mig}", def.MigName);
        await RestoreAsync(def, state, ct);
    }

    private async Task RestoreAsync(RegionOptions def, RegionState state, CancellationToken ct)
    {
        var allZones = def.Zones.Select(z => $"{def.Id}-{z}").ToList();
        if (!allZones.All(state.ActiveZones.Contains)) await PatchZonesAsync(def, allZones, ct);
        if (state.TargetSize == 0) await _migs.ResizeAsync(_gce.ProjectId, def.Id, def.MigName, _fleet.TargetPerRegion, ct);

        state.ActiveZones = [.. allZones];
        state.TargetSize = _fleet.TargetPerRegion;
        state.RestoreAt = null;
    }

    private bool IsDegraded(RegionOptions def, RegionState state) =>
        state.ActiveZones.Count < def.Zones.Length || (state.TargetSize == 0 && _fleet.TargetPerRegion > 0);

    private Task PatchZonesAsync(RegionOptions def, IEnumerable<string> zoneIds, CancellationToken ct)
    {
        var policy = new DistributionPolicy();
        policy.Zones.AddRange(zoneIds.Select(z => new DistributionPolicyZoneConfiguration { Zone = $"projects/{_gce.ProjectId}/zones/{z}" }));

        _log.LogWarning("Chaos: setting {Mig} zones to {Zones}", def.MigName, string.Join(",", zoneIds));
        return _migs.PatchAsync(new PatchRegionInstanceGroupManagerRequest
        {
            Project = _gce.ProjectId,
            Region = def.Id,
            InstanceGroupManager = def.MigName,
            InstanceGroupManagerResource = new InstanceGroupManager { DistributionPolicy = policy },
        }, ct);
    }

    private List<InstanceInfo> LiveInstances(Func<ZoneInfo, bool> zoneFilter) =>
        _last.Regions.SelectMany(r => r.Zones).Where(zoneFilter)
            .SelectMany(z => z.Instances)
            .Where(i => i.State != InstanceState.Stopping)
            .ToList();

    /// <summary>Deletes in parallel; one VM already being removed by the MIG must not abort the rest.</summary>
    private async Task<int> DeleteAllAsync(IEnumerable<InstanceInfo> victims, CancellationToken ct)
    {
        var results = await Task.WhenAll(victims.Select(async v =>
        {
            try { await DeleteAsync(v, ct); return true; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Could not delete {Instance}: {Reason}", v.Name, FirstLine(ex.Message));
                return false;
            }
        }));
        return results.Count(ok => ok);
    }

    private async Task DeleteAsync(InstanceInfo vm, CancellationToken ct)
    {
        _log.LogWarning("Chaos: deleting {Instance} in {Zone}", vm.Name, vm.Zone);
        // The returned operation is intentionally not awaited to completion; the poll loop observes the result.
        await _instances.DeleteAsync(_gce.ProjectId, vm.Zone, vm.Name, ct);
    }

    /// <summary>A refused call becomes a message the visitor can read, never a 500.</summary>
    private async Task<ChaosResult> GuardedAsync(Func<Task<ChaosResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Compute Engine rejected a chaos action");
            return new ChaosResult(false, "Compute Engine refused that: " + FirstLine(ex.Message));
        }
    }

    private async Task<RegionRead> ReadRegionAsync(RegionOptions def, DateTimeOffset now, CancellationToken ct)
    {
        var byZone = def.Zones.ToDictionary(z => $"{def.Id}-{z}", _ => new List<InstanceInfo>());

        var mig = await _migs.GetAsync(_gce.ProjectId, def.Id, def.MigName, ct);
        var policyZones = mig.DistributionPolicy?.Zones.Select(z => LastSegment(z.Zone)).Where(z => z.Length > 0).ToList() ?? [];
        var state = new RegionState
        {
            // No policy at all means every zone is in use.
            ActiveZones = policyZones.Count == 0 ? [.. byZone.Keys] : [.. policyZones.Where(byZone.ContainsKey)],
            TargetSize = mig.TargetSize,
        };

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
            var name = mi.Instance[(mi.Instance.LastIndexOf('/') + 1)..];
            list.Add(new InstanceInfo(name, def.Id, zone, MapState(mi.CurrentAction, mi.InstanceStatus, health), now));
        }

        return new RegionRead(def, state, byZone);
    }

    private RegionInfo Overlay(RegionRead read, RegionState state)
    {
        var regionDown = state.TargetSize == 0 && _fleet.TargetPerRegion > 0;
        var zones = read.ByZone
            .Select(kv => new ZoneInfo(
                kv.Key,
                regionDown || !state.ActiveZones.Contains(kv.Key),
                kv.Value.OrderBy(i => i.Name, StringComparer.Ordinal).ToList()))
            .ToList();

        return new RegionInfo(
            read.Def.Id, read.Def.Name, read.Def.Lat, read.Def.Lon,
            _fleet.TargetPerRegion,
            zones.Sum(z => z.Instances.Count(i => i.State == InstanceState.Healthy)),
            zones);
    }

    private static RegionInfo EmptyRegion(RegionOptions def) => new(
        def.Id, def.Name, def.Lat, def.Lon, 0, 0,
        def.Zones.Select(z => new ZoneInfo($"{def.Id}-{z}", false, [])).ToList());

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

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

    /// <summary>"projects/p/zones/us-central1-a" (or a full URL) to "us-central1-a".</summary>
    internal static string LastSegment(string? url) =>
        string.IsNullOrEmpty(url) ? "" : url[(url.LastIndexOf('/') + 1)..];

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

        // RUNNING from here on: a VM is only "healthy" once it is settled and, if a health check exists, passing it.
        if (currentAction is not (null or "" or "NONE")) return InstanceState.Starting;
        return string.IsNullOrEmpty(health) || health == "HEALTHY" ? InstanceState.Healthy : InstanceState.Starting;
    }

    private sealed record RegionState
    {
        public HashSet<string> ActiveZones { get; set; } = [];
        public int TargetSize { get; set; }
        public DateTimeOffset? RestoreAt { get; set; }
    }

    private sealed record RegionRead(RegionOptions Def, RegionState State, Dictionary<string, List<InstanceInfo>> ByZone);
}
