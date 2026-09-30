namespace ChaosMap.Api.Domain;

/// <summary>Lifecycle of a VM as seen by the map. Mirrors what a regional MIG reports.</summary>
public enum InstanceState
{
    Provisioning,
    Staging,
    Starting,
    Healthy,
    Stopping,
}

public sealed record InstanceInfo(
    string Name,
    string Region,
    string Zone,
    InstanceState State,
    DateTimeOffset StateSince);

public sealed record ZoneInfo(string Id, bool Down, IReadOnlyList<InstanceInfo> Instances);

public sealed record RegionInfo(
    string Id,
    string Name,
    double Lat,
    double Lon,
    int TargetSize,
    int HealthyCount,
    IReadOnlyList<ZoneInfo> Zones);

public sealed record FleetSnapshot(DateTimeOffset At, string Mode, IReadOnlyList<RegionInfo> Regions)
{
    public int TargetSize => Regions.Sum(r => r.TargetSize);
    public int HealthyCount => Regions.Sum(r => r.HealthyCount);
}

public sealed record Incident(
    string Id,
    string Kind,
    string Target,
    DateTimeOffset StartedAt,
    DateTimeOffset? RecoveredAt,
    double? RecoverySeconds);

public sealed record ChaosResult(bool Ok, string Message, int Affected = 0);

public sealed record UserCity(string Name, double Lat, double Lon, double Weight);

public sealed record RequestEvent(
    string City,
    double Lat,
    double Lon,
    string? Region,
    int LatencyMs,
    bool Ok,
    bool Failover);

public sealed record TrafficStats(
    int Total,
    double SuccessRate,
    int P50Ms,
    int P95Ms,
    IReadOnlyDictionary<string, int> ByRegion);

/// <summary>Everything the browser needs for one frame of the map.</summary>
public sealed record FleetUpdate(
    FleetSnapshot Fleet,
    IReadOnlyList<Incident> Incidents,
    IReadOnlyList<RequestEvent> Requests,
    TrafficStats Traffic);
