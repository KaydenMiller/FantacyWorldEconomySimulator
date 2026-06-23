# Shop Economy — Phase 2: Demand Side & Retail Money — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a walleted demand side — a `RepresentativeConsumer` (Seat, Size, Budget) spawned per population that receives a periodic allowance and **buys** tiered needs from shops cheapest-first within budget at a **scarcity-flexing retail price**, crediting shop tills — replacing the current free `ConsumptionPhase`.

**Architecture:** Mirror `RepresentativeMerchant`/`MerchantSpawnPhase` for consumers. Income is an `IConsumerIncome` strategy (default `AllowanceIncome`) so a wage loop can swap in later. A `ConsumerDemandPhase` replaces `ConsumptionPhase`: per settlement it computes a per-good scarcity multiplier (same math as `PricingPhase`), then each consumer buys tier-by-tier (Essential→Standard→Comfort) from the cheapest shops within budget via `ShopMarket`. Per-shop sales credit `Shop.Till` and emit one `Trade` log event per shop·good·day; unmet demand emits a settlement `Stockout`. The wholesale `MarketPrice` (set by `PricingPhase`) is untouched.

**Tech Stack:** C#/.NET 10, EF Core 10 + SQLite, ErrorOr, TUnit + FluentAssertions 7.x, Terminal.Gui 2.4.7. Tests run with `dotnet run --project <testproject> -c Release` (NOT `dotnet test`).

**Spec:** `docs/superpowers/specs/2026-06-22-shop-economy-phase2-demand-design.md`

---

## Conventions (read once)

- Tests: `dotnet run --project tests/WorldEcon.<Project>.Tests.Unit -c Release`. TUnit `[Test] public async Task`. First "run to see it fail" usually fails at **build** (missing type) — expected red.
- Build: `dotnet build src/WorldEcon.<Project>` ends `0 Error(s)`. Solution is **warnings-as-errors** (no unused usings).
- Strongly-typed IDs: `readonly record struct XId(Guid Value) : IStronglyTypedId { public static XId New() => new(Guid.NewGuid()); }`.
- Aggregates: private parameterless EF ctor; private full ctor; `static ErrorOr<X> Create(...)`; `{ get; private set; }`; mutators are methods.
- EF: configs in `src/WorldEcon.Persistence/Configurations/`; `Money`→`MoneyConverter`; enums→`HasConversion<string>()`; `b.Ignore(x => x.DomainEvents)`. Register a new typed-id converter in `WorldDbContext.ConfigureConventions`.
- Migration: `dotnet dotnet-ef migrations add <Name> --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence`; verify with `... migrations has-pending-model-changes ...`.
- Engine phases live in `src/WorldEcon.Engine/Phases/`, implement `ISimulationPhase` (`Name`, `int Order`, `long CadenceTicks`, `ExecuteAsync(SimulationContext ctx, Tick tick)`), and are registered in `src/WorldEcon.Engine/StandardPhases.cs`. They use the `.Local`-merge pattern.
- `ShopMarket` (Engine) API: `ShopsIn(ctx, settlementId)`, `StockpilesForGood(ctx, settlementId, goodId)` (shop stockpiles, stable order, `.Local`-merge), `TotalSupply(ctx, settlementId, goodId)`.
- Logging: `await ctx.Log.EmitAsync(LogEventType type, string message, Tick tick, LogScopeKind originKind, Guid originId, SettlementId? settlement = null, LogMagnitude? magnitude = null, bool isPlayerAction = false, string payloadJson = "{}")`. `Trade` defaults to Routine, `Stockout` to Notable.
- Scarcity math (from `PricingPhase`, verbatim shape): `scarcityBp = FixedMath.DivRound(demand * FixedMath.BpScale, max(supply,1)); mult = Math.Clamp(FixedMath.PowBpInt(scarcityBp, world.ElasticityExponent), world.MinPriceMultBp, world.MaxPriceMultBp)`. `FixedMath.MulBp(value, bp) = value*bp/10000`.
- Temp DB in tests:
  ```csharp
  var path = Path.Combine(Path.GetTempPath(), $"p2-{Guid.NewGuid():N}.db");
  await using var db = new WorldDbContext(
      new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
  await db.Database.MigrateAsync();
  // ... finally File.Delete(path);
  ```
- Engine tests reuse `tests/WorldEcon.Engine.Tests.Unit/LogTestWorld.cs` (continent→country→region→settlement; `s.World`, `s.Settlement`, `s.Db`).

---

## File Structure

**Domain (create/modify):**
- `src/WorldEcon.Domain/Economy/Ids.cs` — add `ConsumerId`.
- `src/WorldEcon.Domain/Economy/RepresentativeConsumer.cs` — **new** aggregate.
- `src/WorldEcon.Domain/Economy/Enums.cs` — add `NeedTier`.
- `src/WorldEcon.Domain/Economy/Good.cs` — add `NeedTier Need` (optional ctor/factory param).
- `src/WorldEcon.Domain/Economy/Shop.cs` — add `CreditTill(Money)`.

**Persistence:**
- `src/WorldEcon.Persistence/Conversions/EconomyIdConverters.cs` — add `ConsumerIdConverter`.
- `src/WorldEcon.Persistence/Configurations/RepresentativeConsumerConfiguration.cs` — **new**.
- `src/WorldEcon.Persistence/Configurations/GoodConfiguration.cs` — map `Need`.
- `src/WorldEcon.Persistence/WorldDbContext.cs` — `Consumers` DbSet + converter registration.
- A migration `..._AddConsumers.cs`.

**Engine (create/modify):**
- `src/WorldEcon.Engine/Demand/IConsumerIncome.cs` + `AllowanceIncome.cs` — **new** income seam.
- `src/WorldEcon.Engine/Demand/RetailPricing.cs` — **new** scarcity-flexing retail price helper.
- `src/WorldEcon.Engine/Phases/ConsumerSpawnPhase.cs`, `ConsumerIncomePhase.cs`, `ConsumerDemandPhase.cs` — **new**.
- `src/WorldEcon.Engine/Phases/ConsumptionPhase.cs` — **delete** (replaced).
- `src/WorldEcon.Engine/StandardPhases.cs` — swap registrations.

