using ChaosMap.Api.Domain;
using ChaosMap.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChaosMap.Api.Services;

/// <summary>
/// Heartbeat of the site: refresh the fleet, generate a burst of synthetic requests against it, and push one frame
/// to every connected browser. When nobody is connected it stops polling (and so stops calling the Compute API).
/// </summary>
public sealed class FleetLoop(
    IFleetProvider provider,
    TrafficSimulator traffic,
    IncidentTracker incidents,
    FleetState state,
    IHubContext<FleetHub> hub,
    ILogger<FleetLoop> log) : BackgroundService
{
    private const int TickMs = 500;
    private const int RequestsPerTick = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        do
        {
            if (state.Connections == 0) continue;

            try
            {
                var frame = await BuildFrameAsync(stoppingToken);
                await hub.Clients.All.SendAsync("state", frame, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Fleet tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Also used by the REST endpoints so a page load never has to wait for the next tick.</summary>
    public async Task<FleetUpdate> BuildFrameAsync(CancellationToken ct)
    {
        var fleet = await provider.RefreshAsync(ct);
        incidents.Observe(fleet);
        var requests = traffic.Generate(fleet, RequestsPerTick);
        var frame = new FleetUpdate(fleet, incidents.Recent(), requests, traffic.Stats());
        state.Latest = frame;
        return frame;
    }
}
