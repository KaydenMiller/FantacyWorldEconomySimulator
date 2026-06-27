# Transport — Layer A: Physical Foundation (weight, volume, real haulage cost)

**Date:** 2026-06-27
**Status:** Approved design — ready for implementation plan
**Roadmap item:** "Weight/dim-weight + transport modes" (capabilities-and-roadmap.md). This spec is **Layer A** only. Layer B (transport modes, vehicle assets, loss risk tied to `Route.Danger`, location-of-loss events, risk-aware dispatch) is a separate later spec.

## Goal

Make logistics friction physically and financially real: goods gain a mass and a volume, caravan capacity is limited by **both** (dimensional weight — dense loads hit the weight cap, bulky-light loads hit the volume cap), and hauling costs the merchant actual money that leaves circulation (a ledger **sink**). Mass and volume are stored in small integer base units and **presented** in familiar metric/imperial units, exactly as `Money` stores copper but displays gp/sp/cp.

## Non-goals (explicitly Layer B or later)

- Transport **modes** (pack animal / wagon / barge / ship / airship) and their differing speed/capacity/cost.
- **Vehicles as owned assets**, acquisition, and replacement.
- **Loss risk** (wagon breaks down, ship sinks), location-of-loss tracking, loss events, `Route.Danger` consumption.
- **Risk-aware dispatch** ("will one bad trip bankrupt me?"), guards/mercenaries.
- **Speed/terrain** effects on travel time — travel time stays flat (`distance × 12 ticks`) in Layer A.
- **Multi-good caravans** — caravans remain single-good (already a pre-existing deferred item).

## Success criteria

1. A `Good` has `MassPerUnit` and `VolumePerUnit`; defaults derive from `SizeClass`, overridable per good.
2. On `advance`, a dense good's caravan is gated by the weight cap and a bulky-light good's caravan by the volume cap — observable via the demo seed.
3. Hauling deducts real money from the merchant's capital, recorded as a `MerchantHaulage` ledger **sink**; the money ledger still reconciles (no conservation discrepancy).
4. Mass/volume display + input work in both metric and imperial; the `World` has a default `DisplayUnitSystem` and the TUI can toggle it per session. Underlying stored values never change with the toggle.
5. Determinism + granularity invariance preserved; 1-year demo advance stays well under the 5-minute ceiling.
6. Full test suite green (Domain / Simulation / Engine / Tui).

---

## Component 1 — `Mass` and `Volume` value types (SharedKernel)

New files under `src/WorldEcon.SharedKernel/Measure/`, mirroring `Money` (`readonly record struct`, integer base unit, arithmetic operators; sim math only ever touches the base unit).

```csharp
namespace WorldEcon.SharedKernel.Measure;

/// <summary>Mass as an integer count of grams. Units (g/kg/oz/lb) are a presentation concern,
/// never used in sim math.</summary>
public readonly record struct Mass(long Grams)
{
    public static readonly Mass Zero = new(0);
    public static Mass operator +(Mass a, Mass b) => new(a.Grams + b.Grams);
    public static Mass operator -(Mass a, Mass b) => new(a.Grams - b.Grams);
    public static Mass operator *(Mass a, long quantity) => new(a.Grams * quantity);
}

/// <summary>Volume as an integer count of cubic centimetres (1 cm³ = 1 mL). Units (cm³/L/m³/in³/ft³)
/// are a presentation concern, never used in sim math.</summary>
public readonly record struct Volume(long CubicCentimeters)
{
    public static readonly Volume Zero = new(0);
    public static Volume operator +(Volume a, Volume b) => new(a.CubicCentimeters + b.CubicCentimeters);
    public static Volume operator -(Volume a, Volume b) => new(a.CubicCentimeters - b.CubicCentimeters);
    public static Volume operator *(Volume a, long quantity) => new(a.CubicCentimeters * quantity);
}
```

### Unit system + formatting/parsing

```csharp
public enum UnitSystem { Metric = 0, Imperial = 1 }
```

A `MeasurementFormat` static class (presentation layer — may use `double` for conversions, since this is display/input only and never feeds sim math):

