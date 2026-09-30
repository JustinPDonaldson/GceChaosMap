using ChaosMap.Api.Domain;
using ChaosMap.Api.Services;

namespace ChaosMap.Tests;

public class TrafficSimulatorTests
{
    private static readonly UserCity Chicago = new("Chicago", 41.88, -87.63, 1);

    [Fact]
    public async Task Routes_to_the_nearest_healthy_region()
    {
        var (fleet, _) = TestFleet.Simulated();
        var snap = await fleet.RefreshAsync(default);

        var ev = TrafficSimulator.Route(Chicago, snap, new Random(1));

        Assert.True(ev.Ok);
        Assert.Equal("us-central1", ev.Region);
        Assert.False(ev.Failover);
    }

    [Fact]
    public async Task Fails_over_when_the_nearest_region_is_down()
    {
        var (fleet, time) = TestFleet.Simulated();
        await fleet.KillRegionAsync("us-central1", default);
        time.Advance(TimeSpan.FromSeconds(4)); // dead VMs are gone, nothing healthy left in Iowa
        var snap = await fleet.RefreshAsync(default);

        var ev = TrafficSimulator.Route(Chicago, snap, new Random(1));

        Assert.True(ev.Ok);
        Assert.Equal("europe-west1", ev.Region);
        Assert.True(ev.Failover);
    }

    [Fact]
    public async Task Failover_costs_latency()
    {
        var (fleet, time) = TestFleet.Simulated();
        var before = TrafficSimulator.Route(Chicago, await fleet.RefreshAsync(default), new Random(1));

        await fleet.KillRegionAsync("us-central1", default);
        time.Advance(TimeSpan.FromSeconds(4));
        var after = TrafficSimulator.Route(Chicago, await fleet.RefreshAsync(default), new Random(1));

        Assert.True(after.LatencyMs > before.LatencyMs + 50);
    }

    [Fact]
    public async Task Returns_503_when_every_region_is_down()
    {
        var (fleet, time) = TestFleet.Simulated();
        await fleet.KillRegionAsync("us-central1", default);
        await fleet.KillRegionAsync("europe-west1", default);
        time.Advance(TimeSpan.FromSeconds(4));

        var ev = TrafficSimulator.Route(Chicago, await fleet.RefreshAsync(default), new Random(1));

        Assert.False(ev.Ok);
        Assert.Null(ev.Region);
    }

    [Fact]
    public async Task Requests_that_hit_a_dying_vm_fail()
    {
        var (fleet, _) = TestFleet.Simulated();
        await fleet.KillZoneAsync("us-central1-a", default);
        var snap = await fleet.RefreshAsync(default); // 2 VMs stopping, 4 still healthy: the balancer has not noticed yet

        var rng = new Random(7);
        var results = Enumerable.Range(0, 400).Select(_ => TrafficSimulator.Route(Chicago, snap, rng)).ToList();

        var failed = results.Count(r => !r.Ok);
        Assert.InRange(failed, 60, 200); // roughly 2 of 6 backends are dying
        Assert.All(results.Where(r => !r.Ok), r => Assert.Equal("us-central1", r.Region));
    }

    [Fact]
    public void Haversine_is_sane()
    {
        // London -> New York is about 5570 km.
        var km = TrafficSimulator.DistanceKm(51.51, -0.13, 40.71, -74.0);
        Assert.InRange(km, 5500, 5650);
    }
}
