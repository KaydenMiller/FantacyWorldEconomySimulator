using System.Text.Json.Serialization;

namespace WorldEcon.WorldGen.Pipeline;

// Sections of WorldGenSettings. Every property is an int with today's value as its default; the inclusive accepted
// range is in each Validate method and in docs/world-generation-settings.md. Units: M metres, Km kilometres, Bp basis
// points (10,000 = 100 %), DeciC tenths of a degree Celsius, DegC whole degrees Celsius, Mm millimetres per year,
// Deg degrees of latitude, Permille thousandths.

/// <summary>Grid resolution and planet size (grid construction, every area computation).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GridSettings
{
    /// <summary>Largest accepted <see cref="Frequency"/>: 10 × 256² + 2 = 655,362 cells.</summary>
    public const int MaxFrequency = 256;

    /// <summary>Icosahedron subdivision frequency m; the grid has 10m² + 2 cells. Range 1–256.</summary>
    public int Frequency { get; init; } = 64;

    /// <summary>Planet radius in km; scales cell areas (sea level, river flow, evaporation). Range 1–1,000,000.</summary>
    public int PlanetRadiusKm { get; init; } = 6371;

    /// <summary>
    /// Largest accepted Frequency × RegionMultiplier × TileMultiplier, equal to <see cref="Grid.TileGrid.MaxFrequency"/>:
    /// 10 × 14,654² + 2 = 2,147,397,162 is below 2³¹ = 2,147,483,648, and 10 × 14,655² + 2 is not, so tile ids fit an int.
    /// </summary>
    public const int MaxTileFrequency = Grid.TileGrid.MaxFrequency;

    /// <summary>
    /// Region level subdivision: the region grid has frequency <see cref="Frequency"/> × this (default 3 gives 368,642
    /// region cells, about 37 km). Range 1–16.
    /// </summary>
    public int RegionMultiplier { get; init; } = 3;

    /// <summary>
    /// Tile level subdivision: the tile grid has frequency <see cref="Frequency"/> × <see cref="RegionMultiplier"/> × this
    /// (default 8 gives frequency 1,536 and 23,592,962 tiles, about 5 km flat-to-flat on Earth radius). Range 1–32.
    /// </summary>
    public int TileMultiplier { get; init; } = 8;

    /// <summary>Cell count for <see cref="Frequency"/>, or null when the frequency is out of range.</summary>
    internal long? CellCountOrNull() => Frequency is >= 1 and <= MaxFrequency ? (10L * Frequency * Frequency) + 2 : null;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(Frequency), Frequency, 1, MaxFrequency);
        v.Range(nameof(PlanetRadiusKm), PlanetRadiusKm, 1, 1_000_000);
        v.Range(nameof(RegionMultiplier), RegionMultiplier, 1, 16);
        v.Range(nameof(TileMultiplier), TileMultiplier, 1, 32);
        // Tile ids are int: 10m² + 2 < 2³¹ requires m ≤ 14,654.
        v.ProductAtMost(MaxTileFrequency, (nameof(Frequency), Frequency), (nameof(RegionMultiplier), RegionMultiplier), (nameof(TileMultiplier), TileMultiplier));
    }
}

/// <summary>Stage 1 (plates).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlateSettings
{
    /// <summary>Number of tectonic plates. Range 1–cell count.</summary>
    public int Count { get; init; } = 20;

    /// <summary>Target share of cells on continental plates (plates are marked continental in id order until reached). Range 0–10,000.</summary>
    public int ContinentalFractionBp { get; init; } = 4000;

    /// <summary>
    /// Lower end of the uniform plate speed draw, mm/year at the plate's fastest point (1 mm/year = 1 km per million
    /// years). Range 0–500; at most <see cref="MaxSpeedMmPerYr"/>. Real plates move about 10–100 mm/year; Cortial et al.
    /// 2019 cap speed at 100 mm/year.
    /// </summary>
    public int MinSpeedMmPerYr { get; init; } = 10;

    /// <summary>Upper end of the uniform plate speed draw, mm/year. Range 0–500.</summary>
    public int MaxSpeedMmPerYr { get; init; } = 100;

    internal void Validate(SectionValidator v, GridSettings grid)
    {
        v.Range(nameof(Count), Count, 1, grid.CellCountOrNull() ?? int.MaxValue);
        v.Range(nameof(ContinentalFractionBp), ContinentalFractionBp, 0, 10_000);
        v.Range(nameof(MinSpeedMmPerYr), MinSpeedMmPerYr, 0, 500);
        v.Range(nameof(MaxSpeedMmPerYr), MaxSpeedMmPerYr, 0, 500);
        v.Order(nameof(MinSpeedMmPerYr), MinSpeedMmPerYr, nameof(MaxSpeedMmPerYr), MaxSpeedMmPerYr, strict: false);
    }
}

