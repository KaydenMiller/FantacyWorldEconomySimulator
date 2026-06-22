# World-Configurable Currency System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persisted, per-world `CurrencyDefinition` value object and Money formatter to the world-economy sim, mirroring how `CalendarDefinition` is structured, with no impact on simulation logic.

**Architecture:** `CurrencyDefinition` lives in `WorldEcon.SharedKernel.Currency` (next to `WorldEcon.SharedKernel.Calendar`), holds an ordered list of denominations (Name/Symbol/Units-in-base), and exposes a `Format(Money)` method. `World` gains a `Currency` property defaulting to `CurrencyDefinition.Default`; a `CurrencyDefinitionConverter` JSON-serializes it into a TEXT column via EF Core, exactly mirroring `CalendarDefinitionConverter`. A migration adds the `Currency` column with a default value matching the JSON output of the converter.

**Tech Stack:** C# 13 / .NET 10, EF Core 10 + SQLite, TUnit + FluentAssertions (test runner: `dotnet run --project ... -c Release`), `System.Text.Json` (JsonSerializerDefaults.General — same as CalendarDefinitionConverter), ErrorOr package.

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/WorldEcon.SharedKernel/Currency/CurrencyDefinition.cs` | Denomination records + `CurrencyDefinition` sealed record + `Default` + `Format(Money)` |
| Modify | `src/WorldEcon.Persistence/Conversions/ValueConverters.cs` | Add `CurrencyDefinitionConverter` |
| Modify | `src/WorldEcon.Domain/Geography/World.cs` | Add `Currency` property, update private ctor + EF ctor |
| Modify | `src/WorldEcon.Persistence/Configurations/WorldConfiguration.cs` | Register `HasConversion<CurrencyDefinitionConverter>()` |
| Create | `src/WorldEcon.Persistence/Migrations/<timestamp>_AddWorldCurrency.cs` | EF migration (generated, then hand-edited for defaultValue) |
| Create | `tests/WorldEcon.SharedKernel.Tests.Unit/CurrencyFormatTests.cs` | Unit tests for Format method |
| Create | `tests/WorldEcon.Persistence.Tests.Unit/CurrencyRoundTripTests.cs` | Persistence round-trip test |

---

### Task 1: CurrencyDefinition value object (failing tests first)

**Files:**
- Create: `tests/WorldEcon.SharedKernel.Tests.Unit/CurrencyFormatTests.cs`
- Create: `src/WorldEcon.SharedKernel/Currency/CurrencyDefinition.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/WorldEcon.SharedKernel.Tests.Unit/CurrencyFormatTests.cs`:

```csharp
using FluentAssertions;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Currency;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class CurrencyFormatTests
{
    private static readonly CurrencyDefinition Def = CurrencyDefinition.Default;

    [Test]
    public void Format_321_Returns3g2s1c()
        => Def.Format(new Money(321)).Should().Be("3g 2s 1c");

    [Test]
    public void Format_300_Returns3g()
        => Def.Format(new Money(300)).Should().Be("3g");

    [Test]
    public void Format_1234_Returns1p2g3s4c()
        => Def.Format(new Money(1234)).Should().Be("1p 2g 3s 4c");

    [Test]
    public void Format_5_Returns5c()
        => Def.Format(new Money(5)).Should().Be("5c");

    [Test]
    public void Format_0_Returns0c()
        => Def.Format(new Money(0)).Should().Be("0c");

    [Test]
    public void Format_Negative321_ReturnsMinus3g2s1c()
        => Def.Format(new Money(-321)).Should().Be("-3g 2s 1c");

    [Test]
    public void Format_Negative5_ReturnsMinus5c()
        => Def.Format(new Money(-5)).Should().Be("-5c");

    [Test]
    public void Default_HasFourDenominations()
        => Def.Denominations.Should().HaveCount(4);

    [Test]
    public void Default_BaseUnitIsCopperWithUnits1()
    {
        var copper = Def.Denominations[0];
        copper.Name.Should().Be("Copper");
        copper.Symbol.Should().Be("c");
        copper.Units.Should().Be(1);
    }

    [Test]
    public void Default_PlatinumIsHighest_1000Units()
    {
        var plat = Def.Denominations[^1];
        plat.Name.Should().Be("Platinum");
        plat.Symbol.Should().Be("p");
        plat.Units.Should().Be(1000);
    }
}
```

- [ ] **Step 2: Run tests — expect compile failure (CurrencyDefinition doesn't exist yet)**

```bash
dotnet run --project /home/kayden/workspaces/dnd/tests/WorldEcon.SharedKernel.Tests.Unit -c Release
```

Expected: Build error — `WorldEcon.SharedKernel.Currency` not found.

- [ ] **Step 3: Implement CurrencyDefinition**

Create `src/WorldEcon.SharedKernel/Currency/CurrencyDefinition.cs`:

```csharp
namespace WorldEcon.SharedKernel.Currency;

