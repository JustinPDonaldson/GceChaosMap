using ChaosMap.Api.Domain;

namespace ChaosMap.Api.Services;

/// <summary>Latest frame plus the number of connected browsers (the loop idles when nobody is watching).</summary>
public sealed class FleetState
{
    private int _connections;

    public FleetUpdate? Latest { get; set; }
    public int Connections => Volatile.Read(ref _connections);

    public void Connected() => Interlocked.Increment(ref _connections);
    public void Disconnected() => Interlocked.Decrement(ref _connections);
}