/// <summary>
/// The Tectonics stage (plate history simulation; design 2026-11-24). Start values from the research report's calibration
/// section; the values marked "calibrated" are tuned against the metrics in <c>globe gen --metrics</c>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TectonicsSettings
{
    /// <summary>0 = cratons start packed into one supercontinent; 1 = cratons start scattered with a minimum separation. Range 0–1.</summary>
    public int StartArrangement { get; init; } = 0;

    /// <summary>Continental cores placed at the start, independent of plates. Range 1–1,000.</summary>
    public int CratonCount { get; init; } = 10;

    /// <summary>Length of the simulated history, million years (My). Range 1–4,000; at least <see cref="StepMy"/>.</summary>
    public int SimulatedMy { get; init; } = 250;

    /// <summary>Time per simulation step, My. Range 1–100.</summary>
    public int StepMy { get; init; } = 2;

    /// <summary>Steps between frames published to the stage viewer. Range 1–10,000.</summary>
    public int FrameEverySteps { get; init; } = 10;

    /// <summary>Rift rate λ0 for a reference plate, basis points of one rift per 100 My (calibrated). Range 0–100,000.</summary>
    public int RiftRateBpPer100My { get; init; } = 5_000;

    /// <summary>Cells inboard of a subduction front where uplift peaks; uplift falls to zero by twice this. Range 1–50.</summary>
    public int SubductionUpliftCells { get; init; } = 2;

    /// <summary>Collision footprint factor, basis points: radius in cells = factor/10,000 × sqrt(terrane cells × closing speed in cells per step) (calibrated). Range 0–100,000.</summary>
    public int CollisionRadiusFactorBp { get; init; } = 10_000;

    /// <summary>Mountain height e-folding time, My (research: 100–350; calibrated to 150 with the longer collisions, design start value 200). Range 1–100,000.</summary>
    public int ErosionEFoldMy { get; init; } = 150;

    /// <summary>Continents in contact whose relative speed is below this share of the maximum plate speed, permille, fuse (calibrated). Range 0–1,000.</summary>
    public int FusionMaxSpeedPermille { get; init; } = 200;

    /// <summary>Steps of continental contact after which two plates fuse whatever their speed (calibrated). Range 1–10,000.</summary>
    public int CollisionMaxSteps { get; init; } = 20;

    /// <summary>Chance that a rift fails and the two plates fuse again before ocean forms, basis points. Range 0–10,000.</summary>
    public int FailedRiftBp { get; init; } = 3_000;

    /// <summary>Steps after which a failing rift fuses again (calibrated). Range 1–1,000.</summary>
    public int FailedRiftSteps { get; init; } = 5;

    /// <summary>Noise added to the final elevation, metres; kept below the continental freeboard so it cannot cut channels. Range 0–2,000.</summary>
    public int TopographyNoiseM { get; init; } = 150;

    /// <summary>
    /// Slab pull: a plate's target speed is its drawn speed × (1 + this/10,000 × share of its boundary that is subducting
    /// under a neighbour), so plates dragged by sinking ocean floor speed up. Basis points. Range 0–100,000.
    /// </summary>
    public int SlabPullBp { get; init; } = 20_000;

    /// <summary>Continental drag: target speed × (1 − this/10,000 × the plate's continental share). Basis points. Range 0–10,000.</summary>
    public int ContinentalDragBp { get; init; } = 5_000;

    /// <summary>Collision drag: target speed × (1 − this/10,000) while the plate is in a continental collision. Basis points. Range 0–10,000.</summary>
    public int CollisionDragBp { get; init; } = 7_000;

    /// <summary>A plate's speed approaches its target with this e-folding time, million years. Range 1–10,000.</summary>
    public int SpeedEaseMy { get; init; } = 20;

    /// <summary>How far each plate's rotation pole wanders, degrees per 100 million years (applied every 10 My). Range 0–180.</summary>
    public int PoleDriftDegPer100My { get; init; } = 15;

    /// <summary>
    /// Plate count the rift rate steers toward: the rift probability is multiplied by (target / current plate count)²,
    /// limited to 0.1–10. Range 1–1,000.
    /// </summary>
    public int TargetPlateCount { get; init; } = 16;

    /// <summary>Weight of an all-oceanic plate in the rift probability, in place of its continental share when larger (oceanic plates split too). Basis points. Range 0–10,000.</summary>
    public int OceanicRiftShareBp { get; init; } = 10_000;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(StartArrangement), StartArrangement, 0, 1);
        v.Range(nameof(CratonCount), CratonCount, 1, 1_000);
        v.Range(nameof(SimulatedMy), SimulatedMy, 1, 4_000);
        v.Range(nameof(StepMy), StepMy, 1, 100);
        v.Range(nameof(FrameEverySteps), FrameEverySteps, 1, 10_000);
        v.Range(nameof(RiftRateBpPer100My), RiftRateBpPer100My, 0, 100_000);
        v.Range(nameof(SubductionUpliftCells), SubductionUpliftCells, 1, 50);
        v.Range(nameof(CollisionRadiusFactorBp), CollisionRadiusFactorBp, 0, 100_000);
        v.Range(nameof(ErosionEFoldMy), ErosionEFoldMy, 1, 100_000);
        v.Range(nameof(FusionMaxSpeedPermille), FusionMaxSpeedPermille, 0, 1_000);
        v.Range(nameof(CollisionMaxSteps), CollisionMaxSteps, 1, 10_000);
        v.Range(nameof(FailedRiftBp), FailedRiftBp, 0, 10_000);
        v.Range(nameof(FailedRiftSteps), FailedRiftSteps, 1, 1_000);
        v.Range(nameof(TopographyNoiseM), TopographyNoiseM, 0, 2_000);
        v.Range(nameof(SlabPullBp), SlabPullBp, 0, 100_000);
        v.Range(nameof(ContinentalDragBp), ContinentalDragBp, 0, 10_000);
        v.Range(nameof(CollisionDragBp), CollisionDragBp, 0, 10_000);
        v.Range(nameof(SpeedEaseMy), SpeedEaseMy, 1, 10_000);
        v.Range(nameof(PoleDriftDegPer100My), PoleDriftDegPer100My, 0, 180);
        v.Range(nameof(TargetPlateCount), TargetPlateCount, 1, 1_000);
        v.Range(nameof(OceanicRiftShareBp), OceanicRiftShareBp, 0, 10_000);
        v.Order(nameof(StepMy), StepMy, nameof(SimulatedMy), SimulatedMy, strict: false);
    }
}

/// <summary>
/// Elevation shaping read by the Tectonics stage (version 6): the noise shape (octaves, frequency, persistence,
/// lacunarity; its amplitude is <see cref="TectonicsSettings.TopographyNoiseM"/>), the clamp
/// [<see cref="MinElevationM"/>, <see cref="MaxElevationM"/>], and continental shelves (<see cref="ShelfDepthM"/>,
/// <see cref="ShelfWidthCells"/>). The version 5 boundary heights and fall-off were removed with the Elevation stage.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ElevationSettings
{
    private const int Limit = 20_000;


    /// <summary>Fractal noise octaves (0 disables noise). Range 0–16.</summary>
    public int NoiseOctaves { get; init; } = 4;

    /// <summary>First-octave lattice frequency on the unit sphere. Range 1–1,000.</summary>
    public int NoiseFrequency { get; init; } = 4;

    /// <summary>Amplitude multiplier per octave. Range 0–10,000.</summary>
    public int NoisePersistenceBp { get; init; } = 5000;

    /// <summary>Frequency multiplier per octave. Range 10,000–40,000.</summary>
    public int NoiseLacunarityBp { get; init; } = 20000;

    /// <summary>
    /// Continental shelf: ocean cells within <see cref="ShelfWidthCells"/> steps of land on a continental plate are raised
    /// to at least sea level + this (a negative depth; the sea level is the one Stage 3 picks from
    /// <see cref="SeaLevelSettings.LandFractionBp"/>). Range −20,000 to −1, so shelves stay below sea level and the land
    /// share is unchanged.
    /// </summary>
    public int ShelfDepthM { get; init; } = -200;

    /// <summary>Shelf width in ocean steps from a continental coast; 0 disables shelves. Range 0–1,000.</summary>
    public int ShelfWidthCells { get; init; } = 2;

    /// <summary>Lowest stored elevation. Range ±20,000; at most <see cref="MaxElevationM"/>.</summary>
    public int MinElevationM { get; init; } = -11_000;

    /// <summary>Highest stored elevation. Range ±20,000.</summary>
    public int MaxElevationM { get; init; } = 9_000;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(NoiseOctaves), NoiseOctaves, 0, 16);
        v.Range(nameof(NoiseFrequency), NoiseFrequency, 1, 1_000);
        v.Range(nameof(NoisePersistenceBp), NoisePersistenceBp, 0, 10_000);
        v.Range(nameof(NoiseLacunarityBp), NoiseLacunarityBp, 10_000, 40_000);
        v.Range(nameof(MinElevationM), MinElevationM, -Limit, Limit);
        v.Range(nameof(MaxElevationM), MaxElevationM, -Limit, Limit);
        v.Range(nameof(ShelfDepthM), ShelfDepthM, -Limit, -1);
        v.Range(nameof(ShelfWidthCells), ShelfWidthCells, 0, 1_000);
        v.Order(nameof(MinElevationM), MinElevationM, nameof(MaxElevationM), MaxElevationM, strict: false);
    }
}

