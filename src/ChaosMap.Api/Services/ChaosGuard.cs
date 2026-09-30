using ChaosMap.Api.Domain;
using Microsoft.Extensions.Options;

namespace ChaosMap.Api.Services;

/// <summary>What a chaos action would touch, worked out from the current snapshot before anything is deleted.</summary>
public sealed record Scope(string Kind, string Target, IReadOnlyList<string> RegionIds, int HealthyVictims);

/// <summary>
/// Last line of defence for a public site that can delete real VMs: a kill switch, a global action budget,
/// and a floor on how much of the fleet may be down at once. Per-visitor throttling lives in the rate limiter.
/// </summary>
public sealed class ChaosGuard(IOptions<ChaosOptions> options, TimeProvider time)
{
    private readonly ChaosOptions _o = options.Value;
    private readonly Queue<DateTimeOffset> _actions = new();
    private readonly object _gate = new();

    public bool Enabled => _o.Enabled;
    public int MinGlobalHealthyPercent => _o.MinGlobalHealthyPercent;

    /// <summary>Returns null when the action may proceed (and counts it), otherwise the reason it was refused.</summary>
    public string? TryAuthorize(FleetSnapshot fleet, Scope scope)
    {
        if (!_o.Enabled) return "Chaos is switched off on this deployment.";

        lock (_gate)
        {
            var now = time.GetUtcNow();
            while (_actions.Count > 0 && now - _actions.Peek() > TimeSpan.FromMinutes(1)) _actions.Dequeue();
            if (_actions.Count >= _o.MaxActionsPerMinute)
                return "Too much chaos right now. Give the fleet a minute.";

            var target = fleet.TargetSize;
            if (target > 0)
            {
                var healthyAfter = Math.Max(0, fleet.HealthyCount - scope.HealthyVictims);
                var percentAfter = healthyAfter * 100.0 / target;
                if (percentAfter < _o.MinGlobalHealthyPercent)
                    return $"Refused: that would leave only {percentAfter:0}% of the fleet healthy (floor is {_o.MinGlobalHealthyPercent}%).";
            }

            _actions.Enqueue(now);
            return null;
        }
    }

    public static Scope? InstanceScope(FleetSnapshot fleet, string name)
    {
        foreach (var r in fleet.Regions)
        foreach (var z in r.Zones)
        {
            var vm = z.Instances.FirstOrDefault(i => i.Name == name);
            if (vm is not null)
                return new Scope("instance", name, [r.Id], vm.State == InstanceState.Healthy ? 1 : 0);
        }
        return null;
    }

    public static Scope? ZoneScope(FleetSnapshot fleet, string zoneId)
    {
        foreach (var r in fleet.Regions)
        {
            var zone = r.Zones.FirstOrDefault(z => z.Id == zoneId);
            if (zone is not null)
                return new Scope("zone", zoneId, [r.Id], zone.Instances.Count(i => i.State == InstanceState.Healthy));
        }
        return null;
    }

    public static Scope? RegionScope(FleetSnapshot fleet, string regionId)
    {
        var region = fleet.Regions.FirstOrDefault(r => r.Id == regionId);
        return region is null ? null : new Scope("region", regionId, [region.Id], region.HealthyCount);
    }
}
