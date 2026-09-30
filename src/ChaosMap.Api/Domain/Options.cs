namespace ChaosMap.Api.Domain;

public sealed class RegionOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }

    /// <summary>Zone suffixes, e.g. ["a","b","c"] for us-central1-a/b/c.</summary>
    public string[] Zones { get; set; } = [];

    /// <summary>Name of the regional MIG (GCE mode only).</summary>
    public string MigName { get; set; } = "";
}

public sealed class CityOptions
{
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double Weight { get; set; } = 1;
}

public sealed class FleetOptions
{
    public const string Section = "Fleet";

    /// <summary>"Simulation" (default, zero cloud cost) or "Gce".</summary>
    public string Provider { get; set; } = "Simulation";

    public int TargetPerRegion { get; set; } = 6;
    public List<RegionOptions> Regions { get; set; } = [];
    public List<CityOptions> Cities { get; set; } = [];
}

public sealed class SimulationOptions
{
    public const string Section = "Simulation";

    public double ProvisioningSeconds { get; set; } = 2;
    public double StagingSeconds { get; set; } = 2;
    public double StartingSeconds { get; set; } = 4;
    public double StoppingSeconds { get; set; } = 2.5;

    /// <summary>How long the MIG takes to notice a missing instance and react.</summary>
    public double ReconcileDelaySeconds { get; set; } = 1.5;

    /// <summary>How long a killed zone stays unusable before capacity may return.</summary>
    public double ZoneOutageSeconds { get; set; } = 30;
}

public sealed class GceOptions
{
    public const string Section = "Gce";

    public string ProjectId { get; set; } = "";
    public double PollSeconds { get; set; } = 2;
}

public sealed class ChaosOptions
{
    public const string Section = "Chaos";

    public bool Enabled { get; set; } = true;

    /// <summary>Refuse any action that would leave less than this share of the fleet healthy.</summary>
    public int MinGlobalHealthyPercent { get; set; } = 34;

    /// <summary>Hard ceiling on chaos actions across all visitors per minute.</summary>
    public int MaxActionsPerMinute { get; set; } = 30;

    /// <summary>Per-visitor token bucket: burst size and one token back every N seconds.</summary>
    public int PerClientBurst { get; set; } = 5;
    public int PerClientRefillSeconds { get; set; } = 3;
}