/// <summary>Stage 3 (sea level).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SeaLevelSettings
{
    /// <summary>Target area-weighted land share; the sea level is the matching elevation percentile. Range 0–10,000.</summary>
    public int LandFractionBp { get; init; } = 3000;

    internal void Validate(SectionValidator v) => v.Range(nameof(LandFractionBp), LandFractionBp, 0, 10_000);
}

/// <summary>
/// Stage 4 (ocean currents). Flow is westward below <see cref="TropicLatDeg"/>, eastward up to <see cref="PolarLatDeg"/>,
/// westward above; margin rules apply up to <see cref="PolarLatDeg"/>. Margin anomaly magnitude is
/// <see cref="AnomalyBaseDeciC"/>, rising linearly to <see cref="AnomalyPeakDeciC"/> at <see cref="AnomalyPeakLatDeg"/>
/// within ± <see cref="AnomalyHalfWidthDeg"/>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurrentSettings
{
    /// <summary>|Latitude| where westward tropical flow turns eastward. Range 0–90; at most <see cref="PolarLatDeg"/>.</summary>
    public int TropicLatDeg { get; init; } = 30;

    /// <summary>|Latitude| above which flow is westward and margin rules stop. Range 0–90.</summary>
    public int PolarLatDeg { get; init; } = 60;

    /// <summary>Margin anomaly magnitude outside the peak band (warm on western margins, cold on eastern). Range 0–1,000.</summary>
    public int AnomalyBaseDeciC { get; init; } = 20;

    /// <summary>Margin anomaly magnitude at <see cref="AnomalyPeakLatDeg"/>. Range 0–1,000.</summary>
    public int AnomalyPeakDeciC { get; init; } = 60;

    /// <summary>|Latitude| of the strongest margin anomaly. Range 0–90.</summary>
    public int AnomalyPeakLatDeg { get; init; } = 30;

    /// <summary>Half-width of the band around <see cref="AnomalyPeakLatDeg"/> where the anomaly exceeds the base. Range 1–90.</summary>
    public int AnomalyHalfWidthDeg { get; init; } = 10;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(TropicLatDeg), TropicLatDeg, 0, 90);
        v.Range(nameof(PolarLatDeg), PolarLatDeg, 0, 90);
        v.Range(nameof(AnomalyBaseDeciC), AnomalyBaseDeciC, 0, 1_000);
        v.Range(nameof(AnomalyPeakDeciC), AnomalyPeakDeciC, 0, 1_000);
        v.Range(nameof(AnomalyPeakLatDeg), AnomalyPeakLatDeg, 0, 90);
        v.Range(nameof(AnomalyHalfWidthDeg), AnomalyHalfWidthDeg, 1, 90);
        v.Order(nameof(TropicLatDeg), TropicLatDeg, nameof(PolarLatDeg), PolarLatDeg, strict: false);
    }
}

/// <summary>
/// Stage 5 (prevailing winds). Three bands of <see cref="BandWidthDeg"/> per hemisphere (band index capped at 2, so the
/// third band reaches the pole). Strength is <see cref="PeakStrength"/> at a band centre, falling linearly to
/// <see cref="EdgeStrength"/> at its edges. Land more than <see cref="MountainThresholdM"/> above sea level loses
/// (height above sea level) / <see cref="MountainLossDivisorM"/>, not below <see cref="MountainFloorStrength"/>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WindSettings
{
    /// <summary>Width of each wind band (trade winds, westerlies, polar easterlies). Range 1–90.</summary>
    public int BandWidthDeg { get; init; } = 30;

    /// <summary>Strength at a band centre (stored as a byte). Range 0–255.</summary>
    public int PeakStrength { get; init; } = 160;

    /// <summary>Strength at a band edge. Range 0–255.</summary>
    public int EdgeStrength { get; init; } = 60;

    /// <summary>Weight of the poleward (westerlies) or equatorward (trade winds) component against the east–west unit vector. Range 0–10,000.</summary>
    public int MeridionalShareBp { get; init; } = 5000;

    /// <summary>
    /// Weight of the equatorward component of the polar easterlies (third band). Lower values keep polar air circling
    /// the pole instead of flowing out of it, so moisture from polar coasts reaches a polar continent's interior.
    /// Range 0–10,000.
    /// </summary>
    public int PolarMeridionalShareBp { get; init; } = 0;

    /// <summary>Height above sea level beyond which land weakens the wind. Range 0–20,000.</summary>
    public int MountainThresholdM { get; init; } = 2000;

    /// <summary>Metres of height above sea level per unit of strength lost. Range 1–10,000.</summary>
    public int MountainLossDivisorM { get; init; } = 50;

    /// <summary>Lowest strength after the mountain loss. Range 0–255.</summary>
    public int MountainFloorStrength { get; init; } = 20;

    /// <summary>
    /// Band edges follow a latitude shifted by this × noise in [−1, 1], so band boundaries wander instead of following
    /// parallels; 0 disables. Range 0–45.
    /// </summary>
    public int BandNoiseAmplitudeDeg { get; init; } = 15;

    /// <summary>First-octave lattice frequency of the band-edge noise on the unit sphere. Range 1–1,000.</summary>
    public int BandNoiseFrequency { get; init; } = 3;

    /// <summary>Octaves of the band-edge noise (persistence 0.5, lacunarity 2); 0 disables. Range 0–16.</summary>
    public int BandNoiseOctaves { get; init; } = 3;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(BandWidthDeg), BandWidthDeg, 1, 90);
        v.Range(nameof(PeakStrength), PeakStrength, 0, 255);
        v.Range(nameof(EdgeStrength), EdgeStrength, 0, 255);
        v.Range(nameof(MeridionalShareBp), MeridionalShareBp, 0, 10_000);
        v.Range(nameof(PolarMeridionalShareBp), PolarMeridionalShareBp, 0, 10_000);
        v.Range(nameof(MountainThresholdM), MountainThresholdM, 0, 20_000);
        v.Range(nameof(MountainLossDivisorM), MountainLossDivisorM, 1, 10_000);
        v.Range(nameof(MountainFloorStrength), MountainFloorStrength, 0, 255);
        v.Range(nameof(BandNoiseAmplitudeDeg), BandNoiseAmplitudeDeg, 0, 45);
        v.Range(nameof(BandNoiseFrequency), BandNoiseFrequency, 1, 1_000);
        v.Range(nameof(BandNoiseOctaves), BandNoiseOctaves, 0, 16);
    }
}

