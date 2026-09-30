namespace ChaosMap.Api.Domain;

/// <summary>
/// Source of truth for the fleet and the only thing allowed to break it.
/// Implemented by an in-memory simulation and by a real Compute Engine MIG client.
/// </summary>
public interface IFleetProvider
{
    /// <summary>"simulation" or "gce"; shown as a badge in the UI.</summary>
    string Mode { get; }

    /// <summary>Advance the model / refresh from the cloud, then return the current picture.</summary>
    Task<FleetSnapshot> RefreshAsync(CancellationToken ct);

    Task<ChaosResult> KillInstanceAsync(string instanceName, CancellationToken ct);
    Task<ChaosResult> KillZoneAsync(string zoneId, CancellationToken ct);
    Task<ChaosResult> KillRegionAsync(string regionId, CancellationToken ct);

    /// <summary>Put the fleet back to steady state (instant in simulation, best-effort on GCE).</summary>
    Task<ChaosResult> ResetAsync(CancellationToken ct);
}
