# Transport — Layer A: Physical Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give goods a mass and volume, make caravan capacity dimensional (weight *and* volume), and turn transport cost into a real money sink the merchant pays — all presented in familiar metric/imperial units.

**Architecture:** Two new `Money`-style value types (`Mass` in grams, `Volume` in cm³) in SharedKernel with a metric/imperial formatter; `Good` and `RepresentativeMerchant` gain physical fields; `TradePhase` gates caravans by weight+volume and deducts a `MerchantHaulage` ledger sink computed from dimensional weight × distance. One EF migration backfills existing rows.

**Tech Stack:** C# / .NET 10, EF Core 10 + SQLite, ErrorOr, TUnit + FluentAssertions 7.x, Terminal.Gui. Tests run via `dotnet run --project <testproject> -c Release` (NOT `dotnet test`).

**Spec:** `docs/superpowers/specs/2026-06-27-transport-layer-a-physical-foundation-design.md`

**Branch:** already on `feat/transport-physical`.

---

## Conventions for every task

- **Names spelled out, no abbreviations** (project rule: `BasisPoints` not `Bp`, `WeightCapacity` not `WtCap`).
- **Read a file with the Read tool before editing it** (Edit fails otherwise).
- Build: `dotnet build -c Release` (warnings are errors; must be `0 Warning(s) 0 Error(s)`).
- Run one suite: `dotnet run --project tests/WorldEcon.<Name>.Tests.Unit -c Release` → look for `failed: 0`.
- Commit messages end with the trailer:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- Determinism is sacred: no `Random`, no `Date.Now`; integer math only in sim paths.

---

## File structure (what changes and why)

| File | Responsibility | Task |
|---|---|---|
| `src/WorldEcon.SharedKernel/Measure/Mass.cs` (new) | grams value type | 1 |
| `src/WorldEcon.SharedKernel/Measure/Volume.cs` (new) | cm³ value type | 1 |
| `src/WorldEcon.SharedKernel/Measure/UnitSystem.cs` (new) | metric/imperial enum | 2 |
| `src/WorldEcon.SharedKernel/Measure/MeasurementFormat.cs` (new) | format + parse | 2 |
| `src/WorldEcon.Domain/Economy/Good.cs` | mass/volume fields + defaults | 3 |
| `src/WorldEcon.Domain/Geography/World.cs` | transport tuning + display units | 4 |
| `src/WorldEcon.Domain/Economy/RepresentativeMerchant.cs` | weight/volume capacity | 5 |
| `src/WorldEcon.Domain/Economy/MoneyChannel.cs` | `MerchantHaulage` sink | 6 |
| `src/WorldEcon.Persistence/Conversions/ValueConverters.cs` | `MassConverter`/`VolumeConverter` | 7 |
| `src/WorldEcon.Persistence/Configurations/{Good,RepresentativeMerchant,World}Configuration.cs` | map new columns | 7 |
| `src/WorldEcon.Persistence/Migrations/*` | `AddPhysicalGoods` migration | 7 |
| `src/WorldEcon.Engine/Trade/Haulage.cs` (new), `CargoFit.cs` (new) | testable haulage + fit math | 8 |
| `src/WorldEcon.Engine/Phases/TradePhase.cs` | gate by weight/volume, pay haulage | 8 |
| `src/WorldEcon.Cli/DemoSeeder.cs`, `samples/aerthos.seed.json`, `src/WorldEcon.Seeding/{SeedModel,SeedImporter}.cs` | author physical goods + tune rate | 9 |
| `src/WorldEcon.Cli/CommandRunner.cs` | `list`/`merchants` columns | 5, 10 |
| `src/WorldEcon.Tui/Forms/{GoodForm,EconomyForms}.cs`, `TuiContext.cs`, `Navigation/Navigator.cs` | form inputs + unit toggle | 5, 11 |
| `docs/capabilities-and-roadmap.md` | mark Layer A done | 12 |

**Verification scoping note:** Tasks 3–6 are pure-domain/enum changes verified with **Domain unit tests only** — do NOT run the full suite between them, because the DB schema is out of sync until the migration lands in Task 7. Task 7 restores full-suite green. Task 5 changes the merchant signature, so it also updates every compile-time caller (it must `dotnet build` clean) but its DB-backed assertions wait for Task 7.

---

### Task 1: `Mass` and `Volume` value types

**Files:**
- Create: `src/WorldEcon.SharedKernel/Measure/Mass.cs`
- Create: `src/WorldEcon.SharedKernel/Measure/Volume.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/MeasureTests.cs` (new; the Domain test project references SharedKernel)

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/MeasureTests.cs`:

```csharp
using FluentAssertions;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Tests.Unit;

public class MassVolumeTests
{
    [Test]
    public void Mass_Arithmetic()
    {
        (new Mass(1000) + new Mass(500)).Grams.Should().Be(1500);
        (new Mass(1000) - new Mass(400)).Grams.Should().Be(600);
        (new Mass(250) * 4).Grams.Should().Be(1000);
        Mass.Zero.Grams.Should().Be(0);
    }