**Seeding:** `src/WorldEcon.Cli/DemoSeeder.cs`, `src/WorldEcon.Seeding/SeedImporter.cs`.

**UI/CLI:** `src/WorldEcon.Tui/Navigation/{Navigator,NavView}.cs`, `src/WorldEcon.Cli/CommandRunner.cs`.

---

## Task 1: Domain — consumer, need tier, shop till credit

**Files:**
- Modify: `src/WorldEcon.Domain/Economy/Ids.cs`, `Enums.cs`, `Good.cs`, `Shop.cs`
- Create: `src/WorldEcon.Domain/Economy/RepresentativeConsumer.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/ConsumerTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Domain.Tests.Unit/ConsumerTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit;

public class ConsumerTests
{
    private static readonly WorldId World = WorldId.New();
    private static readonly SettlementId Seat = SettlementId.New();

    [Test]
    public void Create_Succeeds_AndSpendEarnGuard()
    {
        var c = RepresentativeConsumer.Create(World, Seat, 1000, new Money(500)).Value;
        c.Size.Should().Be(1000);
        c.Budget.Should().Be(new Money(500));

        c.Spend(new Money(200));
        c.Budget.Should().Be(new Money(300));
        c.Earn(new Money(100));
        c.Budget.Should().Be(new Money(400));

        var act = () => c.Spend(new Money(10_000));
        act.Should().Throw<InvalidOperationException>(); // cannot overspend
    }

    [Test]
    public void Create_RejectsZeroSize()
        => RepresentativeConsumer.Create(World, Seat, 0, Money.Zero).IsError.Should().BeTrue();

    [Test]
    public void Good_DefaultNeedTier_IsEssential()
    {
        var g = Good.Create(World, "Bread", GoodCategory.Food, new Money(30), "loaf", SizeClass.Small, 0, true, 50).Value;
        g.Need.Should().Be(NeedTier.Essential);
    }

    [Test]
    public void Good_NeedTier_CanBeSet()
    {
        var g = Good.Create(World, "Lute", GoodCategory.Luxury, new Money(900), "lute", SizeClass.Small, 0, false, 5, NeedTier.Comfort).Value;
        g.Need.Should().Be(NeedTier.Comfort);
    }

    [Test]
    public void Shop_CreditTill_AddsToTill()
    {
        var s = Shop.Create(World, Seat, "The Sundries", 2000, new Money(100)).Value;
        s.CreditTill(new Money(250));
        s.Till.Should().Be(new Money(350));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: build failure — `ConsumerId`/`RepresentativeConsumer`/`NeedTier`/`Good.Need`/`Shop.CreditTill` don't exist.

- [ ] **Step 3: Implement**

In `Ids.cs`, add:
```csharp
public readonly record struct ConsumerId(Guid Value) : IStronglyTypedId { public static ConsumerId New() => new(Guid.NewGuid()); }
```

In `Enums.cs`, add:
```csharp
/// <summary>Need priority for consumer demand: lower tiers are bought first within budget.</summary>
public enum NeedTier { Essential = 0, Standard = 1, Comfort = 2 }
```

Create `RepresentativeConsumer.cs`:
```csharp
using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>A settlement's representative consumer (Phase 2): represents <see cref="Size"/> people,
/// holds a <see cref="Budget"/>, and buys to meet needs. Mirror of <see cref="RepresentativeMerchant"/>.</summary>
public sealed class RepresentativeConsumer : AggregateRoot<ConsumerId>
{
    public WorldId WorldId { get; }
    public SettlementId Seat { get; private set; }
    public long Size { get; private set; }     // number of people represented
    public Money Budget { get; private set; }

    private RepresentativeConsumer() : base(default) { } // EF

    private RepresentativeConsumer(ConsumerId id, WorldId worldId, SettlementId seat, long size, Money budget) : base(id)
    {
        WorldId = worldId;
        Seat = seat;
        Size = size;
        Budget = budget;
    }

    public static ErrorOr<RepresentativeConsumer> Create(WorldId worldId, SettlementId seat, long size, Money budget)
    {
        if (size < 1)
            return Error.Validation("consumer.size.tooSmall", "Size must be at least 1.");
        if (budget.IsNegative)
            return Error.Validation("consumer.budget.negative", "Budget must not be negative.");
        return new RepresentativeConsumer(ConsumerId.New(), worldId, seat, size, budget);
    }

    /// <summary>Spend from the budget (e.g. a purchase). Cannot go into debt.</summary>
    public void Spend(Money amount)
    {
        if (amount.IsNegative)
            throw new InvalidOperationException("Cannot spend a negative amount.");
        if (amount.Units > Budget.Units)
            throw new InvalidOperationException("Cannot spend more than available budget.");
        Budget -= amount;
    }

    /// <summary>Add income / refund to the budget.</summary>
    public void Earn(Money amount)
    {
        if (amount.IsNegative)
            throw new InvalidOperationException("Cannot earn a negative amount.");
        Budget += amount;
    }
}
```

In `Good.cs`, add the property and thread it through. Add to the field list:
```csharp
    public NeedTier Need { get; private set; }