/// <summary>
/// Stage 6 (temperature and precipitation). Temperature = <see cref="PoleDeciC"/> + (<see cref="EquatorDeciC"/> −
/// <see cref="PoleDeciC"/>) × cos(latitude), minus <see cref="LapseDeciCPerKm"/> per km above sea level on land, plus the
/// current anomaly (ocean) or <see cref="CoastalAnomalyShareBp"/> of the strongest neighbouring ocean anomaly (coastal
/// land). Saturation = reference × 2^((T − <see cref="SaturationReferenceDeciC"/>) / <see cref="SaturationDoublingDegC"/>),
/// with the power-of-two exponent clamped to ± <see cref="SaturationMaxOctaves"/>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClimateSettings
{
    private const int TemperatureLimit = 1_000;

    /// <summary>Sea-level temperature at the equator before anomalies. Range ±1,000.</summary>
    public int EquatorDeciC { get; init; } = 300;

    /// <summary>Sea-level temperature at the poles before anomalies. Range ±1,000.</summary>
    public int PoleDeciC { get; init; } = -250;

    /// <summary>Cooling per km of height above sea level on land. Range 0–1,000.</summary>
    public int LapseDeciCPerKm { get; init; } = 65;

    /// <summary>Share of the strongest neighbouring ocean anomaly added to a coastal land cell. Range 0–10,000.</summary>
    public int CoastalAnomalyShareBp { get; init; } = 5000;

    /// <summary>Warm-up passes; the same number of passes is then counted as rain (2 × this in total). Range 1–10,000.</summary>
    public int MoistureIterations { get; init; } = 64;

    /// <summary>Share of carried moisture that falls on every step. Range 0–10,000.</summary>
    public int MoistureBaseLossBp { get; init; } = 500;

    /// <summary>Extra share falling per km of climb (capped at 100 % with the base loss). Range 0–10,000,000.</summary>
    public int MoistureOrographicLossBpPerKm { get; init; } = 1500;

    /// <summary>
    /// Largest share of carried moisture that can fall on one step (base plus orographic loss is capped here), so the
    /// rain from a steep climb continues over the next cells instead of falling on the first slope. Range 0–10,000.
    /// </summary>
    public int MoistureMaxLossBp { get; init; } = 1500;

    /// <summary>Mean per-pass rain, as a share of reference saturation, that maps to <see cref="PrecipitationCeilingMm"/>. Range 1–10,000.</summary>
    public int MoistureFullScaleBp { get; init; } = 2500;

    /// <summary>Share of moisture sent to the two slots beside the wind slot (half each). Range 0–10,000.</summary>
    public int MoistureSpreadBp { get; init; } = 3000;

    /// <summary>Temperature at which saturation equals the reference amount. Range ±1,000.</summary>
    public int SaturationReferenceDeciC { get; init; } = 150;

    /// <summary>Whole °C per doubling of saturation. Range 1–100.</summary>
    public int SaturationDoublingDegC { get; init; } = 10;

    /// <summary>Largest |power of two| applied to saturation. Range 0–10.</summary>
    public int SaturationMaxOctaves { get; init; } = 8;

    /// <summary>Linear full-scale precipitation and the soft-knee asymptote. Range 1–20,000.</summary>
    public int PrecipitationCeilingMm { get; init; } = 4000;

    /// <summary>Precipitation is linear up to this, then compressed toward the ceiling. Range 0–20,000; at most <see cref="PrecipitationCeilingMm"/>.</summary>
    public int PrecipitationSoftKneeMm { get; init; } = 1500;

    /// <summary>
    /// Lowest saturation (moisture capacity) of any cell, as a share of the reference saturation; keeps cold air able to
    /// carry moisture inland. 0 disables. Range 0–10,000.
    /// </summary>
    public int SaturationFloorBp { get; init; } = 3500;

    /// <summary>
    /// Share of a land cell's moisture above its saturation that rains out per pass; the rest is carried downwind and
    /// rains out over the following cells. 10,000 rains the whole excess in one cell. Range 1–10,000.
    /// </summary>
    public int SaturationRainOutBp { get; init; } = 2000;

    /// <summary>Temperature uses a latitude shifted by this × noise in [−1, 1] (isotherms wander); 0 disables. Range 0–45.</summary>
    public int LatitudeNoiseAmplitudeDeg { get; init; } = 15;

    /// <summary>First-octave lattice frequency of the temperature latitude noise. Range 1–1,000.</summary>
    public int LatitudeNoiseFrequency { get; init; } = 3;

    /// <summary>Octaves of the temperature latitude noise (persistence 0.5, lacunarity 2); 0 disables. Range 0–16.</summary>
    public int LatitudeNoiseOctaves { get; init; } = 3;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(EquatorDeciC), EquatorDeciC, -TemperatureLimit, TemperatureLimit);
        v.Range(nameof(PoleDeciC), PoleDeciC, -TemperatureLimit, TemperatureLimit);
        v.Range(nameof(LapseDeciCPerKm), LapseDeciCPerKm, 0, 1_000);
        v.Range(nameof(CoastalAnomalyShareBp), CoastalAnomalyShareBp, 0, 10_000);
        v.Range(nameof(MoistureIterations), MoistureIterations, 1, 10_000);
        v.Range(nameof(MoistureBaseLossBp), MoistureBaseLossBp, 0, 10_000);
        v.Range(nameof(MoistureOrographicLossBpPerKm), MoistureOrographicLossBpPerKm, 0, 10_000_000);
        v.Range(nameof(MoistureMaxLossBp), MoistureMaxLossBp, 0, 10_000);
        v.Range(nameof(MoistureFullScaleBp), MoistureFullScaleBp, 1, 10_000);
        v.Range(nameof(MoistureSpreadBp), MoistureSpreadBp, 0, 10_000);
        v.Range(nameof(SaturationReferenceDeciC), SaturationReferenceDeciC, -TemperatureLimit, TemperatureLimit);
        v.Range(nameof(SaturationDoublingDegC), SaturationDoublingDegC, 1, 100);
        v.Range(nameof(SaturationMaxOctaves), SaturationMaxOctaves, 0, 10);
        v.Range(nameof(PrecipitationCeilingMm), PrecipitationCeilingMm, 1, 20_000);
        v.Range(nameof(PrecipitationSoftKneeMm), PrecipitationSoftKneeMm, 0, 20_000);
        v.Range(nameof(SaturationFloorBp), SaturationFloorBp, 0, 10_000);
        v.Range(nameof(SaturationRainOutBp), SaturationRainOutBp, 1, 10_000);
        v.Range(nameof(LatitudeNoiseAmplitudeDeg), LatitudeNoiseAmplitudeDeg, 0, 45);
        v.Range(nameof(LatitudeNoiseFrequency), LatitudeNoiseFrequency, 1, 1_000);
        v.Range(nameof(LatitudeNoiseOctaves), LatitudeNoiseOctaves, 0, 16);
        v.Order(nameof(PrecipitationSoftKneeMm), PrecipitationSoftKneeMm, nameof(PrecipitationCeilingMm), PrecipitationCeilingMm, strict: false);
    }
}