    [Test]
    public void Volume_Arithmetic()
    {
        (new Volume(1000) + new Volume(500)).CubicCentimeters.Should().Be(1500);
        (new Volume(1000) - new Volume(400)).CubicCentimeters.Should().Be(600);
        (new Volume(250) * 4).CubicCentimeters.Should().Be(1000);
        Volume.Zero.CubicCentimeters.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run it to confirm it fails to compile**

Run: `dotnet build -c Release`
Expected: errors — `Mass`/`Volume` do not exist.

- [ ] **Step 3: Implement `Mass`**

Create `src/WorldEcon.SharedKernel/Measure/Mass.cs`:

```csharp
namespace WorldEcon.SharedKernel.Measure;

/// <summary>Mass as an integer count of grams. Units (g/kg/oz/lb) are a presentation concern
/// (see <see cref="MeasurementFormat"/>) and never used in simulation math.</summary>
public readonly record struct Mass(long Grams)
{
    public static readonly Mass Zero = new(0);
    public bool IsNegative => Grams < 0;

    public static Mass operator +(Mass a, Mass b) => new(a.Grams + b.Grams);
    public static Mass operator -(Mass a, Mass b) => new(a.Grams - b.Grams);
    public static Mass operator *(Mass a, long quantity) => new(a.Grams * quantity);
}
```

- [ ] **Step 4: Implement `Volume`**

Create `src/WorldEcon.SharedKernel/Measure/Volume.cs`:

```csharp
namespace WorldEcon.SharedKernel.Measure;

/// <summary>Volume as an integer count of cubic centimetres (1 cm³ = 1 mL). Units
/// (cm³/L/m³/in³/ft³) are a presentation concern (see <see cref="MeasurementFormat"/>) and never
/// used in simulation math.</summary>
public readonly record struct Volume(long CubicCentimeters)
{
    public static readonly Volume Zero = new(0);
    public bool IsNegative => CubicCentimeters < 0;

    public static Volume operator +(Volume a, Volume b) => new(a.CubicCentimeters + b.CubicCentimeters);
    public static Volume operator -(Volume a, Volume b) => new(a.CubicCentimeters - b.CubicCentimeters);
    public static Volume operator *(Volume a, long quantity) => new(a.CubicCentimeters * quantity);
}
```

- [ ] **Step 5: Run the test**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `failed: 0` (the new tests pass).

- [ ] **Step 6: Commit**

```bash
git add src/WorldEcon.SharedKernel/Measure/Mass.cs src/WorldEcon.SharedKernel/Measure/Volume.cs tests/WorldEcon.Domain.Tests.Unit/MeasureTests.cs
git commit -m "feat(measure): Mass (grams) and Volume (cm³) value types

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `UnitSystem` + `MeasurementFormat` (format & parse)

**Files:**
- Create: `src/WorldEcon.SharedKernel/Measure/UnitSystem.cs`
- Create: `src/WorldEcon.SharedKernel/Measure/MeasurementFormat.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/MeasureTests.cs` (append a class)

- [ ] **Step 1: Write the failing test**

Append to `tests/WorldEcon.Domain.Tests.Unit/MeasureTests.cs`:

```csharp
public class MeasurementFormatTests
{
    [Test]
    public void FormatMass_Metric()
    {
        MeasurementFormat.FormatMass(new Mass(250), UnitSystem.Metric).Should().Be("250 g");
        MeasurementFormat.FormatMass(new Mass(1000), UnitSystem.Metric).Should().Be("1 kg");
        MeasurementFormat.FormatMass(new Mass(1500), UnitSystem.Metric).Should().Be("1.5 kg");
        MeasurementFormat.FormatMass(new Mass(30000), UnitSystem.Metric).Should().Be("30 kg");
        MeasurementFormat.FormatMass(new Mass(2_000_000), UnitSystem.Metric).Should().Be("2 t");
    }

    [Test]
    public void FormatVolume_Metric()
    {
        MeasurementFormat.FormatVolume(new Volume(50), UnitSystem.Metric).Should().Be("50 cm³");
        MeasurementFormat.FormatVolume(new Volume(1000), UnitSystem.Metric).Should().Be("1 L");
        MeasurementFormat.FormatVolume(new Volume(200000), UnitSystem.Metric).Should().Be("200 L");
        MeasurementFormat.FormatVolume(new Volume(1_000_000), UnitSystem.Metric).Should().Be("1 m³");
    }

    [Test]
    public void FormatMass_Imperial_IsApproximate()
    {
        // 1000 g ≈ 2.2 lb
        MeasurementFormat.FormatMass(new Mass(1000), UnitSystem.Imperial).Should().Be("2.2 lb");
    }

    [Test]
    public void ParseMass_IsSystemAgnostic()
    {
        MeasurementFormat.TryParseMass("5 kg", out var a).Should().BeTrue();
        a.Grams.Should().Be(5000);
        MeasurementFormat.TryParseMass("250g", out var b).Should().BeTrue();
        b.Grams.Should().Be(250);
        MeasurementFormat.TryParseMass("1.2 kg", out var c).Should().BeTrue();
        c.Grams.Should().Be(1200);
        MeasurementFormat.TryParseMass("8 oz", out var d).Should().BeTrue();
        d.Grams.Should().Be(227); // round(8 × 28.3495)
        MeasurementFormat.TryParseMass("nonsense", out _).Should().BeFalse();
        MeasurementFormat.TryParseMass("5 furlongs", out _).Should().BeFalse();
    }

    [Test]
    public void ParseVolume_IsSystemAgnostic()
    {
        MeasurementFormat.TryParseVolume("200 L", out var a).Should().BeTrue();
        a.CubicCentimeters.Should().Be(200000);
        MeasurementFormat.TryParseVolume("0.2 m3", out var b).Should().BeTrue();
        b.CubicCentimeters.Should().Be(200000);
        MeasurementFormat.TryParseVolume("500 cm3", out var c).Should().BeTrue();
        c.CubicCentimeters.Should().Be(500);
        MeasurementFormat.TryParseVolume("1 ft3", out var d).Should().BeTrue();
        d.CubicCentimeters.Should().Be(28317); // round(28316.846592)
        MeasurementFormat.TryParseVolume("nope", out _).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet build -c Release`
Expected: errors — `UnitSystem`/`MeasurementFormat` do not exist.

- [ ] **Step 3: Implement `UnitSystem`**

Create `src/WorldEcon.SharedKernel/Measure/UnitSystem.cs`:

```csharp
namespace WorldEcon.SharedKernel.Measure;

/// <summary>Which unit family mass/volume are displayed in. Display-only — the stored base units
/// (grams, cm³) never change.</summary>
public enum UnitSystem { Metric = 0, Imperial = 1 }
```

- [ ] **Step 4: Implement `MeasurementFormat`**

Create `src/WorldEcon.SharedKernel/Measure/MeasurementFormat.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace WorldEcon.SharedKernel.Measure;

/// <summary>Formats and parses <see cref="Mass"/>/<see cref="Volume"/> in familiar units. This is a
/// presentation/input concern: it may use floating point for unit conversion, but the canonical
/// stored value is always the integer base unit (grams, cm³). Parsing is system-agnostic — any
/// known suffix is accepted regardless of the current display system; the system only governs output.
/// </summary>
public static partial class MeasurementFormat
{
    // name → grams-per-unit (mass) / cm³-per-unit (volume). Lower-cased, '³' normalised to '3'.
    private static readonly (string Unit, double Factor)[] MassUnits =
    [
        ("g", 1), ("gram", 1), ("grams", 1),
        ("kg", 1000), ("kilogram", 1000), ("kilograms", 1000),
        ("t", 1_000_000), ("tonne", 1_000_000), ("tonnes", 1_000_000),
        ("oz", 28.349523125), ("ounce", 28.349523125), ("ounces", 28.349523125),
        ("lb", 453.59237), ("lbs", 453.59237), ("pound", 453.59237), ("pounds", 453.59237),
    ];

    private static readonly (string Unit, double Factor)[] VolumeUnits =
    [
        ("cm3", 1), ("ml", 1), ("milliliter", 1), ("millilitre", 1),
        ("l", 1000), ("liter", 1000), ("litre", 1000),
        ("m3", 1_000_000),
        ("in3", 16.387064),
        ("ft3", 28316.846592),
    ];

    // Display ladders, largest-first: (threshold-in-base, divisor, symbol).
    private static readonly (long Divisor, string Symbol)[] MassMetric = [(1_000_000, "t"), (1000, "kg"), (1, "g")];
    private static readonly (long Divisor, string Symbol)[] VolumeMetric = [(1_000_000, "m³"), (1000, "L"), (1, "cm³")];

    public static string FormatMass(Mass mass, UnitSystem system) => system == UnitSystem.Metric
        ? FormatBaseMetric(mass.Grams, MassMetric)
        : FormatImperial(mass.Grams, lbFactor: 453.59237, "lb", ozFactor: 28.349523125, "oz");

    public static string FormatVolume(Volume volume, UnitSystem system) => system == UnitSystem.Metric
        ? FormatBaseMetric(volume.CubicCentimeters, VolumeMetric)
        : FormatImperial(volume.CubicCentimeters, lbFactor: 28316.846592, "ft³", ozFactor: 16.387064, "in³");

    private static string FormatBaseMetric(long value, (long Divisor, string Symbol)[] ladder)
    {
        foreach (var (divisor, symbol) in ladder)
        {
            if (value >= divisor)
            {
                decimal q = (decimal)value / divisor;
                return $"{q.ToString("0.##", CultureInfo.InvariantCulture)} {symbol}";
            }
        }
        return $"0 {ladder[^1].Symbol}";
    }

    // Imperial display picks the larger unit when the value reaches ~1 of it, else the smaller.
    private static string FormatImperial(long baseValue, double lbFactor, string lbSymbol, double ozFactor, string ozSymbol)
    {
        if (baseValue >= lbFactor)
        {
            double v = baseValue / lbFactor;
            return $"{v.ToString("0.##", CultureInfo.InvariantCulture)} {lbSymbol}";
        }
        double small = baseValue / ozFactor;
        return $"{small.ToString("0.##", CultureInfo.InvariantCulture)} {ozSymbol}";
    }

    public static bool TryParseMass(string text, out Mass mass)
    {
        if (TryParse(text, MassUnits, out long grams)) { mass = new Mass(grams); return true; }
        mass = Mass.Zero; return false;
    }

    public static bool TryParseVolume(string text, out Volume volume)
    {
        if (TryParse(text, VolumeUnits, out long cc)) { volume = new Volume(cc); return true; }
        volume = Volume.Zero; return false;
    }

    private static bool TryParse(string text, (string Unit, double Factor)[] units, out long baseUnits)
    {
        baseUnits = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = NumberUnit().Match(text.Trim());
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return false;
        string unit = m.Groups[2].Value.ToLowerInvariant().Replace('³', '3');
        foreach (var (name, factor) in units)
            if (name == unit) { baseUnits = (long)Math.Round(number * factor, MidpointRounding.AwayFromZero); return true; }
        return false;
    }

    [GeneratedRegex(@"^\s*([0-9]*\.?[0-9]+)\s*([a-zA-Z³]+)\s*$")]
    private static partial Regex NumberUnit();
}
```

- [ ] **Step 5: Run the test**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/WorldEcon.SharedKernel/Measure/UnitSystem.cs src/WorldEcon.SharedKernel/Measure/MeasurementFormat.cs tests/WorldEcon.Domain.Tests.Unit/MeasureTests.cs
git commit -m "feat(measure): UnitSystem + MeasurementFormat (metric/imperial format + parse)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `Good` gains mass + volume

**Files:**
- Modify: `src/WorldEcon.Domain/Economy/Good.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/GoodPhysicalTests.cs` (new)

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/GoodPhysicalTests.cs`:

```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Tests.Unit;

public class GoodPhysicalTests
{
    [Test]
    public void Defaults_DeriveFromSizeClass()
    {
        Good.DefaultMassForSize(SizeClass.Tiny).Grams.Should().Be(50);
        Good.DefaultMassForSize(SizeClass.Medium).Grams.Should().Be(10_000);
        Good.DefaultMassForSize(SizeClass.Bulky).Grams.Should().Be(200_000);
        Good.DefaultVolumeForSize(SizeClass.Tiny).CubicCentimeters.Should().Be(50);
        Good.DefaultVolumeForSize(SizeClass.Medium).CubicCentimeters.Should().Be(20_000);
        Good.DefaultVolumeForSize(SizeClass.Bulky).CubicCentimeters.Should().Be(500_000);
    }

    [Test]
    public void Create_UsesSizeDefaults_WhenOmitted()
    {
        var g = Good.Create(WorldId.New(), "Sack", GoodCategory.Food, new Money(10), "sack",
            SizeClass.Medium, 0, true).Value;
        g.MassPerUnit.Grams.Should().Be(10_000);
        g.VolumePerUnit.CubicCentimeters.Should().Be(20_000);
    }

    [Test]
    public void Create_AcceptsExplicitOverride()
    {
        var g = Good.Create(WorldId.New(), "Ingot", GoodCategory.Material, new Money(200), "ingot",
            SizeClass.Small, 0, false,
            massPerUnit: new Mass(30_000), volumePerUnit: new Volume(4_000)).Value;
        g.MassPerUnit.Grams.Should().Be(30_000);
        g.VolumePerUnit.CubicCentimeters.Should().Be(4_000);
    }

    [Test]
    public void Create_RejectsNonPositivePhysicals()
    {
        Good.Create(WorldId.New(), "Bad", GoodCategory.Misc, new Money(1), "u", SizeClass.Small, 0, true,
            massPerUnit: new Mass(0)).IsError.Should().BeTrue();
        Good.Create(WorldId.New(), "Bad", GoodCategory.Misc, new Money(1), "u", SizeClass.Small, 0, true,
            volumePerUnit: new Volume(0)).IsError.Should().BeTrue();
    }

    [Test]
    public void Setters_Validate()
    {
        var g = Good.Create(WorldId.New(), "X", GoodCategory.Misc, new Money(1), "u", SizeClass.Small, 0, true).Value;
        g.SetMassPerUnit(new Mass(5000)).IsError.Should().BeFalse();
        g.MassPerUnit.Grams.Should().Be(5000);
        g.SetMassPerUnit(new Mass(0)).IsError.Should().BeTrue();
        g.SetVolumePerUnit(new Volume(0)).IsError.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet build -c Release`
Expected: errors — members don't exist.

- [ ] **Step 3: Add the using + fields + ctor params**

In `src/WorldEcon.Domain/Economy/Good.cs`, add to the usings:

```csharp
using WorldEcon.SharedKernel.Measure;
```

Add two properties after `PeakWillingnessMultipleBasisPoints` (keep `Provenance` last as it is):

```csharp
    public Mass MassPerUnit { get; private set; }
    public Volume VolumePerUnit { get; private set; }
```

In the private full ctor signature, add two params before `Provenance provenance`:

```csharp
    private Good(GoodId id, WorldId worldId, string name, GoodCategory category, Money baseValue,
        string baseUnit, SizeClass size, long shelfLifeTicks, bool divisible, long consumptionPerCapitaBp,
        long peakWillingnessMultipleBasisPoints, Mass massPerUnit, Volume volumePerUnit,
        Provenance provenance, NeedTier need) : base(id)
```

And assign inside the ctor (after `PeakWillingnessMultipleBasisPoints = ...;`):

```csharp
        MassPerUnit = massPerUnit;
        VolumePerUnit = volumePerUnit;
```

- [ ] **Step 4: Add the default tables**

Add these static methods to `Good` (next to `DefaultPeakWillingnessForTier`):

```csharp
    /// <summary>Default mass per unit by size class (DM-overridable per good).</summary>
    public static Mass DefaultMassForSize(SizeClass size) => new(size switch
    {
        SizeClass.Tiny => 50,
        SizeClass.Small => 1_000,
        SizeClass.Medium => 10_000,
        SizeClass.Large => 50_000,
        SizeClass.Bulky => 200_000,
        _ => 10_000,
    });

    /// <summary>Default volume per unit by size class (DM-overridable per good).</summary>
    public static Volume DefaultVolumeForSize(SizeClass size) => new(size switch
    {
        SizeClass.Tiny => 50,
        SizeClass.Small => 1_000,
        SizeClass.Medium => 20_000,
        SizeClass.Large => 100_000,
        SizeClass.Bulky => 500_000,
        _ => 20_000,
    });
```

- [ ] **Step 5: Update `Create` (optional params + validation + pass-through)**

Change the `Create` signature to add the two optional params at the end:

```csharp
    public static ErrorOr<Good> Create(WorldId worldId, string name, GoodCategory category, Money baseValue,
        string baseUnit, SizeClass size, long shelfLifeTicks, bool divisible, long consumptionPerCapitaBp = 0,
        NeedTier needTier = NeedTier.Essential, long? peakWillingnessMultipleBasisPoints = null,
        Mass? massPerUnit = null, Volume? volumePerUnit = null)
```

Before the final `return`, after the peak-willingness validation, add:

```csharp
        Mass mass = massPerUnit ?? DefaultMassForSize(size);
        Volume volume = volumePerUnit ?? DefaultVolumeForSize(size);
        if (mass.Grams < 1)
            return Error.Validation("good.mass.tooSmall", "Mass per unit must be at least 1 gram.");
        if (volume.CubicCentimeters < 1)
            return Error.Validation("good.volume.tooSmall", "Volume per unit must be at least 1 cm³.");
```

Update the `return new Good(...)` to pass `mass, volume` before `Provenance.Authored`:

```csharp
        return new Good(GoodId.New(), worldId, name.Trim(), category, baseValue,
            baseUnit.Trim(), size, shelfLifeTicks, divisible, consumptionPerCapitaBp, peak,
            mass, volume, Provenance.Authored, needTier);
```

- [ ] **Step 6: Add the setters**

Add after `SetPeakWillingnessMultiple`:

```csharp
    /// <summary>DM tuning: set the mass per unit (≥ 1 gram).</summary>
    public ErrorOr<Success> SetMassPerUnit(Mass mass)
    {
        if (mass.Grams < 1)
            return Error.Validation("good.mass.tooSmall", "Mass per unit must be at least 1 gram.");
        MassPerUnit = mass;
        return Result.Success;
    }

    /// <summary>DM tuning: set the volume per unit (≥ 1 cm³).</summary>
    public ErrorOr<Success> SetVolumePerUnit(Volume volume)
    {
        if (volume.CubicCentimeters < 1)
            return Error.Validation("good.volume.tooSmall", "Volume per unit must be at least 1 cm³.");
        VolumePerUnit = volume;
        return Result.Success;
    }
```

- [ ] **Step 7: Run Domain tests**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `failed: 0` (new `GoodPhysicalTests` pass; existing Good tests still pass).

> Do NOT run the full suite yet — DB schema is out of sync until Task 7.

- [ ] **Step 8: Commit**

```bash
git add src/WorldEcon.Domain/Economy/Good.cs tests/WorldEcon.Domain.Tests.Unit/GoodPhysicalTests.cs
git commit -m "feat(domain): Good gains MassPerUnit/VolumePerUnit with size-class defaults

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `World` transport tuning + display unit system

**Files:**
- Modify: `src/WorldEcon.Domain/Geography/World.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/WorldTransportTuningTests.cs` (new)

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/WorldTransportTuningTests.cs`:

```csharp
using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Tests.Unit;

public class WorldTransportTuningTests
{
    private static World NewWorld() =>
        World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;

    [Test]
    public void Defaults()
    {
        var w = NewWorld();
        w.VolumetricDivisor.Should().Be(5000);
        w.TransportRate.Should().Be(1);
        w.DisplayUnitSystem.Should().Be(UnitSystem.Metric);
    }

    [Test]
    public void SetTransportTuning_Validates()
    {
        var w = NewWorld();
        w.SetTransportTuning(6000, 3);
        w.VolumetricDivisor.Should().Be(6000);
        w.TransportRate.Should().Be(3);
        Action bad = () => w.SetTransportTuning(0, 1);
        bad.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void SetDisplayUnitSystem()
    {
        var w = NewWorld();
        w.SetDisplayUnitSystem(UnitSystem.Imperial);
        w.DisplayUnitSystem.Should().Be(UnitSystem.Imperial);
    }
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet build -c Release`
Expected: errors — members don't exist.

- [ ] **Step 3: Add the fields + defaults**

In `src/WorldEcon.Domain/Geography/World.cs`, add to usings:

```csharp
using WorldEcon.SharedKernel.Measure;
```

After the belief-tuning fields/constants block, add:

```csharp
    // Transport (Layer A). Dimensional-weight haulage cost is computed as
    // max(mass, volume × 1000 / VolumetricDivisor) grams × distance × TransportRate / 1_000_000 copper.
    public long VolumetricDivisor { get; private set; }   // cm³ that bill as one kg of volumetric weight
    public long TransportRate { get; private set; }       // copper per 1000 kg·distance
    public UnitSystem DisplayUnitSystem { get; private set; }

    private const long DefaultVolumetricDivisor = 5_000;  // real-world air-freight number
    private const long DefaultTransportRate = 1;
```

In **both** the EF private ctor and the main private ctor, the get-only props are set via the main ctor; add to the main private ctor (after the belief defaults):

```csharp
        VolumetricDivisor = DefaultVolumetricDivisor;
        TransportRate = DefaultTransportRate;
        DisplayUnitSystem = UnitSystem.Metric;
```

- [ ] **Step 4: Add the setters**

Add after `SetBeliefTuning`:

```csharp
    /// <summary>DM tuning for haulage cost. Both must be ≥ 1.</summary>
    public void SetTransportTuning(long volumetricDivisor, long transportRate)
    {
        if (volumetricDivisor < 1)
            throw new ArgumentOutOfRangeException(nameof(volumetricDivisor), volumetricDivisor, "Volumetric divisor must be at least 1.");
        if (transportRate < 1)
            throw new ArgumentOutOfRangeException(nameof(transportRate), transportRate, "Transport rate must be at least 1.");
        VolumetricDivisor = volumetricDivisor;
        TransportRate = transportRate;
    }

    /// <summary>Which unit family the UI presents mass/volume in (display-only).</summary>
    public void SetDisplayUnitSystem(UnitSystem system) => DisplayUnitSystem = system;
```

- [ ] **Step 5: Run Domain tests**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/WorldEcon.Domain/Geography/World.cs tests/WorldEcon.Domain.Tests.Unit/WorldTransportTuningTests.cs
git commit -m "feat(domain): World transport tuning (VolumetricDivisor, TransportRate) + DisplayUnitSystem

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Merchant capacity → weight + volume (domain + all call sites)

This is the one compile-breaking signature change; it updates every caller so the solution builds. DB-backed assertions wait for Task 7.

**Files:**
- Modify: `src/WorldEcon.Domain/Economy/RepresentativeMerchant.cs`
- Modify: `src/WorldEcon.Cli/DemoSeeder.cs` (`Merchant` helper)
- Modify: `src/WorldEcon.Cli/CommandRunner.cs` (`CmdMerchants` table)
- Modify: `src/WorldEcon.Seeding/SeedModel.cs` (`SeedMerchant`)
- Modify: `src/WorldEcon.Seeding/SeedImporter.cs` (merchant import)
- Modify: `src/WorldEcon.Tui/Forms/EconomyForms.cs` (`MerchantForm`)
- Modify: `src/WorldEcon.Tui/Navigation/Navigator.cs` (merchant row + detail)
- Test: `tests/WorldEcon.Domain.Tests.Unit/MerchantCapacityTests.cs` (new)

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/MerchantCapacityTests.cs`:

```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Tests.Unit;

public class MerchantCapacityTests
{
    [Test]
    public void Create_StoresWeightAndVolumeCapacity()
    {
        var m = RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), new Money(50_000),
            new Mass(600_000), new Volume(1_000_000), 1000).Value;
        m.WeightCapacity.Grams.Should().Be(600_000);
        m.VolumeCapacity.CubicCentimeters.Should().Be(1_000_000);
        m.Reach.Should().Be(1000);
    }

    [Test]
    public void Create_RejectsNonPositiveCapacity()
    {
        RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), new Money(1),
            new Mass(0), new Volume(1000), 1000).IsError.Should().BeTrue();
        RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), new Money(1),
            new Mass(1000), new Volume(0), 1000).IsError.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet build -c Release`
Expected: errors — members don't exist (and many call-site errors once the property is changed).

- [ ] **Step 3: Change the domain type**

In `src/WorldEcon.Domain/Economy/RepresentativeMerchant.cs`, add to usings:

```csharp
using WorldEcon.SharedKernel.Measure;
```

Replace `public long CargoCapacity { get; private set; }` with:

```csharp
    public Mass WeightCapacity { get; private set; }
    public Volume VolumeCapacity { get; private set; }