```
Add `NeedTier need` as the LAST parameter of the private full ctor and assign `Need = need;`. In `Create`, add a trailing optional param `NeedTier needTier = NeedTier.Essential` and pass it as the last arg to the ctor. (The EF private parameterless ctor needs no change — `Need` defaults to `Essential = 0`.)

In `Shop.cs`, add:
```csharp
    /// <summary>Credit the till (e.g. a consumer payment). Phase 2.</summary>
    public void CreditTill(Money amount)
    {
        if (amount.IsNegative)
            throw new InvalidOperationException("Cannot credit a negative amount.");
        Till += amount;
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Domain/Economy/Ids.cs src/WorldEcon.Domain/Economy/Enums.cs \
        src/WorldEcon.Domain/Economy/RepresentativeConsumer.cs src/WorldEcon.Domain/Economy/Good.cs \
        src/WorldEcon.Domain/Economy/Shop.cs tests/WorldEcon.Domain.Tests.Unit/ConsumerTests.cs
git commit -m "feat(demand): RepresentativeConsumer, NeedTier on Good, Shop.CreditTill"
```

---

## Task 2: Persistence — consumer table, NeedTier column, migration

**Files:**
- Modify: `src/WorldEcon.Persistence/Conversions/EconomyIdConverters.cs`, `WorldDbContext.cs`, `Configurations/GoodConfiguration.cs`
- Create: `src/WorldEcon.Persistence/Configurations/RepresentativeConsumerConfiguration.cs`, migration
- Test: `tests/WorldEcon.Persistence.Tests.Unit/ConsumerPersistenceTests.cs`

- [ ] **Step 1: Converters, config, DbSet**

In `EconomyIdConverters.cs` add:
```csharp
public sealed class ConsumerIdConverter() : ValueConverter<ConsumerId, Guid>(v => v.Value, g => new ConsumerId(g));
```

Create `RepresentativeConsumerConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class RepresentativeConsumerConfiguration : IEntityTypeConfiguration<RepresentativeConsumer>
{
    public void Configure(EntityTypeBuilder<RepresentativeConsumer> b)
    {
        b.ToTable("consumers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Budget).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.Seat);
        b.Ignore(x => x.DomainEvents);
    }
}
```

In `GoodConfiguration.cs`, add inside `Configure`:
```csharp
        b.Property(x => x.Need).HasConversion<string>();
```

In `WorldDbContext.cs`: add the DbSet near the other economy sets:
```csharp
    public DbSet<RepresentativeConsumer> Consumers => Set<RepresentativeConsumer>();
```
and register the converter in `ConfigureConventions`:
```csharp
        b.Properties<ConsumerId>().HaveConversion<ConsumerIdConverter>();
```

- [ ] **Step 2: Generate the migration**

Run:
```bash
dotnet dotnet-ef migrations add AddConsumers \
  --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence
```
Expected: a migration that creates `consumers` and adds a `Need` column to `goods`. Hand-edit the `Need` AddColumn to `nullable: false, defaultValue: "Essential"` (so existing goods default to Essential).

- [ ] **Step 3: Confirm no drift**

Run: `dotnet dotnet-ef migrations has-pending-model-changes --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence`
Expected: "No changes have been made to the model since the last migration."

- [ ] **Step 4: Write the round-trip test**

`tests/WorldEcon.Persistence.Tests.Unit/ConsumerPersistenceTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Persistence.Tests.Unit;

public class ConsumerPersistenceTests
{
    [Test]
    public async Task Consumer_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cons-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            await db.Database.MigrateAsync();

            var worldId = new WorldId(Guid.NewGuid());
            var seat = new SettlementId(Guid.NewGuid());
            db.Consumers.Add(RepresentativeConsumer.Create(worldId, seat, 1000, new Money(500)).Value);
            await db.SaveChangesAsync();

            var c = await db.Consumers.SingleAsync();
            c.Size.Should().Be(1000);
            c.Budget.Should().Be(new Money(500));
            c.Seat.Should().Be(seat);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 5: Run the test**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 6: Commit**

```bash
git add src/WorldEcon.Persistence tests/WorldEcon.Persistence.Tests.Unit/ConsumerPersistenceTests.cs
git commit -m "feat(demand): persist consumers + Good.NeedTier (AddConsumers migration)"
```

---

## Task 3: Engine — income seam + retail-pricing helper

**Files:**
- Create: `src/WorldEcon.Engine/Demand/IConsumerIncome.cs`, `AllowanceIncome.cs`, `RetailPricing.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/RetailPricingTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/RetailPricingTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine.Demand;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class RetailPricingTests
{
    private static World NewWorld() => World.Create("T", 1UL, CalendarDefinition.Default, "1.0.0").Value;

    [Test]
    public void ScarceGood_PricesHigherThanGlut()
    {
        var w = NewWorld();
        long scarce = RetailPricing.ScarcityMultBp(demand: 1000, supply: 10, w);   // demand >> supply
        long glut = RetailPricing.ScarcityMultBp(demand: 10, supply: 1000, w);     // supply >> demand
        scarce.Should().BeGreaterThan(glut);

        var costed = new Money(100);
        var scarcePrice = RetailPricing.RetailPrice(costed, markupBp: 2000, scarce);
        var glutPrice = RetailPricing.RetailPrice(costed, markupBp: 2000, glut);
        scarcePrice.Units.Should().BeGreaterThan(glutPrice.Units);
        glutPrice.Units.Should().BeGreaterThanOrEqualTo(costed.Units); // retail never below cost
    }

    [Test]
    public void AllowanceIncome_ScalesWithSize()
    {
        var income = new AllowanceIncome(perCapitaAllowance: 5);
        income.GrantFor(size: 1000).Should().Be(new Money(5000));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: build failure — `RetailPricing`/`AllowanceIncome` don't exist.

- [ ] **Step 3: Implement the seam + helper**

`src/WorldEcon.Engine/Demand/IConsumerIncome.cs`:
```csharp
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Demand;

/// <summary>How a consumer's periodic income is computed. Phase 2 ships <see cref="AllowanceIncome"/>;
/// a later wage/labor phase swaps in an implementation derived from production labor.</summary>
public interface IConsumerIncome
{
    /// <summary>Income granted this period to a consumer representing <paramref name="size"/> people.</summary>
    Money GrantFor(long size);
}
```

`src/WorldEcon.Engine/Demand/AllowanceIncome.cs`:
```csharp
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Demand;

/// <summary>Flat per-capita allowance (a faucet). Money enters the model here; the loop is closed by
/// the future wage phase. Tunable per-capita amount.</summary>
public sealed class AllowanceIncome : IConsumerIncome
{
    private readonly long _perCapitaAllowance;

    // Default tuned so a consumer can afford its Essential tier with margin (demo: bread 50bp/capita).
    public AllowanceIncome(long perCapitaAllowance = 40) => _perCapitaAllowance = perCapitaAllowance;

    public Money GrantFor(long size) => new(size * _perCapitaAllowance);
}
```

`src/WorldEcon.Engine/Demand/RetailPricing.cs`:
```csharp
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Demand;

/// <summary>Scarcity-flexing retail pricing: the markup flexes with the town's supply/demand ratio
/// (same scarcity knobs as wholesale pricing), and the price is built off the shop's own cost basis.</summary>
public static class RetailPricing
{
    /// <summary>Per (settlement, good) scarcity multiplier in bp (10000 = 1.0), clamped to the world's
    /// price-multiplier band. demand/supply > 1 raises it; glut floors it.</summary>
    public static long ScarcityMultBp(long demand, long supply, World world)
    {
        long s = System.Math.Max(supply, 1);
        long scarcityBp = FixedMath.DivRound(demand * FixedMath.BpScale, s);
        return System.Math.Clamp(
            FixedMath.PowBpInt(scarcityBp, world.ElasticityExponent),
            world.MinPriceMultBp, world.MaxPriceMultBp);
    }

    /// <summary>retail = cost × (1 + markup×scarcityMult). Never below cost (markup/scarcity are non-negative).</summary>
    public static Money RetailPrice(Money costBasis, int markupBp, long scarcityMultBp)
    {
        long effectiveMarkupBp = FixedMath.MulBp(markupBp, scarcityMultBp);
        return new Money(costBasis.Units + FixedMath.MulBp(costBasis.Units, effectiveMarkupBp));
    }
}
```
> Verify `FixedMath.BpScale`, `FixedMath.DivRound`, `FixedMath.PowBpInt`, `FixedMath.MulBp`, and `World.ElasticityExponent`/`MinPriceMultBp`/`MaxPriceMultBp` exist (they are used verbatim in `PricingPhase`). If `World.Create` needs different args than `("T", 1UL, CalendarDefinition.Default, "1.0.0")`, adjust the test helper to match (check `World.Create`).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Demand tests/WorldEcon.Engine.Tests.Unit/RetailPricingTests.cs
git commit -m "feat(demand): IConsumerIncome seam + AllowanceIncome + RetailPricing helper"
```

---

## Task 4: Engine — consumer spawn + income phases

**Files:**
- Create: `src/WorldEcon.Engine/Phases/ConsumerSpawnPhase.cs`, `ConsumerIncomePhase.cs`
- Modify: `src/WorldEcon.Engine/StandardPhases.cs` (register the two new weekly phases)
- Test: `tests/WorldEcon.Engine.Tests.Unit/ConsumerSpawnIncomeTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/ConsumerSpawnIncomeTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumerSpawnIncomeTests
{
    [Test]
    public async Task Advance_Week_SpawnsConsumers_AndGrantsIncome()
    {
        var s = await LogTestWorld.CreateAsync(); // settlement population 50000
        try
        {
            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerWeek);

            var consumers = await s.Db.Consumers.Where(c => c.Seat == s.Settlement.Id).ToListAsync();
            consumers.Should().NotBeEmpty();                              // population/Size consumers
            consumers.Sum(c => c.Size).Should().BeGreaterThan(0);
            consumers.Should().OnlyContain(c => c.Budget.Units > 0);      // income granted
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: build failure — `ConsumerSpawnPhase`/`ConsumerIncomePhase` don't exist.

- [ ] **Step 3: Implement the phases**

`src/WorldEcon.Engine/Phases/ConsumerSpawnPhase.cs` (mirror `MerchantSpawnPhase`):
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>Weekly: ensure each settlement has population-scaled representative consumers seated there.
/// Spawn-only; new consumers start with an empty budget (the income phase funds them). Mirror of
/// <see cref="MerchantSpawnPhase"/>.</summary>
public sealed class ConsumerSpawnPhase : ISimulationPhase
{
    // NOTE: tunable; promote to World params later.
    public const long DefaultConsumerSize = 1000;

    public string Name => "ConsumerSpawn";
    public int Order => 6;                       // after MerchantSpawn (5), before ConsumerIncome (7)
    public long CadenceTicks => Tick.DefaultMinutesPerWeek;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var worldId = ctx.World.Id;

        var settlements = (await ctx.Db.Settlements.Where(s => s.WorldId == worldId).ToListAsync())
            .OrderBy(s => s.Id.Value).ToList();

        var consumers = await LoadConsumers(ctx, worldId);
        var countBySeat = consumers.GroupBy(c => c.Seat).ToDictionary(g => g.Key, g => g.Count());

        foreach (var settlement in settlements)
        {
            long target = Math.Max(1, settlement.Population / DefaultConsumerSize);
            long existing = countBySeat.TryGetValue(settlement.Id, out var c) ? c : 0;
            for (long i = 0; i < target - existing; i++)
            {
                var consumer = RepresentativeConsumer.Create(worldId, settlement.Id, DefaultConsumerSize, Money.Zero).Value;
                ctx.Db.Consumers.Add(consumer);
            }
        }
    }

    private static async Task<List<RepresentativeConsumer>> LoadConsumers(SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.Consumers.Where(c => c.WorldId == worldId).ToListAsync();
        var byId = fromDb.ToDictionary(c => c.Id);
        foreach (var local in ctx.Db.Consumers.Local.Where(c => c.WorldId == worldId))
            byId[local.Id] = local;
        return byId.Values.ToList();
    }
}
```

`src/WorldEcon.Engine/Phases/ConsumerIncomePhase.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine.Demand;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>Weekly "paycheck": grants each consumer this period's income via the configured
/// <see cref="IConsumerIncome"/> strategy (default <see cref="AllowanceIncome"/>). The strategy is the
/// seam a future wage/labor phase replaces.</summary>
public sealed class ConsumerIncomePhase : ISimulationPhase
{
    private readonly IConsumerIncome _income;

    public ConsumerIncomePhase(IConsumerIncome? income = null) => _income = income ?? new AllowanceIncome();

    public string Name => "ConsumerIncome";
    public int Order => 7;
    public long CadenceTicks => Tick.DefaultMinutesPerWeek;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var worldId = ctx.World.Id;

        var fromDb = await ctx.Db.Consumers.Where(c => c.WorldId == worldId).ToListAsync();
        var byId = fromDb.ToDictionary(c => c.Id);
        foreach (var local in ctx.Db.Consumers.Local.Where(c => c.WorldId == worldId))
            byId[local.Id] = local;

        foreach (var consumer in byId.Values.OrderBy(c => c.Id.Value))
            consumer.Earn(_income.GrantFor(consumer.Size));
    }
}
```

In `StandardPhases.cs`, add `new Phases.ConsumerSpawnPhase()` and `new Phases.ConsumerIncomePhase()` to the array (keep `ConsumptionPhase` for now — it's removed in Task 5).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `Passed!` (new test green; existing tests unaffected — the new phases only add consumers, which nothing consumes yet).

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Phases/ConsumerSpawnPhase.cs src/WorldEcon.Engine/Phases/ConsumerIncomePhase.cs \
        src/WorldEcon.Engine/StandardPhases.cs tests/WorldEcon.Engine.Tests.Unit/ConsumerSpawnIncomeTests.cs
git commit -m "feat(demand): consumer spawn + income phases"
```

---

## Task 5: Engine — `ConsumerDemandPhase` (replaces `ConsumptionPhase`)

**Files:**
- Create: `src/WorldEcon.Engine/Phases/ConsumerDemandPhase.cs`
- Delete: `src/WorldEcon.Engine/Phases/ConsumptionPhase.cs`
- Modify: `src/WorldEcon.Engine/StandardPhases.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/ConsumerDemandTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/ConsumerDemandTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumerDemandTests
{
    [Test]
    public async Task Consumer_BuysFromShop_PaysTill_LogsSale()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var grain = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, 0, true, consumptionPerCapitaBp: 100, NeedTier.Essential).Value;
            s.Db.Goods.Add(grain);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 2000, Money.Zero).Value; // 20% markup
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, grain.Id, 100_000, new Money(10)).Value);
            // A funded consumer representing 100 people (demand = 100 × 100bp = 1 grain/day).
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(1_000_000)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            // Shop stock fell, till rose, a Shop-scoped Trade event was logged.
            var stock = await s.Db.Stockpiles.SingleAsync(x => x.GoodId == grain.Id);
            stock.Quantity.Should().BeLessThan(100_000);
            (await s.Db.Shops.SingleAsync(x => x.Id == shop.Id)).Till.Units.Should().BeGreaterThan(0);
            (await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.Trade && e.OriginKind == LogScopeKind.Shop))
                .Should().BeGreaterThan(0);
            // No free-consumption "Consumed" events any more.
            (await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.Consumed)).Should().Be(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Consumer_BuysEssentialBeforeComfort_WhenBudgetLimited()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var bread = Good.Create(s.World.Id, "Bread", GoodCategory.Food, new Money(10), "loaf",
                SizeClass.Small, 0, true, 100, NeedTier.Essential).Value;
            var lute = Good.Create(s.World.Id, "Lute", GoodCategory.Luxury, new Money(10), "lute",
                SizeClass.Small, 0, false, 100, NeedTier.Comfort).Value;
            s.Db.Goods.AddRange(bread, lute);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Store", 0, Money.Zero).Value; // 0 markup → retail≈cost=10
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, bread.Id, 1000, new Money(10)).Value);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, lute.Id, 1000, new Money(10)).Value);
            // demand each = 100 × 100bp = 1/day; budget only enough for ~1 item (~10).
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(10)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var breadLeft = (await s.Db.Stockpiles.SingleAsync(x => x.GoodId == bread.Id)).Quantity;
            var luteLeft = (await s.Db.Stockpiles.SingleAsync(x => x.GoodId == lute.Id)).Quantity;
            breadLeft.Should().BeLessThan(1000);  // bought essential bread
            luteLeft.Should().Be(1000);           // no budget left for comfort lute
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: build failure — `ConsumerDemandPhase` doesn't exist (and `StandardPhases` still has `ConsumptionPhase`).