/// <summary>
/// Biome thresholds of a tile (TileLandform; <c>BiomeStage.MainType</c> for the rule order). Height h is elevation − tile
/// sea level. Mountain and Hills main types come from the tile landform; the height rules below also apply to tiles
/// whose landform is not Mountain or Hills (a high plateau).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BiomeSettings
{
    private const int HeightLimit = 20_000;
    private const int TemperatureLimit = 1_000;
    private const int RainLimit = 20_000;
    private const int SlopeLimit = 100_000_000;

    /// <summary>Mountain (landform and biome) when h is above this. Range 0–20,000; greater than <see cref="HillsHeightM"/>.</summary>
    public int MountainHeightM { get; init; } = 2_500;

    /// <summary>Biome Hills (or forested hills) when h is above this, whatever the landform. Range 0–20,000.</summary>
    public int HillsHeightM { get; init; } = 1_500;

    /// <summary>Ice below this temperature. Range ±1,000; less than <see cref="TundraBelowDeciC"/>.</summary>
    public int IceBelowDeciC { get; init; } = -100;

    /// <summary>Tundra below this temperature. Range ±1,000; less than <see cref="TaigaBelowDeciC"/>.</summary>
    public int TundraBelowDeciC { get; init; } = 0;

    /// <summary>Taiga below this temperature (after the swamp test). Range ±1,000; less than <see cref="TemperateMaxDeciC"/>.</summary>
    public int TaigaBelowDeciC { get; init; } = 50;

    /// <summary>Temperate types up to and including this temperature; tropical types above. Range ±1,000.</summary>
    public int TemperateMaxDeciC { get; init; } = 210;

    /// <summary>Desert below this precipitation (temperate and tropical). Range 0–20,000; less than <see cref="ShrublandBelowMm"/> and <see cref="SavannaBelowMm"/>.</summary>
    public int DesertBelowMm { get; init; } = 250;

    /// <summary>Temperate shrubland below this precipitation. Range 0–20,000; less than <see cref="GrasslandBelowMm"/>.</summary>
    public int ShrublandBelowMm { get; init; } = 500;

    /// <summary>Temperate grassland below this precipitation; temperate forest at or above. Range 0–20,000.</summary>
    public int GrasslandBelowMm { get; init; } = 700;

    /// <summary>
    /// A Hills tile with a temperate temperature (<see cref="TaigaBelowDeciC"/> to <see cref="TemperateMaxDeciC"/>) and
    /// at least this precipitation has biome TemperateForest; its landform stays Hills. Values above the precipitation
    /// ceiling turn the rule off. Range 0–20,000.
    /// </summary>
    public int ForestedHillsMinMm { get; init; } = 650;

    /// <summary>Tropical savanna below this precipitation; tropical forest at or above. Range 0–20,000.</summary>
    public int SavannaBelowMm { get; init; } = 1_200;

    /// <summary>Swamp at or above this precipitation (with low relief, at or above <see cref="TundraBelowDeciC"/>). Range 0–20,000.</summary>
    public int SwampMinMm { get; init; } = 2_000;

    /// <summary>Swamp needs the steepest tile slope (largest surface difference to a neighbour / centre distance) below this, cm per km (84 is 4.2 m over 5 km). Range 0–100,000,000.</summary>
    public int SwampMaxSlopeCmPerKm { get; init; } = 84;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(MountainHeightM), MountainHeightM, 0, HeightLimit);
        v.Range(nameof(HillsHeightM), HillsHeightM, 0, HeightLimit);
        v.Range(nameof(IceBelowDeciC), IceBelowDeciC, -TemperatureLimit, TemperatureLimit);
        v.Range(nameof(TundraBelowDeciC), TundraBelowDeciC, -TemperatureLimit, TemperatureLimit);
        v.Range(nameof(TaigaBelowDeciC), TaigaBelowDeciC, -TemperatureLimit, TemperatureLimit);
        v.Range(nameof(TemperateMaxDeciC), TemperateMaxDeciC, -TemperatureLimit, TemperatureLimit);
        v.Range(nameof(DesertBelowMm), DesertBelowMm, 0, RainLimit);
        v.Range(nameof(ShrublandBelowMm), ShrublandBelowMm, 0, RainLimit);
        v.Range(nameof(GrasslandBelowMm), GrasslandBelowMm, 0, RainLimit);
        v.Range(nameof(SavannaBelowMm), SavannaBelowMm, 0, RainLimit);
        v.Range(nameof(ForestedHillsMinMm), ForestedHillsMinMm, 0, RainLimit);
        v.Range(nameof(SwampMinMm), SwampMinMm, 0, RainLimit);
        v.Range(nameof(SwampMaxSlopeCmPerKm), SwampMaxSlopeCmPerKm, 0, SlopeLimit);
        v.Order(nameof(HillsHeightM), HillsHeightM, nameof(MountainHeightM), MountainHeightM, strict: true);
        v.Order(nameof(IceBelowDeciC), IceBelowDeciC, nameof(TundraBelowDeciC), TundraBelowDeciC, strict: true);
        v.Order(nameof(TundraBelowDeciC), TundraBelowDeciC, nameof(TaigaBelowDeciC), TaigaBelowDeciC, strict: true);
        v.Order(nameof(TaigaBelowDeciC), TaigaBelowDeciC, nameof(TemperateMaxDeciC), TemperateMaxDeciC, strict: true);
        v.Order(nameof(DesertBelowMm), DesertBelowMm, nameof(ShrublandBelowMm), ShrublandBelowMm, strict: true);
        v.Order(nameof(ShrublandBelowMm), ShrublandBelowMm, nameof(GrasslandBelowMm), GrasslandBelowMm, strict: true);
        v.Order(nameof(DesertBelowMm), DesertBelowMm, nameof(SavannaBelowMm), SavannaBelowMm, strict: true);
    }
}

/// <summary>Evaporation of tile lakes (TileHydrology) and the summary river-system rule (Summary).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HydrologySettings
{
    /// <summary>Lake evaporation (TileHydrology): this × max(0, tile temperature °C) × tile area km², in mm·km² per year. Range 0–1,000.</summary>
    public int EvaporationMmPerDegree { get; init; } = 100;

    /// <summary>
    /// Lake evaporation added at every temperature (sublimation and evaporation from cold surfaces), mm per year × tile
    /// area km². Range 0–20,000.
    /// </summary>
    public int EvaporationBaseMm { get; init; } = 300;

    /// <summary>
    /// A summary river system (globe river edges connected through shared cells) with fewer edges than this is dropped
    /// unless it is in the top flow tier (<see cref="RiverTopTierPercentileBp"/>); removes one-step coastal stubs. The
    /// renderer applies the same rule to the region-cell river segments drawn at Globe and Region View. 1 keeps every
    /// system. Range 1–1,000,000.
    /// </summary>
    public int RiverMinSystemEdges { get; init; } = 3;

    /// <summary>A short system is kept when one of its edges has a flow at or above this percentile of all river edge flows. Range 0–10,000.</summary>
    public int RiverTopTierPercentileBp { get; init; } = 9000;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(EvaporationMmPerDegree), EvaporationMmPerDegree, 0, 1_000);
        v.Range(nameof(EvaporationBaseMm), EvaporationBaseMm, 0, 20_000);
        v.Range(nameof(RiverMinSystemEdges), RiverMinSystemEdges, 1, 1_000_000);
        v.Range(nameof(RiverTopTierPercentileBp), RiverTopTierPercentileBp, 0, 10_000);
    }
}