```

Replace the private ctor signature + body assignment:

```csharp
    private RepresentativeMerchant(MerchantId id, WorldId worldId, SettlementId seat, Money capital,
        Mass weightCapacity, Volume volumeCapacity, long reach) : base(id)
    {
        WorldId = worldId;
        Seat = seat;
        Capital = capital;
        WeightCapacity = weightCapacity;
        VolumeCapacity = volumeCapacity;
        Reach = reach;
    }
```

Replace `Create`:

```csharp
    public static ErrorOr<RepresentativeMerchant> Create(WorldId worldId, SettlementId seat, Money capital,
        Mass weightCapacity, Volume volumeCapacity, long reach)
    {
        if (capital.IsNegative)
            return Error.Validation("merchant.capital.negative", "Capital must not be negative.");
        if (weightCapacity.Grams < 1)
            return Error.Validation("merchant.weightcapacity.tooSmall", "Weight capacity must be at least 1 gram.");
        if (volumeCapacity.CubicCentimeters < 1)
            return Error.Validation("merchant.volumecapacity.tooSmall", "Volume capacity must be at least 1 cm³.");
        if (reach < 1)
            return Error.Validation("merchant.reach.tooSmall", "Reach must be at least 1.");

        return new RepresentativeMerchant(MerchantId.New(), worldId, seat, capital, weightCapacity, volumeCapacity, reach);
    }
