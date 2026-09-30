using System.Net;
using System.Threading.RateLimiting;
using ChaosMap.Api.Domain;
using ChaosMap.Api.Hubs;
using ChaosMap.Api.Providers;
using ChaosMap.Api.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FleetOptions>(builder.Configuration.GetSection(FleetOptions.Section));
builder.Services.Configure<SimulationOptions>(builder.Configuration.GetSection(SimulationOptions.Section));
builder.Services.Configure<GceOptions>(builder.Configuration.GetSection(GceOptions.Section));
builder.Services.Configure<ChaosOptions>(builder.Configuration.GetSection(ChaosOptions.Section));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TrafficSimulator>();
builder.Services.AddSingleton<IncidentTracker>();
builder.Services.AddSingleton<ChaosGuard>();
builder.Services.AddSingleton<FleetState>();

var useGce = string.Equals(
    builder.Configuration[$"{FleetOptions.Section}:Provider"], "Gce", StringComparison.OrdinalIgnoreCase);
if (useGce) builder.Services.AddSingleton<IFleetProvider, GceFleetProvider>();
else builder.Services.AddSingleton<IFleetProvider, SimulatedFleet>();

builder.Services.AddSingleton<FleetLoop>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetLoop>());

builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.Configure<Microsoft.AspNetCore.SignalR.JsonHubProtocolOptions>(o =>
    o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Behind a Google Cloud load balancer the client address arrives in X-Forwarded-For; per-visitor throttling needs it.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("chaos", ctx =>
    {
        var chaos = ctx.RequestServices.GetRequiredService<IOptions<ChaosOptions>>().Value;
        var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetTokenBucketLimiter(client, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = chaos.PerClientBurst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(chaos.PerClientRefillSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<FleetHub>("/hubs/fleet");

app.MapGet("/api/config", (IFleetProvider provider, IOptions<FleetOptions> fleet, ChaosGuard guard, TrafficSimulator traffic) => new
{
    mode = provider.Mode,
    chaosEnabled = guard.Enabled,
    minHealthyPercent = guard.MinGlobalHealthyPercent,
    cities = traffic.Cities,
    regions = fleet.Value.Regions.Select(r => new { r.Id, r.Name, r.Lat, r.Lon, r.Zones }),
});

app.MapGet("/api/state", async (FleetLoop loop, FleetState state, CancellationToken ct) =>
    state.Latest ?? await loop.BuildFrameAsync(ct));

var chaos = app.MapGroup("/api/chaos").RequireRateLimiting("chaos");

chaos.MapPost("/instance", (KillInstanceRequest body, IFleetProvider p, FleetState s, ChaosGuard g, IncidentTracker t, CancellationToken ct) =>
    RunAsync(s, g, t, ChaosGuard.InstanceScope, body.Name, p.KillInstanceAsync, ct));

chaos.MapPost("/zone", (KillZoneRequest body, IFleetProvider p, FleetState s, ChaosGuard g, IncidentTracker t, CancellationToken ct) =>
    RunAsync(s, g, t, ChaosGuard.ZoneScope, body.Zone, p.KillZoneAsync, ct));

chaos.MapPost("/region", (KillRegionRequest body, IFleetProvider p, FleetState s, ChaosGuard g, IncidentTracker t, CancellationToken ct) =>
    RunAsync(s, g, t, ChaosGuard.RegionScope, body.Region, p.KillRegionAsync, ct));

chaos.MapPost("/reset", async (IFleetProvider p, IncidentTracker t, CancellationToken ct) =>
{
    var result = await p.ResetAsync(ct);
    t.Clear();
    return Results.Ok(result);
});

app.Run();

static async Task<IResult> RunAsync(
    FleetState state,
    ChaosGuard guard,
    IncidentTracker incidents,
    Func<FleetSnapshot, string, Scope?> scopeOf,
    string? target,
    Func<string, CancellationToken, Task<ChaosResult>> execute,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(target)) return Results.BadRequest(new ChaosResult(false, "A target is required."));
    if (state.Latest?.Fleet is not { } fleet) return Results.Conflict(new ChaosResult(false, "Fleet is not ready yet."));

    var scope = scopeOf(fleet, target);
    if (scope is null) return Results.NotFound(new ChaosResult(false, $"Unknown target '{target}'."));

    var refusal = guard.TryAuthorize(fleet, scope);
    if (refusal is not null) return Results.Json(new ChaosResult(false, refusal), statusCode: StatusCodes.Status409Conflict);

    var result = await execute(target, ct);
    if (result.Ok) incidents.Start(scope.Kind, scope.Target, scope.RegionIds);
    return Results.Ok(result);
}

public sealed record KillInstanceRequest(string? Name);
public sealed record KillZoneRequest(string? Zone);
public sealed record KillRegionRequest(string? Region);

public partial class Program;
