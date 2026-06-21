# Foundation Core Implementation Plan (Plan 1a)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the WorldEcon solution and the pure, integer-only determinism + calendar primitives that every later stage depends on.

**Architecture:** Two UI-agnostic, EF-free class libraries — `WorldEcon.SharedKernel` (value types: `Money`, `FixedMath`, `Tick`, calendar data records) and `WorldEcon.Simulation` (behavioral primitives: `CalendarSystem`, deterministic RNG streams). All math is integer/fixed-point; randomness flows from an in-house, version-pinned PRNG with per-subsystem streams. Everything here is exhaustively unit-tested because determinism is the backbone of snapshots, branch/compare, and pins (see spec Build §4).

**Tech Stack:** C# / .NET 10, file-scoped namespaces, nullable enabled, central package management. Tests: TUnit + FluentAssertions. Reproduction key for the whole sim is `(worldSeed, rulesetVersion)`.

**Reference spec:** `docs/superpowers/specs/2026-06-20-world-economy-sim-csharp-build-design.md` (Build §2 conventions, §4 determinism, §5 time/calendar).

**Out of scope for 1a (later plans):** strongly-typed IDs + entities + EF (Plan 1b), `ITickScheduler`/tick loop (Plan 3), `FixedMath.PowBp` and the pricing formula (Plan 2 — fractional powers need fixed-point exp/ln and get proper treatment there).

---

## File structure

| File | Responsibility |
|---|---|
| `Directory.Build.props` | Shared TFM / nullable / langversion / warnings-as-errors |
| `Directory.Packages.props` | Central NuGet versions |
| `.gitignore` | Standard .NET ignores |
| `WorldEcon.sln` | Solution |
| `src/WorldEcon.SharedKernel/Money.cs` | Fixed-point integer money value type |
| `src/WorldEcon.SharedKernel/FixedMath.cs` | Deterministic mul/div/rounding + floor helpers (basis points) |
| `src/WorldEcon.SharedKernel/Tick.cs` | Integer in-world time value type (minutes since epoch) |
| `src/WorldEcon.SharedKernel/Calendar/CalendarDate.cs` | `(Year,Month,Day,Hour,Minute)` value |
| `src/WorldEcon.SharedKernel/Calendar/CalendarDefinition.cs` | Data-driven calendar config + `Default` (12×30) |
| `src/WorldEcon.Simulation/Time/CalendarSystem.cs` | Tick ↔ date conversion, weekday, season |
| `src/WorldEcon.Simulation/Random/IRng.cs` | RNG abstraction + `RngState` |
| `src/WorldEcon.Simulation/Random/Xoshiro256StarStar.cs` | PRNG impl + `SplitMix64` seeder |
| `src/WorldEcon.Simulation/Random/RngStreams.cs` | Per-subsystem stream manager |
| `tests/WorldEcon.SharedKernel.Tests.Unit/*` | Money, FixedMath, Tick, calendar-data tests |
| `tests/WorldEcon.Simulation.Tests.Unit/*` | CalendarSystem, RNG tests |

---

## Task 0: Solution & build scaffolding

**Files:**
- Create: `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`, `WorldEcon.sln`
- Create: the four project `.csproj` files + one smoke test

- [ ] **Step 1: Initialize git and the solution**

Run from the repo root (`/home/kayden/workspaces/dnd`):
```bash
git init
dotnet --version   # expect 10.x
dotnet new sln -n WorldEcon
```
Expected: `WorldEcon.sln` created.

- [ ] **Step 2: Create `.gitignore`**

Create `.gitignore`:
```gitignore
bin/
obj/
*.user
.vs/
.idea/
*.db
*.db-wal
*.db-shm
[Tt]est[Rr]esults/
artifacts/
```

- [ ] **Step 3: Create `Directory.Build.props`**

Create `Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Create `Directory.Packages.props`**

Create `Directory.Packages.props`. (FluentAssertions pinned to 7.2.0 — the last MIT-licensed version; v8+ requires a paid commercial license. TUnit pinned to the 1.x line used in the reference backend.)
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="TUnit" Version="1.32.0" />
    <PackageVersion Include="FluentAssertions" Version="7.2.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create the two source projects**

Create `src/WorldEcon.SharedKernel/WorldEcon.SharedKernel.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>WorldEcon.SharedKernel</RootNamespace>
  </PropertyGroup>
</Project>
```

Create `src/WorldEcon.Simulation/WorldEcon.Simulation.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>WorldEcon.Simulation</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\WorldEcon.SharedKernel\WorldEcon.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Create the two test projects**

Create `tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\WorldEcon.SharedKernel\WorldEcon.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

Create `tests/WorldEcon.Simulation.Tests.Unit/WorldEcon.Simulation.Tests.Unit.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\WorldEcon.Simulation\WorldEcon.Simulation.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 7: Add all projects to the solution**