```

Add a setter (for edit forms / future use):

```csharp
    /// <summary>DM tuning: set hauling capacity (both ≥ 1 base unit).</summary>
    public ErrorOr<Success> SetCapacity(Mass weightCapacity, Volume volumeCapacity)
    {
        if (weightCapacity.Grams < 1)
            return Error.Validation("merchant.weightcapacity.tooSmall", "Weight capacity must be at least 1 gram.");
        if (volumeCapacity.CubicCentimeters < 1)
            return Error.Validation("merchant.volumecapacity.tooSmall", "Volume capacity must be at least 1 cm³.");
        WeightCapacity = weightCapacity;
        VolumeCapacity = volumeCapacity;
        return Result.Success;
    }
```

(Add `using ErrorOr;` is already present.)

- [ ] **Step 4: Fix `DemoSeeder.Merchant`**

In `src/WorldEcon.Cli/DemoSeeder.cs`, add `using WorldEcon.SharedKernel.Measure;` if absent, then replace the `Merchant` helper:

```csharp
    private static void Merchant(WorldDbContext ctx, WorldId w, Settlement s, long capital)
        => ctx.Merchants.Add(Unwrap(RepresentativeMerchant.Create(w, s.Id, new Money(capital),
            new Mass(600_000), new Volume(1_000_000), 1000), $"{s.Name} merchant")); // 600 kg / 1000 L
```

- [ ] **Step 5: Fix `CmdMerchants` table**

In `src/WorldEcon.Cli/CommandRunner.cs`, add `using WorldEcon.SharedKernel.Measure;` if absent. Replace the header + row lines (currently at ~349 and ~353):

```csharp
        var units = world?.DisplayUnitSystem ?? UnitSystem.Metric;
        Console.WriteLine($"  {"Seat",-16} {"Capital",12} {"Weight cap",12} {"Volume cap",12} {"Reach",8}");
        foreach (var m in merchants)
        {
            var seat = settlementById.TryGetValue(m.Seat, out var n) ? n : "(unknown)";
            Console.WriteLine($"  {seat,-16} {currency.Format(m.Capital),12} {MeasurementFormat.FormatMass(m.WeightCapacity, units),12} {MeasurementFormat.FormatVolume(m.VolumeCapacity, units),12} {m.Reach,8}");
        }
```

- [ ] **Step 6: Fix the seed model + importer**

In `src/WorldEcon.Seeding/SeedModel.cs`, replace `SeedMerchant`:

```csharp
// Capacities are optional familiar-unit strings ("600 kg", "1000 L"); omitted → sensible defaults so
// older fixtures still import. Reach defaults to 1000.
public sealed record SeedMerchant(long Capital, string? WeightCapacity = null, string? VolumeCapacity = null, long Reach = 1000);
```

In `src/WorldEcon.Seeding/SeedImporter.cs`, add `using WorldEcon.SharedKernel.Measure;`, then find the merchant import (search for `RepresentativeMerchant.Create`) and replace its construction with parsing + defaults:

```csharp
        var weightCapacity = m.WeightCapacity is not null && MeasurementFormat.TryParseMass(m.WeightCapacity, out var wc)
            ? wc : new Mass(600_000);
        var volumeCapacity = m.VolumeCapacity is not null && MeasurementFormat.TryParseVolume(m.VolumeCapacity, out var vc)
            ? vc : new Volume(1_000_000);
        var merchant = Unwrap(RepresentativeMerchant.Create(worldId, settlementId, new Money(m.Capital),
            weightCapacity, volumeCapacity, m.Reach));
```

(Match the existing variable names `worldId`/`settlementId` used in that method; read the surrounding lines first.)

- [ ] **Step 7: Fix `MerchantForm`**

In `src/WorldEcon.Tui/Forms/EconomyForms.cs`, add `using WorldEcon.SharedKernel.Measure;`. Replace the `cargo` prompt and the `Create` call:

```csharp
        var weightText = await FormPrompts.RequiredTextAsync(ui, t, "Weight capacity (e.g. 600 kg):");
        if (weightText is null) return FormOutcome.Cancelled;
        if (!MeasurementFormat.TryParseMass(weightText, out var weightCap))
            return FormOutcome.Fail("Could not parse weight capacity (try e.g. '600 kg').");

        var volumeText = await FormPrompts.RequiredTextAsync(ui, t, "Volume capacity (e.g. 1000 L):");
        if (volumeText is null) return FormOutcome.Cancelled;
        if (!MeasurementFormat.TryParseVolume(volumeText, out var volumeCap))
            return FormOutcome.Fail("Could not parse volume capacity (try e.g. '1000 L').");

        var reach = await FormPrompts.NumberAsync(ui, t, "Trade reach (max route distance):", 1000);
        if (reach is null) return FormOutcome.Cancelled;

        var result = RepresentativeMerchant.Create(ctx.World.Id, new SettlementId(seatId.Value),
            new Money(capital.Value), weightCap, volumeCap, reach.Value);
```

- [ ] **Step 8: Fix `Navigator` merchant row + detail**

In `src/WorldEcon.Tui/Navigation/Navigator.cs`, add `using WorldEcon.SharedKernel.Measure;` if absent. Update the merchant table row (~line 355) and detail line (~line 576). Read those lines first; replace `m.CargoCapacity.ToString()` (row) with:

```csharp
                 ctx.FormatMass(m.WeightCapacity), ctx.FormatVolume(m.VolumeCapacity), m.Reach.ToString()
```

(adjust the surrounding column array + header to include both columns — read the method to see the header list and add a "Volume cap" column header next to the weight one). For the detail line (~576), replace `$"Cargo capacity: {m.CargoCapacity}"` with:

```csharp
        $"Weight cap: {ctx.FormatMass(m.WeightCapacity)}", $"Volume cap: {ctx.FormatVolume(m.VolumeCapacity)}",
```

`ctx.FormatMass`/`FormatVolume` are added in Task 11; to keep this task compiling, **temporarily** use the world default directly here:

```csharp
// Task 5 (pre-toggle): format with world default until TuiContext.FormatMass lands in Task 11.
MeasurementFormat.FormatMass(m.WeightCapacity, ctx.World.DisplayUnitSystem)
MeasurementFormat.FormatVolume(m.VolumeCapacity, ctx.World.DisplayUnitSystem)
```

Use those two calls in both the row and the detail in this task; Task 11 swaps them for `ctx.FormatMass/FormatVolume`.

- [ ] **Step 9: Build + run Domain tests**

Run: `dotnet build -c Release`
Expected: `0 Warning(s) 0 Error(s)`.
Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `failed: 0`.

> The full suite (Seeding/Tui DB tests) will fail until Task 7's migration — that is expected; do not run it here.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(domain): merchant capacity becomes weight + volume (all call sites updated)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: `MerchantHaulage` money channel

**Files:**
- Modify: `src/WorldEcon.Domain/Economy/MoneyChannel.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/MoneyChannelsTests.cs` (append)

- [ ] **Step 1: Write the failing test**

Append to `tests/WorldEcon.Domain.Tests.Unit/MoneyChannelsTests.cs` (inside the existing test class):

```csharp
    [Test]
    public void MerchantHaulage_IsSink()
    {
        MoneyChannels.KindOf(MoneyChannel.MerchantHaulage).Should().Be(MoneyFlowKind.Sink);
    }
```

(If the file's namespace/usings differ, match the existing test methods around it.)

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet build -c Release`
Expected: error — `MerchantHaulage` does not exist.

- [ ] **Step 3: Add the channel**

In `src/WorldEcon.Domain/Economy/MoneyChannel.cs`, add to the `MoneyChannel` enum (after `MerchantSale = 3`):

```csharp
    /// <summary>What a merchant pays to move a caravan (porter/teamster wages until the labour loop
    /// exists; also the future hook for paid guards/mercenaries). A sink.</summary>
    MerchantHaulage = 4,
```

And add the classification arm in `MoneyChannels.KindOf` before the `_ =>`:

```csharp
        MoneyChannel.MerchantHaulage => MoneyFlowKind.Sink,
```

- [ ] **Step 4: Run Domain tests**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Domain/Economy/MoneyChannel.cs tests/WorldEcon.Domain.Tests.Unit/MoneyChannelsTests.cs
git commit -m "feat(domain): MerchantHaulage money channel (sink)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Persistence — converters, configs, migration (restores full-suite green)

