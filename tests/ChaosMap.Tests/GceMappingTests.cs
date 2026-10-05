using ChaosMap.Api.Domain;
using ChaosMap.Api.Providers;

namespace ChaosMap.Tests;

public class GceMappingTests
{
    [Theory]
    [InlineData("DELETING", "RUNNING", "HEALTHY", InstanceState.Stopping)]
    [InlineData("NONE", "STOPPING", null, InstanceState.Stopping)]
    [InlineData("CREATING", "PROVISIONING", null, InstanceState.Provisioning)]
    [InlineData("CREATING", null, null, InstanceState.Provisioning)]
    [InlineData("CREATING", "STAGING", null, InstanceState.Staging)]
    [InlineData("VERIFYING", "RUNNING", null, InstanceState.Starting)]
    [InlineData("NONE", "RUNNING", "UNHEALTHY", InstanceState.Starting)]
    [InlineData("NONE", "RUNNING", "HEALTHY", InstanceState.Healthy)]
    [InlineData("NONE", "RUNNING", null, InstanceState.Healthy)]
    public void Maps_mig_and_vm_status_to_map_states(string? action, string? status, string? health, InstanceState expected) =>
        Assert.Equal(expected, GceFleetProvider.MapState(action, status, health));

    [Theory]
    [InlineData("projects/gcechaosmap/zones/us-central1-b", "us-central1-b")]
    [InlineData("https://www.googleapis.com/compute/v1/projects/p/zones/europe-west1-d", "europe-west1-d")]
    [InlineData("us-central1-c", "us-central1-c")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Extracts_zone_name_from_a_distribution_policy_entry(string? url, string expected) =>
        Assert.Equal(expected, GceFleetProvider.LastSegment(url));

    [Theory]
    [InlineData("https://www.googleapis.com/compute/v1/projects/p/zones/us-central1-a/instances/web-x", "us-central1-a")]
    [InlineData("zones/europe-west1-b/instances/y", null)] // no leading slash before the marker: refuse to guess
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Extracts_zone_from_instance_url(string? url, string? expected) =>
        Assert.Equal(expected, GceFleetProvider.ZoneFromUrl(url));
}