- [ ] **Step 3: Implement `ConsumerDemandPhase`**

`src/WorldEcon.Engine/Phases/ConsumerDemandPhase.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Demand;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>Daily demand phase (replaces the free ConsumptionPhase): each settlement's consumers buy
/// their tiered needs (Essential→Standard→Comfort) from shops, cheapest-retail-price first, within
/// budget. Purchases credit the shop till and emit one Shop-scoped Trade event per shop·good·day;
/// unmet demand emits a settlement Stockout. Stable id ordering throughout for determinism.</summary>
public sealed class ConsumerDemandPhase : ISimulationPhase
{
    public string Name => "ConsumerDemand";
    public int Order => 20;                       // takes ConsumptionPhase's slot
    public long CadenceTicks => Tick.DefaultMinutesPerDay;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var worldId = ctx.World.Id;

        var consumableGoods = (await ctx.Db.Goods
                .Where(g => g.WorldId == worldId && g.ConsumptionPerCapitaBp > 0)
                .ToListAsync())
            .OrderBy(g => (int)g.Need).ThenBy(g => g.Id.Value)   // tier order, then stable id
            .ToList();
        if (consumableGoods.Count == 0)
            return;

        var settlements = (await ctx.Db.Settlements.Where(s => s.WorldId == worldId).ToListAsync())
            .OrderBy(s => s.Id.Value).ToList();

        foreach (var settlement in settlements)
        {
            var consumers = (await LoadConsumers(ctx, worldId))
                .Where(c => c.Seat == settlement.Id).OrderBy(c => c.Id.Value).ToList();
            if (consumers.Count == 0)
                continue;

            // Per-good scarcity multiplier from the day's total demand vs supply (fixed for the day).
            var scarcityByGood = new Dictionary<GoodId, long>();
            var supplyByGood = new Dictionary<GoodId, long>();
            foreach (var good in consumableGoods)
            {
                long demand = consumers.Sum(c => FixedMath.MulBp(c.Size, good.ConsumptionPerCapitaBp));
                long supply = await ShopMarket.TotalSupply(ctx, settlement.Id, good.Id);
                supplyByGood[good.Id] = supply;
                scarcityByGood[good.Id] = RetailPricing.ScarcityMultBp(demand, supply, ctx.World);
            }

            // Shops for till crediting + markup lookup.
            var shopsById = (await ShopMarket.ShopsIn(ctx, settlement.Id)).ToDictionary(sh => sh.Id.Value);

            // Accumulators emitted once per settlement-day.
            var sales = new Dictionary<(Guid Shop, GoodId Good), (long Qty, long Money)>();
            var unmet = new Dictionary<GoodId, (long Needed, long Got)>();

            foreach (var consumer in consumers)
            {
                bool broke = false;
                foreach (var good in consumableGoods)   // already in tier then id order
                {
                    if (broke) break;
                    long demand = FixedMath.MulBp(consumer.Size, good.ConsumptionPerCapitaBp);
                    if (demand <= 0) continue;
                    long needed = demand;

                    // Shops holding this good, priced by retail = cost × (1+markup×scarcity), cheapest first.
                    long scarcity = scarcityByGood[good.Id];
                    var offers = (await ShopMarket.StockpilesForGood(ctx, settlement.Id, good.Id))
                        .Where(st => st.Quantity > 0 && shopsById.ContainsKey(st.OwnerId))
                        .Select(st => (Stock: st, Shop: shopsById[st.OwnerId],
                                       Price: RetailPricing.RetailPrice(st.CostBasis, shopsById[st.OwnerId].MarkupBp, scarcity)))
                        .OrderBy(o => o.Price.Units).ThenBy(o => o.Shop.Id.Value)
                        .ToList();

                    foreach (var offer in offers)
                    {
                        if (needed <= 0) break;
                        long unit = offer.Price.Units;
                        long affordable = unit > 0 ? consumer.Budget.Units / unit : needed;
                        long take = Math.Min(needed, Math.Min(offer.Stock.Quantity, affordable));
                        if (take <= 0)
                        {
                            if (unit > consumer.Budget.Units) { broke = true; break; } // can't afford the cheapest → done for today
                            continue;
                        }
                        long cost = unit * take;
                        offer.Stock.Withdraw(take).OrThrow("consumer purchase");
                        consumer.Spend(new Money(cost));
                        offer.Shop.CreditTill(new Money(cost));
                        needed -= take;
                        var key = (offer.Shop.Id.Value, good.Id);
                        var prev = sales.TryGetValue(key, out var pv) ? pv : (0L, 0L);
                        sales[key] = (prev.Item1 + take, prev.Item2 + cost);
                    }

                    long got = demand - needed;
                    var u = unmet.TryGetValue(good.Id, out var uv) ? uv : (0L, 0L);
                    unmet[good.Id] = (u.Needed + demand, u.Got + got);
                }
            }

            // Emit one Trade per shop·good, one Stockout per good with a shortfall.
            var goodNames = consumableGoods.ToDictionary(g => g.Id, g => g.Name);
            foreach (var ((shopGuid, goodId), (qty, money)) in sales.OrderBy(k => k.Key.Shop).ThenBy(k => k.Key.Good.Value))
            {
                if (qty <= 0) continue;
                var shopName = shopsById.TryGetValue(shopGuid, out var sh) ? sh.Name : shopGuid.ToString();
                await ctx.Log.EmitAsync(LogEventType.Trade,
                    $"Sold {qty} {goodNames[goodId]} to townsfolk for {ctx.World.Currency.Format(new Money(money))} at {shopName}",
                    tick, LogScopeKind.Shop, shopGuid, settlement.Id,
                    payloadJson: $"{{\"qty\":{qty},\"money\":{money}}}");
            }
            foreach (var (goodId, (needed, got)) in unmet.OrderBy(k => k.Key.Value))
            {
                if (got >= needed) continue;
                await ctx.Log.EmitAsync(LogEventType.Stockout,
                    $"Consumers in {settlement.Name} couldn't afford/find {goodNames[goodId]} (needed {needed}, got {got})",
                    tick, LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);
            }
        }
    }

    private static async Task<List<RepresentativeConsumer>> LoadConsumers(SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.Consumers.Where(c => c.WorldId == worldId).ToListAsync();
        var byId = fromDb.ToDictionary(c => c.Id);
        foreach (var local in ctx.Db.Consumers.Local.Where(c => c.WorldId == worldId))
            byId[local.Id] = local;
        return byId.Values.ToList();
    }
}
```
> `ctx.World.Currency.Format(Money)` exists (Phase: world-configurable currency). If the `payloadJson` interpolation risks a warning, it's a constant-shaped JSON string — fine.

