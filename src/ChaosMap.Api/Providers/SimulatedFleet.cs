using ChaosMap.Api.Domain;
using Microsoft.Extensions.Options;

namespace ChaosMap.Api.Providers;

/// <summary>
/// An in-memory model of one regional managed instance group per region. It reproduces the behaviours the
/// demo is about: a deleted VM is noticed after a delay, the MIG recreates it in the least-loaded healthy
/// zone, and a new VM walks PROVISIONING -> STAGING -> RUNNING -> passing a health check before it takes traffic.
/// State is advanced from timestamps, so it catches up correctly after idle periods.
/// </summary>
public sealed class SimulatedFleet : IFleetProvider
{
    private const string NameAlphabet = "bcdfghjklmnpqrstvwxz";

    private readonly object _gate = new();
    private readonly FleetOptions _fleet;
    private readonly SimulationOptions _sim;
    private readonly TimeProvider _time;
    private readonly Random _rng;
    private readonly List<RegionState> _regions = [];
    private DateTimeOffset _cursor;

    public SimulatedFleet(IOptions<FleetOptions> fleet, IOptions<SimulationOptions> sim, TimeProvider time, Random? rng = null)
    {
        _fleet = fleet.Value;
        _sim = sim.Value;
        _time = time;
        _rng = rng ?? new Random();
        BuildSteadyState();
    }

    public string Mode => "simulation";