- `string FormatMass(Mass m, UnitSystem system)` — Metric: picks the largest sensible unit (`g` / `kg` / `t`), e.g. `1000 g → "1 kg"`, `30000 g → "30 kg"`, `250 g → "250 g"`. Imperial: `lb` / `oz`, e.g. `1000 g → "2.2 lb"`. One decimal place when not whole.
- `string FormatVolume(Volume v, UnitSystem system)` — Metric: `cm³` / `L` / `m³` (`1000 → "1 L"`, `200000 → "0.2 m³"`). Imperial: `in³` / `ft³`.
- `bool TryParseMass(string text, out Mass mass)` — **system-agnostic**: accepts any known suffix regardless of the display toggle (`g`, `kg`, `t`, `oz`, `lb`), case-insensitive, optional space, decimals allowed (`"1.2 kg"`, `"5kg"`, `"8 oz"`). Rounds to nearest gram.
- `bool TryParseVolume(string text, out Volume volume)` — accepts `cm³`/`cm3`/`ml`, `l`/`L`, `m³`/`m3`, `in3`, `ft3`. Rounds to nearest cm³.

Conversion constants (presentation only): `1 lb = 453.592 g`, `1 oz = 28.3495 g`, `1 in³ = 16.387064 cm³`, `1 ft³ = 28316.846592 cm³`. Imperial is approximate display; the canonical stored value is always the metric base unit.

**Rationale for system-agnostic parsing:** input should never fail because the view happens to be toggled the other way; the toggle governs *output* only.

---

## Component 2 — `Good` gains mass + volume (Domain)

`src/WorldEcon.Domain/Economy/Good.cs`:

- Add `Mass MassPerUnit { get; }` and `Volume VolumePerUnit { get; }`.
- `Create(...)` gains two optional params `Mass? massPerUnit = null`, `Volume? volumePerUnit = null`. When null, derive from `SizeClass` via `DefaultMassForSize` / `DefaultVolumeForSize`.
- Validation: both must be ≥ 1 base unit (≥ 1 g, ≥ 1 cm³) — reject otherwise with an `ErrorOr` validation error.
- Setters `SetMassPerUnit(Mass)` / `SetVolumePerUnit(Volume)` (for edit forms), same validation.

**Default tables (rough but sane; per-good override is the realism knob):**

| SizeClass | DefaultMass | DefaultVolume |
|---|---|---|
| Tiny | 50 g | 50 cm³ |
| Small | 1 kg (1000 g) | 1 L (1000 cm³) |
| Medium | 10 kg (10000 g) | 20 L (20000 cm³) |
| Large | 50 kg (50000 g) | 100 L (100000 cm³) |
| Bulky | 200 kg (200000 g) | 500 L (500000 cm³) |

`SizeClass` seeds *both*, but mass and volume are independent fields. The demo seed overrides where reality diverges from the tier (e.g. Iron Ingot: `Small` size but `MassPerUnit` ≈ 5 kg; Cloth: light mass, `Large` volume).

---

## Component 3 — Merchant capacity becomes weight + volume (Domain)

`src/WorldEcon.Domain/Economy/RepresentativeMerchant.cs`:

- **Remove** `long CargoCapacity`.
- **Add** `Mass WeightCapacity { get; }` and `Volume VolumeCapacity { get; }`.
- `Create(...)` takes the two capacities (validation ≥ 1 base unit each).
- Setter(s) for edit forms.

(Layer B will move capacity onto owned vehicles; for Layer A it lives on the merchant, which is the current behaviour just re-typed.)

---

## Component 4 — Real haulage cost (Engine + Domain)

### Money channel

`src/WorldEcon.Domain/Economy/MoneyChannel.cs`: add `MerchantHaulage = 4`, classified as a **Sink** in `MoneyChannels.KindOf`. Doc comment: "What a merchant pays to move a caravan (porter/teamster wages until the labour loop exists); also the future hook for paid guards/mercenaries."

### World tuning knobs

`src/WorldEcon.Domain/Geography/World.cs` (mirroring the price-belief tuning fields):

- `long VolumetricDivisor` — cubic centimetres of cargo that bill as one kilogram of volumetric weight. Default `5000` (the real-world air-freight number). Higher = volume matters less.
- `long TransportRate` — copper per kilogram·distance of dimensional weight. Default `1` (a placeholder; the seed-balancing step tunes it so merchants still profit).
- `SetTransportTuning(long volumetricDivisor, long transportRate)` with validation (both ≥ 1).
- Migration backfills the defaults.

