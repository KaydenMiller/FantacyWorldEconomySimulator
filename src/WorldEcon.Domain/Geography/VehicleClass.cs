using ErrorOr;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// A kind of vehicle a caravan travels with. A vehicle may use a route when the route's
/// <see cref="Route.Category"/> equals <see cref="Category"/> and <see cref="Tier"/> lies in the route's
/// [MinTier, MaxTier]. <see cref="SpeedFactor"/> is a percentage of the surface speed (100 = surface speed).
/// Identified by (<see cref="WorldId"/>, <see cref="Name"/>).
/// </summary>
public sealed class VehicleClass
{
    public const int MaxSpeedFactor = 1_000;

    public WorldId WorldId { get; private set; }
    public string Name { get; private set; }
    public RouteCategory Category { get; private set; }
    public int Tier { get; private set; }
    public int SpeedFactor { get; private set; }

    /// <summary>Most cargo mass one caravan of this class carries, grams.</summary>
    public long MaxMassGrams { get; private set; }

    /// <summary>Most cargo volume one caravan of this class carries, cm³.</summary>
    public long MaxVolumeCm3 { get; private set; }

    /// <summary>
    /// cm³ of cargo billed as one kg of dimensional weight on this class (dimensional weight = max(mass,
    /// volume × 1000 ÷ divisor)). Smaller counts volume more.
    /// </summary>
    public long VolumetricDivisor { get; private set; }

    /// <summary>Haulage rate of this class as a percentage of <see cref="World.TransportRate"/>.</summary>
    public int RatePct { get; private set; }

    private VehicleClass() { Name = null!; } // EF

    private VehicleClass(WorldId worldId, string name, RouteCategory category, int tier, int speedFactor,
        long maxMassGrams, long maxVolumeCm3, long volumetricDivisor, int ratePct)
    {
        WorldId = worldId;
        Name = name;
        Category = category;
        Tier = tier;
        SpeedFactor = speedFactor;
        MaxMassGrams = maxMassGrams;
        MaxVolumeCm3 = maxVolumeCm3;
        VolumetricDivisor = volumetricDivisor;
        RatePct = ratePct;
    }

    public static ErrorOr<VehicleClass> Create(WorldId worldId, string name, RouteCategory category, int tier, int speedFactor,
        long maxMassGrams = 1_000_000, long maxVolumeCm3 = 3_000_000, long volumetricDivisor = 5_000, int ratePct = 100)
    {
        if (maxMassGrams < 1 || maxVolumeCm3 < 1)
            return Error.Validation("vehicleclass.capacity.min", "Vehicle capacity must be at least 1 gram and 1 cm³.");
        if (volumetricDivisor < 1)
            return Error.Validation("vehicleclass.divisor.min", "Volumetric divisor must be at least 1.");
        if (ratePct < 0 || ratePct > 100_000)
            return Error.Validation("vehicleclass.rate.range", "Rate must be between 0 and 100,000 %.");
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("vehicleclass.name.empty", "Vehicle class name must not be empty.");
        if (tier < 1)
            return Error.Validation("vehicleclass.tier.min", "Vehicle tier must be at least 1.");
        if (speedFactor < 1 || speedFactor > MaxSpeedFactor)
            return Error.Validation("vehicleclass.speed.range", $"Vehicle speed factor must be between 1 and {MaxSpeedFactor}.");
        return new VehicleClass(worldId, name.Trim(), category, tier, speedFactor, maxMassGrams, maxVolumeCm3, volumetricDivisor, ratePct);
    }

    /// <summary>Name of the vehicle class merchants use when the seed names none.</summary>
    public const string DefaultMerchantClass = "Wagon";

    /// <summary>
    /// The default vehicle classes, in this order (name, category, tier, speed %, max mass, max volume, divisor cm³/kg, rate %):
    /// HandCart (Land, 1, 80, 200 kg, 0.5 m³, 4,000, 100), Wagon (Land, 2, 100, 1,000 kg, 3 m³, 5,000, 100),
    /// Longboat (Sea, 1, 100, 3,000 kg, 10 m³, 6,000, 60), MerchantVessel (Sea, 2, 120, 100,000 kg, 200 m³, 10,000, 30),
    /// Airship (Air, 1, 150, 500 kg, 1.5 m³, 2,000, 400).
    /// </summary>
    public static IReadOnlyList<VehicleClass> Defaults(WorldId worldId) =>
    [
        new(worldId, "HandCart", RouteCategory.Land, 1, 80, 200_000, 500_000, 4_000, 100),
        new(worldId, "Wagon", RouteCategory.Land, 2, 100, 1_000_000, 3_000_000, 5_000, 100),
        new(worldId, "Longboat", RouteCategory.Sea, 1, 100, 3_000_000, 10_000_000, 6_000, 60),
        new(worldId, "MerchantVessel", RouteCategory.Sea, 2, 120, 100_000_000, 200_000_000, 10_000, 30),
        new(worldId, "Airship", RouteCategory.Air, 1, 150, 500_000, 1_500_000, 2_000, 400),
    ];

    /// <summary>
    /// True when a settlement with port kind <paramref name="port"/> offers this class (until fleets exist, a merchant may
    /// use every class its seat offers): Land and Air everywhere; Sea classes at coastal ports, and at river ports only
    /// tier 1 (small craft).
    /// </summary>
    public bool OfferedAt(PortKind port) => Category switch
    {
        RouteCategory.Sea => port == PortKind.Coastal || (port == PortKind.River && Tier <= 1),
        _ => true,
    };
}