Delete `ConsumptionPhase`:
```bash
git rm src/WorldEcon.Engine/Phases/ConsumptionPhase.cs
```

In `StandardPhases.cs`, **remove** `new Phases.ConsumptionPhase()` and **add** `new Phases.ConsumerDemandPhase()` (the spawn/income phases were added in Task 4). Final ordered set: MerchantSpawn(5), ConsumerSpawn(6), ConsumerIncome(7), Production(10), ConsumerDemand(20), Perishability(30), Pricing(40), Trade(50).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: the two new `ConsumerDemandTests` pass. **Pre-existing free-consumption tests will FAIL** (they assert the old free `ConsumptionPhase`/`Consumed` behavior) — that's expected; they're swept in Task 6. Confirm your new tests pass and that the failures are only the old consumption tests.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Phases/ConsumerDemandPhase.cs src/WorldEcon.Engine/StandardPhases.cs \
        tests/WorldEcon.Engine.Tests.Unit/ConsumerDemandTests.cs
git commit -m "feat(demand): ConsumerDemandPhase replaces free ConsumptionPhase"
```

---

## Task 6: Sweep old consumption tests; full green + granularity regression

**Files:**
- Modify/remove: Engine tests that asserted the old free-consumption behavior.
- Test: `tests/WorldEcon.Engine.Tests.Unit/ConsumerGranularityTests.cs`

- [ ] **Step 1: Find the stale tests**

Run:
```bash
grep -rn "ConsumptionPhase\|LogEventType.Consumed\|Consumption_\|EmitsConsumed\|DrawsDownMarketStock\|EmitsStockout_WhenMarketStockEmpty" tests --include=*.cs | grep -vE "obj/|bin/"
```
Expected hits: tests that exercised free consumption / the `Consumed` event (e.g. in `PhaseLoggingTests.cs`, `ConsumptionPerishabilityTests.cs`, `ConsumptionOnShopsTests.cs`).

- [ ] **Step 2: Update each stale test to the new model**

For each: the population no longer consumes for free — a **funded consumer** buys instead. Update the test to: seed a consumer (with budget) in the settlement and a shop with stock, advance a day, and assert the new behavior (shop stock fell / a `Trade` Shop-scoped event / a `Stockout` when unaffordable-or-empty). Tests that asserted a `Consumed` event should now assert a `Trade` (Shop scope) sale event. Tests that asserted "stockout when market empty" still hold (no stock → unmet → `Stockout`). DELETE tests whose entire premise was free consumption with no shop/consumer if they can't be meaningfully re-pointed (note which and why). PRESERVE the intent (goods get consumed/demanded; shortfalls log).
> If a re-pointed test reveals a real `ConsumerDemandPhase` bug (e.g. determinism or over-withdraw), STOP and report rather than weakening the test.

- [ ] **Step 3: Granularity regression**

`tests/WorldEcon.Engine.Tests.Unit/ConsumerGranularityTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumerGranularityTests
{
    private static async Task<(long stock, long till, long budget)> RunAsync(int chunks, long perChunkTicks)
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var grain = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, 0, true, 100, NeedTier.Essential).Value;
            s.Db.Goods.Add(grain);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 2000, Money.Zero).Value;
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, grain.Id, 1_000_000, new Money(10)).Value);
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(1_000_000)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            var engine = new TickEngine(StandardPhases.All());
            for (int i = 0; i < chunks; i++) await engine.AdvanceAsync(sim, perChunkTicks);

            var stock = (await s.Db.Stockpiles.SingleAsync(x => x.GoodId == grain.Id)).Quantity;
            var till = (await s.Db.Shops.SingleAsync(x => x.Id == shop.Id)).Till.Units;
            var budget = (await s.Db.Consumers.SingleAsync()).Budget.Units;
            return (stock, till, budget);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task DemandIsGranularityIndependent()
    {
        var single = await RunAsync(1, 6 * Tick.DefaultMinutesPerDay);
        var chunked = await RunAsync(6, Tick.DefaultMinutesPerDay);
        chunked.Should().Be(single); // same stock, till, budget after 6 days either way
    }
}
```

- [ ] **Step 4: Full build + every suite**

Run:
```bash
dotnet build
for p in SharedKernel Simulation Domain Persistence Application Engine Seeding Tui; do
  echo "=== $p ==="; dotnet run --project tests/WorldEcon.$p.Tests.Unit -c Release 2>&1 | grep -oE "Passed!|Failed!" | head -1