/// <summary>
/// A denomination within a currency system (e.g. "Gold" / "g" / 100 copper).
/// <paramref name="Units"/> is the value of this denomination in base units (the denomination
/// with Units == 1 is the base).
/// </summary>
public sealed record Denomination(string Name, string Symbol, long Units);

/// <summary>
/// Data-driven currency configuration. Denominations must be ordered ascending by Units,
/// with exactly one denomination having Units == 1 (the base unit).
/// <para>
/// Currency is display-only: <see cref="Money"/> always stores base units (copper by default).
/// Denominations are used only for formatting — they have zero impact on simulation logic.
/// </para>
/// </summary>
public sealed record CurrencyDefinition(IReadOnlyList<Denomination> Denominations)
{
    /// <summary>
    /// Default D&amp;D-style 4-tier currency: copper (1), silver (10), gold (100), platinum (1000).
    /// </summary>
    public static CurrencyDefinition Default { get; } = new(
    [
        new Denomination("Copper",   "c",    1),
        new Denomination("Silver",   "s",   10),
        new Denomination("Gold",     "g",  100),
        new Denomination("Platinum", "p", 1000),
    ]);

    /// <summary>
    /// Formats <paramref name="money"/> using this currency's denominations.
    /// Decomposes highest-denomination-first, omitting zero parts.
    /// Always shows at least the base-unit part when the value is zero.
    /// Negative values are prefixed with "-"; the parts themselves are unsigned.
    /// Examples (Default): 321 → "3g 2s 1c"; 300 → "3g"; 0 → "0c"; -5 → "-5c".
    /// </summary>
    public string Format(Money money)
    {
        bool negative = money.Units < 0;
        long remaining = negative ? -money.Units : money.Units;

        // Work high-to-low through denominations (they are ordered ascending, so reverse).
        var parts = new List<string>();
        for (int i = Denominations.Count - 1; i >= 0; i--)
        {
            var denom = Denominations[i];
            long amount = remaining / denom.Units;
            remaining %= denom.Units;

            if (amount > 0)
                parts.Add($"{amount}{denom.Symbol}");
        }

        // Zero value: show "0<baseSymbol>"
        if (parts.Count == 0)
        {
            var baseSymbol = Denominations[0].Symbol;
            return $"0{baseSymbol}";
        }

        var formatted = string.Join(" ", parts);
        return negative ? $"-{formatted}" : formatted;
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet run --project /home/kayden/workspaces/dnd/tests/WorldEcon.SharedKernel.Tests.Unit -c Release
```

Expected output ends with: `Passed!` and all `CurrencyFormatTests` pass. Zero warnings (warnings-as-errors is enabled).

- [ ] **Step 5: Commit**

```bash
git -C /home/kayden/workspaces/dnd add \
  src/WorldEcon.SharedKernel/Currency/CurrencyDefinition.cs \
  tests/WorldEcon.SharedKernel.Tests.Unit/CurrencyFormatTests.cs
git -C /home/kayden/workspaces/dnd commit -m "$(cat <<'EOF'
feat(currency): add CurrencyDefinition value object with Money formatter

TDD: format tests assert all spec examples (321→"3g 2s 1c", 0→"0c",
negatives, etc.). Default scheme is copper/silver/gold/platinum.
EOF
)"
```

---

### Task 2: Add CurrencyDefinitionConverter to Persistence

**Files:**
- Modify: `src/WorldEcon.Persistence/Conversions/ValueConverters.cs`

- [ ] **Step 1: Add the converter**

Open `src/WorldEcon.Persistence/Conversions/ValueConverters.cs`. Add these lines after the `CalendarDefinitionConverter` class and before `RecipeLinesConverter`:

The file currently ends with:

```csharp
public sealed class CalendarDefinitionConverter() : ValueConverter<CalendarDefinition, string>(
    c => JsonSerializer.Serialize(c, Options),
    s => JsonSerializer.Deserialize<CalendarDefinition>(s, Options)!)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);
}

