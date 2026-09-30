using ChaosMap.Api.Domain;
using ChaosMap.Api.Services;
using Microsoft.Extensions.Time.Testing;

namespace ChaosMap.Tests;

public class GuardAndTrackerTests
{
    private static ChaosGuard Guard(ChaosOptions? o = null, FakeTimeProvider? time = null) =>
        new(Microsoft.Extensions.Options.Options.Create(o ?? new ChaosOptions()), time ?? new FakeTimeProvider());

    [Fact]
    public async Task Refuses_actions_that_drop_the_fleet_below_the_floor()
    {
        var (fleet, _) = TestFleet.Simulated(); // 12 VMs, floor 34% => at least 5 must survive
        var snap = await fleet.RefreshAsync(default);
        var guard = Guard();

        Assert.Null(guard.TryAuthorize(snap, ChaosGuard.RegionScope(snap, "us-central1")!));   // 12 -> 6 (50%)
        Assert.NotNull(guard.TryAuthorize(snap, new Scope("region", "x", ["x"], 9)));           // 12 -> 3 (25%)
    }

    [Fact]
    public async Task Kill_switch_and_action_budget_are_enforced()
    {
        var (fleet, _) = TestFleet.Simulated();
        var snap = await fleet.RefreshAsync(default);
        var scope = ChaosGuard.InstanceScope(snap, TestFleet.All(snap).First().Name)!;

        Assert.NotNull(Guard(new ChaosOptions { Enabled = false }).TryAuthorize(snap, scope));

        var time = new FakeTimeProvider();
        var guard = Guard(new ChaosOptions { MaxActionsPerMinute = 2 }, time);
        Assert.Null(guard.TryAuthorize(snap, scope));
        Assert.Null(guard.TryAuthorize(snap, scope));
        Assert.NotNull(guard.TryAuthorize(snap, scope));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(guard.TryAuthorize(snap, scope));
    }

    [Fact]
    public async Task Scopes_resolve_only_real_targets()
    {
        var (fleet, _) = TestFleet.Simulated();
        var snap = await fleet.RefreshAsync(default);

        Assert.Equal(2, ChaosGuard.ZoneScope(snap, "us-central1-a")!.HealthyVictims);
        Assert.Equal(6, ChaosGuard.RegionScope(snap, "europe-west1")!.HealthyVictims);
        Assert.Null(ChaosGuard.InstanceScope(snap, "web-not-a-vm"));
        Assert.Null(ChaosGuard.ZoneScope(snap, "mars-1-a"));
    }

    [Fact]
    public async Task Incident_closes_with_a_measured_recovery_time()
    {
        var (fleet, time) = TestFleet.Simulated();
        var tracker = new IncidentTracker(time);

        var victim = TestFleet.InRegion(await fleet.RefreshAsync(default), "us-central1").First();
        await fleet.KillInstanceAsync(victim.Name, default);
        tracker.Start("instance", victim.Name, ["us-central1"]);

        tracker.Observe(await fleet.RefreshAsync(default));
        Assert.Null(tracker.Recent().Single().RecoveredAt);

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            tracker.Observe(await fleet.RefreshAsync(default));
        }

        var incident = tracker.Recent().Single();
        Assert.NotNull(incident.RecoveredAt);
        // 1.5s notice + 2s provisioning + 2s staging + 4s health check = ~9.5s, measured on a 1s tick.
        Assert.InRange(incident.RecoverySeconds!.Value, 8, 12);
    }

    [Fact]
    public async Task Incident_is_not_closed_by_a_stale_healthy_snapshot()
    {
        var (fleet, time) = TestFleet.Simulated();
        var tracker = new IncidentTracker(time);
        var healthy = await fleet.RefreshAsync(default);

        tracker.Start("instance", "vm", ["us-central1"]);
        tracker.Observe(healthy); // cached snapshot from before the delete became visible

        Assert.Null(tracker.Recent().Single().RecoveredAt);
    }
}