done
```
Expected: `0 Error(s) 0 Warning(s)` and `Passed!` everywhere. (Tui/Seeding may need their seeders updated — if a Tui/Seeding test fails because the demo world now has consumers or a good has a `NeedTier`, fix in Task 7; if it fails purely on the consumption change, address here.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(demand): sweep free-consumption tests onto consumer model; granularity regression"
```

---

## Task 7: Seeding — consumers + tiered demo goods

**Files:**
- Modify: `src/WorldEcon.Cli/DemoSeeder.cs`, `src/WorldEcon.Seeding/SeedImporter.cs`

- [ ] **Step 1: DemoSeeder — pre-seed consumers + tiered goods**

In `src/WorldEcon.Cli/DemoSeeder.cs`:
- Give a couple of demo goods explicit tiers and consumption rates so tiering is exercised. Keep `Bread` as `Essential` (it already has `consumptionPerCapitaBp: 50`). Add two goods, e.g.:
  ```csharp
  var cloth = Unwrap(Good.Create(world.Id, "Cloth", GoodCategory.Material, new Money(80), "bolt", SizeClass.Small, 0, true, consumptionPerCapitaBp: 10, NeedTier.Standard), "Cloth");
  var ale = Unwrap(Good.Create(world.Id, "Ale", GoodCategory.Luxury, new Money(40), "mug", SizeClass.Small, 720, true, consumptionPerCapitaBp: 20, NeedTier.Comfort), "Ale");
  ```
  Add them to `ctx.Goods.AddRange(...)`, and seed some shop stock for them in Hammerfell/Riverwood shops (e.g. a couple of `Stockpile.CreateForShop(...)` rows) so consumers have something to buy.