public sealed class RecipeLinesConverter() ...
```

Add after `CalendarDefinitionConverter`:

```csharp
public sealed class CurrencyDefinitionConverter() : ValueConverter<CurrencyDefinition, string>(
    c => JsonSerializer.Serialize(c, Options),
    s => JsonSerializer.Deserialize<CurrencyDefinition>(s, Options)!)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);
}
```

Also add the using at the top of the file (after the existing `WorldEcon.SharedKernel.Calendar` using):

```csharp
using WorldEcon.SharedKernel.Currency;
```

The full top-of-file using block should then be:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Economy;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Currency;
```

- [ ] **Step 2: Build to verify no errors**

```bash
dotnet build /home/kayden/workspaces/dnd/src/WorldEcon.Persistence -c Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git -C /home/kayden/workspaces/dnd add src/WorldEcon.Persistence/Conversions/ValueConverters.cs
git -C /home/kayden/workspaces/dnd commit -m "$(cat <<'EOF'
feat(currency): add CurrencyDefinitionConverter (JSON, mirrors CalendarDefinitionConverter)
EOF
)"
```

---

### Task 3: Add Currency property to World domain entity

**Files:**
- Modify: `src/WorldEcon.Domain/Geography/World.cs`

- [ ] **Step 1: Add the Currency property**

The current `World.cs` has these fields and constructors:

```csharp
public string Name { get; private set; }
public ulong Seed { get; }
public CalendarDefinition Calendar { get; }
public Tick CurrentTick { get; private set; }
public string RulesetVersion { get; private set; }
// ...pricing params...

private World() : base(default)
{
    Name = null!;
    Calendar = null!;
    RulesetVersion = null!;
}

private World(WorldId id, string name, ulong seed, CalendarDefinition calendar, Tick currentTick, string rulesetVersion)
    : base(id)
{
    Name = name;
    Seed = seed;
    Calendar = calendar;
    CurrentTick = currentTick;
    RulesetVersion = rulesetVersion;
    ElasticityExponent = DefaultElasticityExponent;
    MinPriceMultBp = DefaultMinPriceMultBp;
    MaxPriceMultBp = DefaultMaxPriceMultBp;
}

public static ErrorOr<World> Create(string name, ulong seed, CalendarDefinition calendar, string rulesetVersion)
{
    // ...validation...
    return new World(WorldId.New(), name.Trim(), seed, calendar, Tick.Zero, rulesetVersion);
}
```

Apply these changes:

1. Add using at top of file: `using WorldEcon.SharedKernel.Currency;`

2. After `public CalendarDefinition Calendar { get; }`, add:
   ```csharp
   public CurrencyDefinition Currency { get; private set; }
   ```

3. In the EF parameterless constructor `private World() : base(default)`, add:
   ```csharp
   Currency = null!;
   ```
   (alongside `Calendar = null!;` — EF will overwrite it via the backing field)

4. In the private parameterized constructor, add after `Calendar = calendar;`:
   ```csharp
   Currency = CurrencyDefinition.Default;
   ```

5. The `Create` factory signature stays exactly the same — no new parameter. Currency always defaults to `CurrencyDefinition.Default`.

The final relevant sections of World.cs should look like:

