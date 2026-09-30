using ChaosMap.Api.Domain;
using Microsoft.Extensions.Options;

namespace ChaosMap.Api.Services;

/// <summary>
/// Synthetic users spread around the world. Each request is routed the way a global external load balancer would:
/// to the closest region that still has a healthy backend. Requests that land on a VM that is already
/// being deleted (the balancer has not noticed yet) fail with a 502, which is what makes a kill visible as an error blip.
/// </summary>
public sealed class TrafficSimulator
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly List<UserCity> _cities;
    private readonly double _totalWeight;
    private readonly TimeProvider _time;
    private readonly Random _rng;
    private readonly Queue<Sample> _samples = new();
    private readonly object _gate = new();

    public TrafficSimulator(IOptions<FleetOptions> fleet, TimeProvider time, Random? rng = null)
    {
        _cities = fleet.Value.Cities.Select(c => new UserCity(c.Name, c.Lat, c.Lon, c.Weight)).ToList();
        _totalWeight = _cities.Sum(c => c.Weight);
        _time = time;
        _rng = rng ?? new Random();
    }

    public IReadOnlyList<UserCity> Cities => _cities;

    public IReadOnlyList<RequestEvent> Generate(FleetSnapshot fleet, int count)
    {
        if (_cities.Count == 0) return [];

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var events = new List<RequestEvent>(count);
            for (var i = 0; i < count; i++)
            {
                var ev = Route(PickCity(), fleet, _rng);
                events.Add(ev);
                _samples.Enqueue(new Sample(now, ev.Ok, ev.LatencyMs, ev.Region));
            }
            return events;
        }
    }

    public TrafficStats Stats()
    {
        lock (_gate)
        {
            var cutoff = _time.GetUtcNow() - Window;
            while (_samples.Count > 0 && _samples.Peek().At < cutoff) _samples.Dequeue();

            var total = _samples.Count;
            if (total == 0) return new TrafficStats(0, 1, 0, 0, new Dictionary<string, int>());

            var ok = _samples.Where(s => s.Ok).ToList();
            var latencies = ok.Select(s => s.LatencyMs).Order().ToList();
            var byRegion = ok.Where(s => s.Region is not null)
                .GroupBy(s => s.Region!)
                .ToDictionary(g => g.Key, g => g.Count());

            return new TrafficStats(
                total,
                (double)ok.Count / total,
                Percentile(latencies, 0.50),
                Percentile(latencies, 0.95),
                byRegion);
        }
    }

    internal static RequestEvent Route(UserCity city, FleetSnapshot fleet, Random rng)
    {
        var byDistance = fleet.Regions
            .OrderBy(r => DistanceKm(city.Lat, city.Lon, r.Lat, r.Lon))
            .ToList();
        var nearest = byDistance.FirstOrDefault();
        var chosen = byDistance.FirstOrDefault(r => r.HealthyCount > 0);

        // Every region is out of healthy backends: the balancer answers 503 itself.
        if (chosen is null || nearest is null)
            return new RequestEvent(city.Name, city.Lat, city.Lon, null, 0, false, false);

        var backends = chosen.Zones.SelectMany(z => z.Instances)
            .Where(i => i.State is InstanceState.Healthy or InstanceState.Stopping)
            .ToList();
        var target = backends[rng.Next(backends.Count)];
        var failover = chosen.Id != nearest.Id;

        if (target.State == InstanceState.Stopping)
            return new RequestEvent(city.Name, city.Lat, city.Lon, chosen.Id, 0, false, failover);

        var km = DistanceKm(city.Lat, city.Lon, chosen.Lat, chosen.Lon);
        var latency = (int)Math.Round(15 + km * 0.02 + rng.NextDouble() * 8);
        return new RequestEvent(city.Name, city.Lat, city.Lon, chosen.Id, latency, true, failover);
    }

    internal static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthKm = 6371;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private UserCity PickCity()
    {
        var roll = _rng.NextDouble() * _totalWeight;
        foreach (var city in _cities)
        {
            roll -= city.Weight;
            if (roll <= 0) return city;
        }
        return _cities[^1];
    }

    private static double ToRad(double deg) => deg * Math.PI / 180;

    private static int Percentile(List<int> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    private readonly record struct Sample(DateTimeOffset At, bool Ok, int LatencyMs, string? Region);
}
