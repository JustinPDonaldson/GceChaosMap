using ChaosMap.Api.Domain;

namespace ChaosMap.Tests;

public class SimulatedFleetTests
{
    [Fact]
    public async Task Starts_healthy_and_zone_balanced()
    {
        var (fleet, _) = TestFleet.Simulated();
        var snap = await fleet.RefreshAsync(default);

        Assert.All(snap.Regions, r =>
        {
            Assert.Equal(6, r.HealthyCount);
            Assert.All(r.Zones, z => Assert.Equal(2, z.Instances.Count));
        });
    }

    [Fact]
    public async Task Killed_instance_is_replaced_and_walks_the_boot_states()
    {
        var (fleet, time) = TestFleet.Simulated();
        var victim = TestFleet.InRegion(await fleet.RefreshAsync(default), "us-central1").First();

        var result = await fleet.KillInstanceAsync(victim.Name, default);
        Assert.True(result.Ok);

        var snap = await fleet.RefreshAsync(default);
        Assert.Equal(InstanceState.Stopping, TestFleet.All(snap).Single(i => i.Name == victim.Name).State);
        Assert.Equal(5, snap.Regions.First(r => r.Id == "us-central1").HealthyCount);

        // Autohealing notices, then a replacement is created.
        time.Advance(TimeSpan.FromSeconds(2.6));
        snap = await fleet.RefreshAsync(default);
        Assert.DoesNotContain(TestFleet.All(snap), i => i.Name == victim.Name);
        var fresh = TestFleet.InRegion(snap, "us-central1").Single(i => i.State == InstanceState.Provisioning);

        time.Advance(TimeSpan.FromSeconds(2));
        snap = await fleet.RefreshAsync(default);
        Assert.Equal(InstanceState.Staging, TestFleet.All(snap).Single(i => i.Name == fresh.Name).State);

        time.Advance(TimeSpan.FromSeconds(2));
        snap = await fleet.RefreshAsync(default);
        Assert.Equal(InstanceState.Starting, TestFleet.All(snap).Single(i => i.Name == fresh.Name).State);

        time.Advance(TimeSpan.FromSeconds(4));
        snap = await fleet.RefreshAsync(default);
        Assert.Equal(6, snap.Regions.First(r => r.Id == "us-central1").HealthyCount);
    }

    [Fact]
    public async Task Zone_failure_redistributes_capacity_to_surviving_zones()
    {
        var (fleet, time) = TestFleet.Simulated();
        var result = await fleet.KillZoneAsync("us-central1-a", default);
        Assert.True(result.Ok);
        Assert.Equal(2, result.Affected);

        time.Advance(TimeSpan.FromSeconds(20)); // long enough to recover capacity, not long enough for the zone to return
        var snap = await fleet.RefreshAsync(default);
        var iowa = snap.Regions.First(r => r.Id == "us-central1");

        Assert.Equal(6, iowa.HealthyCount);
        var downZone = iowa.Zones.Single(z => z.Id == "us-central1-a");
        Assert.True(downZone.Down);
        Assert.Empty(downZone.Instances);
        Assert.Equal(3, iowa.Zones.Single(z => z.Id == "us-central1-b").Instances.Count);
        Assert.Equal(3, iowa.Zones.Single(z => z.Id == "us-central1-c").Instances.Count);
    }

    [Fact]
    public async Task Zone_comes_back_after_the_outage_window()
    {
        var (fleet, time) = TestFleet.Simulated();
        await fleet.KillZoneAsync("us-central1-a", default);

        time.Advance(TimeSpan.FromSeconds(31));
        var snap = await fleet.RefreshAsync(default);

        Assert.False(snap.Regions.First(r => r.Id == "us-central1").Zones.Single(z => z.Id == "us-central1-a").Down);
    }

    [Fact]
    public async Task Region_failure_has_no_capacity_until_a_zone_returns()
    {
        var (fleet, time) = TestFleet.Simulated();
        var result = await fleet.KillRegionAsync("us-central1", default);
        Assert.Equal(6, result.Affected);

        time.Advance(TimeSpan.FromSeconds(15));
        var snap = await fleet.RefreshAsync(default);
        Assert.Equal(0, snap.Regions.First(r => r.Id == "us-central1").HealthyCount);
        Assert.Equal(6, snap.Regions.First(r => r.Id == "europe-west1").HealthyCount); // other region untouched

        time.Advance(TimeSpan.FromSeconds(16)); // zones return at 30s
        await fleet.RefreshAsync(default);
        time.Advance(TimeSpan.FromSeconds(10)); // MIG rebuilds, health checks pass
        snap = await fleet.RefreshAsync(default);
        Assert.Equal(6, snap.Regions.First(r => r.Id == "us-central1").HealthyCount);
    }

    [Fact]
    public async Task Catches_up_correctly_after_a_long_idle_gap()
    {
        var (fleet, time) = TestFleet.Simulated();
        var victim = TestFleet.InRegion(await fleet.RefreshAsync(default), "europe-west1").First();
        await fleet.KillInstanceAsync(victim.Name, default);

        time.Advance(TimeSpan.FromMinutes(5)); // nobody watching; a single refresh must land in the final state
        var snap = await fleet.RefreshAsync(default);

        var belgium = snap.Regions.First(r => r.Id == "europe-west1");
        Assert.Equal(6, belgium.HealthyCount);
        Assert.Equal(6, belgium.Zones.Sum(z => z.Instances.Count));
    }

    [Fact]
    public async Task Rejects_unknown_and_repeated_targets()
    {
        var (fleet, _) = TestFleet.Simulated();
        Assert.False((await fleet.KillInstanceAsync("nope", default)).Ok);
        Assert.False((await fleet.KillZoneAsync("nope-a", default)).Ok);

        Assert.True((await fleet.KillZoneAsync("us-central1-a", default)).Ok);
        Assert.False((await fleet.KillZoneAsync("us-central1-a", default)).Ok);
    }

    [Fact]
    public async Task Reset_restores_steady_state()
    {
        var (fleet, _) = TestFleet.Simulated();
        await fleet.KillRegionAsync("us-central1", default);
        await fleet.ResetAsync(default);

        var snap = await fleet.RefreshAsync(default);
        Assert.All(snap.Regions, r => Assert.Equal(6, r.HealthyCount));
        Assert.DoesNotContain(snap.Regions.SelectMany(r => r.Zones), z => z.Down);
    }

    [Fact]
    public async Task Instance_names_are_unique()
    {
        var (fleet, _) = TestFleet.Simulated();
        var names = TestFleet.All(await fleet.RefreshAsync(default)).Select(i => i.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