```csharp
using ErrorOr;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Currency;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class World : AggregateRoot<WorldId>
{
    public string Name { get; private set; }
    public ulong Seed { get; }
    public CalendarDefinition Calendar { get; }
    public CurrencyDefinition Currency { get; private set; }
    public Tick CurrentTick { get; private set; }
    public string RulesetVersion { get; private set; }
    // ...pricing params unchanged...

    private World() : base(default)
    {
        Name = null!;
        Calendar = null!;
        Currency = null!;
        RulesetVersion = null!;
    }

    private World(WorldId id, string name, ulong seed, CalendarDefinition calendar, Tick currentTick, string rulesetVersion)
        : base(id)
    {
        Name = name;
        Seed = seed;
        Calendar = calendar;
        Currency = CurrencyDefinition.Default;
        CurrentTick = currentTick;
        RulesetVersion = rulesetVersion;
        ElasticityExponent = DefaultElasticityExponent;
        MinPriceMultBp = DefaultMinPriceMultBp;
        MaxPriceMultBp = DefaultMaxPriceMultBp;
    }

    public static ErrorOr<World> Create(string name, ulong seed, CalendarDefinition calendar, string rulesetVersion)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("world.name.blank", "World name must not be blank.");
        if (string.IsNullOrWhiteSpace(rulesetVersion))
            return Error.Validation("world.ruleset.blank", "Ruleset version must not be blank.");

        return new World(WorldId.New(), name.Trim(), seed, calendar, Tick.Zero, rulesetVersion);
    }
    // ...rest unchanged...
```

- [ ] **Step 2: Build Domain to verify**

```bash
dotnet build /home/kayden/workspaces/dnd/src/WorldEcon.Domain -c Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git -C /home/kayden/workspaces/dnd add src/WorldEcon.Domain/Geography/World.cs
git -C /home/kayden/workspaces/dnd commit -m "$(cat <<'EOF'
feat(currency): add Currency property to World, defaulting to CurrencyDefinition.Default

World.Create signature unchanged — no breaking callers.
EOF
)"
```

---

### Task 4: Register CurrencyDefinitionConverter in EF WorldConfiguration

**Files:**
- Modify: `src/WorldEcon.Persistence/Configurations/WorldConfiguration.cs`

- [ ] **Step 1: Add HasConversion for Currency**

The current `WorldConfiguration.Configure` method body is:

```csharp
b.ToTable("worlds");
b.HasKey(x => x.Id);
b.Property(x => x.Name).IsRequired();
b.Property(x => x.Seed).HasConversion<UInt64Converter>();
b.Property(x => x.CurrentTick).HasConversion<TickConverter>();
b.Property(x => x.Calendar).HasConversion<CalendarDefinitionConverter>();
b.Property(x => x.RulesetVersion).IsRequired();
b.Ignore(x => x.DomainEvents);
```

Add after the `Calendar` line:

```csharp
b.Property(x => x.Currency).HasConversion<CurrencyDefinitionConverter>();
```

The full method body should be:

```csharp
b.ToTable("worlds");
b.HasKey(x => x.Id);
b.Property(x => x.Name).IsRequired();
b.Property(x => x.Seed).HasConversion<UInt64Converter>();
b.Property(x => x.CurrentTick).HasConversion<TickConverter>();
b.Property(x => x.Calendar).HasConversion<CalendarDefinitionConverter>();
b.Property(x => x.Currency).HasConversion<CurrencyDefinitionConverter>();
b.Property(x => x.RulesetVersion).IsRequired();
b.Ignore(x => x.DomainEvents);
```

- [ ] **Step 2: Build Persistence to verify**

```bash
dotnet build /home/kayden/workspaces/dnd/src/WorldEcon.Persistence -c Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git -C /home/kayden/workspaces/dnd add src/WorldEcon.Persistence/Configurations/WorldConfiguration.cs
git -C /home/kayden/workspaces/dnd commit -m "$(cat <<'EOF'
feat(currency): register CurrencyDefinitionConverter in WorldConfiguration EF config
EOF
)"
```

---

### Task 5: Generate and hand-edit the EF Core migration

**Files:**
- Create: `src/WorldEcon.Persistence/Migrations/<timestamp>_AddWorldCurrency.cs` (generated)
- Modify: same file (hand-edit defaultValue)

- [ ] **Step 1: Determine the JSON default value string**

The converter uses `JsonSerializer.Serialize(CurrencyDefinition.Default, new JsonSerializerOptions(JsonSerializerDefaults.General))`. With `JsonSerializerDefaults.General` (case-sensitive, PascalCase property names), the serialized JSON for `CurrencyDefinition.Default` is:

```
{"Denominations":[{"Name":"Copper","Symbol":"c","Units":1},{"Name":"Silver","Symbol":"s","Units":10},{"Name":"Gold","Symbol":"g","Units":100},{"Name":"Platinum","Symbol":"p","Units":1000}]}
```