**Files:**
- Modify: `src/WorldEcon.Persistence/Conversions/ValueConverters.cs`
- Modify: `src/WorldEcon.Persistence/Configurations/GoodConfiguration.cs`
- Modify: `src/WorldEcon.Persistence/Configurations/RepresentativeMerchantConfiguration.cs`
- Modify: `src/WorldEcon.Persistence/Configurations/WorldConfiguration.cs`
- Create (via EF tool, then edit): `src/WorldEcon.Persistence/Migrations/<timestamp>_AddPhysicalGoods.cs` (+ Designer + snapshot auto-update)

- [ ] **Step 1: Add the value converters**

In `src/WorldEcon.Persistence/Conversions/ValueConverters.cs`, add `using WorldEcon.SharedKernel.Measure;` and two converters next to `MoneyConverter`:

```csharp
public sealed class MassConverter() : ValueConverter<Mass, long>(m => m.Grams, v => new Mass(v));

public sealed class VolumeConverter() : ValueConverter<Volume, long>(v => v.CubicCentimeters, v => new Volume(v));
```

- [ ] **Step 2: Map the Good columns**

In `GoodConfiguration.Configure`, add:

```csharp
        b.Property(x => x.MassPerUnit).HasConversion<MassConverter>();
        b.Property(x => x.VolumePerUnit).HasConversion<VolumeConverter>();
```

- [ ] **Step 3: Map the Merchant columns**

In `RepresentativeMerchantConfiguration.Configure`, add:

```csharp
        b.Property(x => x.WeightCapacity).HasConversion<MassConverter>();
        b.Property(x => x.VolumeCapacity).HasConversion<VolumeConverter>();
```

- [ ] **Step 4: Map the World display-unit enum**

In `WorldConfiguration.Configure`, add (the two longs map by convention; the enum needs a converter):

```csharp
        b.Property(x => x.DisplayUnitSystem).HasConversion<string>();
```

- [ ] **Step 5: Generate the migration**

Run: `dotnet dotnet-ef migrations add AddPhysicalGoods --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Cli`
Expected: creates `<timestamp>_AddPhysicalGoods.cs`, its `.Designer.cs`, and updates `WorldDbContextModelSnapshot.cs`.

- [ ] **Step 6: Edit the migration `Up` for safe backfill**

