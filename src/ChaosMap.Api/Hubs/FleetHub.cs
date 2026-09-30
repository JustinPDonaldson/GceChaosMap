using ChaosMap.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace ChaosMap.Api.Hubs;

/// <summary>Server-to-client only: the browser receives "state" frames and never sends chaos through the socket.</summary>
public sealed class FleetHub(FleetState state) : Hub
{
    public override async Task OnConnectedAsync()
    {
        state.Connected();
        if (state.Latest is { } latest)
            await Clients.Caller.SendAsync("state", latest);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        state.Disconnected();
        return base.OnDisconnectedAsync(exception);
    }
}