### Formulas (all integer; deterministic)

Per unit of a single good (the binding dimension is the same for every unit of one good, so per-unit costs are linear in quantity):

```
unitBillableFromVolume(g) = VolumePerUnit.CubicCentimeters × 1000 / VolumetricDivisor
unitDimWeight(g)          = max( MassPerUnit.Grams, unitBillableFromVolume )
haulagePerUnit(copper)    = unitDimWeight × route.Distance × TransportRate / 1000
totalHaulage(copper)      = haulagePerUnit × quantity
```

### TradePhase changes (`src/WorldEcon.Engine/Phases/TradePhase.cs`)

1. **Capacity gating** — replace the unit-count cap:
   ```
   maxByWeight = WeightCapacity.Grams / good.MassPerUnit.Grams
   maxByVolume = VolumeCapacity.CubicCentimeters / good.VolumePerUnit.CubicCentimeters
   capacityUnits = min(maxByWeight, maxByVolume)
   ```
2. **Profit ranking** — replace `transportPerUnit = distance × 1` with `haulagePerUnit` (above). `profitPerUnit = destPrice − seatPrice − haulagePerUnit`; a pair is only chosen if `profitPerUnit > 0`.
3. **Affordability** — the merchant must cover both the goods purchase and the haulage:
   ```
   affordable = Capital / (seatPrice + haulagePerUnit)        // guard divide-by-zero as today
   quantity   = min(capacityUnits, availableAtSeat, affordable)
   ```
4. **On dispatch** — after computing `quantity` (> 0): deduct `totalHaulage` from the merchant's capital and record the sink (the affordability gate guarantees `Capital ≥ purchase + haulage`, so `Spend` never throws):
   ```
   merchant.Spend(new Money(totalHaulage))           // existing RepresentativeMerchant.Spend(Money)
   ctx.Money.Record(MoneyChannel.MerchantHaulage, totalHaulage)   // Record(MoneyChannel, long)
   ```
   Caravan creation and the (flat) arrival-tick math are unchanged.