Open the generated `<timestamp>_AddPhysicalGoods.cs`. EF will have generated `AddColumn`s for goods (`MassPerUnit`,`VolumePerUnit`), merchants (`WeightCapacity`,`VolumeCapacity`), worlds (`VolumetricDivisor`,`TransportRate`,`DisplayUnitSystem`) and a `DropColumn("CargoCapacity","merchants")`. Ensure the **Up** body is ordered so backfill runs before the drop. Make it read exactly:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    // Goods: add physical columns with a Medium-ish default, then backfill by size class.
    migrationBuilder.AddColumn<long>(name: "MassPerUnit", table: "goods", type: "INTEGER", nullable: false, defaultValue: 10000L);
    migrationBuilder.AddColumn<long>(name: "VolumePerUnit", table: "goods", type: "INTEGER", nullable: false, defaultValue: 20000L);
    migrationBuilder.Sql(
        "UPDATE goods SET MassPerUnit = CASE Size " +
        "WHEN 'Tiny' THEN 50 WHEN 'Small' THEN 1000 WHEN 'Medium' THEN 10000 WHEN 'Large' THEN 50000 WHEN 'Bulky' THEN 200000 ELSE 10000 END, " +
        "VolumePerUnit = CASE Size " +
        "WHEN 'Tiny' THEN 50 WHEN 'Small' THEN 1000 WHEN 'Medium' THEN 20000 WHEN 'Large' THEN 100000 WHEN 'Bulky' THEN 500000 ELSE 20000 END;");

    // Merchants: add weight/volume capacity, backfill from the old unit cap, THEN drop it.
    migrationBuilder.AddColumn<long>(name: "WeightCapacity", table: "merchants", type: "INTEGER", nullable: false, defaultValue: 600000L);
    migrationBuilder.AddColumn<long>(name: "VolumeCapacity", table: "merchants", type: "INTEGER", nullable: false, defaultValue: 1000000L);
    migrationBuilder.Sql("UPDATE merchants SET WeightCapacity = CargoCapacity * 10000, VolumeCapacity = CargoCapacity * 20000;");
    migrationBuilder.DropColumn(name: "CargoCapacity", table: "merchants");

    // Worlds: transport tuning + display units.
    migrationBuilder.AddColumn<long>(name: "VolumetricDivisor", table: "worlds", type: "INTEGER", nullable: false, defaultValue: 5000L);
    migrationBuilder.AddColumn<long>(name: "TransportRate", table: "worlds", type: "INTEGER", nullable: false, defaultValue: 1L);
    migrationBuilder.AddColumn<string>(name: "DisplayUnitSystem", table: "worlds", type: "TEXT", nullable: false, defaultValue: "Metric");
}
```

Adjust the generated `Down` to mirror it (drop the added columns; re-add `CargoCapacity` as `INTEGER NOT NULL DEFAULT 0`). Keep whatever exact column types EF emitted; only the **ordering** and the two `Sql` backfills are the manual part.

- [ ] **Step 7: Build + run the FULL suite**

Run: `dotnet build -c Release`
Expected: `0 Warning(s) 0 Error(s)`.
Run each: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`, `...Simulation...`, `...Engine...`, `...Tui...`, and any Seeding test project.
Expected: every suite `failed: 0`. (TradePhase still uses the old capacity path — that's fine; capacity gating changes in Task 8. The merchant table now has the new columns so all DB reads work.)

- [ ] **Step 8: Smoke-test an existing DB migrates + a fresh DB seeds**

Run:
```bash
rm -f /tmp/we_mig.db*; dotnet run --project src/WorldEcon.Cli -c Release -- new /tmp/we_mig.db && dotnet run --project src/WorldEcon.Cli -c Release -- merchants /tmp/we_mig.db
```
Expected: merchants list shows `Weight cap` / `Volume cap` columns (e.g. `600 kg` / `1000 L`).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(persistence): map Mass/Volume + AddPhysicalGoods migration (backfill by size)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Engine — dimensional capacity gating + real haulage sink

**Files:**
- Create: `src/WorldEcon.Engine/Trade/Haulage.cs`
- Create: `src/WorldEcon.Engine/Trade/CargoFit.cs`
- Modify: `src/WorldEcon.Engine/Phases/TradePhase.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/HaulageTests.cs` (new, pure math)
- Test: `tests/WorldEcon.Engine.Tests.Unit/TradeHaulageTests.cs` (new, DB-backed)

- [ ] **Step 1: Write the failing pure-math test**

Create `tests/WorldEcon.Engine.Tests.Unit/HaulageTests.cs`:

```csharp
using FluentAssertions;
using WorldEcon.Engine.Trade;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Engine.Tests.Unit;

public class HaulageMathTests
{
    [Test]
    public void DimensionalWeight_TakesTheBindingDimension()
    {
        // Dense good: 30 kg, 4 L. volumetric = 4000 × 1000 / 5000 = 800 g. max(30000, 800) = 30000.
        Haulage.DimensionalWeightGrams(30_000, 4_000, 5_000).Should().Be(30_000);
        // Bulky-light good: 2 kg, 60 L. volumetric = 60000 × 1000 / 5000 = 12000 g. max(2000, 12000) = 12000.
        Haulage.DimensionalWeightGrams(2_000, 60_000, 5_000).Should().Be(12_000);
    }

    [Test]
    public void Cost_ScalesWithDimWeightDistanceAndRate()
    {
        // 25000 g × 120 distance × rate 1 / 1_000_000 = 3 copper.
        Haulage.Cost(25_000, 120, 1).Should().Be(3);
        Haulage.Cost(25_000, 120, 4).Should().Be(12);
    }

    [Test]
    public void CargoFit_GatesByWeightForDense_ByVolumeForBulky()
    {
        // Cap 600 kg / 1000 L. Dense 30 kg / 4 L → weight binds: 600000/30000 = 20.
        CargoFit.MaxUnits(new Mass(600_000), new Volume(1_000_000), new Mass(30_000), new Volume(4_000)).Should().Be(20);
        // Bulky 2 kg / 60 L → volume binds: 1000000/60000 = 16.
        CargoFit.MaxUnits(new Mass(600_000), new Volume(1_000_000), new Mass(2_000), new Volume(60_000)).Should().Be(16);
    }
}
```

- [ ] **Step 2: Run to confirm failure**

Run: `dotnet build -c Release`
Expected: errors — `Haulage`/`CargoFit` do not exist.

- [ ] **Step 3: Implement `Haulage`**

Create `src/WorldEcon.Engine/Trade/Haulage.cs`:

```csharp
namespace WorldEcon.Engine.Trade;

/// <summary>Dimensional-weight haulage math (Layer A). All integer → deterministic.</summary>
public static class Haulage
{
    /// <summary>Billable weight in grams: the larger of actual mass and volumetric weight
    /// (volume cm³ × 1000 / volumetricDivisor).</summary>
    public static long DimensionalWeightGrams(long massGrams, long volumeCubicCentimeters, long volumetricDivisor)
    {
        long volumetric = volumeCubicCentimeters * 1000 / volumetricDivisor;
        return Math.Max(massGrams, volumetric);
    }

    /// <summary>Haulage cost in copper: dimensional weight (g) × distance × rate / 1_000_000
    /// (so rate reads as copper per 1000 kg·distance).</summary>
    public static long Cost(long dimensionalWeightGrams, long distance, long transportRate)
        => dimensionalWeightGrams * distance * transportRate / 1_000_000;
}
```

- [ ] **Step 4: Implement `CargoFit`**

Create `src/WorldEcon.Engine/Trade/CargoFit.cs`:

```csharp
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Engine.Trade;

/// <summary>How many units of a single good fit in a merchant's hauling capacity — the smaller of the
/// weight-limited and volume-limited counts (dimensional capacity).</summary>
public static class CargoFit
{
    public static long MaxUnits(Mass weightCapacity, Volume volumeCapacity, Mass unitMass, Volume unitVolume)
    {
        long byWeight = weightCapacity.Grams / unitMass.Grams;
        long byVolume = volumeCapacity.CubicCentimeters / unitVolume.CubicCentimeters;
        return Math.Min(byWeight, byVolume);
    }
}
```

- [ ] **Step 5: Run the pure-math test**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `HaulageMathTests` pass (`failed: 0`).

- [ ] **Step 6: Wire into `TradePhase` — capacity gating + affordability**

In `src/WorldEcon.Engine/Phases/TradePhase.cs`, add `using WorldEcon.Engine.Trade;` and `using WorldEcon.SharedKernel.Measure;`. Remove the now-unused constant `TransportCostUnitsPerDistance` and read tuning from the world. In `ExecuteAsync`, the per-merchant dispatch block changes as follows.

Replace the profit-ranking transport term. Currently:

```csharp
                    long transportPerUnit = dest.Distance * TransportCostUnitsPerDistance;
                    long profitPerUnit = destPrice - offer.Price - transportPerUnit;
```

with (compute per-unit haulage from the good's physicals + world tuning):

```csharp
                    long unitDimWeight = Haulage.DimensionalWeightGrams(
                        goods[offer.Good].MassPerUnit.Grams, goods[offer.Good].VolumePerUnit.CubicCentimeters,
                        ctx.World.VolumetricDivisor);
                    long haulagePerUnit = Haulage.Cost(unitDimWeight, dest.Distance, ctx.World.TransportRate);
                    long profitPerUnit = destPrice - offer.Price - haulagePerUnit;
```

Replace the capacity + affordability + quantity block. Currently:

```csharp
            long affordable = bestSeatPrice == 0 ? merchant.CargoCapacity : merchant.Capital.Units / bestSeatPrice;
            long quantity = Math.Min(merchant.CargoCapacity, Math.Min(bestSeatQty, affordable));
            if (quantity < 1)
                continue;
```

with:

```csharp
            var bestGood = goods[bestGoodId];
            long capacityUnits = CargoFit.MaxUnits(merchant.WeightCapacity, merchant.VolumeCapacity,
                bestGood.MassPerUnit, bestGood.VolumePerUnit);

            // Affordable units must cover BOTH the purchase and the per-unit haulage. Use a precise
            // per-unit haulage cost (copper per unit, ×1_000_000 internally to avoid truncation to 0).
            long unitDimWeightBest = Haulage.DimensionalWeightGrams(
                bestGood.MassPerUnit.Grams, bestGood.VolumePerUnit.CubicCentimeters, ctx.World.VolumetricDivisor);
            long haulageNumeratorPerUnit = unitDimWeightBest * bestDest.Distance * ctx.World.TransportRate; // ÷1_000_000 = copper
            long denom = bestSeatPrice * 1_000_000L + haulageNumeratorPerUnit;
            long affordable = denom > 0 ? merchant.Capital.Units * 1_000_000L / denom : capacityUnits;

            long quantity = Math.Min(capacityUnits, Math.Min(bestSeatQty, affordable));
            if (quantity < 1)
                continue;
```

> Overflow note: `merchant.Capital.Units * 1_000_000L` is safe for demo-scale capital (≤ ~1e9 → ≤ 1e15 < long max). If a far larger economy is modelled later, switch this one line to `Int128`.

- [ ] **Step 7: Wire into `TradePhase` — pay the haulage sink on dispatch**

After the existing withdraw + purchase block:

```csharp
            merchant.Spend(new Money(quantity * bestSeatPrice));
            ctx.Money.Record(MoneyChannel.MerchantPurchase, quantity * bestSeatPrice); // sink (source shop uncredited today)
```

add the haulage payment (compute total from totals, exact):

```csharp
            long totalMassGrams = bestGood.MassPerUnit.Grams * quantity;
            long totalVolumeCc = bestGood.VolumePerUnit.CubicCentimeters * quantity;
            long totalDimWeight = Haulage.DimensionalWeightGrams(totalMassGrams, totalVolumeCc, ctx.World.VolumetricDivisor);
            long totalHaulage = Haulage.Cost(totalDimWeight, bestDest.Distance, ctx.World.TransportRate);
            if (totalHaulage > merchant.Capital.Units)
                totalHaulage = merchant.Capital.Units; // affordability gate makes this rare; never overspend
            if (totalHaulage > 0)
            {
                merchant.Spend(new Money(totalHaulage));
                ctx.Money.Record(MoneyChannel.MerchantHaulage, totalHaulage);
            }
```

Update the stale trailing comment block (`// NOTE: transport cost affects only the decision threshold...`) to:

```csharp
            // NOTE: haulage is now a real MerchantHaulage sink (above). Mode/vehicle/loss-risk are Layer B.
```

- [ ] **Step 8: Write the DB-backed test**

Create `tests/WorldEcon.Engine.Tests.Unit/TradeHaulageTests.cs`. This seeds two settlements joined by a route, a seat-shop holding a good with a price gradient, and one merchant; advances one day; asserts a caravan was dispatched, the merchant's capital dropped by purchase + haulage, and the ledger shows a `MerchantHaulage` sink. Model the seed/advance helpers on `PriceDiscoveryTests.cs` in the same folder (same `NewContextOnFile`, `World.Create`, geography chain). Use:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Engine.Tests.Unit;

public class TradeHaulageTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task Dispatch_PaysHaulageSink_AndDebitsCapital()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_haul_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var seat = Settlement.Create(world.Id, region.Id, "Seat", SettlementType.Town, 0, 0, 5000).Value;
        var dest = Settlement.Create(world.Id, region.Id, "Dest", SettlementType.Town, 1, 0, 5000).Value;

        // Good: 10 kg / 20 L, base 100; seat sells cheap, dest values it high (gradient).
        var good = Good.Create(world.Id, "Ingot", GoodCategory.Material, new Money(100), "ingot",
            SizeClass.Medium, 0, false,
            massPerUnit: new Mass(10_000), volumePerUnit: new Volume(20_000)).Value;

        var seatShop = Shop.Create(world.Id, seat.Id, "Seat Market", 0, Money.Zero).Value;
        var seatStock = Stockpile.CreateForShop(world.Id, seatShop.Id, good.Id, 1000, new Money(100)).Value;
        seatStock.SetMarketPrice(new Money(50));   // cheap at seat
        var destShop = Shop.Create(world.Id, dest.Id, "Dest Market", 0, Money.Zero).Value;
        var destStock = Stockpile.CreateForShop(world.Id, destShop.Id, good.Id, 10, new Money(100)).Value;
        destStock.SetMarketPrice(new Money(500));  // dear at dest

        var route = Route.Create(world.Id, seat.Id, dest.Id, 120, Terrain.Plains, 0, RouteCategory.Land).Value;
        var merchant = RepresentativeMerchant.Create(world.Id, seat.Id, new Money(1_000_000),
            new Mass(600_000), new Volume(1_000_000), 1000).Value;

        await using (var ctx = NewContextOnFile(path))
        {
            await ctx.Database.MigrateAsync();
            ctx.Worlds.Add(world); ctx.Continents.Add(continent); ctx.Countries.Add(country); ctx.Regions.Add(region);
            ctx.Settlements.AddRange(seat, dest);
            ctx.Goods.Add(good);
            ctx.Shops.AddRange(seatShop, destShop);
            ctx.Stockpiles.AddRange(seatStock, destStock);
            ctx.Routes.Add(route);
            ctx.Merchants.Add(merchant);
            await ctx.SaveChangesAsync();
        }

        long capitalBefore;
        await using (var ctx = NewContextOnFile(path))
            capitalBefore = (await ctx.Merchants.FirstAsync()).Capital.Units;

        await using (var ctx = NewContextOnFile(path))
        {
            var sim = await SimulationContext.LoadAsync(ctx, world.Id);
            var engine = new TickEngine(new ISimulationPhase[] { new Phases.TradePhase() });
            await engine.AdvanceAsync(sim, Tick.DefaultMinutesPerDay);
        }

        await using (var ctx = NewContextOnFile(path))
        {
            var caravan = await ctx.Caravans.FirstOrDefaultAsync();
            caravan.Should().NotBeNull("a profitable trade should dispatch a caravan");

            long qty = caravan!.Quantity;
            // Capacity: 600 kg / 10 kg = 60 by weight; 1000 L / 20 L = 50 by volume → 50 cap.
            qty.Should().BeLessThanOrEqualTo(50);

            // Haulage for the load: dim weight = max(qty×10000, qty×20000×1000/5000) = max(10000qty, 4000qty)=10000qty.
            long expectedHaulage = 10_000L * qty * 120 * 1 / 1_000_000;
            long purchase = qty * 50;
            long capitalAfter = (await ctx.Merchants.FirstAsync()).Capital.Units;
            capitalAfter.Should().Be(capitalBefore - purchase - expectedHaulage);

            var snapshot = await ctx.MoneyLedgerSnapshots.Include(s => s.Lines)
                .OrderByDescending(s => s.Tick).FirstAsync();
            var haulLine = snapshot.Lines.FirstOrDefault(l => l.Channel == MoneyChannel.MerchantHaulage);
            haulLine.Should().NotBeNull();
            haulLine!.Amount.Units.Should().Be(expectedHaulage);
        }

        File.Delete(path);
    }
}
```

> Before writing, **read `PriceDiscoveryTests.cs` and `MoneyLedgerTests.cs`** in the same folder to confirm: the `MoneyLedgerSnapshot` navigation name (`Lines`), the line's `Channel`/`Amount` property names, and that the ledger snapshots at end-of-advance. Adjust the snapshot-reading lines to match the real API (e.g. the line amount may be `l.Amount` as `Money` or a `long`). Keep the capital-delta assertion as the primary check; relax the ledger assertion to "a MerchantHaulage line exists with amount > 0" if the exact API differs.

- [ ] **Step 9: Run Engine tests**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `failed: 0` (new haulage tests + existing trade/granularity tests pass).

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(engine): dimensional capacity gating + real MerchantHaulage sink in TradePhase

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: Seed — author physical goods, tune the rate (both seed paths)

**Files:**
- Modify: `src/WorldEcon.Cli/DemoSeeder.cs` (`Good_` helper + the 13 goods)
- Modify: `src/WorldEcon.Seeding/SeedModel.cs` (`SeedGood`)
- Modify: `src/WorldEcon.Seeding/SeedImporter.cs` (good import mass/volume)
- Modify: `samples/aerthos.seed.json`

- [ ] **Step 1: Extend `Good_` to accept optional mass/volume**

In `src/WorldEcon.Cli/DemoSeeder.cs` add `using WorldEcon.SharedKernel.Measure;` if absent, and change the `Good_` helper signature + call:

```csharp
    private static Good Good_(WorldDbContext ctx, WorldId w, string name, GoodCategory category, long baseValue,
        string unit, SizeClass size, long shelfLifeTicks, bool divisible, long consumptionPerCapitaBp = 0,
        NeedTier needTier = NeedTier.Essential, Mass? mass = null, Volume? volume = null)
    {
        var good = Unwrap(Good.Create(w, name, category, new Money(baseValue), unit, size, shelfLifeTicks,
            divisible, consumptionPerCapitaBp, needTier, peakWillingnessMultipleBasisPoints: null,
            massPerUnit: mass, volumePerUnit: volume), name);
        ctx.Goods.Add(good);
        return good;
    }