    public Task<FleetSnapshot> RefreshAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var now = CatchUp();
            return Task.FromResult(Snapshot(now));
        }
    }

    public Task<ChaosResult> KillInstanceAsync(string instanceName, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = CatchUp();
            foreach (var region in _regions)
            {
                var vm = region.Vms.FirstOrDefault(v => v.Name == instanceName);
                if (vm is null) continue;
                if (vm.State == InstanceState.Stopping)
                    return Task.FromResult(new ChaosResult(false, $"{instanceName} is already stopping."));

                Stop(vm, now);
                region.MissingSince ??= now;
                return Task.FromResult(new ChaosResult(true, $"Deleted {instanceName} in {vm.Zone}.", 1));
            }
            return Task.FromResult(new ChaosResult(false, $"No instance named {instanceName}."));
        }
    }

    public Task<ChaosResult> KillZoneAsync(string zoneId, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = CatchUp();
            foreach (var region in _regions)
            {
                var zone = region.Zones.FirstOrDefault(z => ZoneId(region, z) == zoneId);
                if (zone is null) continue;
                if (zone.Down)
                    return Task.FromResult(new ChaosResult(false, $"{zoneId} is already down."));

                var affected = TakeZoneDown(region, zone, now);
                return Task.FromResult(new ChaosResult(true, $"Zone {zoneId} failed; {affected} instances lost.", affected));
            }
            return Task.FromResult(new ChaosResult(false, $"No zone named {zoneId}."));
        }
    }

    public Task<ChaosResult> KillRegionAsync(string regionId, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = CatchUp();
            var region = _regions.FirstOrDefault(r => r.Def.Id == regionId);
            if (region is null)
                return Task.FromResult(new ChaosResult(false, $"No region named {regionId}."));

            var affected = 0;
            foreach (var zone in region.Zones.Where(z => !z.Down))
                affected += TakeZoneDown(region, zone, now);

            return affected == 0
                ? Task.FromResult(new ChaosResult(false, $"{regionId} is already down."))
                : Task.FromResult(new ChaosResult(true, $"Region {regionId} failed; {affected} instances lost.", affected));
        }
    }

    public Task<ChaosResult> ResetAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            BuildSteadyState();
            return Task.FromResult(new ChaosResult(true, "Fleet restored to steady state."));
        }
    }

    /// <summary>
    /// Runs the model forward to the present in short slices. Stepping (rather than jumping) keeps timing exact:
    /// a replacement is created when autohealing would have created it, even if nobody polled for minutes.
    /// </summary>
    private DateTimeOffset CatchUp()
    {
        var now = _time.GetUtcNow();
        var step = TimeSpan.FromMilliseconds(250);
        while (_cursor < now)
        {
            _cursor = _cursor + step < now ? _cursor + step : now;
            foreach (var region in _regions) Advance(region, _cursor);
        }
        return now;
    }

    private int TakeZoneDown(RegionState region, ZoneState zone, DateTimeOffset now)
    {
        zone.Down = true;
        zone.DownUntil = now.AddSeconds(_sim.ZoneOutageSeconds);

        var affected = 0;
        foreach (var vm in region.Vms.Where(v => v.Zone == ZoneId(region, zone) && v.State != InstanceState.Stopping))
        {
            Stop(vm, now);
            affected++;
        }
        region.MissingSince ??= now;
        return affected;
    }

    private static void Stop(Vm vm, DateTimeOffset now)
    {
        vm.State = InstanceState.Stopping;
        vm.Since = now;
    }

    private void BuildSteadyState()
    {
        _regions.Clear();
        var now = _time.GetUtcNow();
        _cursor = now;
        foreach (var def in _fleet.Regions)
        {
            var region = new RegionState(def, def.Zones.Select(z => new ZoneState(z)).ToList());
            for (var i = 0; i < _fleet.TargetPerRegion; i++)
            {
                var zone = region.Zones[i % region.Zones.Count];
                region.Vms.Add(NewVm(region, zone, InstanceState.Healthy, now.AddSeconds(-60)));
            }
            _regions.Add(region);
        }
    }

    private void Advance(RegionState region, DateTimeOffset now)
    {
        foreach (var zone in region.Zones.Where(z => z.Down && now >= z.DownUntil))
            zone.Down = false;

        foreach (var vm in region.Vms.ToList())
        {
            // Step by exact boundaries so a long gap between refreshes still lands in the right state.
            while (vm.State != InstanceState.Healthy)
            {
                var (next, seconds) = vm.State switch
                {
                    InstanceState.Provisioning => (InstanceState.Staging, _sim.ProvisioningSeconds),
                    InstanceState.Staging => (InstanceState.Starting, _sim.StagingSeconds),
                    InstanceState.Starting => (InstanceState.Healthy, _sim.StartingSeconds),
                    _ => (InstanceState.Stopping, _sim.StoppingSeconds),
                };

                var due = vm.Since.AddSeconds(seconds);
                if (now < due) break;

                if (vm.State == InstanceState.Stopping)
                {
                    region.Vms.Remove(vm);
                    break;
                }
                vm.State = next;
                vm.Since = due;
            }
        }

        Reconcile(region, now);
    }

    private void Reconcile(RegionState region, DateTimeOffset now)
    {
        var live = region.Vms.Count(v => v.State != InstanceState.Stopping);
        var deficit = _fleet.TargetPerRegion - live;
        if (deficit <= 0)
        {
            region.MissingSince = null;
            return;
        }

        region.MissingSince ??= now;
        if (now < region.MissingSince.Value.AddSeconds(_sim.ReconcileDelaySeconds)) return;

        var upZones = region.Zones.Where(z => !z.Down).ToList();
        if (upZones.Count == 0) return; // nowhere to place VMs; keep waiting for a zone to come back

        for (var i = 0; i < deficit; i++)
        {
            // Regional MIGs keep zones balanced: place into the zone with the fewest live VMs.
            var zone = upZones
                .OrderBy(z => region.Vms.Count(v => v.Zone == ZoneId(region, z) && v.State != InstanceState.Stopping))
                .ThenBy(_ => _rng.Next())
                .First();
            region.Vms.Add(NewVm(region, zone, InstanceState.Provisioning, now));
        }
        region.MissingSince = null;
    }

    private Vm NewVm(RegionState region, ZoneState zone, InstanceState state, DateTimeOffset since)
    {
        string name;
        do
        {
            var suffix = string.Create(4, _rng, static (span, rng) =>
            {
                for (var i = 0; i < span.Length; i++) span[i] = NameAlphabet[rng.Next(NameAlphabet.Length)];
            });
            name = $"web-{Abbrev(region.Def.Id)}-{suffix}";
        } while (_regions.Any(r => r.Vms.Any(v => v.Name == name)) || region.Vms.Any(v => v.Name == name));

        return new Vm(name, ZoneId(region, zone), state, since);
    }

    private FleetSnapshot Snapshot(DateTimeOffset now)
    {
        var regions = _regions.Select(r =>
        {
            var zones = r.Zones.Select(z =>
            {
                var id = ZoneId(r, z);
                var instances = r.Vms
                    .Where(v => v.Zone == id)
                    .OrderBy(v => v.Name, StringComparer.Ordinal)
                    .Select(v => new InstanceInfo(v.Name, r.Def.Id, v.Zone, v.State, v.Since))
                    .ToList();
                return new ZoneInfo(id, z.Down, instances);
            }).ToList();

            return new RegionInfo(
                r.Def.Id, r.Def.Name, r.Def.Lat, r.Def.Lon,
                _fleet.TargetPerRegion,
                r.Vms.Count(v => v.State == InstanceState.Healthy),
                zones);
        }).ToList();

        return new FleetSnapshot(now, Mode, regions);
    }

    private static string ZoneId(RegionState region, ZoneState zone) => $"{region.Def.Id}-{zone.Suffix}";

    private static string Abbrev(string regionId)
    {
        var parts = regionId.Split('-');
        var digits = new string(parts[^1].Where(char.IsDigit).ToArray());
        return string.Concat(parts.Select(p => p[0])) + digits;
    }

    private sealed class RegionState(RegionOptions def, List<ZoneState> zones)
    {
        public RegionOptions Def { get; } = def;
        public List<ZoneState> Zones { get; } = zones;
        public List<Vm> Vms { get; } = [];
        public DateTimeOffset? MissingSince { get; set; }
    }

    private sealed class ZoneState(string suffix)
    {
        public string Suffix { get; } = suffix;
        public bool Down { get; set; }
        public DateTimeOffset DownUntil { get; set; }
    }

    private sealed class Vm(string name, string zone, InstanceState state, DateTimeOffset since)
    {
        public string Name { get; } = name;
        public string Zone { get; } = zone;
        public InstanceState State { get; set; } = state;
        public DateTimeOffset Since { get; set; } = since;
    }
}