- Pre-seed a few consumers per settlement so day-1 demand exists (mirror the merchant pre-seed). Use an initial budget ≈ one week's allowance so they can buy before the first weekly income:
  ```csharp
  // ~one week's allowance (AllowanceIncome default 40/capita) so day-1 buying works.
  var hammerConsumer = Unwrap(RepresentativeConsumer.Create(world.Id, hammerfell.Id, 1000, new Money(40_000)), "Hammerfell consumer");
  var riverConsumer  = Unwrap(RepresentativeConsumer.Create(world.Id, riverwood.Id, 800,  new Money(32_000)), "Riverwood consumer");
  // ... ctx.Consumers.AddRange(hammerConsumer, riverConsumer);
  ```
  Add `ctx.Consumers.AddRange(...)` before `ctx.SaveChanges()`.

- [ ] **Step 2: SeedImporter — consumers + NeedTier**

In `src/WorldEcon.Seeding/SeedImporter.cs`:
- When creating goods, pass the seed's need tier if present (extend the seed good model with an optional `NeedTier`/string defaulting to `Essential`; map it). If extending the JSON model is out of scope for a first pass, default all imported goods to `Essential` (they already do via the optional param) — **note this** so JSON authors can add tiers later.
- Add consumer import: if a settlement declares consumers (extend `SeedSettlement` with an optional `Consumers` list of `{ size, budget }`), create `RepresentativeConsumer.Create(worldId, settlementId, size, new Money(budget))` and `db.Consumers.Add(...)` — mirror the merchant import block. If you keep the JSON model unchanged for now, skip consumer import and note that imported worlds get consumers from the weekly `ConsumerSpawnPhase` instead (acceptable — they appear after week 1).

- [ ] **Step 3: Smoke test**

```bash
rm -f /tmp/p2.db
dotnet run --project src/WorldEcon.Cli -c Release -- new /tmp/p2.db
dotnet run --project src/WorldEcon.Cli -c Release -- advance /tmp/p2.db 1w
command -v sqlite3 >/dev/null && sqlite3 /tmp/p2.db \
  "SELECT (SELECT COUNT(*) FROM consumers) consumers,
          (SELECT COUNT(*) FROM log_events WHERE Type='Trade') trades,
          (SELECT SUM(Till) FROM shops) total_tills;"
rm -f /tmp/p2.db
```
Expected: `consumers >= 2`, `trades > 0` (sales happened), `total_tills > 0` (shops earned).

- [ ] **Step 4: Run Seeding + CLI-affected suites**