/// <summary>Stage 9 (natural areas).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AreaSettings
{
    private const int CellLimit = 1_000_000;

    /// <summary>Smallest landform share that can make a cell a member of a landform area. Range 1–10,000.</summary>
    public int MemberThresholdBp { get; init; } = 2000;

    /// <summary>A non-Plain landform with at least this share is dominant even when another landform is larger. Range 1–10,000.</summary>
    public int DominantShareBp { get; init; } = 3333;

    /// <summary>Smallest component (cells) that becomes an area, for kinds other than Ocean, Lake and DryLakeBed. Range 1–1,000,000.</summary>
    public int MinAreaCells { get; init; } = 3;

    /// <summary>Smallest component (cells) that becomes an Ocean, Lake or DryLakeBed area. Range 1–1,000,000.</summary>
    public int MinWaterAreaCells { get; init; } = 1;

    /// <summary>An ocean component with fewer cells than this is a Sea. Range 0–1,000,000.</summary>
    public int SeaMaxCells { get; init; } = 500;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(MemberThresholdBp), MemberThresholdBp, 1, 10_000);
        v.Range(nameof(DominantShareBp), DominantShareBp, 1, 10_000);
        v.Range(nameof(MinAreaCells), MinAreaCells, 1, CellLimit);
        v.Range(nameof(MinWaterAreaCells), MinWaterAreaCells, 1, CellLimit);
        v.Range(nameof(SeaMaxCells), SeaMaxCells, 0, CellLimit);
    }
}

/// <summary>
/// Stage 10 (settlement pinning; lookup only). Suitability = <see cref="TemperatureScore"/> when temperature is within
/// [<see cref="IdealMinDeciC"/>, <see cref="IdealMaxDeciC"/>] + <see cref="PrecipitationScore"/> when precipitation is
/// within [<see cref="IdealMinMm"/>, <see cref="IdealMaxMm"/>] + <see cref="RiverScore"/> on a river cell +
/// <see cref="CoastScore"/> with an ocean neighbour.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PinSettings
{
    private const int RingLimit = 10_000;
    private const int ScoreLimit = 1_000;

    /// <summary>Requested ring separation between auto-placed group anchors (relaxed one ring at a time when no cell qualifies). Range 1–10,000.</summary>
    public int MinGroupSeparationRings { get; init; } = 6;

    /// <summary>Auto-placed group members search this many rings around the anchor before widening. Range 0–10,000.</summary>
    public int MemberRadiusRings { get; init; } = 3;

    /// <summary>Lower end of the ideal temperature band. Range ±1,000; at most <see cref="IdealMaxDeciC"/>.</summary>
    public int IdealMinDeciC { get; init; } = 50;

    /// <summary>Upper end of the ideal temperature band. Range ±1,000.</summary>
    public int IdealMaxDeciC { get; init; } = 250;

    /// <summary>Lower end of the ideal precipitation band. Range 0–20,000; at most <see cref="IdealMaxMm"/>.</summary>
    public int IdealMinMm { get; init; } = 400;

    /// <summary>Upper end of the ideal precipitation band. Range 0–20,000.</summary>
    public int IdealMaxMm { get; init; } = 1500;

    /// <summary>Score for a temperature inside the ideal band. Range 0–1,000.</summary>
    public int TemperatureScore { get; init; } = 4;

    /// <summary>Score for precipitation inside the ideal band. Range 0–1,000.</summary>
    public int PrecipitationScore { get; init; } = 4;

    /// <summary>Score for a river cell. Range 0–1,000.</summary>
    public int RiverScore { get; init; } = 2;

    /// <summary>Score for a cell with an ocean neighbour. Range 0–1,000.</summary>
    public int CoastScore { get; init; } = 2;

    /// <summary>Tile pinning: score for a tile that is a river tile or has a river neighbour (water supply, transport). Range 0–1,000.</summary>
    public int TileRiverScore { get; init; } = 3;

    /// <summary>Tile pinning: score for a tile with an ocean or lake neighbour (harbour, fishing). Range 0–1,000.</summary>
    public int TileShoreScore { get; init; } = 2;

    /// <summary>Tile pinning: score for a flat tile (<see cref="TileFlatMaxDiffM"/>). Range 0–1,000.</summary>
    public int TileFlatScore { get; init; } = 2;

    /// <summary>Tile pinning: a tile is flat when the largest elevation difference to a land neighbour is at most this; 50 m over about 5 km is a 1 % slope. Range 0–20,000.</summary>
    public int TileFlatMaxDiffM { get; init; } = 50;

    /// <summary>
    /// Tile pinning: settlements pinned to the same globe cell are placed at least this far apart (tile centre chord
    /// distance) when the cell has such a tile; 15 km is 3 tile spacings at the default grid, about half a day's walk.
    /// Range 0–100,000.
    /// </summary>
    public int TileMinSpacingKm { get; init; } = 15;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(MinGroupSeparationRings), MinGroupSeparationRings, 1, RingLimit);
        v.Range(nameof(MemberRadiusRings), MemberRadiusRings, 0, RingLimit);
        v.Range(nameof(IdealMinDeciC), IdealMinDeciC, -1_000, 1_000);
        v.Range(nameof(IdealMaxDeciC), IdealMaxDeciC, -1_000, 1_000);
        v.Range(nameof(IdealMinMm), IdealMinMm, 0, 20_000);
        v.Range(nameof(IdealMaxMm), IdealMaxMm, 0, 20_000);
        v.Range(nameof(TemperatureScore), TemperatureScore, 0, ScoreLimit);
        v.Range(nameof(PrecipitationScore), PrecipitationScore, 0, ScoreLimit);
        v.Range(nameof(RiverScore), RiverScore, 0, ScoreLimit);
        v.Range(nameof(CoastScore), CoastScore, 0, ScoreLimit);
        v.Range(nameof(TileRiverScore), TileRiverScore, 0, ScoreLimit);
        v.Range(nameof(TileShoreScore), TileShoreScore, 0, ScoreLimit);
        v.Range(nameof(TileFlatScore), TileFlatScore, 0, ScoreLimit);
        v.Range(nameof(TileFlatMaxDiffM), TileFlatMaxDiffM, 0, 20_000);
        v.Range(nameof(TileMinSpacingKm), TileMinSpacingKm, 0, 100_000);
        v.Order(nameof(IdealMinDeciC), IdealMinDeciC, nameof(IdealMaxDeciC), IdealMaxDeciC, strict: false);
        v.Order(nameof(IdealMinMm), IdealMinMm, nameof(IdealMaxMm), IdealMaxMm, strict: false);
    }
}