```

- [ ] **Step 2: Author mass/volume on the demo goods**

Update the 13 `Good_` calls (lines ~55–69) to pass physicals where reality diverges from the size default. Set these explicitly:

```csharp
        var grain = Good_(ctx, w, "Grain", GoodCategory.Raw, 15, "sack", SizeClass.Medium, 0, true,
            mass: new Mass(25_000), volume: new Volume(30_000));
        var ironOre = Good_(ctx, w, "Iron Ore", GoodCategory.Raw, 20, "unit", SizeClass.Medium, 0, false,
            mass: new Mass(20_000), volume: new Volume(12_000));
        var coal = Good_(ctx, w, "Coal", GoodCategory.Raw, 12, "unit", SizeClass.Medium, 0, false,
            mass: new Mass(15_000), volume: new Volume(20_000));
        var wool = Good_(ctx, w, "Wool", GoodCategory.Raw, 25, "bale", SizeClass.Medium, 0, true,
            mass: new Mass(5_000), volume: new Volume(80_000));
        var grapes = Good_(ctx, w, "Grapes", GoodCategory.Raw, 18, "crate", SizeClass.Medium, 4320, true,
            mass: new Mass(10_000), volume: new Volume(25_000));
        var flour = Good_(ctx, w, "Flour", GoodCategory.Material, 40, "sack", SizeClass.Medium, 0, true,
            mass: new Mass(25_000), volume: new Volume(30_000));
        var ironIngot = Good_(ctx, w, "Iron Ingot", GoodCategory.Material, 200, "ingot", SizeClass.Medium, 0, false,
            mass: new Mass(30_000), volume: new Volume(4_000));   // heavy-small → weight-bound
        var bread = Good_(ctx, w, "Bread", GoodCategory.Food, 30, "loaf", SizeClass.Small, 4320, true, 50, NeedTier.Essential,
            mass: new Mass(500), volume: new Volume(2_000));
        var cloth = Good_(ctx, w, "Cloth", GoodCategory.Material, 80, "bolt", SizeClass.Small, 0, true, 10, NeedTier.Standard,
            mass: new Mass(2_000), volume: new Volume(60_000));   // light-bulky → volume-bound
        var tools = Good_(ctx, w, "Tools", GoodCategory.Tool, 150, "set", SizeClass.Medium, 0, false, 5, NeedTier.Standard,
            mass: new Mass(8_000), volume: new Volume(15_000));
        var ale = Good_(ctx, w, "Ale", GoodCategory.Luxury, 40, "mug", SizeClass.Small, 720, true, 20, NeedTier.Comfort,
            mass: new Mass(1_000), volume: new Volume(1_000));
        var wine = Good_(ctx, w, "Wine", GoodCategory.Luxury, 120, "bottle", SizeClass.Small, 0, false, 15, NeedTier.Comfort,
            mass: new Mass(1_200), volume: new Volume(1_000));
        var potion = Good_(ctx, w, "Health Potion", GoodCategory.Potion, 5000, "vial", SizeClass.Small, 0, false,
            mass: new Mass(200), volume: new Volume(200));
```

- [ ] **Step 3: Extend the seed JSON model + importer for goods**

In `src/WorldEcon.Seeding/SeedModel.cs`, replace `SeedGood` (add two optional string fields):

```csharp
public sealed record SeedGood(string Name, string Category, long BaseValue, string BaseUnit, string Size,
    long ShelfLifeTicks, bool Divisible, long ConsumptionPerCapitaBp, string? NeedTier = null,
    string? MassPerUnit = null, string? VolumePerUnit = null);
```

In `src/WorldEcon.Seeding/SeedImporter.cs` `ImportGoods`, parse them (read the method first; insert before the `Good.Create` call):

```csharp
            Mass? mass = g.MassPerUnit is not null && MeasurementFormat.TryParseMass(g.MassPerUnit, out var mm) ? mm : null;
            Volume? volume = g.VolumePerUnit is not null && MeasurementFormat.TryParseVolume(g.VolumePerUnit, out var vv) ? vv : null;