Run: `dotnet run --project tests/WorldEcon.Seeding.Tests.Unit -c Release` → `Passed!` (update any seed test that now expects consumers/tiers).

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Cli/DemoSeeder.cs src/WorldEcon.Seeding/SeedImporter.cs tests/WorldEcon.Seeding.Tests.Unit
git commit -m "feat(demand): seed consumers + tiered demo goods"
```

---

## Task 8: UI/CLI — consumers resource + retail price on the board

**Files:**
- Modify: `src/WorldEcon.Tui/Navigation/Navigator.cs`, `NavView.cs`, `src/WorldEcon.Cli/CommandRunner.cs`
- Test: `tests/WorldEcon.Tui.Tests.Unit/ConsumersViewTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Tui.Tests.Unit/ConsumersViewTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Tui;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Tests.Unit;

public class ConsumersViewTests
{
    [Test]
    public async Task ConsumersRoot_ListsConsumers_WithSizeAndBudget()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);
            var settlement = await ctx.Settlements.FirstAsync();
            ctx.Consumers.Add(RepresentativeConsumer.Create(tui.World.Id, settlement.Id, 1000,
                new WorldEcon.SharedKernel.Money(500)).Value);
            await ctx.SaveChangesAsync();

            var nav = new Navigator();
            var view = await nav.RootAsync("consumers", tui);
            view.Columns.Should().ContainInOrder("Settlement", "Size", "Budget");
            view.Rows.Should().NotBeEmpty();
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release`
Expected: build/assert failure — `"consumers"` root not resolved.

- [ ] **Step 3: Add the consumers resource (Navigator)**

In `Navigator.cs`:
- Add `"consumers"` to the `Roots` array and a `TryResolveRoot` arm (`"consumers" or "consumer" => "consumers"`).
- Add a `RootAsync` arm `"consumers" => await ConsumersView(ctx, await AllConsumers(ctx), "Consumers")`.
- Add helpers (mirror `MerchantsView`/`AllMerchants`):
  ```csharp
  private async Task<List<RepresentativeConsumer>> AllConsumers(TuiContext ctx)
      => (await ctx.Db.Consumers.Where(c => c.WorldId == ctx.World.Id).ToListAsync())
          .OrderBy(c => c.Id.Value).ToList();

  private async Task<NavView> ConsumersView(TuiContext ctx, List<RepresentativeConsumer> consumers, string title)
  {
      var names = await Lookups.SettlementNamesAsync(ctx);
      var rows = consumers.Select(c => new NavRow(c.Id.Value.ToString(), NavKind.Leaf,
          new[] { names.Resolve(c.Seat.Value), c.Size.ToString(), ctx.FormatMoney(c.Budget) })).ToList();
      return new NavView(title, ["Settlement", "Size", "Budget"], rows);
  }
  ```
- Add a **Consumers** category to `CityChooserView` (a `CityCategory` row keyed `"consumers"`) and a `CityCategoryView` `"consumers"` case calling `ConsumersView` filtered to the settlement (mirror the `"merchants"` case). Add the count to the chooser.

- [ ] **Step 4: Marketplace board → retail price**

In `MarketBoardAsync`, change the **`Price`** column from `ctx.FormatMoney(s.MarketPrice)` to the **retail price**: compute `RetailPricing.RetailPrice(s.CostBasis, shop.MarkupBp, scarcityMult)` where `scarcityMult` is `RetailPricing.ScarcityMultBp(demand, supply, ctx.World)` for that settlement+good. The UI doesn't have the day's consumer demand handy; for the board, approximate demand as the population-based consumption (`settlement.Population × good.ConsumptionPerCapitaBp`) — load settlements + goods, compute supply via the shop aggregate already in the method. Keep `Min Price` = cost basis. Add the wholesale `MarketPrice` to the good/shop **details** view (label it "Wholesale"). `RetailPricing` is in `WorldEcon.Engine.Demand` — the Tui project already references Engine (used by `AdvanceAction`), so `using WorldEcon.Engine.Demand;` is available.
> Keep this read-only and side-effect free. If wiring demand into the board is awkward, an acceptable simplification is to show the retail price at the **shop's base markup with scarcityMult = 1.0** (i.e. `RetailPrice(cost, markup, 10000)`) and note it; the live scarcity-flexed price is what consumers actually pay in the sim. Pick one and note it in the commit.

- [ ] **Step 5: CLI `consumers` command**

In `CommandRunner.cs`, add `"consumers" => await CmdConsumers(args)` to the switch and implement `CmdConsumers` mirroring `CmdMerchants` (open ctx, list consumers grouped/seated, print `Seat | Size | Budget` using the world currency formatter). Update `PrintUsage`.

- [ ] **Step 6: Run + smoke**

Run: `dotnet build` → 0/0; `dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release` → `Passed!`.
tmux smoke (paste pane): `new` + `advance 1w`, open the TUI, `:consumers`, confirm the list; open a settlement Market and confirm `Price` reflects retail.

- [ ] **Step 7: Commit**

```bash
git add src/WorldEcon.Tui src/WorldEcon.Cli/CommandRunner.cs tests/WorldEcon.Tui.Tests.Unit/ConsumersViewTests.cs
git commit -m "feat(demand): consumers resource (TUI+CLI); marketplace board shows retail price"
```

---

## Final verification

- [ ] **Every suite green:**
```bash
for p in SharedKernel Simulation Domain Persistence Application Engine Seeding Tui; do
  echo "=== $p ==="; dotnet run --project tests/WorldEcon.$p.Tests.Unit -c Release 2>&1 | grep -E "Passed!|Failed!" | head -1
done
```
- [ ] **Warnings-as-errors build:** `dotnet build` → `0 Error(s) 0 Warning(s)`.
- [ ] **End-to-end:** `new` + `advance 1w` → consumers exist, `Trade` sale events logged, shop tills > 0, some `Stockout` events when a need can't be met; the marketplace board's `Price` shows retail.
- [ ] **Final code review** (subagent-driven-development final reviewer) against this plan + the spec; then `superpowers:finishing-a-development-branch`.
- [ ] **Update memory + decisions log:** Phase 2 shipped; roadmap position (Phase 3 = retail restocking + trade/industrial over shops + caravan stalls next; wage loop swaps the income seam later).
