# World generation settings

_Last updated: 2026-11-22 (generator version 4, tile detail world: Grid multipliers, Tiles section and presets added; LocalMap section, the globe composition settings and the globe hydrology settings removed; tile defaults calibrated in plan task 5)._

`WorldGenSettings` (in `src/WorldEcon.WorldGen/Pipeline/`) holds every number that shapes generated world output: 151 integer settings in 13 sections (the 13th, `Roads`, added 2026-11-23, shapes the route graph and no stage output): the globe stages, `Biomes` and `Hydrology` (read by the tile stages), `Tiles` (23 settings) and the two tile grid multipliers in `Grid`. The former `LocalMap` section is removed (an old `generation` block naming it is an unknown-setting error). Phase 1 exposed the 91 version 2 constants; phase 2 added 19 settings for new mechanisms, replaced three relief thresholds (metres) with slope thresholds (cm per km) and changed 9 existing defaults. Default settings produce generator version 4 output; golden stage hashes are in `tests/WorldEcon.WorldGen.Tests.Unit/GoldenSeedTests.cs`. Version 4 removed 13 settings that no longer shape output (listed in the Biomes, Hydrology and Tiles sections). [Phase 2 changes](#phase-2-changes) lists every new setting and every changed default with its version 2 value.

## Contents

- [Using the settings](#using-the-settings)
- [Validation](#validation)
- [Units](#units)
- [Grid](#grid)
- [Plates](#plates)
- [Elevation](#elevation)
- [SeaLevel](#sealevel)
- [Currents](#currents)
- [Winds](#winds)
- [Climate](#climate)
- [Biomes](#biomes)
- [Hydrology](#hydrology)
- [Areas](#areas)
- [Pinning](#pinning)
- [Tiles](#tiles)
- [Roads](#roads)
- [Presets](#presets)
- [Phase 2 changes](#phase-2-changes)
- [Numbers that stay constants](#numbers-that-stay-constants)

## Using the settings

The JSON (JavaScript Object Notation) form is one object per section:

```json
{
  "Grid": { "Frequency": 32 },
  "Climate": { "MoistureIterations": 32, "PrecipitationSoftKneeMm": 2500 }
}
```

- Omitted sections and omitted properties take their defaults; `{}` is the default settings.
- Section and property names match case-insensitively.
- An unknown name is an error: `unknown setting 'Climate.MoistureIteration'` (or `unknown setting 'Climat'` for a section). The flat names used before phase 1 (for example `GridFrequency`) are unknown names.
- A value that is not an integer in the 32-bit range, or a section that is not an object, is an error.
- A section or property named twice (names compared case-insensitively) is an error: `duplicate setting 'Grid.Frequency'` (or `duplicate setting 'Grid'` for a section).

Where the form is accepted:

| Place | Effect |
|---|---|
| Seed file `generation` block (`import <json> <db>`) | Settings for the imported world. A JSON error reads `Seed invalid: generation: unknown setting '…'`. |
| `new <db> --generation <file.json>` | Settings for the demo world. The file is checked (JSON and ranges) before the database is created. |
| `globe defaults` | Prints the full default settings, indented, as a template to edit. |
| `globe settings <db>` | Prints the settings the world's globe was generated with (from `worldgen_runs.SettingsJson`). |
| `worldgen_runs.SettingsJson` | Stores the same nested form. |

## Validation

`WorldGenSettings.Validate()` runs before generation (`WorldGenerator.Generate`), at seed import and in `new --generation`. Error codes are `settings.<Section>.<Property>`; every out-of-range property is reported, not only the first.

- Every property has an inclusive range (tables below). A test sets each of the 128 properties to `int.MinValue` and to `int.MaxValue` and checks that `Validate` names it.
- Cross-field rules (listed under each section) are checked only when both properties are inside their own ranges. Their error carries the code of the last property named in the rule (the second of two; for the Grid product rule, the third: `TileMultiplier`).
- Upper bounds were chosen so that no stage arithmetic overflows; the reasoning for each bound is in the calibration phase 1 report (`.superpowers/sdd/2026-11-21-globe-world-generation/calib-phase1-report.md`).

## Units

| Suffix | Unit |
|---|---|
| `M` | metres |
| `CmPerKm` | centimetres of elevation gap per kilometre of distance (1,000 = 10 m/km) |
| `Km` | kilometres |
| `Bp` | basis points (10,000 = 100 %) |
| `DeciC` | tenths of a degree Celsius |
| `DegC` | whole degrees Celsius |
| `Mm` | millimetres per year (precipitation) |
| `Deg` | degrees of latitude |
| `Permille` | thousandths |
| `Cells`, `Rings` | grid cells; neighbour steps (breadth-first search depth) |

## Grid

Read by grid construction (frequency) and every area computation (radius): sea level, river flow, basin evaporation.

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `Frequency` | subdivisions | 64 | 1–256 | Cell count 10m² + 2 (64 → 40,962 cells; 256 → 655,362). |
| `PlanetRadiusKm` | km | 6371 | 1–1,000,000 | Cell areas; with a fixed land share it scales flow and evaporation together. |
| `RegionMultiplier` | factor | 3 | 1–16 | Region level frequency = `Frequency` × this (192 at the default: 368,642 region cells, about 37 km). Tile detail world. |
| `TileMultiplier` | factor | 8 | 1–32 | Tile level frequency = `Frequency` × `RegionMultiplier` × this (1,536 at the default: 23,592,962 tiles of 21.6 km², about 5 km flat-to-flat on Earth radius). Tile detail world. |

Cross-field: `Frequency` × `RegionMultiplier` × `TileMultiplier` ≤ 14,654 (error code `settings.Grid.TileMultiplier`), so that tile ids 10m² + 2 fit an `int` (10 × 14,654² + 2 = 2,147,397,162 < 2³¹; m = 14,655 gives 2,147,690,252). The default product is 1,536.

## Plates

Stage 1 (plates).

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `Count` | plates | 20 | 1–cell count | Number of plates grown by random flood fill. |
| `ContinentalFractionBp` | bp | 4000 | 0–10,000 | Plates are marked continental, in id order, until this share of cells is reached. |
| `MinAngularSpeedPermille` | ‰ of model speed | 200 | 0–10,000 | Lower end of each plate's uniform angular-speed draw. |
| `MaxAngularSpeedPermille` | ‰ of model speed | 1000 | 0–10,000 | Upper end of the draw. Only the ratio between plates matters: boundary pressure is divided by the largest pressure. |

Cross-field: `MinAngularSpeedPermille` ≤ `MaxAngularSpeedPermille`.

## Elevation

Stage 2 (elevation). A boundary cell takes a value from its boundary kind and the two plates' crust types. Convergent values interpolate from the `…WeakM` value at zero pressure to the `…StrongM` value at the largest pressure on the globe. Interior cells interpolate from their nearest boundary value to their plate's base over `FalloffCells` steps. Noise is added, then the value is clamped to [`MinElevationM`, `MaxElevationM`]. Last, continental shelves: Stage 2 computes the sea level Stage 3 will pick (`SeaLevel.LandFractionBp`), then raises every ocean cell within `ShelfWidthCells` ocean steps of land on a continental plate to at least sea level + `ShelfDepthM`. Convergent boundary cells (trenches, island arcs) are neither raised nor crossed. Raised cells stay below the sea level, so Stage 3 picks the same sea level and the land share is unchanged.

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `FalloffCells` | cells | 6 | 1–1,000,000 | Width of the band over which boundary relief fades into the plate base. |
| `NoiseAmplitudeM` | m | 400 | 0–10,000 | Noise in [−1, 1] is multiplied by this. |
| `NoiseOctaves` | octaves | 4 | 0–16 | Fractal noise layers; 0 disables noise. |
| `NoiseFrequency` | lattice cells per unit radius | 4 | 1–1,000 | Feature size of the first octave. |
| `NoisePersistenceBp` | bp | 5000 | 0–10,000 | Amplitude multiplier per octave. |
| `NoiseLacunarityBp` | bp | 20000 | 10,000–40,000 | Frequency multiplier per octave. |
| `ContinentalBaseM` | m | 300 | ±20,000 | Continental plate interior and transform-boundary elevation. |
| `OceanicBaseM` | m | −4000 | ±20,000 | Oceanic plate interior and transform-boundary elevation. |
| `CollisionWeakM` / `CollisionStrongM` | m | 4000 / 7000 | ±20,000 | Convergent, both plates continental. |
| `CoastalRangeWeakM` / `CoastalRangeStrongM` | m | 2000 / 4000 | ±20,000 | Convergent, this plate continental, partner oceanic. |
| `TrenchWeakM` / `TrenchStrongM` | m | −6000 / −9000 | ±20,000 | Convergent, this plate oceanic, partner continental. |
| `IslandArcWeakM` / `IslandArcStrongM` | m | 500 / 2000 | ±20,000 | Convergent, both plates oceanic. |
| `DivergentContinentalM` | m | −500 | ±20,000 | Divergent boundary on a continental plate (rift). |
| `DivergentOceanicM` | m | −2000 | ±20,000 | Divergent boundary on an oceanic plate (ridge). |
| `MinElevationM` | m | −11,000 | ±20,000 | Lowest stored elevation. |
| `MaxElevationM` | m | 9,000 | ±20,000 | Highest stored elevation. |
| `ShelfDepthM` | m relative to sea level | −200 | −20,000 to −1 | Shelf floor: ocean cells near continental coasts are raised to at least this far below sea level. New in phase 2. |
| `ShelfWidthCells` | ocean steps | 2 | 0–1,000 | Shelf width from a continental coast; 0 disables shelves. New in phase 2. |

Cross-field: `MinElevationM` ≤ `MaxElevationM`. Weak and strong values may be in either order.

## SeaLevel

Stage 3 (sea level).

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `LandFractionBp` | bp | 3000 | 0–10,000 | Area-weighted land share; the sea level is the matching elevation percentile. |

## Currents

Stage 4 (ocean currents). Read only by this stage; the anomaly feeds Stage 6 temperature.

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `TropicLatDeg` | ° latitude | 30 | 0–90 | Westward tropical flow turns eastward here. |
| `PolarLatDeg` | ° latitude | 60 | 0–90 | Flow turns westward again above this; coastal margin rules apply only up to here. |
| `AnomalyBaseDeciC` | 0.1 °C | 20 | 0–1,000 | Margin temperature anomaly outside the peak band (warm on western margins, cold on eastern). |
| `AnomalyPeakDeciC` | 0.1 °C | 60 | 0–1,000 | Anomaly at `AnomalyPeakLatDeg`; linear between base and peak. |
| `AnomalyPeakLatDeg` | ° latitude | 30 | 0–90 | Latitude of the strongest anomaly. |
| `AnomalyHalfWidthDeg` | ° latitude | 10 | 1–90 | Half-width of the band where the anomaly exceeds the base. |

Cross-field: `TropicLatDeg` ≤ `PolarLatDeg`.

## Winds

Stage 5 (prevailing winds). Wind direction drives Stage 6 moisture transport; strength is stored but not read by later stages. A cell's band is chosen from its latitude shifted by `BandNoiseAmplitudeDeg` × fractal noise in [−1, 1] (persistence 0.5, lacunarity 2), seeded from the Stage 5 seed; Stage 6 uses the same seed for its isotherm noise, so equal noise settings move band edges and isotherms together.

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `BandWidthDeg` | ° latitude | 30 | 1–90 | Width of the trade-wind, westerly and polar-easterly bands; the third band reaches the pole. |
| `PeakStrength` | strength (byte) | 160 | 0–255 | Strength at a band centre. |
| `EdgeStrength` | strength (byte) | 60 | 0–255 | Strength at a band edge. |
| `MeridionalShareBp` | bp | 5000 | 0–10,000 | Weight of the poleward (westerlies) or equatorward (trade winds) component against the east–west unit vector; sets the wind slot chosen. |
| `PolarMeridionalShareBp` | bp | 0 | 0–10,000 | Same weight for the polar easterlies. At 5,000 (the version 2 behaviour) polar air flows out of the pole and a polar continent's interior gets no moisture (50.7 % of land above 65° at 0 mm on seed 42); at 0 polar air circles the pole. New in phase 2. |
| `MountainThresholdM` | m above sea level | 2000 | 0–20,000 | Land above this weakens the wind. |
| `MountainLossDivisorM` | m per strength unit | 50 | 1–10,000 | Strength lost = height above sea level / this. |
| `MountainFloorStrength` | strength (byte) | 20 | 0–255 | Lowest strength after the mountain loss. |
| `BandNoiseAmplitudeDeg` | ° latitude | 15 | 0–45 | Band-edge shift amplitude; 0 gives straight bands along parallels. New in phase 2. |
| `BandNoiseFrequency` | lattice cells per unit radius | 3 | 1–1,000 | Feature size of the band-edge noise. New in phase 2. |
| `BandNoiseOctaves` | octaves | 3 | 0–16 | Fractal layers of the band-edge noise; 0 disables it. New in phase 2. |

## Climate

Stage 6 (temperature and precipitation). Temperature = `PoleDeciC` + (`EquatorDeciC` − `PoleDeciC`) × cos(latitude'), minus the lapse rate on land, plus the current anomaly; latitude' is the latitude shifted by `LatitudeNoiseAmplitudeDeg` × noise. Saturation = max(reference × 2^((T − `SaturationReferenceDeciC`) / `SaturationDoublingDegC`), `SaturationFloorBp` × reference). Per step a share of carried moisture (base + orographic, capped at `MoistureMaxLossBp`) rains out; a land cell above its saturation rains out `SaturationRainOutBp` of the excess per pass and carries the rest downwind. Precipitation is rounded to the nearest mm (version 2 truncated, so cells with less than 1 mm were 0).

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `EquatorDeciC` | 0.1 °C | 300 | ±1,000 | Sea-level temperature at the equator before anomalies. |
| `PoleDeciC` | 0.1 °C | −250 | ±1,000 | Sea-level temperature at the poles before anomalies. |
| `LapseDeciCPerKm` | 0.1 °C per km | 65 | 0–1,000 | Cooling with height above sea level on land. |
| `CoastalAnomalyShareBp` | bp | 5000 | 0–10,000 | Share of the strongest neighbouring ocean anomaly added to coastal land. |
| `MoistureIterations` | passes | 64 | 1–10,000 | Warm-up passes; the same number is then counted as rain. Run time is linear in this. |
| `MoistureBaseLossBp` | bp per step | 500 | 0–10,000 | Share of carried moisture that rains out on every step. |
| `MoistureOrographicLossBpPerKm` | bp per km of climb | 1500 | 0–10,000,000 | Extra rain-out on uphill steps (rain shadow strength). |
| `MoistureMaxLossBp` | bp per step | 1500 | 0–10,000 | Cap on base + orographic loss per step; a steep climb's rain continues over the next cells. Version 2 behaviour: 10,000. New in phase 2. |
| `MoistureFullScaleBp` | bp of reference saturation | 2500 | 1–10,000 | Mean per-pass rain that maps to `PrecipitationCeilingMm`; lower values make every cell wetter. |
| `MoistureSpreadBp` | bp | 3000 | 0–10,000 | Share sent to the two slots beside the wind slot. |
| `SaturationReferenceDeciC` | 0.1 °C | 150 | ±1,000 | Temperature at which saturation equals the reference amount. |
| `SaturationDoublingDegC` | °C | 10 | 1–100 | Warming per doubling of saturation. |
| `SaturationMaxOctaves` | powers of two | 8 | 0–10 | Clamp on the whole power of two (at defaults it affects only cells below −65 °C or at or above 105 °C). |
| `PrecipitationCeilingMm` | mm/year | 4000 | 1–20,000 | Linear full-scale value and the soft-knee asymptote. |
| `PrecipitationSoftKneeMm` | mm/year | 1500 | 0–20,000 | Linear up to this; compressed toward the ceiling above. Version 2: 2000. |
| `SaturationFloorBp` | bp of reference saturation | 3500 | 0–10,000 | Lowest moisture capacity of any cell, so cold air carries moisture inland. Version 2 behaviour: 0. New in phase 2. |
| `SaturationRainOutBp` | bp of the excess per pass | 2000 | 1–10,000 | Share of moisture above saturation that rains out per pass on a land cell; the rest moves on. Version 2 behaviour: 10,000 (whole excess in one cell). New in phase 2. |
| `LatitudeNoiseAmplitudeDeg` | ° latitude | 15 | 0–45 | Isotherm shift amplitude; 0 gives isotherms along parallels (before lapse and anomalies). New in phase 2. |
| `LatitudeNoiseFrequency` | lattice cells per unit radius | 3 | 1–1,000 | Feature size of the isotherm noise. New in phase 2. |
| `LatitudeNoiseOctaves` | octaves | 3 | 0–16 | Fractal layers of the isotherm noise; 0 disables it. New in phase 2. |

Cross-field: `PrecipitationSoftKneeMm` ≤ `PrecipitationCeilingMm`.

## Biomes

Biome thresholds of a tile (TileLandform, `BiomeStage.MainType`). h = tile elevation − tile sea level; slope S = largest surface difference to a neighbouring tile / centre distance, in cm per km (used only by the Swamp test). Rule order: Mountain (h above `MountainHeightM`), Hills (h above `HillsHeightM`, or forested hills), Ice, Tundra, Swamp, Taiga, then temperate (T ≤ `TemperateMaxDeciC`) or tropical precipitation ladders. Tiles with the Mountain or Hills landform (relief rules in `Tiles`) take those main types first. Version 4 removed the globe composition settings (`MountainSlopeCmPerKm`, `HillsSlopeCmPerKm`, `LowlandGapM`, `RoughGapM`, `LowlandSlopeShareBp`, `HighlandMinShareBp`, `RoughHillsShareBp`): composition is now the landform tile counts, so a `generation` block naming them is an unknown-setting error.

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `MountainHeightM` | m | 2500 | 0–20,000 | Mountain (landform and biome) when h is above this. The only Mountain height threshold (version 4 removed `Tiles.MountainHeightM`). |
| `HillsHeightM` | m | 1500 | 0–20,000 | Biome Hills (or forested hills) when h is above this, whatever the landform (high plateaus). Version 2: 800. |
| `IceBelowDeciC` | 0.1 °C | −100 | ±1,000 | Ice below this temperature. |
| `TundraBelowDeciC` | 0.1 °C | 0 | ±1,000 | Tundra below this; also the lowest temperature for Swamp. |
| `TaigaBelowDeciC` | 0.1 °C | 50 | ±1,000 | Taiga below this (after the Swamp test). |
| `TemperateMaxDeciC` | 0.1 °C | 210 | ±1,000 | Temperate ladder up to and including this; tropical ladder above. Version 2: 200. |
| `DesertBelowMm` | mm/year | 250 | 0–20,000 | Desert below this (both ladders). |
| `ShrublandBelowMm` | mm/year | 500 | 0–20,000 | Temperate shrubland below this. |
| `GrasslandBelowMm` | mm/year | 700 | 0–20,000 | Temperate grassland below this; temperate forest at or above. Version 2: 1000. |
| `ForestedHillsMinMm` | mm/year | 650 | 0–20,000 | A Hills tile with a temperate temperature (`TaigaBelowDeciC` to `TemperateMaxDeciC`) and at least this precipitation has biome TemperateForest; its landform stays Hills. A value above the precipitation ceiling turns the rule off. |
| `SavannaBelowMm` | mm/year | 1200 | 0–20,000 | Tropical savanna below this; tropical forest at or above. |
| `SwampMinMm` | mm/year | 2000 | 0–20,000 | Swamp at or above this (with low slope). |
| `SwampMaxSlopeCmPerKm` | cm/km | 84 | 0–100,000,000 | Swamp needs S below this (4.2 m over a 5 km tile spacing). |

Cross-field: `HillsHeightM` < `MountainHeightM`; `IceBelowDeciC` < `TundraBelowDeciC` < `TaigaBelowDeciC` < `TemperateMaxDeciC`; `DesertBelowMm` < `ShrublandBelowMm` < `GrasslandBelowMm`; `DesertBelowMm` < `SavannaBelowMm`.

## Hydrology

Lake evaporation for TileHydrology and the summary river-system rule. Version 4 removed the globe-stage settings `RiverFlowPercentileBp`, `FloorShareBp`, `FloorMinShareBp`, `FloorFullShareDepthM` and `MinBasinDepthM` (the tile counterpart is `Tiles.MinBasinDepthM`).

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `EvaporationMmPerDegree` | mm per °C | 100 | 0–1,000 | Tile lake evaporation = (`EvaporationBaseMm` + this × max(0, tile °C)) × tile area, summed over the flooded tiles. Version 2: 60. |
| `EvaporationBaseMm` | mm/year | 300 | 0–20,000 | Evaporation at every temperature (cold surfaces evaporate and sublimate too). |
| `RiverMinSystemEdges` | edges | 3 | 1–1,000,000 | A summary river system (globe river edges connected through shared cells) with fewer edges is dropped unless it is in the top flow tier; removes one- and two-step coastal stubs (by definition: the coastal-stub calibration count is 0 because of this rule). The renderer applies the same rule to the region-cell river segments it draws at Globe and Region View (segments connected through shared region cells, the top tier taken over the segment flows), so the drawn map has no such stubs either (seed 42, Frequency 16 × 3 × 4: 3,209 segments, 283 short systems dropped, 2,819 drawn). 1 keeps every system. A globe cell left without a kept edge loses water kind River. |
| `RiverTopTierPercentileBp` | bp | 9000 | 0–10,000 | A short system is kept when one of its edges carries a flow at or above this percentile of all summary river edge flows. |

A tile basin fills to the level where its inflow meets the evaporation of its flooded tiles (or to its spill level, then overflows through its outlet); nested depressions fill inner to outer, a full inner lake overflowing into the next one. See `Tiles.MinBasinDepthM` and `Tiles.BreachDepthM` for the drain-through, fill and breach rules.

## Areas

Stage 9 (natural areas).

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `MemberThresholdBp` | bp | 2000 | 1–10,000 | Smallest landform share that can make a cell a member of a landform area. |
| `DominantShareBp` | bp | 3333 | 1–10,000 | A non-Plain landform with at least this share is dominant even when another landform is larger (a mountain rim cell joins both its range and its lake or dry bed). |
| `MinAreaCells` | cells | 3 | 1–1,000,000 | Smallest component that becomes an area, for kinds other than Ocean, Lake and DryLakeBed. |
| `MinWaterAreaCells` | cells | 1 | 1–1,000,000 | Smallest Ocean, Lake or DryLakeBed component that becomes an area. |
| `SeaMaxCells` | cells | 500 | 0–1,000,000 | An ocean component with fewer cells is a Sea. |

## Pinning

Stage 10 (settlement pinning; lookup only, never changes terrain). Not part of the golden stage hashes.

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `MinGroupSeparationRings` | rings | 6 | 1–10,000 | Requested separation between auto-placed group anchors; relaxed one ring at a time, with a warning, when no cell qualifies. |
| `MemberRadiusRings` | rings | 3 | 0–10,000 | Auto-placed group members search this many rings around the anchor before widening. |
| `IdealMinDeciC` / `IdealMaxDeciC` | 0.1 °C | 50 / 250 | ±1,000 | Temperature band that earns `TemperatureScore`. |
| `IdealMinMm` / `IdealMaxMm` | mm/year | 400 / 1500 | 0–20,000 | Precipitation band that earns `PrecipitationScore`. |
| `TemperatureScore` | points | 4 | 0–1,000 | Suitability points for the temperature band. |
| `PrecipitationScore` | points | 4 | 0–1,000 | Suitability points for the precipitation band. |
| `RiverScore` | points | 2 | 0–1,000 | Suitability points for a river cell. |
| `CoastScore` | points | 2 | 0–1,000 | Suitability points for a cell with an ocean neighbour. |
| `TileRiverScore` | points | 3 | 0–1,000 | Tile pinning (inside the pinned cell): points for a river tile or a tile with a river neighbour. |
| `TileShoreScore` | points | 2 | 0–1,000 | Tile pinning: points for a tile with an ocean or lake neighbour. |
| `TileFlatScore` | points | 2 | 0–1,000 | Tile pinning: points for a flat tile (`TileFlatMaxDiffM`). |
| `TileFlatMaxDiffM` | m | 50 | 0–20,000 | Tile pinning: flat when the largest elevation difference to a land neighbour is at most this (1 % slope over about 5 km). |
| `TileMinSpacingKm` | km | 15 | 0–100,000 | Tile pinning: settlements pinned to one globe cell are at least this far apart (tile centre chord distance) when the cell has such a tile, else the farthest candidate is taken. 3 tile spacings at the default grid. |

Tile pinning candidates are the cell's tiles that are not ocean, lake or dry lake bed (dry lake bed tiles only when the cell has no other land tile; none at all is a `pin.notile` error). A request with latitude and longitude takes the nearest tile when it is a candidate in its cell. Ties: a seeded hash of the tile id, then the lower id.

Cross-field: `IdealMinDeciC` ≤ `IdealMaxDeciC`; `IdealMinMm` ≤ `IdealMaxMm`.

## Tiles

Tile stages (TileElevation, TileSeaLevel, TileClimate, TileHydrology, TileLandform, summaries; spec `docs/superpowers/specs/2026-11-22-tile-detail-world-design.md` sections 6 and 8). Tiles are about 5 km flat-to-flat at the default grid. Every default comes from the physical reasoning in the table; plan task 5 calibrated `MinBasinDepthM`, `BreachDepthM`, `OrographicGainBp` and `MountainReliefM` against the phase 2 targets on tile-derived data (task 5 report). TileSeaLevel areas are integers in units of the mean tile area / 2²⁰ with `Int128` comparisons, exact over the whole `Grid.PlanetRadiusKm` range. Overflow bounds: elevation amplitudes are at most 5,000 m and the relief scale at most 30,000 bp (3.0 times), so the sum of detail and ridge amplitude is at most 20,000 m in `int` arithmetic (the stage clamps stored tile elevation to `short`); precipitation (at most 20,000 mm) × `OrographicMaxBp` (at most 50,000) is at most 10⁹ < 2³¹; flow thresholds (at most 2 × 10⁹) are compared against `long` flow sums.

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `DetailOctaves` | count | 4 | 0–8 | TileElevation: detail noise octaves added to the globe elevation interpolated over the corner triangle; 0 disables detail. From `DetailFrequency` 128 the octaves span about 50, 25, 12 and 6 km, the last near the tile size. |
| `DetailFrequency` | lattice units | 128 | 1–100,000 | TileElevation: first-octave lattice frequency on the unit sphere; features are about radius ÷ frequency (128: about 50 km, 10 tiles, at radius 6,371 km). |
| `DetailAmplitudeM` | m | 300 | 0–5,000 | TileElevation: detail noise amplitude (noise in [−1, 1] times this) at a relief scale of 10,000 bp; about the hill relief inside a 50 km area. |
| `ReliefScaleMinBp` | bp | 500 | 0–10,000 | TileElevation: lower clamp of the relief scale applied to `DetailAmplitudeM`. The scale of a globe cell is its relief (largest minus smallest elevation among the cell and its neighbours) × 10 bp per metre (1,000 m = 10,000 bp). 500 bp leaves 15 m of detail on plains. |
| `ReliefScaleMaxBp` | bp | 20,000 | 100–30,000 | TileElevation: upper clamp of the relief scale; 20,000 bp (600 m detail) is reached at 2,000 m relief. |
| `RidgeAmplitudeM` | m | 400 | 0–5,000 | TileElevation: additional ridge noise amplitude near convergent plate boundaries; about the ridge-line height over a mountain belt flank. |
| `OrographicGainBp` | bp per (m/km) | 60 | 0–10,000 | TileClimate: precipitation gain per metre of upwind rise per km, as bp of base precipitation (20 m/km windward slope: +12 %; 50 m/km: +30 %). Task 5 calibration: 150 gave 2.09 % of land at ≥ 3,500 mm on seed 42 and 100 gave 2.49 % on seed 1 (target ≤ 2 %); 60 gives 0.90–1.62 % on seeds 42, 1, 2, 3, 7. |
| `OrographicMinBp` | bp | 3,000 | 0–10,000 | TileClimate: lower clamp of tile precipitation as a share of the interpolated globe value (30 % in a rain shadow). |
| `OrographicMaxBp` | bp | 25,000 | 0–50,000 | TileClimate: upper clamp (2.5 times on windward slopes). |
| `MinOceanAreaKm2` | km² | 100,000 | 0–1,000,000,000 | TileSeaLevel: a connected below-sea region is ocean when its area is at least this; smaller enclosed below-sea regions stay land and become lake or dry-lake-bed candidates for TileHydrology. 100,000 km² is about 4,600 tiles at the default grid, 1.3 times Lake Superior; the Caspian Sea (371,000 km²) would count as ocean. 0: every below-sea region is ocean. The sea level is the integer level whose area-weighted land fraction is closest to `SeaLevel.LandFractionBp` under this rule (see TileSeaLevel in the stage comments). |
| `MinBasinDepthM` | m | 10 | 0–40,000 | TileHydrology: a closed depression shallower than this drains through; a pit shallower than this below its saddle is absorbed; a lake less than this above its floor is a dry lake bed. Replaces the version 3 `Hydrology.MinBasinDepthM`. The tiles of a depression that drains through, and the dry tiles of an absorbed pit, are raised to its spill or saddle level (sediment fill), so drainage never runs uphill. On plains the detail noise is 15 m (`ReliefScaleMinBp` × `DetailAmplitudeM`); pits a few metres deep in a 5 km tile fill with sediment or drain through soil. Task 5: 2 → 10 m lowered the lake share 2.20 → 1.97 % of land at seed 42 (with `BreachDepthM` 300, before carving). 0 and 1 keep every depression. |
| `BreachDepthM` | m | 600 | 0–20,000 | TileHydrology outlet breaching: a basin whose inflow fills it to the spill level drains when its depth is at most `BreachDepthM` × (outflow / `MajorRiverFlow`)^(`BreachExponentPermille` / 1,000), outflow = inflow − evaporation of the filled basin. River incision grows with discharge (stream-power law, erosion ∝ Q^m, m about 0.5), so large rivers keep outlets open and small ones leave lakes. A breached basin is carved and filled (stored elevations follow the drainage): T = max(lowest elevation, tile sea level); the path from the lowest tile through the outlet is set to T, and downstream of the outlet it is lowered to T until the first tile at or below T, a water tile (ocean, lake or dry lake bed), or a tile bordering a lake or dry lake bed whose level is above T; the rest of the basin is raised to a surface that drains into that channel; tiles below T connected to the lowest tile (floor below sea level) stay a full lake at level T (partial breach; carving never goes below sea level). A final pass raises any land tile below its non-water downstream tile. Only whole basins whose top lake is full are breached. Known limits: the reference flow is the drawing threshold `MajorRiverFlow`, so changing it also changes breaching; when the downstream channel of a partial breach stops at its first tile (a tile bordering a higher lake), the final pass raises the lake's exit path above level T while the lake keeps level T (earth-5km seed 42: 5 of 1,641 lakes with an outlet, largest gap 117 m; small-5km: 4 of 3,684, 77 m; the default-preset slow test bounds it at 5 lakes and 117 m). Task 5: 0 → 300 lowered the lake share 8.47 → 2.20 % of land at seed 42 (drain-only breach); with carving and partial breach (fix round) 300 gave 2.50 % (seed 42) and 3.02 % (seed 7), 600 gives 2.03 % and 2.40 %. 0 disables. |
| `BreachExponentPermille` | thousandths | 500 | 0–3,000 | Exponent of the outflow ratio in the breach depth (500: square root). |
| `RiverMinFlow` | mm·km² per year | 600,000 | 1–2,000,000,000 | TileHydrology: a tile with flow at or above this is a river. Same unit as the globe `FlowAccum` (precipitation × upstream area). 600,000 is a 1,000 km² catchment (about 46 tiles) at 600 mm per year. |
| `MajorRiverFlow` | mm·km² per year | 50,000,000 | 1–2,000,000,000 | Summaries and large features: flow at or above which a river is major (globe water kind River, globe and region river edges, Globe and Region View; also the reference flow of `BreachDepthM`). Summary river systems shorter than `Hydrology.RiverMinSystemEdges` outside the `Hydrology.RiverTopTierPercentileBp` tier are dropped, as at globe level. A 71,000 km² catchment at 700 mm per year, about 6 globe cells at Frequency 64, the scale of the globe level's 95th percentile river. |
| `LargeLakeMinKm2` | km² | 500 | 1–1,000,000,000 | Large features: a lake of at least this area is drawn at Globe View (about 23 tiles) from its region cells: every region cell of which at least 1/3 of the tiles belong to such lakes, plus, for each such lake, the region cell holding the most of its tiles (ties: lowest id), so every large lake has at least one drawn cell (a lake spread thinly over several region cells is drawn as one region cell, 1,383 km² at the default grid). |
| `ReliefRadius` | tiles | 3 | 1–16 | TileLandform: radius of the neighbourhood over which local relief and the basin test are measured (3 tiles: about 15 km, 37 tiles). |
| `MountainReliefM` | m | 900 | 0–20,000 | TileLandform: Mountain when the elevation range over the `ReliefRadius` neighbourhood is at least this (900 m over about 30 km), or when the tile is higher than `Biomes.MountainHeightM` above sea level. Task 5: 700 gave Mountain 16.0 % of land at seed 42 (target 8–15 %); 900 gives 10.2–14.1 % on the five seeds. |
| `HillsReliefM` | m | 250 | 0–20,000 | TileLandform: Hills when the neighbourhood relief is at least this. |
| `ValleyDepthM` | m | 100 | 0–20,000 | TileLandform: a river tile whose highest tile within `ReliefRadius` steps is at least this far above it is Valley (100 m over about 15 km). |
| `BasinDepthM` | m | 100 | 0–20,000 | TileLandform: a land tile at least this far below the mean of its neighbourhood is Basin. |
| `WetlandMaxSlopeM` | m | 15 | 0–20,000 | TileLandform: Wetland needs the largest elevation difference to a neighbouring tile at most this (15 m over 5 km is a 0.3 % slope). |
| `WetlandMinPrecipMm` | mm/year | 600 | 0–20,000 | TileLandform: Wetland needs tile precipitation at least this. |

Cross-field: `ReliefScaleMinBp` ≤ `ReliefScaleMaxBp`; `OrographicMinBp` ≤ `OrographicMaxBp`; `RiverMinFlow` ≤ `MajorRiverFlow`; `HillsReliefM` < `MountainReliefM`.

## Roads

Road generation (spec `docs/superpowers/specs/2026-11-23-road-graph-design.md`): runs after pinning, outside the staged pipeline, so no stage hash depends on these values. Seeds set them in `generation.Roads`; `new --generation <file>` reads the same block. Land routes are searched with A* over land tiles and Air routes follow the great circle; Sea travel is generated as a voyage between every pair of ports that share water (ocean, lake or river), with no seed entry. All costs are integers; percentages are of 100.

| Setting | Unit | Default | Range | What it changes |
|---|---|---|---|---|
| `BaseStepCost` | cost | 100 | 1–10,000 | Land: cost of one step before the landform factor; also the divisor of route difficulty (mean step cost × 100 / this). |
| `PlainFactorPct` | % | 100 | 1–10,000 | Land: step cost factor onto a plain tile (and any landform not listed below). |
| `HillsFactorPct` | % | 150 | 1–10,000 | Land: step cost factor onto hills. |
| `MountainFactorPct` | % | 400 | 1–10,000 | Land: step cost factor onto mountains. |
| `ValleyFactorPct` | % | 110 | 1–10,000 | Land: step cost factor onto a valley. |
| `BasinFactorPct` | % | 110 | 1–10,000 | Land: step cost factor onto a basin. |
| `WetlandFactorPct` | % | 250 | 1–10,000 | Land: step cost factor onto wetland. |
| `DryLakeBedFactorPct` | % | 130 | 1–10,000 | Land: step cost factor onto a dry lake bed. |
| `SlopeCostPerM` | cost per m | 2 | 0–1,000 | Land: added step cost per metre of elevation change. |
| `RiverCostPerLog2Flow` | cost | 10 | 0–1,000 | Land: added cost of stepping onto a river tile, per log2 of its flow (a ford or bridge). |
| `RoadDiscountPct` | % | 60 | 1–100 | Land: search cost of a step onto a tile an earlier route of the same category uses, so later roads join earlier ones. Not applied to difficulty. |
| `UphillPenalty` | factor per m/km | 1 | 0–100 | Land: speed factor lost per metre climbed per km of route. |
| `DownhillBonus` | factor per m/km | 1 | 0–100 | Land: speed factor gained per metre descended per km of route. |
| `MinDirectionFactor` | % | 25 | 1–100 | Land: lower clamp of a direction's speed factor. |
| `MaxDirectionFactor` | % | 150 | 100–1,000 | Land: upper clamp of a direction's speed factor. |
| `SteepSlopeMPerKm` | m per km | 150 | 1–10,000 | Land: a step steeper than this limits the route to tier 1 vehicles (HandCart). |
| `DefaultMaxTier` | tier | 99 | 1–99 | All: highest vehicle tier a route allows when nothing limits it. |
| `LakeMaxTier` | tier | 1 | 1–99 | Voyages: highest tier of a voyage that crosses lake water (Longboat, not MerchantVessel). |
| `RiverDownstreamPct` | % | 130 | 1–1,000 | Voyages: speed factor of a river step with the flow. A voyage's factor per direction is its length ÷ Σ(step metres ÷ step factor). |
| `RiverUpstreamPct` | % | 60 | 1–1,000 | Voyages: speed factor of a river step against the flow. |
| `RiverMaxTier` | tier | 1 | 1–99 | Voyages: highest tier of a voyage that uses a river. |
| `MaxVoyagePorts` | count | 2,000 | 0–100,000 | More ports than this is a seed error (voyages grow with the square of the port count). |
| `MaxVoyageKm` | km | 8,000 | 1–40,000 | Longest voyage generated (water path); ports farther apart get none, and a search stops once its path would pass this. At 8,000 km a merchant vessel needs about 67 days, beyond the default 60-day merchant reach. |

## Presets

`WorldGenPresets.Names` / `WorldGenPresets.Get(name)` (names match case-insensitively; an unknown name is an `ArgumentException` listing the valid names). A preset is a full `WorldGenSettings`; every setting not listed equals the default.

| Preset | PlanetRadiusKm | Frequency | RegionMultiplier | TileMultiplier | Tile size |
|---|---|---|---|---|---|
| `earth-5km` (default) | 6371 | 64 | 3 | 8 | about 5 km (23,592,962 tiles) |
| `earth-10km` | 6371 | 64 | 3 | 4 | about 10 km (5,898,242 tiles) |
| `small-5km` | 3000 | 32 | 3 | 8 | about 5 km (5,898,242 tiles) |

## Phase 2 changes

Targets, measurements and the iteration log are in `.superpowers/sdd/2026-11-21-globe-world-generation/calib-phase2-report.md`. Historical: settings marked in this table that the Biomes, Hydrology and Tiles sections list as removed in version 4 no longer exist.

| Setting | Version 2 | Version 3 | Reason (target item) |
|---|---|---|---|
| `Elevation.ShelfDepthM`, `ShelfWidthCells` | no shelves | −200 m, 2 steps | Continental shelves (item 2 mechanism; no measurable effect on Mountain/Hills, which measure relief to sea level). |
| `Winds.PolarMeridionalShareBp` | (5,000 via `MeridionalShareBp`) | 0 | Polar rain (item 6). |
| `Winds.BandNoise…`, `Climate.LatitudeNoise…` | none | 15°, frequency 3, 3 octaves | Latitude stripes (item 5). |
| `Climate.SaturationFloorBp` | 0 | 3500 | Polar rain (item 6). |
| `Climate.SaturationRainOutBp` | 10,000 | 2000 | Extreme rain tail (item 7). |
| `Climate.MoistureMaxLossBp` | 10,000 | 1500 | Extreme rain tail (item 7). |
| `Climate.PrecipitationSoftKneeMm` | 2000 | 1500 | Extreme rain tail (item 7). |
| `Biomes.MountainReliefM` → `MountainSlopeCmPerKm` | 1,500 m | 3,000 cm/km | Mountain share, grid-independent thresholds (item 2). |
| `Biomes.HillsReliefM` → `HillsSlopeCmPerKm` | 600 m | 1,000 cm/km | Mountain + Hills share (item 2). |
| `Biomes.SwampMaxReliefM` → `SwampMaxSlopeCmPerKm` | 100 m | 84 cm/km | Same terrain at m = 64 (unit change only). |
| `Biomes.HillsHeightM` | 800 | 1500 | Mountain + Hills share (item 2). |
| `Biomes.TemperateMaxDeciC` | 200 | 210 | Temperate forest share (item 3). |
| `Biomes.GrasslandBelowMm` | 1000 | 700 | Temperate forest share (item 3). |
| `Biomes.ForestedHillsMinMm` | none | 650 | Temperate forest share (item 3). |
| `Hydrology.EvaporationMmPerDegree` | 60 | 100 | Lake share (item 1). |
| `Hydrology.EvaporationBaseMm` | none | 300 | Lake share (item 1). |
| `Hydrology.MinBasinDepthM` | none | 2 | Lake share (item 1). |
| `Hydrology.FloorMinShareBp`, `FloorFullShareDepthM` | flat 7,000 | 5,000 → 7,000 over 50 m | Depth-scaled floor composition (item 1 mechanism). |
| `Hydrology.RiverFlowPercentileBp` | 9700 | 9500 | Long river systems (item 4). |
| `Hydrology.RiverMinSystemEdges`, `RiverTopTierPercentileBp` | none | 3, 9000 | Coastal stubs (item 4). |

Model changes without a setting: precipitation is rounded to the nearest mm instead of truncated; the Hydrology PNG draws a river's mouth edge only to the coast (halfway to the ocean cell centre).

## Numbers that stay constants

These are invariants (scales, storage formats, hash and seed tags, geometry), not tuning values. Changing them would change units, storage or determinism rather than the modelled world.

| Constant | Where | Reason |
|---|---|---|
| 10,000 (`BpFull`) | Climate, Biome, Hydrology, Area stages; sea level | Basis-point full scale. |
| Icosahedron vertices, 10m² + 2 cell count, 12 pentagons, neighbour slot order | `Grid/` | Grid geometry and topology. |
| 0.999 polar fallback | `TangentFrame.East` | Numerical fallback for the east vector near the poles; not a modelled quantity. |
| 3 outgoing edges per cell (wind slot ± 1) | `ClimateStage.EdgesPerCell` | Array layout of the moisture step. |
| 1,000,000 reference saturation units | `ClimateStage.ReferenceSaturationUnits` | Fixed-point unit of moisture; precipitation is divided by the same unit, so it sets rounding resolution only. |
| 8 saturations retained excess cap | `ClimateStage.RetainedExcessCapSaturations` | Overflow guard for `SaturationRainOutBp`: carried moisture ≤ 9 × saturation. At default settings output is identical with 8 and 16 on seeds 42, 1, 2, 3, 7 (m = 64), so it does not bind. |
| 10¹² mm linear clamp | `ClimateStage.LinearClampMm` | Keeps `span × over` in the soft knee inside a long; for any input at or above ceiling² the knee already returns ceiling − 1, so the clamp does not change output. |
| Persistence 0.5, lacunarity 2 | band-edge and isotherm noise (`ClimateStage.PerturbedLatitude`) | Fractal noise shape; the amplitude, frequency and octave count are the settings. |
| 1,000 m per km, 10 tenths per °C | Climate stage | Unit conversions. |
| 0–255 wind strength clamp; `short` temperature clamp | Wind, Climate stages | Storage type ranges. |
| "WORLDGEN" stage seed tag, "PINS" tag, `DeterministicHash` constants | `StageSeed`, `GlobeSeeding` | Seed derivation; changing them changes every random draw. |
| Name syllable tables, hash slot numbers, roman numerals | `AreaNameGenerator`, `AreaStage` | Name vocabulary; names only, no terrain effect. |
| 65,536 tiles per chunk | `TileParallel.ChunkSize` | Parallel tile loops cut [0, count) into fixed chunks; each chunk writes only its own slots and reductions run in chunk order, so the output does not depend on the thread count. Changing the size changes no output, but the thread-count independence relies on chunk boundaries not depending on the machine. |
| Brotli quality 5, window 2²² bytes; format version 1 | `TileBlockCodec`, `RegionSummaryCodec` | Storage format of `tile_blocks.Data` and `region_level.Data` (quality from the task 6a measurement). A different version byte is rejected on load. |
| 30 bytes per tile worst case + 16 | `TileBlockCodec.Decode(data, tileCount)` | Upper bound of a block's uncompressed payload, checked against the row's tile count before the buffer is allocated. |
| Generator version 4 | `WorldGenerator.GeneratorVersion` | Stored in `worldgen_runs`; a world from another version is rejected on load ("regenerate the world", or "open it with a newer build"). |
| 256 globe cells, 2 concurrent loads, retry 1 s doubling to 30 s | `TileBlockCache.DefaultCapacity`, `MapRenderer` `maxConcurrentLoads`, `TileBlockCache.RetryDelay` / `MaxRetryDelay` | Detail View tile block cache (spec §11): capacity in globe cells, loads in flight, and the wait before a failed block is loaded again. |
| 2 stale frames, 1 pinned frame | `TileBlockCache.StaleFrames`, `TileBlockCache.PinnedFrames` | A queued load not requested in the last 2 frames is dropped; an entry used in the current or previous frame is not evicted. |
| 22 px tile, 30 px region cell; flatness 30–90 px | `MapLevelThresholds` | Level choice (Detail when a tile spans ≥ 22 px, else Region when a region cell spans ≥ 30 px) and the smoothstep flatness ramp over the region-cell size (spec §11). |
| Gaps 1 px (Globe View, between cells), 4 px between globe-cell groups (Region and Detail View) | `MapRenderer.GlobeGapPx`, `CellGapPx`, `GroupGapPx` | Border drawing (spec §11); `GroupGapPx` is the total gap, the group step plus the 1 px cell inset. |
| 2,500 bp mountain symbol | `MapRenderer.MountainSymbolMinBp` | Mountain share at which Globe and Region View draw a mountain symbol (spec §8). |
| 45 px region cell, 26 px tile | `MapViewModel.RegionViewCellPx`, `MapViewModel.DetailViewTilePx` | Scale set by "zoom to Region View" (inside the flatness ramp) and by zoom to Detail View (above the 22 px threshold). |
