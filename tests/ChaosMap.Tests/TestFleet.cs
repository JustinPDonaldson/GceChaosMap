using ChaosMap.Api.Domain;
using ChaosMap.Api.Providers;
using Microsoft.Extensions.Time.Testing;

namespace ChaosMap.Tests;

internal static class TestFleet
{
    public static FleetOptions Options(int target = 6) => new()
    {
        TargetPerRegion = target,
        Regions =
        [
            new() { Id = "us-central1", Name = "Iowa", Lat = 41.26, Lon = -95.86, Zones = ["a", "b", "c"] },
            new() { Id = "europe-west1", Name = "Belgium", Lat = 50.45, Lon = 3.82, Zones = ["b", "c", "d"] },
        ],
        Cities =
        [
            new() { Name = "Chicago", Lat = 41.88, Lon = -87.63, Weight = 1 },
        ],
    };

    public static (SimulatedFleet Fleet, FakeTimeProvider Time) Simulated(int target = 6)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var fleet = new SimulatedFleet(
            Microsoft.Extensions.Options.Options.Create(Options(target)),
            Microsoft.Extensions.Options.Options.Create(new SimulationOptions()),
            time,
            new Random(42));
        return (fleet, time);
    }

    public static IEnumerable<InstanceInfo> All(FleetSnapshot s) =>
        s.Regions.SelectMany(r => r.Zones).SelectMany(z => z.Instances);

    public static IEnumerable<InstanceInfo> InRegion(FleetSnapshot s, string region) =>
        s.Regions.First(r => r.Id == region).Zones.SelectMany(z => z.Instances);
}