This is the string you will use as `defaultValue:` in the migration (no extra spaces — compact JSON).

- [ ] **Step 2: Generate the migration**

```bash
dotnet ef migrations add AddWorldCurrency \
  --project /home/kayden/workspaces/dnd/src/WorldEcon.Persistence \
  --startup-project /home/kayden/workspaces/dnd/src/WorldEcon.Persistence
```

Expected output: `Build succeeded.` and `Done. To undo this action, use 'ef migrations remove'`.

This creates `src/WorldEcon.Persistence/Migrations/<timestamp>_AddWorldCurrency.cs`.

- [ ] **Step 3: Hand-edit the migration to add defaultValue for existing rows**

Open the generated migration file. In the `Up` method you will see an `AddColumn` call like:

```csharp
migrationBuilder.AddColumn<string>(
    name: "Currency",
    table: "worlds",
    type: "TEXT",
    nullable: false,
    defaultValue: "");
```

Change the `defaultValue: ""` to the JSON string from Step 1:

```csharp
migrationBuilder.AddColumn<string>(
    name: "Currency",
    table: "worlds",
    type: "TEXT",
    nullable: false,
    defaultValue: "{\"Denominations\":[{\"Name\":\"Copper\",\"Symbol\":\"c\",\"Units\":1},{\"Name\":\"Silver\",\"Symbol\":\"s\",\"Units\":10},{\"Name\":\"Gold\",\"Symbol\":\"g\",\"Units\":100},{\"Name\":\"Platinum\",\"Symbol\":\"p\",\"Units\":1000}]}");
```

Do NOT change the `Down` method — it should remain as-is (drop the column).

- [ ] **Step 4: Verify no pending model changes**

```bash
dotnet ef migrations has-pending-model-changes \
  --project /home/kayden/workspaces/dnd/src/WorldEcon.Persistence \
  --startup-project /home/kayden/workspaces/dnd/src/WorldEcon.Persistence
```

Expected: exits with code 0 and no output (or "Your model has no pending changes"). If it reports pending changes, check that `WorldConfiguration` has the `Currency` property registered and `World` has the property defined.

- [ ] **Step 5: Build everything**

```bash
dotnet build /home/kayden/workspaces/dnd -c Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` across all projects.

- [ ] **Step 6: Commit**

```bash
git -C /home/kayden/workspaces/dnd add \
  src/WorldEcon.Persistence/Migrations/ \
  src/WorldEcon.Persistence/Migrations/WorldDbContextModelSnapshot.cs
git -C /home/kayden/workspaces/dnd commit -m "$(cat <<'EOF'
feat(currency): migration AddWorldCurrency — adds Currency TEXT column to worlds table

Existing rows default to serialized CurrencyDefinition.Default (compact JSON).
EOF
)"
```

---

### Task 6: Write and pass the persistence round-trip test

**Files:**
- Create: `tests/WorldEcon.Persistence.Tests.Unit/CurrencyRoundTripTests.cs`

- [ ] **Step 1: Write the test**

Create `tests/WorldEcon.Persistence.Tests.Unit/CurrencyRoundTripTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Currency;

namespace WorldEcon.Persistence.Tests.Unit;

public class CurrencyRoundTripTests
{
    private static WorldDbContext NewContextOnFile(string path)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new WorldDbContext(options);
    }

    [Test]
    public async Task World_Currency_PersistsAndReloadsAsDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_cur_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("CurrencyTest", 99UL, CalendarDefinition.Default, "1.0.0").Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var w = await ctx.Worlds.SingleAsync();

                // Currency JSON round-trip survived
                w.Currency.Should().NotBeNull();
                w.Currency.Denominations.Should().HaveCount(4);

                var copper = w.Currency.Denominations[0];
                copper.Name.Should().Be("Copper");
                copper.Symbol.Should().Be("c");
                copper.Units.Should().Be(1);

                var plat = w.Currency.Denominations[^1];
                plat.Name.Should().Be("Platinum");
                plat.Symbol.Should().Be("p");
                plat.Units.Should().Be(1000);

                // Formatter works after round-trip
                w.Currency.Format(new Money(321)).Should().Be("3g 2s 1c");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run persistence tests — expect pass**

```bash
dotnet run --project /home/kayden/workspaces/dnd/tests/WorldEcon.Persistence.Tests.Unit -c Release
```

Expected output ends with `Passed!`. Zero warnings.

- [ ] **Step 3: Run SharedKernel tests — confirm still passing**

```bash
dotnet run --project /home/kayden/workspaces/dnd/tests/WorldEcon.SharedKernel.Tests.Unit -c Release
```

Expected: `Passed!`

- [ ] **Step 4: Commit**

```bash
git -C /home/kayden/workspaces/dnd add tests/WorldEcon.Persistence.Tests.Unit/CurrencyRoundTripTests.cs
git -C /home/kayden/workspaces/dnd commit -m "$(cat <<'EOF'
test(currency): persistence round-trip test for World.Currency JSON serialization
EOF
)"
```

---

### Task 7: CLI smoke test and final build validation

- [ ] **Step 1: Full solution build**

```bash
dotnet build /home/kayden/workspaces/dnd -c Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 2: CLI smoke test — `new` command applies migration cleanly**