If after these gates `quantity` is 0 (can't afford purchase + haulage, or no capacity), the merchant simply makes no trip for that good this tick — same control flow as today's "no profitable trade" path.

---

## Component 5 — `World.DisplayUnitSystem` + TUI toggle (Domain, Persistence, Tui)

- `World` gains `UnitSystem DisplayUnitSystem { get; }`, default `Metric`, with `SetDisplayUnitSystem(UnitSystem)`. Migration backfills `Metric`. This sits beside `Currency` — "how this campaign reads numbers."
- **CLI** formats mass/volume using the world's `DisplayUnitSystem` (no per-command flag in Layer A).
- **TUI** holds a session-level `UnitSystem` initialised from the world default, with a keypress (proposed `u`) to toggle metric/imperial; all mass/volume rendering reads that session value. The toggle is display-only.

---

## Component 6 — Surfacing (CLI + TUI)

- **CLI `list good`** — add Mass and Volume columns (formatted in the world's unit system).
- **CLI `merchants`** — show `WeightCapacity` / `VolumeCapacity` instead of the old unit-count cargo capacity.
- **CLI `ledger`/`money`** — `MerchantHaulage` appears automatically as a sink (no code change beyond the enum; the ledger iterates channels).
- **CLI `price`** — unchanged (no physical columns; out of scope).
- **TUI Good create/edit forms** — add mass + volume fields that accept familiar-unit input via `TryParseMass`/`TryParseVolume` (e.g. type `5 kg`, `200 L`); show validation errors on bad input.
- **TUI Merchant create/edit forms** — capacity fields become weight + volume (familiar-unit input).
- **TUI** — the `u` unit-system toggle; relevant detail/list views render mass/volume formatted.

---

## Data flow

```
advance → TradePhase (per merchant, stable id order):
  for each good at seat (stable id order):
    haulagePerUnit = f(good.Mass, good.Volume, route.Distance, World.VolumetricDivisor, World.TransportRate)
    rank (good,dest) by profitPerUnit = destPrice − seatPrice − haulagePerUnit
  choose best positive-profit pair
  quantity = min(capacityUnits[weight,volume], availableAtSeat, affordable[purchase+haulage])
  if quantity > 0:
    merchant.SpendCapital(totalHaulage); ctx.Money.Record(MerchantHaulage, totalHaulage)
    create single-good caravan (arrival = tick + distance×12, unchanged)
```

No new RNG. Haulage is a pure function of stored integer fields → deterministic and granularity-invariant (the existing granularity test must still pass).

---

## Migration

One EF migration `AddPhysicalGoods`:

- `goods`: add `MassPerUnit` (long grams) + `VolumePerUnit` (long cm³). Backfill existing rows from `SizeClass` via the default tables (a `migrationBuilder.Sql` UPDATE keyed on the size column, matching the price-discovery migration's peak-willingness backfill style).
- `merchants` (representative): drop `CargoCapacity`; add `WeightCapacity` (long g) + `VolumeCapacity` (long cm³). Backfill: `WeightCapacity = CargoCapacity × 10000` (treat each old unit as a 10 kg medium good), `VolumeCapacity = CargoCapacity × 20000` (20 L). Done via raw SQL before dropping the old column so existing worlds keep a comparable hauling scale.
- `worlds`: add `VolumetricDivisor` (default 5000), `TransportRate` (default 1), `DisplayUnitSystem` (default 0 = Metric).

EF value converters for `Mass`/`Volume` (long ↔ struct), mirroring `MoneyConverter`. `UnitSystem` stored as int.

---

## Seed updates (keep both paths in sync)

`src/WorldEcon.Cli/DemoSeeder.cs` **and** `samples/aerthos.seed.json` (the latter is also the import/TUI/Seeding test fixture):

- Author mass/volume on the 13 demo goods so dimensional effects are visible — at minimum make **Iron Ingot heavy-and-small** (weight-bound) and **Cloth light-and-bulky** (volume-bound). Bread/Flour/Grain mid-range.
- Add the two physical fields to the seed JSON good schema + the importer (`SeedGood`), like `consumptionPerCapitaBp`/`needTier` were added. Defaults applied when a field is omitted (so existing fixtures still load).
- Merchants seeded with weight + volume capacities replacing the old unit cap.
- Sanity-tune `TransportRate` during this step so merchants still run profitable trades on `advance` (the seed-balancing pass; document the chosen value).

---

## Testing

**Domain (`WorldEcon.Domain.Tests.Unit`)**
- `Mass`/`Volume` arithmetic operators.
- `MeasurementFormat`: metric format (`1000 g → "1 kg"`, `200000 cm³ → "0.2 m³"`), imperial format, and round-trip parse for representative inputs (`"5 kg"`, `"8 oz"`, `"200 L"`, `"1.5 ft3"`); parse is system-agnostic.
- `Good.Create` default mass/volume by `SizeClass`; explicit override; rejects `< 1` base unit.
- `MoneyChannels.KindOf(MerchantHaulage) == Sink`.

**Engine (`WorldEcon.Engine.Tests.Unit`)**
- Capacity gated by **weight** for a dense good (small volume, high mass) → fewer units than volume would allow.
- Capacity gated by **volume** for a bulky-light good → fewer units than weight would allow.
- Haulage is a **real sink**: after a dispatch, merchant `Capital` drops by `totalHaulage`, and a `MoneyLedgerSnapshot` shows a `MerchantHaulage` sink line of that amount; total-supply conservation still reconciles.
- Merchant with capital below `purchase + haulage` makes no (or a smaller) trip — no negative capital.
- Existing granularity test (`ConsumerGranularityTests`) still passes (extend its seed with mass/volume if needed).

**Tui (`WorldEcon.Tui.Tests.Unit`)**
- Good form accepts `"5 kg"` / `"200 L"` and stores the right base units; rejects garbage.
- Unit-system toggle flips rendered output without changing stored values (a navigator/format-level test).

---

## Open risks / notes

- **Imperial rounding:** imperial display/parse is approximate (non-integer gram ratios). Acceptable — canonical value is metric base units; the toggle is a reading aid. Tests assert metric exactly and imperial within a tolerance.
- **`TransportRate` default** is a placeholder; the real value is set in the seed-balancing step and remains a `World` knob for ongoing tuning. Surfacing a tuning command is out of scope (the field is settable via the existing world-edit path).
- **Forward hook (Layer B):** `MerchantHaulage` as a per-trip money-out is the same mechanism future guard/mercenary hiring will extend; vehicle capacity will later supersede the merchant-level capacity added here.