Run:
```bash
dotnet sln add src/WorldEcon.SharedKernel/WorldEcon.SharedKernel.csproj
dotnet sln add src/WorldEcon.Simulation/WorldEcon.Simulation.csproj
dotnet sln add tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj
dotnet sln add tests/WorldEcon.Simulation.Tests.Unit/WorldEcon.Simulation.Tests.Unit.csproj
```

- [ ] **Step 8: Add a smoke test to prove the toolchain runs**

Create `tests/WorldEcon.SharedKernel.Tests.Unit/SmokeTest.cs`:
```csharp
using FluentAssertions;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class SmokeTest
{
    [Test]
    public void Toolchain_Runs()
    {
        (1 + 1).Should().Be(2);
    }
}
```

- [ ] **Step 9: Build and run the smoke test**

Run:
```bash
dotnet build
dotnet test tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj
```
Expected: build succeeds; 1 test passes. (If `dotnet test` does not discover TUnit tests, run `dotnet run --project tests/WorldEcon.SharedKernel.Tests.Unit` instead — both are valid for the Microsoft.Testing.Platform that TUnit uses.)

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "chore: scaffold WorldEcon solution, build config, and test toolchain"
```

---

## Task 1: `Money` value type

**Files:**
- Create: `src/WorldEcon.SharedKernel/Money.cs`
- Test: `tests/WorldEcon.SharedKernel.Tests.Unit/MoneyTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.SharedKernel.Tests.Unit/MoneyTests.cs`:
```csharp
using FluentAssertions;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class MoneyTests
{
    [Test]
    public void Zero_IsZeroUnits()
        => Money.Zero.Units.Should().Be(0);

    [Test]
    public void Add_SumsUnits()
        => (new Money(150) + new Money(25)).Units.Should().Be(175);

    [Test]
    public void Subtract_DiffsUnits()
        => (new Money(150) - new Money(25)).Units.Should().Be(125);

    [Test]
    public void MultiplyByQuantity_ScalesUnits()
        => (new Money(40) * 3L).Units.Should().Be(120);

    [Test]
    public void Negate_FlipsSign()
        => (-new Money(40)).Units.Should().Be(-40);

    [Test]
    public void IsNegative_TrueForBelowZero()
        => new Money(-1).IsNegative.Should().BeTrue();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj`
Expected: FAIL — `Money` does not exist (compile error).

- [ ] **Step 3: Implement `Money`**

Create `src/WorldEcon.SharedKernel/Money.cs`:
```csharp
namespace WorldEcon.SharedKernel;

/// <summary>
/// Money as an integer count of the smallest currency unit (e.g. copper).
/// Denominations (gp/sp/cp) are a presentation concern and never used in sim math.
/// </summary>
public readonly record struct Money(long Units)
{
    public static readonly Money Zero = new(0);

    public bool IsNegative => Units < 0;

    public static Money operator +(Money a, Money b) => new(a.Units + b.Units);
    public static Money operator -(Money a, Money b) => new(a.Units - b.Units);
    public static Money operator *(Money a, long quantity) => new(a.Units * quantity);
    public static Money operator -(Money a) => new(-a.Units);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj`
Expected: PASS (all Money tests).

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.SharedKernel/Money.cs tests/WorldEcon.SharedKernel.Tests.Unit/MoneyTests.cs
git commit -m "feat: add fixed-point Money value type"
```

---

## Task 2: `FixedMath` deterministic arithmetic

**Files:**
- Create: `src/WorldEcon.SharedKernel/FixedMath.cs`
- Test: `tests/WorldEcon.SharedKernel.Tests.Unit/FixedMathTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.SharedKernel.Tests.Unit/FixedMathTests.cs`:
```csharp
using FluentAssertions;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class FixedMathTests
{
    [Test]
    public void BpScale_Is10000() => FixedMath.BpScale.Should().Be(10_000);

    [Test]
    public void MulBp_AppliesPercentage()
        => FixedMath.MulBp(1000, 2500).Should().Be(250); // 25% of 1000

    [Test]
    public void MulBp_RoundsHalfToEven_Down()
        => FixedMath.MulBp(5, 5000).Should().Be(2); // 2.5 -> 2 (even)

    [Test]
    public void MulBp_RoundsHalfToEven_Up()
        => FixedMath.MulBp(3, 5000).Should().Be(2); // 1.5 -> 2 (even)

    [Test]
    public void MulDiv_UsesWideIntermediate_NoOverflow()
        => FixedMath.MulDiv(1_000_000_000L, 1_000_000_000L, 1_000_000_000L)
            .Should().Be(1_000_000_000L);

    [Test]
    public void DivFloor_RoundsTowardNegativeInfinity()
    {
        FixedMath.DivFloor(7, 2).Should().Be(3);
        FixedMath.DivFloor(-7, 2).Should().Be(-4);
    }

    [Test]
    public void DivRound_RoundsHalfToEven()
    {
        FixedMath.DivRound(5, 2).Should().Be(2);  // 2.5 -> 2
        FixedMath.DivRound(7, 2).Should().Be(4);  // 3.5 -> 4
        FixedMath.DivRound(8, 2).Should().Be(4);  // exact
    }

    [Test]
    public void FloorMod_IsAlwaysNonNegativeForPositiveModulus()
    {
        FixedMath.FloorMod(7, 3).Should().Be(1);
        FixedMath.FloorMod(-1, 3).Should().Be(2);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj`
Expected: FAIL — `FixedMath` does not exist.

- [ ] **Step 3: Implement `FixedMath`**

Create `src/WorldEcon.SharedKernel/FixedMath.cs`:
```csharp
namespace WorldEcon.SharedKernel;

/// <summary>
/// Deterministic integer / fixed-point arithmetic. All rounding is half-to-even
/// so price/cost computations are reproducible and auditable (spec Build §4.4).
/// Fractions are expressed in basis points (1 bp = 1/10000).
/// </summary>
public static class FixedMath
{
    public const long BpScale = 10_000;

    /// <summary>value * (bp / 10000), half-to-even.</summary>
    public static long MulBp(long value, long bp) => MulDiv(value, bp, BpScale);

    /// <summary>(a * b) / denominator using a 128-bit intermediate; half-to-even.</summary>
    public static long MulDiv(long a, long b, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        Int128 product = (Int128)a * b;
        Int128 q = product / denominator;
        Int128 r = product - q * denominator;
        if (r == 0) return (long)q;

        Int128 twiceR = Int128.Abs(r) * 2;
        Int128 absD = Int128.Abs((Int128)denominator);
        bool roundAway = twiceR > absD || (twiceR == absD && ((long)(q % 2)) != 0);
        if (roundAway)
        {
            bool negative = (a < 0) ^ (b < 0) ^ (denominator < 0);
            q += negative ? -1 : 1;
        }
        return (long)q;
    }

    /// <summary>Division rounding toward negative infinity.</summary>
    public static long DivFloor(long numerator, long denominator)
    {
        long q = numerator / denominator;
        long r = numerator % denominator;
        if (r != 0 && ((r < 0) != (denominator < 0))) q--;
        return q;
    }

    /// <summary>Division rounding half-to-even.</summary>
    public static long DivRound(long numerator, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        long q = numerator / denominator;
        long r = numerator - q * denominator;
        if (r == 0) return q;

        long twiceR = Math.Abs(r) * 2;
        long absD = Math.Abs(denominator);
        bool roundAway = twiceR > absD || (twiceR == absD && (q % 2) != 0);
        if (roundAway)
        {
            bool negative = (numerator < 0) ^ (denominator < 0);
            q += negative ? -1 : 1;
        }
        return q;
    }

    /// <summary>Modulo that is always in [0, modulus) for positive modulus.</summary>
    public static long FloorMod(long a, long modulus) => ((a % modulus) + modulus) % modulus;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.SharedKernel/FixedMath.cs tests/WorldEcon.SharedKernel.Tests.Unit/FixedMathTests.cs
git commit -m "feat: add deterministic FixedMath (mul/div, half-to-even, floor helpers)"
```

---

## Task 3: `Tick` value type

**Files:**
- Create: `src/WorldEcon.SharedKernel/Tick.cs`
- Test: `tests/WorldEcon.SharedKernel.Tests.Unit/TickTests.cs`

> Note: a day is **not** a fixed multiple of minutes globally — day length comes from the `CalendarDefinition` (default 1,440). `Tick` therefore only knows minutes; day/week math lives in `CalendarSystem` (Task 5). The `MinutesPer*` constants here are convenience defaults for the standard 24h/60m calendar only.

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.SharedKernel.Tests.Unit/TickTests.cs`:
```csharp
using FluentAssertions;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class TickTests
{
    [Test]
    public void Zero_IsZero() => Tick.Zero.Value.Should().Be(0);

    [Test]
    public void DefaultConstants_MatchStandardCalendar()
    {
        Tick.MinutesPerHour.Should().Be(60);
        Tick.DefaultMinutesPerDay.Should().Be(1_440);
        Tick.DefaultMinutesPerWeek.Should().Be(10_080);
    }

    [Test]
    public void AddMinutes_AdvancesValue()
        => new Tick(100).AddMinutes(10).Value.Should().Be(110);

    [Test]
    public void Comparisons_OrderByValue()
    {
        (new Tick(5) < new Tick(6)).Should().BeTrue();
        (new Tick(6) > new Tick(5)).Should().BeTrue();
        (new Tick(5) <= new Tick(5)).Should().BeTrue();
        (new Tick(5) >= new Tick(5)).Should().BeTrue();
        new Tick(5).CompareTo(new Tick(7)).Should().BeNegative();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj`
Expected: FAIL — `Tick` does not exist.

- [ ] **Step 3: Implement `Tick`**

Create `src/WorldEcon.SharedKernel/Tick.cs`:
```csharp
namespace WorldEcon.SharedKernel;

/// <summary>
/// In-world time as an integer count of minutes since the world epoch (spec Build §5.1).
/// Day/week length is calendar-defined; only minute arithmetic lives here.
/// </summary>
public readonly record struct Tick(long Value) : IComparable<Tick>
{
    public const long MinutesPerHour = 60;
    public const long DefaultMinutesPerDay = 1_440;   // 24h * 60m (standard calendar only)
    public const long DefaultMinutesPerWeek = 10_080; // 7 * 1440 (standard calendar only)

    public static readonly Tick Zero = new(0);

    public Tick AddMinutes(long minutes) => new(Value + minutes);

    public int CompareTo(Tick other) => Value.CompareTo(other.Value);

    public static bool operator <(Tick a, Tick b) => a.Value < b.Value;
    public static bool operator >(Tick a, Tick b) => a.Value > b.Value;
    public static bool operator <=(Tick a, Tick b) => a.Value <= b.Value;
    public static bool operator >=(Tick a, Tick b) => a.Value >= b.Value;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.SharedKernel/Tick.cs tests/WorldEcon.SharedKernel.Tests.Unit/TickTests.cs
git commit -m "feat: add Tick (integer in-world minutes) value type"
```

---

## Task 4: Calendar data records + `CalendarDefinition.Default`

**Files:**
- Create: `src/WorldEcon.SharedKernel/Calendar/CalendarDate.cs`
- Create: `src/WorldEcon.SharedKernel/Calendar/CalendarDefinition.cs`
- Test: `tests/WorldEcon.SharedKernel.Tests.Unit/CalendarDefinitionTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.SharedKernel.Tests.Unit/CalendarDefinitionTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class CalendarDefinitionTests
{
    [Test]
    public void Default_Has12MonthsOf30Days()
    {
        var def = CalendarDefinition.Default;
        def.Months.Should().HaveCount(12);
        def.Months.Should().OnlyContain(m => m.Days == 30);
    }

    [Test]
    public void Default_Has7Weekdays_24Hours_60Minutes()
    {
        var def = CalendarDefinition.Default;
        def.Weekdays.Should().HaveCount(7);
        def.HoursPerDay.Should().Be(24);
        def.MinutesPerHour.Should().Be(60);
    }

    [Test]
    public void Default_EpochIsYear1Month1Day1()
        => CalendarDefinition.Default.Epoch.Should().Be(new CalendarDate(1, 1, 1, 0, 0));

    [Test]
    public void Default_HasNoLeapRule()
        => CalendarDefinition.Default.LeapRule.Should().Be(LeapRule.None);

    [Test]
    public void Default_HasFourSeasonsCoveringAllMonths()
        => CalendarDefinition.Default.Seasons.Should().HaveCount(4);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj`
Expected: FAIL — calendar types do not exist.

- [ ] **Step 3: Implement the calendar data types**

Create `src/WorldEcon.SharedKernel/Calendar/CalendarDate.cs`:
```csharp
namespace WorldEcon.SharedKernel.Calendar;

/// <summary>A point on the in-world calendar. All fields are 1-based except Hour/Minute (0-based).</summary>
public readonly record struct CalendarDate(int Year, int Month, int Day, int Hour, int Minute);
```

Create `src/WorldEcon.SharedKernel/Calendar/CalendarDefinition.cs`:
```csharp
namespace WorldEcon.SharedKernel.Calendar;

public sealed record MonthDef(string Name, int Days);

public sealed record SeasonDef(string Name, int StartMonth, int StartDay, int EndMonth, int EndDay);

public enum LeapRule { None }

/// <summary>
/// Data-driven calendar configuration (spec Build §5.2). Gregorian is expressible by
/// supplying real month lengths + a leap rule; the default below is this world's 12×30 calendar.
/// </summary>
public sealed record CalendarDefinition(
    int MinutesPerHour,
    int HoursPerDay,
    IReadOnlyList<MonthDef> Months,
    IReadOnlyList<string> Weekdays,
    CalendarDate Epoch,
    string EraLabel,
    LeapRule LeapRule,
    IReadOnlyList<SeasonDef> Seasons)
{
    /// <summary>This world's calendar: 12 months × 30 days (360-day year), 7-day week, placeholder names.</summary>
    public static CalendarDefinition Default { get; } = CreateDefault();

    private static CalendarDefinition CreateDefault()
    {
        var months = Enumerable.Range(1, 12)
            .Select(i => new MonthDef($"Month {i}", 30))
            .ToArray();

        var weekdays = Enumerable.Range(1, 7)
            .Select(i => $"Day {i}")
            .ToArray();

        var seasons = new[]
        {
            new SeasonDef("Spring", 1, 1, 3, 30),
            new SeasonDef("Summer", 4, 1, 6, 30),
            new SeasonDef("Autumn", 7, 1, 9, 30),
            new SeasonDef("Winter", 10, 1, 12, 30),
        };

        return new CalendarDefinition(
            MinutesPerHour: 60,
            HoursPerDay: 24,
            Months: months,
            Weekdays: weekdays,
            Epoch: new CalendarDate(1, 1, 1, 0, 0),
            EraLabel: "",
            LeapRule: LeapRule.None,
            Seasons: seasons);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/WorldEcon.SharedKernel.Tests.Unit/WorldEcon.SharedKernel.Tests.Unit.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.SharedKernel/Calendar tests/WorldEcon.SharedKernel.Tests.Unit/CalendarDefinitionTests.cs
git commit -m "feat: add data-driven calendar definition with 12x30 default"
```

---

## Task 5: `CalendarSystem` (tick ↔ date, weekday, season)

**Files:**
- Create: `src/WorldEcon.Simulation/Time/CalendarSystem.cs`
- Test: `tests/WorldEcon.Simulation.Tests.Unit/CalendarSystemTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Simulation.Tests.Unit/CalendarSystemTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Simulation.Time;

namespace WorldEcon.Simulation.Tests.Unit;

public class CalendarSystemTests
{
    private static readonly CalendarSystem Sut = new(CalendarDefinition.Default);

    [Test]
    public void Epoch_MapsToTickZero()
        => Sut.ToTick(new CalendarDate(1, 1, 1, 0, 0)).Should().Be(Tick.Zero);

    [Test]
    public void ToDate_TickZero_IsEpoch()
        => Sut.ToDate(Tick.Zero).Should().Be(new CalendarDate(1, 1, 1, 0, 0));

    [Test]
    public void OneDay_Is1440Ticks_AndAdvancesTheDay()
        => Sut.ToDate(new Tick(1_440)).Should().Be(new CalendarDate(1, 1, 2, 0, 0));

    [Test]
    public void MonthRollsOverAfter30Days()
        => Sut.ToDate(new Tick(30 * 1_440)).Should().Be(new CalendarDate(1, 2, 1, 0, 0));

    [Test]
    public void YearRollsOverAfter360Days()
        => Sut.ToDate(new Tick(360 * 1_440)).Should().Be(new CalendarDate(2, 1, 1, 0, 0));

    [Test]
    public void IntradayMinutesAndHours()
        => Sut.ToDate(new Tick(90)).Should().Be(new CalendarDate(1, 1, 1, 1, 30)); // 90 min = 01:30

    [Test]
    public void RoundTrip_HoldsAcrossMultiYearRange()
    {
        for (long t = 0; t < 360 * 1_440 * 3; t += 777) // ~3 years, irregular stride
        {
            var tick = new Tick(t);
            Sut.ToTick(Sut.ToDate(tick)).Should().Be(tick);
        }
    }

    [Test]
    public void Weekday_CyclesEverySevenDays()
    {
        Sut.WeekdayIndex(Tick.Zero).Should().Be(0);
        Sut.WeekdayIndex(new Tick(1_440)).Should().Be(1);
        Sut.WeekdayIndex(new Tick(7 * 1_440)).Should().Be(0);
    }

    [Test]
    public void SeasonAt_MapsMonthToSeason()
    {
        Sut.SeasonAt(Sut.ToTick(new CalendarDate(1, 1, 15, 0, 0))).Name.Should().Be("Spring");
        Sut.SeasonAt(Sut.ToTick(new CalendarDate(1, 5, 15, 0, 0))).Name.Should().Be("Summer");
        Sut.SeasonAt(Sut.ToTick(new CalendarDate(1, 11, 15, 0, 0))).Name.Should().Be("Winter");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/WorldEcon.Simulation.Tests.Unit/WorldEcon.Simulation.Tests.Unit.csproj`
Expected: FAIL — `CalendarSystem` does not exist.

- [ ] **Step 3: Implement `CalendarSystem`**

Create `src/WorldEcon.Simulation/Time/CalendarSystem.cs`:
```csharp
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Simulation.Time;

/// <summary>
/// Deterministic, integer-only converter between <see cref="Tick"/> and <see cref="CalendarDate"/>
/// for a given <see cref="CalendarDefinition"/> (spec Build §5.3). No NodaTime / DateTime / float.
/// Assumes uniform years (LeapRule.None); other leap rules extend the year-length logic.
/// </summary>
public sealed class CalendarSystem
{
    private readonly CalendarDefinition _def;
    private readonly long _minutesPerDay;
    private readonly int _daysPerYear;
    private readonly int[] _monthStartDay; // 0-based cumulative day offset at the start of each month
    private readonly long _epochAbsMinute;
    private readonly long _epochAbsDay;

    public CalendarSystem(CalendarDefinition def)
    {
        if (def.LeapRule != LeapRule.None)
            throw new NotSupportedException("Only LeapRule.None is supported in Plan 1a.");

        _def = def;
        _minutesPerDay = (long)def.HoursPerDay * def.MinutesPerHour;

        _monthStartDay = new int[def.Months.Count];
        int acc = 0;
        for (int i = 0; i < def.Months.Count; i++)
        {
            _monthStartDay[i] = acc;
            acc += def.Months[i].Days;
        }
        _daysPerYear = acc;

        _epochAbsMinute = AbsMinute(def.Epoch);
        _epochAbsDay = FixedMath.DivFloor(_epochAbsMinute, _minutesPerDay);
    }

    public Tick ToTick(CalendarDate date) => new(AbsMinute(date) - _epochAbsMinute);

    public CalendarDate ToDate(Tick tick)
    {
        long absMinute = tick.Value + _epochAbsMinute;
        long day = FixedMath.DivFloor(absMinute, _minutesPerDay);
        long minuteOfDay = absMinute - day * _minutesPerDay;
        int hour = (int)(minuteOfDay / _def.MinutesPerHour);
        int minute = (int)(minuteOfDay % _def.MinutesPerHour);

        long year = FixedMath.DivFloor(day, _daysPerYear);
        int dayOfYear = (int)(day - year * _daysPerYear);

        int month = _monthStartDay.Length; // default last month; corrected below
        for (int i = 0; i < _monthStartDay.Length; i++)
        {
            int start = _monthStartDay[i];
            int end = start + _def.Months[i].Days;
            if (dayOfYear >= start && dayOfYear < end)
            {
                month = i + 1;
                break;
            }
        }
        int dayOfMonth = dayOfYear - _monthStartDay[month - 1] + 1;

        return new CalendarDate((int)year, month, dayOfMonth, hour, minute);
    }

    public int WeekdayIndex(Tick tick)
    {
        long absMinute = tick.Value + _epochAbsMinute;
        long day = FixedMath.DivFloor(absMinute, _minutesPerDay);
        return (int)FixedMath.FloorMod(day - _epochAbsDay, _def.Weekdays.Count);
    }

    public SeasonDef SeasonAt(Tick tick)
    {
        if (_def.Seasons.Count == 0)
            throw new InvalidOperationException("Calendar defines no seasons.");

        CalendarDate d = ToDate(tick);
        foreach (SeasonDef s in _def.Seasons)
            if (InSeason(d.Month, d.Day, s))
                return s;

        return _def.Seasons[0];
    }

    private long AbsMinute(CalendarDate d)
        => AbsDay(d.Year, d.Month, d.Day) * _minutesPerDay
           + (long)d.Hour * _def.MinutesPerHour
           + d.Minute;

    private long AbsDay(int year, int month, int day)
        => (long)year * _daysPerYear + _monthStartDay[month - 1] + (day - 1);

    private static bool InSeason(int month, int day, SeasonDef s)
    {
        int v = month * 100 + day;
        int start = s.StartMonth * 100 + s.StartDay;
        int end = s.EndMonth * 100 + s.EndDay;
        return start <= end ? (v >= start && v <= end) : (v >= start || v <= end);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/WorldEcon.Simulation.Tests.Unit/WorldEcon.Simulation.Tests.Unit.csproj`
Expected: PASS (all CalendarSystem tests, including the multi-year round-trip).

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Simulation/Time/CalendarSystem.cs tests/WorldEcon.Simulation.Tests.Unit/CalendarSystemTests.cs
git commit -m "feat: add deterministic CalendarSystem (tick<->date, weekday, season)"
```

---

## Task 6: Deterministic PRNG (`Xoshiro256StarStar` + `SplitMix64`)

**Files:**
- Create: `src/WorldEcon.Simulation/Random/IRng.cs`
- Create: `src/WorldEcon.Simulation/Random/Xoshiro256StarStar.cs`
- Test: `tests/WorldEcon.Simulation.Tests.Unit/RngTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Simulation.Tests.Unit/RngTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Simulation.Random;

namespace WorldEcon.Simulation.Tests.Unit;

public class RngTests
{
    [Test]
    public void SameSeed_ProducesIdenticalSequence()
    {
        var a = new Xoshiro256StarStar(42UL);
        var b = new Xoshiro256StarStar(42UL);
        var seqA = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var seqB = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        seqA.Should().Equal(seqB);
    }

    [Test]
    public void DifferentSeeds_DivergeQuickly()
    {
        var a = new Xoshiro256StarStar(1UL);
        var b = new Xoshiro256StarStar(2UL);
        a.NextULong().Should().NotBe(b.NextULong());
    }

    [Test]
    public void CaptureAndRestore_ResumesSameSequence()
    {
        var rng = new Xoshiro256StarStar(7UL);
        rng.NextULong(); // advance a bit
        var state = rng.Capture();
        var expected = Enumerable.Range(0, 8).Select(_ => rng.NextULong()).ToArray();

        var restored = new Xoshiro256StarStar(state);
        var actual = Enumerable.Range(0, 8).Select(_ => restored.NextULong()).ToArray();
        actual.Should().Equal(expected);
    }

    [Test]
    public void NextInt_StaysInRange()
    {
        var rng = new Xoshiro256StarStar(123UL);
        for (int i = 0; i < 10_000; i++)
            rng.NextInt(6).Should().BeInRange(0, 5);
    }

    [Test]
    public void NextInt_Throws_OnNonPositiveBound()
    {
        var rng = new Xoshiro256StarStar(1UL);
        var act = () => rng.NextInt(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/WorldEcon.Simulation.Tests.Unit/WorldEcon.Simulation.Tests.Unit.csproj`
Expected: FAIL — RNG types do not exist.

- [ ] **Step 3: Implement the RNG**

Create `src/WorldEcon.Simulation/Random/IRng.cs`:
```csharp
namespace WorldEcon.Simulation.Random;

/// <summary>Persistable snapshot of an <see cref="IRng"/>'s internal state.</summary>
public readonly record struct RngState(ulong S0, ulong S1, ulong S2, ulong S3);

/// <summary>
/// Deterministic, version-pinned PRNG (spec Build §4.3). NOT System.Random — its
/// seeded sequence is not stable across .NET versions, which would break replay.
/// </summary>
public interface IRng
{
    ulong NextULong();
    int NextInt(int maxExclusive);
    RngState Capture();
}
```

Create `src/WorldEcon.Simulation/Random/Xoshiro256StarStar.cs`:
```csharp
namespace WorldEcon.Simulation.Random;

/// <summary>
/// xoshiro256** PRNG, seeded via SplitMix64. Fixed algorithm = stable across .NET versions,
/// so (seed) reproduces a sequence forever (spec Build §4.3).
/// </summary>
public sealed class Xoshiro256StarStar : IRng
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256StarStar(ulong seed)
    {
        var sm = new SplitMix64(seed);
        _s0 = sm.Next();
        _s1 = sm.Next();
        _s2 = sm.Next();
        _s3 = sm.Next();
    }

    public Xoshiro256StarStar(RngState state)
    {
        _s0 = state.S0;
        _s1 = state.S1;
        _s2 = state.S2;
        _s3 = state.S3;
    }

    public RngState Capture() => new(_s0, _s1, _s2, _s3);

    public ulong NextULong()
    {
        unchecked
        {
            ulong result = Rotl(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = Rotl(_s3, 45);
            return result;
        }
    }

    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));

        // Lemire's unbiased bounded integer.
        ulong range = (ulong)maxExclusive;
        ulong x = NextULong();
        UInt128 m = (UInt128)x * range;
        ulong low = (ulong)m;
        if (low < range)
        {
            ulong threshold = (0UL - range) % range;
            while (low < threshold)
            {
                x = NextULong();
                m = (UInt128)x * range;
                low = (ulong)m;
            }
        }
        return (int)(ulong)(m >> 64);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));
}

/// <summary>SplitMix64 — used only to expand a single seed into xoshiro's 256-bit state.</summary>
internal struct SplitMix64(ulong seed)
{
    private ulong _x = seed;

    public ulong Next()
    {
        unchecked
        {
            _x += 0x9E3779B97F4A7C15UL;
            ulong z = _x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/WorldEcon.Simulation.Tests.Unit/WorldEcon.Simulation.Tests.Unit.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Simulation/Random/IRng.cs src/WorldEcon.Simulation/Random/Xoshiro256StarStar.cs tests/WorldEcon.Simulation.Tests.Unit/RngTests.cs
git commit -m "feat: add deterministic xoshiro256** PRNG with capture/restore"
```

---

## Task 7: `RngStreams` (per-subsystem isolation)

**Files:**
- Create: `src/WorldEcon.Simulation/Random/RngStreams.cs`
- Test: `tests/WorldEcon.Simulation.Tests.Unit/RngStreamsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Simulation.Tests.Unit/RngStreamsTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Simulation.Random;

namespace WorldEcon.Simulation.Tests.Unit;

public class RngStreamsTests
{
    [Test]
    public void StreamsFromSameSeed_AreIndependentOfEachOther()
    {
        // Drawing from one stream must not affect another's sequence.
        var streamsA = new RngStreams(99UL);
        streamsA.For(RngStream.Pricing).NextULong(); // perturb pricing only
        var tradeAfterPerturb = streamsA.For(RngStream.Trade).NextULong();

        var streamsB = new RngStreams(99UL);
        var tradeFresh = streamsB.For(RngStream.Trade).NextULong();

        tradeAfterPerturb.Should().Be(tradeFresh);
    }

    [Test]
    public void DifferentStreams_ProduceDifferentSequences()
    {
        var streams = new RngStreams(99UL);
        var pricing = streams.For(RngStream.Pricing).NextULong();
        var trade = streams.For(RngStream.Trade).NextULong();
        pricing.Should().NotBe(trade);
    }

    [Test]
    public void SameSeed_ReproducesAllStreams()
    {
        var a = new RngStreams(7UL);
        var b = new RngStreams(7UL);
        foreach (var s in Enum.GetValues<RngStream>())
            a.For(s).NextULong().Should().Be(b.For(s).NextULong());
    }

    [Test]
    public void CaptureAndRestore_ResumesAllStreams()
    {
        var original = new RngStreams(55UL);
        original.For(RngStream.Production).NextULong(); // advance one stream
        var captured = original.Capture();

        var expected = original.For(RngStream.Production).NextULong();

        var restored = new RngStreams(55UL, captured);
        restored.For(RngStream.Production).NextULong().Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/WorldEcon.Simulation.Tests.Unit/WorldEcon.Simulation.Tests.Unit.csproj`
Expected: FAIL — `RngStreams` / `RngStream` do not exist.

- [ ] **Step 3: Implement `RngStreams`**

Create `src/WorldEcon.Simulation/Random/RngStreams.cs`:
```csharp
namespace WorldEcon.Simulation.Random;

/// <summary>One independent RNG stream per simulation subsystem (spec Build §4.3, §8.2).</summary>
public enum RngStream
{
    Pricing = 1,
    Production = 2,
    Trade = 3,
    Events = 4,
}

public interface IRngStreams
{
    IRng For(RngStream stream);
    IReadOnlyDictionary<RngStream, RngState> Capture();
}

/// <summary>
/// Seeds one xoshiro256** stream per subsystem from the world seed mixed with a per-stream
/// constant, so adding a draw in one subsystem never shifts another's sequence.
/// </summary>
public sealed class RngStreams : IRngStreams
{
    private readonly Dictionary<RngStream, Xoshiro256StarStar> _streams = new();

    public RngStreams(ulong worldSeed, IReadOnlyDictionary<RngStream, RngState>? restore = null)
    {
        foreach (RngStream stream in Enum.GetValues<RngStream>())
        {
            _streams[stream] = restore is not null && restore.TryGetValue(stream, out RngState state)
                ? new Xoshiro256StarStar(state)
                : new Xoshiro256StarStar(MixSeed(worldSeed, (ulong)stream));
        }
    }

    public IRng For(RngStream stream) => _streams[stream];

    public IReadOnlyDictionary<RngStream, RngState> Capture()
        => _streams.ToDictionary(kv => kv.Key, kv => kv.Value.Capture());

    private static ulong MixSeed(ulong worldSeed, ulong stream)
    {
        unchecked
        {
            var sm = new SplitMix64(worldSeed ^ (stream * 0x9E3779B97F4A7C15UL));
            return sm.Next();
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/WorldEcon.Simulation.Tests.Unit/WorldEcon.Simulation.Tests.Unit.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Simulation/Random/RngStreams.cs tests/WorldEcon.Simulation.Tests.Unit/RngStreamsTests.cs
git commit -m "feat: add per-subsystem RngStreams with capture/restore"
```

---

## Final verification

- [ ] **Step 1: Full solution build + all tests**

Run:
```bash
dotnet build
dotnet test
```
Expected: build succeeds with zero warnings (warnings are errors); all tests across both test projects pass.

- [ ] **Step 2: Confirm clean tree**

Run: `git status`
Expected: nothing to commit, working tree clean.

---

## What this unlocks
- `Money` + `FixedMath` → deterministic pricing & cost-basis math (Plan 2).
- `Tick` + `CalendarSystem` → cadences, the tick loop, and the scheduler (Plan 3); the UI's date display (Plan 1c).
- `RngStreams` → reproducible production/trade/event rolls; snapshot capture/restore of RNG positions (Plan 1b persistence).
- All of it is the determinism substrate the snapshot/branch/compare design (spec Build §4) rests on.
```