```bash
dotnet run --project /home/kayden/workspaces/dnd/src/WorldEcon.Cli -c Release -- new /tmp/cur_smoke.db
```

Expected: exits without error (the `new` subcommand seeds the demo world and saves; the new `Currency` column is applied by the migration). If the CLI accepts a `new` sub-command differently, check the CLI's `Program.cs` for the exact invocation.

- [ ] **Step 3: Remove temp DB**

```bash
rm -f /tmp/cur_smoke.db
```

- [ ] **Step 4: Final commit (squash or tagged)**

```bash
git -C /home/kayden/workspaces/dnd commit --allow-empty -m "$(cat <<'EOF'
feat(currency): world-configurable CurrencyDefinition + Money formatter (display only)

- CurrencyDefinition sealed record in WorldEcon.SharedKernel.Currency
- Format(Money) implements spec examples: 321→"3g 2s 1c", 0→"0c", negatives
- CurrencyDefinitionConverter JSON-serializes to worlds.Currency TEXT column
- World.Currency property defaults to CurrencyDefinition.Default; World.Create unchanged
- Migration AddWorldCurrency with defaultValue for existing rows
- SharedKernel + Persistence unit tests pass; CLI smoke test clean

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review Checklist

**Spec coverage:**
- [x] `CurrencyDefinition` in `WorldEcon.SharedKernel.Currency` namespace — Task 1
- [x] `Denomination` record with Name/Symbol/Units — Task 1
- [x] `CurrencyDefinition.Default` = copper(1)/silver(10)/gold(100)/platinum(1000) with c/s/g/p symbols — Task 1
- [x] `Format(Money)` decompose high-to-low, omit zeros, show base for 0, negative prefix — Task 1
- [x] All 7 spec examples tested: 321→"3g 2s 1c", 300→"3g", 1234→"1p 2g 3s 4c", 5→"5c", 0→"0c", -321→"-3g 2s 1c", -5→"-5c" — Task 1
- [x] `CurrencyDefinitionConverter` JSON with `JsonSerializerDefaults.General` — Task 2
- [x] `World.Currency` property, private setter, EF ctor nulled — Task 3
- [x] `World.Create` signature unchanged (no new parameter) — Task 3
- [x] `WorldConfiguration.HasConversion<CurrencyDefinitionConverter>()` — Task 4
- [x] Migration `AddWorldCurrency` — Task 5
- [x] Migration `defaultValue:` set to JSON of Default — Task 5
- [x] `has-pending-model-changes` check — Task 5
- [x] Persistence round-trip test — Task 6
- [x] No changes to Money arithmetic or simulation logic — CurrencyDefinition is display-only
- [x] No unused usings — all usings in new/modified files are used
- [x] CLI smoke test — Task 7

**Type consistency:**
- `CurrencyDefinition` used consistently (Tasks 1/2/3/4/6)
- `Denomination` record used in `CurrencyDefinition.Denominations` — `IReadOnlyList<Denomination>`
- `Format(Money money)` signature consistent in definition (Task 1) and test calls (Tasks 1/6)
- `Denominations[0]` is copper (Units=1, base), `Denominations[^1]` is platinum — consistent in Default and tests

**Placeholder scan:** No TBD/TODO/similar-to-task references. All code blocks are complete.