```

and extend the `Good.Create(...)` call's named args with `peakWillingnessMultipleBasisPoints: null, massPerUnit: mass, volumePerUnit: volume` (omitting → size defaults).

- [ ] **Step 4: Author physicals in `aerthos.seed.json`**

Read `samples/aerthos.seed.json`. For at least **Iron Ingot** add `"MassPerUnit": "30 kg", "VolumePerUnit": "4 L"` and **Cloth** add `"MassPerUnit": "2 kg", "VolumePerUnit": "60 L"` (mirroring the demo). Other goods may keep size defaults (omit the fields). For each merchant entry, add `"WeightCapacity": "600 kg", "VolumeCapacity": "1000 L"` (and keep/rename away the old `CargoCapacity` key — it's now ignored, so deleting it is cleanest).

- [ ] **Step 5: Run the Seeding + Tui suites and the import path**

Run the Seeding test project and `...Tui...`:
Expected: `failed: 0` (the fixture still imports; the renamed/added fields parse).

- [ ] **Step 6: Tune `TransportRate` empirically**

Build the CLI and exercise a fresh world; confirm trades still happen and capacity binds differently for dense vs bulky goods:

```bash
rm -f /tmp/we_seed.db*; dotnet run --project src/WorldEcon.Cli -c Release -- new /tmp/we_seed.db
dotnet run --project src/WorldEcon.Cli -c Release -- advance /tmp/we_seed.db 4w
dotnet run --project src/WorldEcon.Cli -c Release -- caravans /tmp/we_seed.db
dotnet run --project src/WorldEcon.Cli -c Release -- ledger /tmp/we_seed.db
```

Expected: caravans are dispatched (trade still profitable) and `ledger` shows a `MerchantHaulage` sink line. If haulage is negligible (sink ~0) and you want visible friction, raise the world's `TransportRate` default in `World.cs` (`DefaultTransportRate`) to a value where haulage is a small but non-zero share of trade value (try 5, re-run). Pick a value, note it in the commit message. Keep merchants profitable (caravans keep flowing).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(seed): author good mass/volume + merchant capacities; tune TransportRate=<value>

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: CLI — surface good mass/volume in `list`

**Files:**
- Modify: `src/WorldEcon.Cli/CommandRunner.cs` (the `list` goods block, ~135–139)

- [ ] **Step 1: Update the goods listing**

Read lines ~135–139 of `src/WorldEcon.Cli/CommandRunner.cs`. Replace the goods loop with one that adds Mass + Volume formatted in the world's unit system (`MeasurementFormat` + `using WorldEcon.SharedKernel.Measure;` already added in Task 5):

```csharp
        var goods = await ctx.Goods.ToListAsync();
        var listUnits = listWorld?.DisplayUnitSystem ?? UnitSystem.Metric;
        Console.WriteLine("Goods:");
        foreach (var g in goods.OrderBy(g => g.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Name} | {g.Category} | baseValue {listCurrency.Format(g.BaseValue)} | {MeasurementFormat.FormatMass(g.MassPerUnit, listUnits)} | {MeasurementFormat.FormatVolume(g.VolumePerUnit, listUnits)}");
        Console.WriteLine();
```

- [ ] **Step 2: Build + eyeball**

Run:
```bash
dotnet build -c Release && dotnet run --project src/WorldEcon.Cli -c Release -- list /tmp/we_seed.db
```
Expected: each good line ends with e.g. `30 kg | 4 L` (Iron Ingot) and `2 kg | 60 L` (Cloth).

- [ ] **Step 3: Commit**

```bash
git add src/WorldEcon.Cli/CommandRunner.cs
git commit -m "feat(cli): show good mass/volume in list

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: TUI — form inputs + unit-system toggle

**Files:**
- Modify: `src/WorldEcon.Tui/TuiContext.cs` (add `DisplayUnits`, `FormatMass/Volume`, `ToggleUnits`)
- Modify: `src/WorldEcon.Tui/Forms/GoodForm.cs` (mass/volume prompts)
- Modify: `src/WorldEcon.Tui/Navigation/Navigator.cs` (swap the temporary formatting from Task 5; bind a `u` key)
- Test: `tests/WorldEcon.Tui.Tests.Unit/FormTests.cs` (append a Good-form physicals test)

- [ ] **Step 1: Add formatting + toggle to `TuiContext`**

In `src/WorldEcon.Tui/TuiContext.cs`, add `using WorldEcon.SharedKernel.Measure;`. Add a mutable display-unit field initialised from the world, plus helpers:

```csharp
    /// <summary>Which unit family the UI presents mass/volume in. Initialised from the world default;
    /// toggled at runtime via <see cref="ToggleUnits"/> (display-only).</summary>
    public UnitSystem DisplayUnits { get; private set; }

    public string FormatMass(Mass mass) => MeasurementFormat.FormatMass(mass, DisplayUnits);
    public string FormatVolume(Volume volume) => MeasurementFormat.FormatVolume(volume, DisplayUnits);

    /// <summary>Flip metric ↔ imperial for display.</summary>
    public void ToggleUnits() =>
        DisplayUnits = DisplayUnits == UnitSystem.Metric ? UnitSystem.Imperial : UnitSystem.Metric;
```

In the private ctor, initialise it:

```csharp
        DisplayUnits = world.DisplayUnitSystem;
```

- [ ] **Step 2: Swap Task-5 temporary formatting in Navigator**

In `src/WorldEcon.Tui/Navigation/Navigator.cs`, replace the two `MeasurementFormat.FormatMass(m.WeightCapacity, ctx.World.DisplayUnitSystem)` / `FormatVolume(...)` calls added in Task 5 (row + detail) with `ctx.FormatMass(m.WeightCapacity)` / `ctx.FormatVolume(m.VolumeCapacity)` so the runtime toggle takes effect.

- [ ] **Step 3: Bind a `u` toggle key**

In `Navigator.cs`, find the top-level key handler (where single-key commands like `l`, `/`, `d` are dispatched — search for the existing key switch). Add a case for `u` that calls `ctx.ToggleUnits()` and refreshes the current view (re-render — follow the same refresh call the other view-mutating keys use). Read the surrounding handler to match its exact refresh idiom; add:

```csharp
            // 'u' flips metric/imperial for mass/volume display (session-only).
            case 'u':
                ctx.ToggleUnits();
                // <refresh current frame exactly as the other view keys do>
                return true; // or the handler's "handled" convention
```

- [ ] **Step 4: Add mass/volume prompts to `GoodForm`**

In `src/WorldEcon.Tui/Forms/GoodForm.cs`, add `using WorldEcon.SharedKernel.Measure;`. After the `needTier` prompt and before `Good.Create`, add optional physical prompts (blank = use size default):

```csharp
        var massText = await FormPrompts.OptionalTextAsync(ui, t, "Mass per unit (e.g. 5 kg; blank = size default):");
        Mass? mass = null;
        if (!string.IsNullOrWhiteSpace(massText))
        {
            if (!MeasurementFormat.TryParseMass(massText, out var parsed))
                return FormOutcome.Fail("Could not parse mass (try e.g. '5 kg').");
            mass = parsed;
        }

        var volumeText = await FormPrompts.OptionalTextAsync(ui, t, "Volume per unit (e.g. 4 L; blank = size default):");
        Volume? volume = null;
        if (!string.IsNullOrWhiteSpace(volumeText))
        {
            if (!MeasurementFormat.TryParseVolume(volumeText, out var parsed))
                return FormOutcome.Fail("Could not parse volume (try e.g. '4 L').");
            volume = parsed;
        }
```

Update the `Good.Create(...)` call to pass them:

```csharp
        var result = Good.Create(ctx.World.Id, name, category.Value, new Money(baseValue.Value), unit,
            size.Value, shelfLife.Value, divisible.Value, consumption.Value, needTier.Value,
            peakWillingnessMultipleBasisPoints: null, massPerUnit: mass, volumePerUnit: volume);
```

> **Check `FormPrompts` for an optional-text method.** Read `src/WorldEcon.Tui/Forms/FormPrompts.cs`. If there is no `OptionalTextAsync` (one that returns empty/null for a blank entry rather than treating blank as cancel), add one modelled on `RequiredTextAsync` that returns `""`/null on blank instead of cancelling. Implement it in the same file with the same UI plumbing the other prompts use.

- [ ] **Step 5: Append a Tui test**

Read `tests/WorldEcon.Tui.Tests.Unit/FormTests.cs` to learn its harness (how it drives `IUserInteraction` with scripted answers and asserts on the DB). Add a test that drives `GoodForm` with a mass answer `"5 kg"` and volume `"4 L"` and asserts the saved good has `MassPerUnit.Grams == 5000` and `VolumePerUnit.CubicCentimeters == 4000`. Mirror the existing Good-form test exactly, adding the two new scripted answers in order.

- [ ] **Step 6: Build + run Tui tests**

Run: `dotnet build -c Release && dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release`
Expected: `0 Warning(s) 0 Error(s)`, `failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(tui): Good-form mass/volume input + metric/imperial toggle (u)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 12: Full validation, performance, docs

**Files:**
- Modify: `docs/capabilities-and-roadmap.md`

- [ ] **Step 1: Full build + all suites**

Run: `dotnet build -c Release` then each test project (`Domain`, `Simulation`, `Engine`, `Tui`, and any Seeding suite).
Expected: `0 Warning(s) 0 Error(s)`; every suite `failed: 0`.

- [ ] **Step 2: 1-year performance check**

Run:
```bash
rm -f /tmp/we_perf.db*; dotnet run --project src/WorldEcon.Cli -c Release -- new /tmp/we_perf.db >/dev/null
start=$(date +%s%N); dotnet run --project src/WorldEcon.Cli -c Release -- advance /tmp/we_perf.db 1y; end=$(date +%s%N); echo "1y advance: $(( (end-start)/1000000 )) ms"
```
Expected: well under 300000 ms (5 min). (Baseline before this work was ~72s; haulage adds negligible cost.)

- [ ] **Step 3: Live behaviour check**

Run:
```bash
dotnet run --project src/WorldEcon.Cli -c Release -- list /tmp/we_perf.db          # mass/volume columns
dotnet run --project src/WorldEcon.Cli -c Release -- merchants /tmp/we_perf.db      # weight/volume caps
dotnet run --project src/WorldEcon.Cli -c Release -- caravans /tmp/we_perf.db       # trades still flow
dotnet run --project src/WorldEcon.Cli -c Release -- ledger /tmp/we_perf.db         # MerchantHaulage sink present
```
Expected: mass/volume shown; caravans dispatched; `MerchantHaulage` appears as a sink in the ledger. Clean up: `rm -f /tmp/we_perf.db* /tmp/we_seed.db* /tmp/we_mig.db*`.

- [ ] **Step 4: Update the capabilities doc**

In `docs/capabilities-and-roadmap.md`, in the "Recommended foundational build ordering" list, change item 3 from `← next` to done for Layer A, e.g.:

```markdown
3. ~~Weight/dim-weight + transport modes (makes friction real).~~ **Layer A done** (weight/volume + dimensional capacity + real MerchantHaulage sink, validated <date>); **Layer B next** (transport modes, vehicle assets, loss risk, location events, risk-aware dispatch).
```

Add a ✅ capability bullet under Trade & logistics:

```markdown
- ✅ **Physical goods + dimensional haulage (Transport Layer A).** Goods carry mass (g) + volume (cm³); caravan capacity is limited by both (dense goods weight-bound, bulky-light goods volume-bound); merchants pay a real `MerchantHaulage` sink = dimensional weight × distance × rate. Mass/volume presented in metric/imperial (`World.DisplayUnitSystem` + TUI `u` toggle). Validated live <date>.
```

- [ ] **Step 5: Commit**

```bash
git add docs/capabilities-and-roadmap.md
git commit -m "docs: mark Transport Layer A complete (capabilities + roadmap)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 6: Finish the branch**

Use the **superpowers:finishing-a-development-branch** skill to merge `feat/transport-physical` → `master`, run tests on the merged result, push, and clean up.

---

## Self-review (against the spec)

- **Spec §1 Mass/Volume value types + units + format/parse + toggle** → Tasks 1, 2 (types + format/parse), Task 4 (`World.DisplayUnitSystem`), Task 11 (TUI toggle). ✅
- **Spec §2 Good mass/volume + size defaults + override + setters** → Task 3. ✅
- **Spec §3 Merchant weight/volume capacity** → Task 5. ✅
- **Spec §4 MerchantHaulage sink + World tuning + formulas + TradePhase gating/affordability/dispatch** → Task 6 (channel), Task 4 (tuning), Task 8 (Haulage/CargoFit + TradePhase). ✅
- **Spec §5 migration + value converters + DisplayUnitSystem mapping** → Task 7. ✅
- **Spec §6 surfacing (CLI list/merchants, TUI forms, toggle, ledger)** → Tasks 5 (merchants CLI), 10 (good list), 11 (TUI). Ledger shows MerchantHaulage automatically (Task 6). ✅
- **Spec "Seed updates" (both paths, tune rate)** → Task 9. ✅
- **Spec "Testing"** → domain (1,2,3,4,5,6), engine (8), tui (11); conservation/granularity covered by existing suites re-run in 7/8/12. ✅
- **Spec boundary (flat speed; single-good; no modes/vehicles/risk)** → unchanged in plan; Task 8 comment updated to point at Layer B. ✅

**Type consistency check:** `Mass(long Grams)`, `Volume(long CubicCentimeters)`, `MeasurementFormat.FormatMass/FormatVolume/TryParseMass/TryParseVolume`, `Good.MassPerUnit/VolumePerUnit` + `DefaultMassForSize/DefaultVolumeForSize` + `SetMassPerUnit/SetVolumePerUnit`, `RepresentativeMerchant.WeightCapacity/VolumeCapacity` + `Create(..., Mass, Volume, long)` + `SetCapacity`, `World.VolumetricDivisor/TransportRate/DisplayUnitSystem` + `SetTransportTuning/SetDisplayUnitSystem`, `MoneyChannel.MerchantHaulage`, `Haulage.DimensionalWeightGrams/Cost`, `CargoFit.MaxUnits`, `TuiContext.DisplayUnits/FormatMass/FormatVolume/ToggleUnits` — names used identically across all tasks. ✅

**Known cross-task dependency:** Task 5 introduces a *temporary* `MeasurementFormat.Format*(…, ctx.World.DisplayUnitSystem)` call in Navigator that Task 11 swaps for `ctx.FormatMass/FormatVolume`. Flagged in both tasks.
