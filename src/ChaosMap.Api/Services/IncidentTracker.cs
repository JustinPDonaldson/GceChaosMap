using ChaosMap.Api.Domain;

namespace ChaosMap.Api.Services;

/// <summary>
/// Turns "someone killed a thing" into a measured recovery time: an incident opens when chaos is injected and
/// closes when every region it touched is back to its full healthy target.
/// </summary>
public sealed class IncidentTracker(TimeProvider time)
{
    private const int Keep = 12;

    /// <summary>If a kill is never seen degrading (stale poll, tiny blast radius), close it after this long.</summary>
    private static readonly TimeSpan MaxGrace = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly List<Tracked> _incidents = [];
    private int _sequence;

    public void Start(string kind, string target, IEnumerable<string> regionIds)
    {
        lock (_gate)
        {
            var id = $"inc-{++_sequence}";
            _incidents.Add(new Tracked(id, kind, target, time.GetUtcNow(), regionIds.Distinct().ToList()));
            if (_incidents.Count > Keep) _incidents.RemoveAt(0);
        }
    }

    public void Observe(FleetSnapshot fleet)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var inc in _incidents.Where(i => i.RecoveredAt is null))
            {
                var regions = fleet.Regions.Where(r => inc.RegionIds.Contains(r.Id)).ToList();
                var full = regions.Count > 0 && regions.All(r => r.HealthyCount >= r.TargetSize);

                if (!full) inc.SeenDegraded = true;
                else if (inc.SeenDegraded || now - inc.StartedAt > MaxGrace) inc.RecoveredAt = now;
            }
        }
    }

    public void Clear()
    {
        lock (_gate) _incidents.Clear();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Incident> Recent()
    {
        lock (_gate)
        {
            return _incidents
                .OrderByDescending(i => i.StartedAt)
                .Select(i => new Incident(
                    i.Id, i.Kind, i.Target, i.StartedAt, i.RecoveredAt,
                    i.RecoveredAt is { } r ? Math.Round((r - i.StartedAt).TotalSeconds, 1) : null))
                .ToList();
        }
    }

    private sealed class Tracked(string id, string kind, string target, DateTimeOffset startedAt, List<string> regionIds)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string Target { get; } = target;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public List<string> RegionIds { get; } = regionIds;
        public bool SeenDegraded { get; set; }
        public DateTimeOffset? RecoveredAt { get; set; }
    }
}