/// <summary>
/// Tile level generation (stages TileElevation, TileSeaLevel, TileClimate, TileHydrology, TileLandform, summaries). Tiles
/// are about 5 km flat-to-flat at the default grid on an Earth-radius planet (21.6 km² each). The defaults are starting
/// values chosen from the physical reasoning in each property comment; calibration (plan task 5) tuned MinBasinDepthM, BreachDepthM, OrographicGainBp and MountainReliefM. Range
/// reasoning: elevation amplitudes are at most 5,000 m, scaled by at most 3.0 (<see cref="ReliefScaleMaxBp"/> 30,000 bp),
/// so every product is below 2³¹ in <see cref="int"/> arithmetic; the stage clamps the final tile elevation to the
/// <see cref="short"/> range. Precipitation (at most 20,000 mm) × <see cref="OrographicMaxBp"/> (at most 50,000 bp) is
/// 10⁹, below 2³¹. Flow thresholds are compared with <see cref="long"/> flow sums.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TilesSettings
{
    private const int HeightLimit = 20_000;
    private const int AmplitudeLimit = 5_000;
    private const int FlowLimit = 2_000_000_000;

    /// <summary>Detail noise octaves added to the interpolated globe elevation (0 disables detail noise). Each octave doubles the frequency; 4 octaves from <see cref="DetailFrequency"/> 128 span about 50 km down to 6 km features, the last near the 5 km tile size. Range 0–8.</summary>
    public int DetailOctaves { get; init; } = 4;

    /// <summary>First-octave lattice frequency of the detail noise on the unit sphere; features are about planet radius ÷ this: 128 gives about 50 km at radius 6,371 km (10 tiles). Range 1–100,000.</summary>
    public int DetailFrequency { get; init; } = 128;

    /// <summary>Detail noise amplitude (noise in [−1, 1] times this) at a relief scale of 100 % (10,000 bp). 300 m is the order of hill relief inside a 50 km area. Range 0–5,000.</summary>
    public int DetailAmplitudeM { get; init; } = 300;

    /// <summary>
    /// Lower clamp of the relief scale (bp) applied to <see cref="DetailAmplitudeM"/>. The relief scale of a globe cell is
    /// its relief (largest minus smallest elevation among the cell and its neighbours) × 10 bp per metre, so 1,000 m is
    /// 10,000 bp. 500 bp leaves 15 m of detail on flat plains. Range 0–10,000; at most <see cref="ReliefScaleMaxBp"/>.
    /// </summary>
    public int ReliefScaleMinBp { get; init; } = 500;

    /// <summary>Upper clamp of the relief scale: 20,000 bp doubles the detail amplitude (600 m) over 2,000 m relief and above. Range 100–30,000; at least <see cref="ReliefScaleMinBp"/>.</summary>
    public int ReliefScaleMaxBp { get; init; } = 20_000;

    /// <summary>Extra ridge noise amplitude near convergent plate boundaries. 400 m is the order of ridge-line height over a mountain-belt flank. Range 0–5,000.</summary>
    public int RidgeAmplitudeM { get; init; } = 400;

    /// <summary>
    /// Crests are drawn on cells whose last mountain building (Tectonics stage orogeny record) is younger than this many
    /// million years, fading with age (calibrated). Range 0–10,000.
    /// </summary>
    public int RidgeMaxOrogenyAgeMy { get; init; } = 150;

    /// <summary>
    /// Precipitation gain per metre of upwind rise per km (a windward slope of 20 m/km gives +12 %, 50 m/km +30 %), in bp
    /// of the base precipitation. The globe climate already rains out moisture on rising cells, so the tile gain only
    /// redistributes inside a cell; calibration (task 5): 150 gave 2.09 % (seed 42) and 100 gave 2.49 % (seed 1) of land
    /// at ≥ 3,500 mm (target ≤ 2 %); 60 gives 1.62 % on seed 1. Range 0–10,000.
    /// </summary>
    public int OrographicGainBp { get; init; } = 60;

    /// <summary>Lower clamp of tile precipitation as a share of the base (interpolated globe) value: 3,000 bp = 30 % in a rain shadow. Range 0–10,000; at most <see cref="OrographicMaxBp"/>.</summary>
    public int OrographicMinBp { get; init; } = 3_000;

    /// <summary>Upper clamp of tile precipitation as a share of the base value: 25,000 bp = 2.5 times on windward slopes. Range 0–50,000; at least <see cref="OrographicMinBp"/>.</summary>
    public int OrographicMaxBp { get; init; } = 25_000;

    /// <summary>
    /// TileSeaLevel: a connected region of tiles below the sea level is ocean when its area (sum of tile areas) is at
    /// least this; smaller enclosed below-sea regions are land depressions (lake or dry lake bed candidates for
    /// TileHydrology). 100,000 km² is about 4,600 tiles at the default grid and 1.3 times the area of Lake Superior; the
    /// Caspian Sea (371,000 km²) would count as ocean. 0 makes every below-sea region ocean. Range 0–1,000,000,000.
    /// </summary>
    public int MinOceanAreaKm2 { get; init; } = 100_000;

    /// <summary>
    /// TileHydrology: a closed depression shallower than this (spill level minus its lowest tile) is not a basin and
    /// drains through toward its spill point; inside a basin a pit shallower than this below its saddle is absorbed;
    /// a lake whose level is less than this above its floor is a dry lake bed. The tiles of a depression that drains
    /// through, and of an absorbed pit that stays dry, are raised to its spill level (sediment fill), so drainage never
    /// runs uphill. 10 m: on plains the detail noise amplitude is 15 m (<see cref="ReliefScaleMinBp"/> × <see cref="DetailAmplitudeM"/>), and pits a few metres
    /// deep in a 5 km tile fill with sediment or drain through soil rather than hold a lake; calibration (task 5): lake
    /// share 2.20 → 1.97 % of land at seed 42 together with <see cref="BreachDepthM"/> 300 (before the fix-round carving).
    /// Range 0–40,000; 0 and 1 keep every depression.
    /// </summary>
    public int MinBasinDepthM { get; init; } = 10;

    /// <summary>
    /// TileHydrology outlet breaching: a basin whose inflow fills it to its spill level overflows, and the outflowing
    /// river erodes the sill. The basin drains when its depth (spill level minus lowest tile) is at most BreachDepthM × (outflow / <see cref="MajorRiverFlow"/>)^(<see cref="BreachExponentPermille"/>
    /// / 1,000), outflow = inflow − evaporation of the filled basin. Physical basis: river incision grows with discharge
    /// (stream-power law, erosion ∝ Q^m with m about 0.5), so large rivers keep their outlets open while small ones
    /// leave lakes behind. A breached basin is carved and filled to T = max(lowest tile elevation, tile sea level): the
    /// drainage path from its lowest tile through the outlet is set to T, and downstream of the outlet it is lowered to T
    /// until the first tile at or below T, a water tile, or a tile bordering a lake or dry lake bed whose level is above T;
    /// the rest of the basin is raised to a surface that drains monotonically into that channel (hybrid breach and fill),
    /// so the stored elevations agree with the drainage. A basin whose lowest tile is below the tile sea level is breached
    /// partially (carving never goes below sea level): its tiles below T connected to the lowest tile stay a full lake at
    /// level T. Only whole basins whose top lake is full are breached; nested inner lakes are not. 0 disables.
    /// 600 m at the major-river flow (a gorge of that depth through a sill, e.g. river gorges across ranges); a river of
    /// 1/100 of that flow breaches 60 m. Calibration (task 5): without breaching the lake share is 8.47 % of land at seed
    /// 42; with carving and partial breach (fix round) 300 m gave 2.50 % (seed 42) and 3.02 % (seed 7), 600 m gives
    /// 2.03 % and 2.40 % (target 1.0–2.5 %). Range 0–20,000.
    /// </summary>
    public int BreachDepthM { get; init; } = 600;

    /// <summary>Exponent of the outflow ratio in the breach depth (<see cref="BreachDepthM"/>), in thousandths: 500 is the square root (stream-power exponent m ≈ 0.5). Range 0–3,000.</summary>
    public int BreachExponentPermille { get; init; } = 500;

    /// <summary>
    /// A tile is a river when its flow (precipitation mm × upstream area km², same unit as the globe <c>FlowAccum</c>) is
    /// at least this. 600,000 is a 1,000 km² catchment (about 46 tiles) at 600 mm per year.
    /// Range 1–2,000,000,000; at most <see cref="MajorRiverFlow"/>.
    /// </summary>
    public int RiverMinFlow { get; init; } = 600_000;

    /// <summary>
    /// Flow at or above which a river is major: drawn at Globe and Region View and summarised into globe cells. 50,000,000
    /// is a 71,000 km² catchment at 700 mm per year, about 6 globe cells at Frequency 64, the scale of the globe level's
    /// 95th percentile river. Range 1–2,000,000,000; at least <see cref="RiverMinFlow"/>.
    /// </summary>
    public int MajorRiverFlow { get; init; } = 50_000_000;

    /// <summary>A lake of at least this area (km²) is drawn at Globe View from its region cells (rule in <see cref="Tiles.TileSummaryStage"/>; every such lake gets at least one). 500 km² is about 23 tiles. Range 1–1,000,000,000.</summary>
    public int LargeLakeMinKm2 { get; init; } = 500;

    /// <summary>Radius in tiles of the neighbourhood over which local relief is measured: 3 tiles is about 15 km (37 tiles). Range 1–16.</summary>
    public int ReliefRadius { get; init; } = 3;

    /// <summary>Mountain when the elevation range over the <see cref="ReliefRadius"/> neighbourhood is at least this (or the tile is higher than <see cref="BiomeSettings.MountainHeightM"/> above sea level): 900 m over about 30 km (calibration task 5: 700 gave Mountain 16.0 % of land, above the 8–15 % target; 900 gives 14.1 %). Range 0–20,000; greater than <see cref="HillsReliefM"/>.</summary>
    public int MountainReliefM { get; init; } = 900;

    /// <summary>Hills when the neighbourhood relief is at least this: 250 m over about 30 km. Range 0–20,000; less than <see cref="MountainReliefM"/>.</summary>
    public int HillsReliefM { get; init; } = 250;

    /// <summary>Valley: a river tile whose highest tile within <see cref="ReliefRadius"/> steps is at least this far above it (100 m over about 15 km is a clear incision). Range 0–20,000.</summary>
    public int ValleyDepthM { get; init; } = 100;

    /// <summary>Basin: a land tile at least this far below the mean of its neighbourhood. Range 0–20,000.</summary>
    public int BasinDepthM { get; init; } = 100;

    /// <summary>Wetland requires the largest elevation difference to a neighbouring tile to be at most this (15 m over 5 km is a 0.3 % slope). Range 0–20,000.</summary>
    public int WetlandMaxSlopeM { get; init; } = 15;

    /// <summary>Wetland requires tile precipitation of at least this (mm per year). Range 0–20,000.</summary>
    public int WetlandMinPrecipMm { get; init; } = 600;

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(DetailOctaves), DetailOctaves, 0, 8);
        v.Range(nameof(DetailFrequency), DetailFrequency, 1, 100_000);
        v.Range(nameof(DetailAmplitudeM), DetailAmplitudeM, 0, AmplitudeLimit);
        v.Range(nameof(ReliefScaleMinBp), ReliefScaleMinBp, 0, 10_000);
        v.Range(nameof(ReliefScaleMaxBp), ReliefScaleMaxBp, 100, 30_000);
        v.Range(nameof(RidgeAmplitudeM), RidgeAmplitudeM, 0, AmplitudeLimit);
        v.Range(nameof(RidgeMaxOrogenyAgeMy), RidgeMaxOrogenyAgeMy, 0, 10_000);
        v.Range(nameof(OrographicGainBp), OrographicGainBp, 0, 10_000);
        v.Range(nameof(OrographicMinBp), OrographicMinBp, 0, 10_000);
        v.Range(nameof(OrographicMaxBp), OrographicMaxBp, 0, 50_000);
        v.Range(nameof(MinOceanAreaKm2), MinOceanAreaKm2, 0, 1_000_000_000);
        v.Range(nameof(MinBasinDepthM), MinBasinDepthM, 0, 40_000);
        v.Range(nameof(BreachDepthM), BreachDepthM, 0, 20_000);
        v.Range(nameof(BreachExponentPermille), BreachExponentPermille, 0, 3_000);
        v.Range(nameof(RiverMinFlow), RiverMinFlow, 1, FlowLimit);
        v.Range(nameof(MajorRiverFlow), MajorRiverFlow, 1, FlowLimit);
        v.Range(nameof(LargeLakeMinKm2), LargeLakeMinKm2, 1, 1_000_000_000);
        v.Range(nameof(ReliefRadius), ReliefRadius, 1, 16);
        v.Range(nameof(MountainReliefM), MountainReliefM, 0, HeightLimit);
        v.Range(nameof(HillsReliefM), HillsReliefM, 0, HeightLimit);
        v.Range(nameof(ValleyDepthM), ValleyDepthM, 0, HeightLimit);
        v.Range(nameof(BasinDepthM), BasinDepthM, 0, HeightLimit);
        v.Range(nameof(WetlandMaxSlopeM), WetlandMaxSlopeM, 0, HeightLimit);
        v.Range(nameof(WetlandMinPrecipMm), WetlandMinPrecipMm, 0, 20_000);
        v.Order(nameof(ReliefScaleMinBp), ReliefScaleMinBp, nameof(ReliefScaleMaxBp), ReliefScaleMaxBp, strict: false);
        v.Order(nameof(OrographicMinBp), OrographicMinBp, nameof(OrographicMaxBp), OrographicMaxBp, strict: false);
        v.Order(nameof(RiverMinFlow), RiverMinFlow, nameof(MajorRiverFlow), MajorRiverFlow, strict: false);
        v.Order(nameof(HillsReliefM), HillsReliefM, nameof(MountainReliefM), MountainReliefM, strict: true);
    }
}
