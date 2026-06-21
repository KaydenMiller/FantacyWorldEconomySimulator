# Static Economy Implementation Plan (Plan 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the core DM read — for a good in a settlement, list each shop's sale price, stock, cost basis, and margin — backed by `Good`/`Stockpile`/`Shop` entities and first-class weighted-average cost basis.

**Architecture:** Add the Economy aggregate to `WorldEcon.Domain` (reusing 1b's strongly-typed-ID + `ErrorOr` + `AggregateRoot` pattern), persist it via the existing `WorldDbContext` (new configs + migration + repositories), and introduce a thin `WorldEcon.Application` project holding the price/margin query (EF-free; it composes Domain repository interfaces). Cost basis is weighted-average behind the `ICostBasisValuation` seam (per-lot deferred). Shop sale price = `cost_basis × (1 + markup)` — pure integer/fixed-point math via the existing `FixedMath`.

**Tech Stack:** C# / .NET 10, EF Core 10 + SQLite, `ErrorOr<T>`, `Money`/`FixedMath` from SharedKernel, TUnit + FluentAssertions. Determinism rules carry forward (integer/fixed-point only; explicit in-memory ordering on list reads).

**Reference spec:** `docs/superpowers/specs/2026-06-20-world-economy-sim-csharp-build-design.md` (Build §6.4 economy, §9.1/§9.3 pricing, §15 phase-2 query). **Builds on:** Plans 1a + 1b (merged to `master`).

### Scope decisions (read first)
- **Delivered:** `Good`, `Stockpile` (weighted-average cost basis), `Shop` (markup + till), repositories, and the **price/margin query** via a new `WorldEcon.Application` project.
- **Deferred to Plan 2b (supply/demand market pricing):** the `scarcity^elasticity` market-price curve, fractional-exponent `FixedMath.PowBp` (needs fixed-point ln/exp), demographics-driven demand (`Race`/`SettlementDemographic`), and settlement-market stockpiles as a *priced* concept. The `StockpileOwnerKind` enum includes `SettlementMarket`/`Agent` so they fit later without migration churn, but only `Shop`-owned stockpiles are exercised here.
- **Deferred to stage 3:** "next shipment" (next inbound caravan / completing batch) — the query omits it for now; production/trade don't exist yet.
- **Cost basis is per-unit `Money`.** Byproduct cost allocation (§7.3) and `ICostBasisValuation.AllocateByproduct` are stage-3 (production) concerns — only `Blend` (weighted average) is built now.
- Pattern reuse: IDs hand-written; EF converters per-ID (source-gen deferred, per 1b carry-forward); list reads order in memory by `Id.Value`.

---

## File structure

| File | Responsibility |
|---|---|
| `src/WorldEcon.Application/WorldEcon.Application.csproj` | New project (refs Domain only) |
| `src/WorldEcon.Domain/Economy/Ids.cs` | `GoodId`, `StockpileId`, `ShopId` |
| `src/WorldEcon.Domain/Economy/Enums.cs` | `GoodCategory`, `SizeClass`, `StockpileOwnerKind` |
| `src/WorldEcon.Domain/Economy/Good.cs` | Good entity |
| `src/WorldEcon.Domain/Economy/ICostBasisValuation.cs` | Valuation seam |
| `src/WorldEcon.Domain/Economy/WeightedAverageValuation.cs` | Weighted-average impl |
| `src/WorldEcon.Domain/Economy/Stockpile.cs` | Stockpile + Deposit/Withdraw |
| `src/WorldEcon.Domain/Economy/Shop.cs` | Shop + `Quote` (margin math) + `ShopQuote` |
| `src/WorldEcon.Domain/Economy/I{Good,Shop,Stockpile}Repository.cs` | Repo interfaces (EF-free) |
| `src/WorldEcon.Persistence/Conversions/EconomyIdConverters.cs` | Converters for the new IDs |
| `src/WorldEcon.Persistence/Configurations/{Good,Stockpile,Shop}Configuration.cs` | EF configs |
| `src/WorldEcon.Persistence/Repositories/{Good,Shop,Stockpile}Repository.cs` | Repo impls |
| `src/WorldEcon.Persistence/Migrations/*` | `AddEconomy` migration |
| `src/WorldEcon.Application/Queries/PriceMarginQuery.cs` | The price/margin query + result DTOs |
| `tests/WorldEcon.Domain.Tests.Unit/Economy/*` | Good/valuation/stockpile/shop tests |
| `tests/WorldEcon.Persistence.Tests.Unit/EconomyRepositoryTests.cs` | Round-trip + repo tests |
| `tests/WorldEcon.Application.Tests.Unit/*` | Price/margin query test |

---

## Task 0: Scaffold the Application project

**Files:** new project + test project.

- [ ] **Step 1: Create `src/WorldEcon.Application/WorldEcon.Application.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>WorldEcon.Application</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\WorldEcon.Domain\WorldEcon.Domain.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create `tests/WorldEcon.Application.Tests.Unit/WorldEcon.Application.Tests.Unit.csproj`** (it references Application + Persistence so tests can seed a real SQLite DB and wire repo impls):
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
    <ProjectReference Include="..\..\src\WorldEcon.Application\WorldEcon.Application.csproj" />
    <ProjectReference Include="..\..\src\WorldEcon.Persistence\WorldEcon.Persistence.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Register both in the solution**
```bash
dotnet sln add src/WorldEcon.Application/WorldEcon.Application.csproj
dotnet sln add tests/WorldEcon.Application.Tests.Unit/WorldEcon.Application.Tests.Unit.csproj
```

- [ ] **Step 4: Add a temporary placeholder so the Application project has content and builds**

Create `src/WorldEcon.Application/AssemblyMarker.cs`:
```csharp
namespace WorldEcon.Application;

/// <summary>Marker type for assembly references / DI scanning. Replaced by real types in this plan.</summary>
public sealed class AssemblyMarker;
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: success, 0 warnings. (The Application test project has no tests yet — fine; it just needs to build.)

- [ ] **Step 6: Commit**
```bash
git add -A
git commit -m "chore: scaffold WorldEcon.Application project"
```

---

## Task 1: Economy IDs & enums

**Files:**
- Create: `src/WorldEcon.Domain/Economy/Ids.cs`, `Enums.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/Economy/EconomyIdTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/Economy/EconomyIdTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class EconomyIdTests
{
    [Test]
    public void New_ProducesUniqueNonEmptyIds()
    {
        GoodId.New().Should().NotBe(GoodId.New());
        GoodId.New().Value.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void Ids_AreStronglyTyped()
        => ShopId.New().Should().BeAssignableTo<IStronglyTypedId>();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: compile failure.

- [ ] **Step 3: Implement**

Create `src/WorldEcon.Domain/Economy/Ids.cs`:
```csharp
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public readonly record struct GoodId(Guid Value) : IStronglyTypedId { public static GoodId New() => new(Guid.NewGuid()); }
public readonly record struct StockpileId(Guid Value) : IStronglyTypedId { public static StockpileId New() => new(Guid.NewGuid()); }
public readonly record struct ShopId(Guid Value) : IStronglyTypedId { public static ShopId New() => new(Guid.NewGuid()); }
```

Create `src/WorldEcon.Domain/Economy/Enums.cs`:
```csharp
namespace WorldEcon.Domain.Economy;

public enum GoodCategory { Raw = 0, Food = 1, Material = 2, Tool = 3, Weapon = 4, Armor = 5, Luxury = 6, Potion = 7, Misc = 8 }

public enum SizeClass { Tiny = 0, Small = 1, Medium = 2, Large = 3, Bulky = 4 }

/// <summary>What kind of entity owns a stockpile. Only Shop is exercised in Plan 2.</summary>
public enum StockpileOwnerKind { SettlementMarket = 0, Shop = 1, Agent = 2 }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: all pass.

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.Domain/Economy/Ids.cs src/WorldEcon.Domain/Economy/Enums.cs tests/WorldEcon.Domain.Tests.Unit/Economy/EconomyIdTests.cs
git commit -m "feat: add economy strongly-typed IDs and enums"
```

---

## Task 2: Good entity

**Files:**
- Create: `src/WorldEcon.Domain/Economy/Good.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/Economy/GoodTests.cs`

> `BaseValue` is the canonical reference value per base unit (feeds the deferred scarcity curve). `ShelfLifeTicks = 0` means imperishable. Invariants: name + base unit non-blank; base value ≥ 0; shelf-life ≥ 0.

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/Economy/GoodTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class GoodTests
{
    [Test]
    public void Create_SetsFields_DefaultProvenanceAuthored()
    {
        var g = Good.Create(WorldId.New(), "Health Potion", GoodCategory.Potion,
            baseValue: new Money(5000), baseUnit: "vial", SizeClass.Small,
            shelfLifeTicks: 0, divisible: false).Value;
        g.Name.Should().Be("Health Potion");
        g.Category.Should().Be(GoodCategory.Potion);
        g.BaseValue.Should().Be(new Money(5000));
        g.BaseUnit.Should().Be("vial");
        g.Size.Should().Be(SizeClass.Small);
        g.ShelfLifeTicks.Should().Be(0);
        g.Divisible.Should().BeFalse();
        g.Provenance.Should().Be(Provenance.Authored);
    }

    [Test]
    public void Create_RejectsBlankName()
        => Good.Create(WorldId.New(), " ", GoodCategory.Misc, Money.Zero, "u", SizeClass.Small, 0, false)
            .IsError.Should().BeTrue();

    [Test]
    public void Create_RejectsNegativeBaseValue()
        => Good.Create(WorldId.New(), "X", GoodCategory.Misc, new Money(-1), "u", SizeClass.Small, 0, false)
            .IsError.Should().BeTrue();

    [Test]
    public void Create_RejectsNegativeShelfLife()
        => Good.Create(WorldId.New(), "X", GoodCategory.Misc, Money.Zero, "u", SizeClass.Small, -1, false)
            .IsError.Should().BeTrue();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: compile failure.

- [ ] **Step 3: Implement**

Create `src/WorldEcon.Domain/Economy/Good.cs`:
```csharp
using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public sealed class Good : AggregateRoot<GoodId>
{
    public WorldId WorldId { get; }
    public string Name { get; private set; }
    public GoodCategory Category { get; private set; }
    public Money BaseValue { get; private set; }
    public string BaseUnit { get; private set; }
    public SizeClass Size { get; private set; }
    public long ShelfLifeTicks { get; private set; } // 0 = imperishable
    public bool Divisible { get; private set; }
    public Provenance Provenance { get; private set; }

    private Good() : base(default) { Name = null!; BaseUnit = null!; } // EF

    private Good(GoodId id, WorldId worldId, string name, GoodCategory category, Money baseValue,
        string baseUnit, SizeClass size, long shelfLifeTicks, bool divisible, Provenance provenance) : base(id)
    {
        WorldId = worldId;
        Name = name;
        Category = category;
        BaseValue = baseValue;
        BaseUnit = baseUnit;
        Size = size;
        ShelfLifeTicks = shelfLifeTicks;
        Divisible = divisible;
        Provenance = provenance;
    }

    public static ErrorOr<Good> Create(WorldId worldId, string name, GoodCategory category, Money baseValue,
        string baseUnit, SizeClass size, long shelfLifeTicks, bool divisible)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("good.name.blank", "Good name must not be blank.");
        if (string.IsNullOrWhiteSpace(baseUnit))
            return Error.Validation("good.baseunit.blank", "Base unit must not be blank.");
        if (baseValue.IsNegative)
            return Error.Validation("good.basevalue.negative", "Base value must not be negative.");
        if (shelfLifeTicks < 0)
            return Error.Validation("good.shelflife.negative", "Shelf life must not be negative.");

        return new Good(GoodId.New(), worldId, name.Trim(), category, baseValue,
            baseUnit.Trim(), size, shelfLifeTicks, divisible, Provenance.Authored);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: all pass.

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.Domain/Economy/Good.cs tests/WorldEcon.Domain.Tests.Unit/Economy/GoodTests.cs
git commit -m "feat: add Good entity with validation"
```

---

## Task 3: Cost-basis valuation (weighted average)

**Files:**
- Create: `src/WorldEcon.Domain/Economy/ICostBasisValuation.cs`, `WeightedAverageValuation.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/Economy/WeightedAverageValuationTests.cs`

> `Blend` computes a new **per-unit** cost basis when `incomingQty` units at `incomingUnitBasis` merge into `existingQty` units at `existingUnitBasis`. Pure integer/fixed-point (half-to-even via `FixedMath.DivRound`). This is the seam that lets per-lot/FIFO replace weighted-average later (spec §3.2, §6.4).

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/Economy/WeightedAverageValuationTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class WeightedAverageValuationTests
{
    private static readonly ICostBasisValuation Sut = new WeightedAverageValuation();

    [Test]
    public void Blend_IntoEmpty_TakesIncomingBasis()
        => Sut.Blend(0, Money.Zero, 10, new Money(100)).Should().Be(new Money(100));

    [Test]
    public void Blend_EqualQuantities_AveragesBasis()
        => Sut.Blend(10, new Money(100), 10, new Money(200)).Should().Be(new Money(150));

    [Test]
    public void Blend_WeightsByQuantity()
        // (90*100 + 10*200) / 100 = 110
        => Sut.Blend(90, new Money(100), 10, new Money(200)).Should().Be(new Money(110));

    [Test]
    public void Blend_RoundsHalfToEven()
        // (1*100 + 1*101)/2 = 100.5 -> 100 (even)
        => Sut.Blend(1, new Money(100), 1, new Money(101)).Should().Be(new Money(100));

    [Test]
    public void Blend_RejectsNonPositiveIncoming()
    {
        var act = () => Sut.Blend(10, new Money(100), 0, new Money(50));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: compile failure.

- [ ] **Step 3: Implement**

Create `src/WorldEcon.Domain/Economy/ICostBasisValuation.cs`:
```csharp
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Economy;

/// <summary>
/// Computes per-unit cost basis on deposit. Weighted-average now; per-lot/FIFO promotable
/// behind this seam later (spec §3.2, §6.4).
/// </summary>
public interface ICostBasisValuation
{
    /// <summary>New per-unit basis after merging <paramref name="incomingQty"/> units at
    /// <paramref name="incomingUnitBasis"/> into <paramref name="existingQty"/> units at
    /// <paramref name="existingUnitBasis"/>. <paramref name="incomingQty"/> must be &gt; 0.</summary>
    Money Blend(long existingQty, Money existingUnitBasis, long incomingQty, Money incomingUnitBasis);
}
```

Create `src/WorldEcon.Domain/Economy/WeightedAverageValuation.cs`:
```csharp
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Economy;

public sealed class WeightedAverageValuation : ICostBasisValuation
{
    public Money Blend(long existingQty, Money existingUnitBasis, long incomingQty, Money incomingUnitBasis)
    {
        if (incomingQty <= 0)
            throw new ArgumentOutOfRangeException(nameof(incomingQty), "Incoming quantity must be positive.");
        if (existingQty < 0)
            throw new ArgumentOutOfRangeException(nameof(existingQty), "Existing quantity must not be negative.");

        long totalQty = existingQty + incomingQty;
        long totalCost = existingQty * existingUnitBasis.Units + incomingQty * incomingUnitBasis.Units;
        return new Money(FixedMath.DivRound(totalCost, totalQty));
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: all pass.

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.Domain/Economy/ICostBasisValuation.cs src/WorldEcon.Domain/Economy/WeightedAverageValuation.cs tests/WorldEcon.Domain.Tests.Unit/Economy/WeightedAverageValuationTests.cs
git commit -m "feat: add ICostBasisValuation seam with weighted-average implementation"
```

---

## Task 4: Stockpile entity

**Files:**
- Create: `src/WorldEcon.Domain/Economy/Stockpile.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/Economy/StockpileTests.cs`

> A stockpile holds a quantity of one good owned by one owner, with a per-unit cost basis. `Deposit` blends cost basis via the valuation seam; `Withdraw` reduces quantity (weighted-average basis is unchanged by withdrawal). Invariant: quantity never negative. `OwnerId` is a raw `Guid` because owners are polymorphic (a `ShopId`/`SettlementId`); helper factories take the typed ids.

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/Economy/StockpileTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class StockpileTests
{
    private static readonly ICostBasisValuation Valuation = new WeightedAverageValuation();

    private static Stockpile NewShopStockpile(out ShopId shop, out GoodId good)
    {
        shop = ShopId.New();
        good = GoodId.New();
        return Stockpile.CreateForShop(WorldId.New(), shop, good, quantity: 10, unitCostBasis: new Money(100)).Value;
    }

    [Test]
    public void CreateForShop_SetsOwnerAndFields()
    {
        var s = NewShopStockpile(out var shop, out var good);
        s.OwnerKind.Should().Be(StockpileOwnerKind.Shop);
        s.OwnerId.Should().Be(shop.Value);
        s.GoodId.Should().Be(good);
        s.Quantity.Should().Be(10);
        s.CostBasis.Should().Be(new Money(100));
    }

    [Test]
    public void Create_RejectsNegativeQuantity()
        => Stockpile.CreateForShop(WorldId.New(), ShopId.New(), GoodId.New(), -1, Money.Zero)
            .IsError.Should().BeTrue();

    [Test]
    public void Deposit_BlendsCostBasis_AndAddsQuantity()
    {
        var s = NewShopStockpile(out _, out _); // 10 @ 100
        s.Deposit(10, new Money(200), Valuation); // -> 20 @ 150
        s.Quantity.Should().Be(20);
        s.CostBasis.Should().Be(new Money(150));
    }

    [Test]
    public void Withdraw_ReducesQuantity_KeepsBasis()
    {
        var s = NewShopStockpile(out _, out _); // 10 @ 100
        var result = s.Withdraw(4);
        result.IsError.Should().BeFalse();
        s.Quantity.Should().Be(6);
        s.CostBasis.Should().Be(new Money(100));
    }

    [Test]
    public void Withdraw_MoreThanOnHand_IsError_AndDoesNotMutate()
    {
        var s = NewShopStockpile(out _, out _); // 10
        s.Withdraw(11).IsError.Should().BeTrue();
        s.Quantity.Should().Be(10);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: compile failure.

- [ ] **Step 3: Implement**

Create `src/WorldEcon.Domain/Economy/Stockpile.cs`:
```csharp
using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public sealed class Stockpile : AggregateRoot<StockpileId>
{
    public WorldId WorldId { get; }
    public StockpileOwnerKind OwnerKind { get; private set; }
    public Guid OwnerId { get; private set; }
    public GoodId GoodId { get; private set; }
    public long Quantity { get; private set; }
    public Money CostBasis { get; private set; } // per-unit

    private Stockpile() : base(default) { } // EF

    private Stockpile(StockpileId id, WorldId worldId, StockpileOwnerKind ownerKind, Guid ownerId,
        GoodId goodId, long quantity, Money costBasis) : base(id)
    {
        WorldId = worldId;
        OwnerKind = ownerKind;
        OwnerId = ownerId;
        GoodId = goodId;
        Quantity = quantity;
        CostBasis = costBasis;
    }

    public static ErrorOr<Stockpile> CreateForShop(WorldId worldId, ShopId shop, GoodId good, long quantity, Money unitCostBasis)
        => Create(worldId, StockpileOwnerKind.Shop, shop.Value, good, quantity, unitCostBasis);

    public static ErrorOr<Stockpile> Create(WorldId worldId, StockpileOwnerKind ownerKind, Guid ownerId,
        GoodId good, long quantity, Money unitCostBasis)
    {
        if (quantity < 0)
            return Error.Validation("stockpile.quantity.negative", "Quantity must not be negative.");
        if (unitCostBasis.IsNegative)
            return Error.Validation("stockpile.costbasis.negative", "Cost basis must not be negative.");

        return new Stockpile(StockpileId.New(), worldId, ownerKind, ownerId, good, quantity, unitCostBasis);
    }

    public void Deposit(long quantity, Money incomingUnitBasis, ICostBasisValuation valuation)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Deposit quantity must be positive.");
        CostBasis = valuation.Blend(Quantity, CostBasis, quantity, incomingUnitBasis);
        Quantity += quantity;
    }

    public ErrorOr<Success> Withdraw(long quantity)
    {
        if (quantity <= 0)
            return Error.Validation("stockpile.withdraw.nonpositive", "Withdraw quantity must be positive.");
        if (quantity > Quantity)
            return Error.Validation("stockpile.withdraw.insufficient", "Not enough on hand to withdraw.");
        Quantity -= quantity;
        return Result.Success;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: all pass. (If `Result.Success`/`Success` differ in ErrorOr 2.x, use the installed API — e.g. `return new Success();` — and report.)

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.Domain/Economy/Stockpile.cs tests/WorldEcon.Domain.Tests.Unit/Economy/StockpileTests.cs
git commit -m "feat: add Stockpile entity with weighted-average deposit and guarded withdraw"
```

---

## Task 5: Shop entity + margin quote

**Files:**
- Create: `src/WorldEcon.Domain/Economy/Shop.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/Economy/ShopTests.cs`

> `Quote(unitCostBasis)` returns sale price, absolute margin, and margin in basis points (markup over cost). Sale price = `cost + cost×markup_bp`. Pure integer/fixed-point (`FixedMath.MulBp`). Invariant: markup ≥ 0.

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/Economy/ShopTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class ShopTests
{
    [Test]
    public void Create_SetsFields()
    {
        var s = Shop.Create(WorldId.New(), SettlementId.New(), "The Sundries", markupBp: 2000, till: new Money(10_000)).Value;
        s.Name.Should().Be("The Sundries");
        s.MarkupBp.Should().Be(2000);
        s.Till.Should().Be(new Money(10_000));
    }

    [Test]
    public void Create_RejectsNegativeMarkup()
        => Shop.Create(WorldId.New(), SettlementId.New(), "X", -1, Money.Zero).IsError.Should().BeTrue();

    [Test]
    public void Quote_AppliesMarkupOverCost()
    {
        var s = Shop.Create(WorldId.New(), SettlementId.New(), "X", markupBp: 2000, Money.Zero).Value; // 20%
        var q = s.Quote(new Money(100));
        q.SalePrice.Should().Be(new Money(120));
        q.MarginAbs.Should().Be(new Money(20));
        q.MarginBp.Should().Be(2000);
    }

    [Test]
    public void Quote_ZeroMarkup_SalePriceEqualsCost()
    {
        var s = Shop.Create(WorldId.New(), SettlementId.New(), "X", 0, Money.Zero).Value;
        var q = s.Quote(new Money(100));
        q.SalePrice.Should().Be(new Money(100));
        q.MarginAbs.Should().Be(Money.Zero);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: compile failure.

- [ ] **Step 3: Implement**

Create `src/WorldEcon.Domain/Economy/Shop.cs`:
```csharp
using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>Quoted price for one good at one shop.</summary>
public readonly record struct ShopQuote(Money SalePrice, Money MarginAbs, int MarginBp);

public sealed class Shop : AggregateRoot<ShopId>
{
    public WorldId WorldId { get; }
    public SettlementId SettlementId { get; private set; }
    public string Name { get; private set; }
    public int MarkupBp { get; private set; }
    public Money Till { get; private set; }

    private Shop() : base(default) { Name = null!; } // EF

    private Shop(ShopId id, WorldId worldId, SettlementId settlementId, string name, int markupBp, Money till) : base(id)
    {
        WorldId = worldId;
        SettlementId = settlementId;
        Name = name;
        MarkupBp = markupBp;
        Till = till;
    }

    public static ErrorOr<Shop> Create(WorldId worldId, SettlementId settlementId, string name, int markupBp, Money till)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("shop.name.blank", "Shop name must not be blank.");
        if (markupBp < 0)
            return Error.Validation("shop.markup.negative", "Markup must not be negative.");
        if (till.IsNegative)
            return Error.Validation("shop.till.negative", "Till must not be negative.");

        return new Shop(ShopId.New(), worldId, settlementId, name.Trim(), markupBp, till);
    }

    /// <summary>Sale price = cost + cost×markup; margin reported absolute and in basis points (over cost).</summary>
    public ShopQuote Quote(Money unitCostBasis)
    {
        var salePrice = new Money(unitCostBasis.Units + FixedMath.MulBp(unitCostBasis.Units, MarkupBp));
        return new ShopQuote(salePrice, salePrice - unitCostBasis, MarkupBp);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: all pass.

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.Domain/Economy/Shop.cs tests/WorldEcon.Domain.Tests.Unit/Economy/ShopTests.cs
git commit -m "feat: add Shop entity with markup-over-cost margin quote"
```

---

## Task 6: Persistence — economy configs, converters & migration

**Files:**
- Create: `src/WorldEcon.Persistence/Conversions/EconomyIdConverters.cs`
- Create: `src/WorldEcon.Persistence/Configurations/GoodConfiguration.cs`, `StockpileConfiguration.cs`, `ShopConfiguration.cs`
- Modify: `src/WorldEcon.Persistence/WorldDbContext.cs` (add DbSets + register new ID converters)
- Create: migration `AddEconomy`
- Test: deferred to Task 7

- [ ] **Step 1: ID converters**

Create `src/WorldEcon.Persistence/Conversions/EconomyIdConverters.cs`:
```csharp
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Persistence.Conversions;

public sealed class GoodIdConverter() : ValueConverter<GoodId, Guid>(v => v.Value, g => new GoodId(g));
public sealed class StockpileIdConverter() : ValueConverter<StockpileId, Guid>(v => v.Value, g => new StockpileId(g));
public sealed class ShopIdConverter() : ValueConverter<ShopId, Guid>(v => v.Value, g => new ShopId(g));
```

- [ ] **Step 2: Configurations**

Create `src/WorldEcon.Persistence/Configurations/GoodConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class GoodConfiguration : IEntityTypeConfiguration<Good>
{
    public void Configure(EntityTypeBuilder<Good> b)
    {
        b.ToTable("goods");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.BaseUnit).IsRequired();
        b.Property(x => x.Category).HasConversion<string>();
        b.Property(x => x.Size).HasConversion<string>();
        b.Property(x => x.Provenance).HasConversion<string>();
        b.Property(x => x.BaseValue).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.Ignore(x => x.DomainEvents);
    }
}
```

Create `src/WorldEcon.Persistence/Configurations/StockpileConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class StockpileConfiguration : IEntityTypeConfiguration<Stockpile>
{
    public void Configure(EntityTypeBuilder<Stockpile> b)
    {
        b.ToTable("stockpiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.OwnerKind).HasConversion<string>();
        b.Property(x => x.CostBasis).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => new { x.OwnerKind, x.OwnerId, x.GoodId });
        b.Ignore(x => x.DomainEvents);
    }
}
```

Create `src/WorldEcon.Persistence/Configurations/ShopConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class ShopConfiguration : IEntityTypeConfiguration<Shop>
{
    public void Configure(EntityTypeBuilder<Shop> b)
    {
        b.ToTable("shops");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Till).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.SettlementId);
        b.Ignore(x => x.DomainEvents);
    }
}
```

- [ ] **Step 3: Add a `MoneyConverter`**

`Money` (a `record struct` with `long Units`) needs a converter so EF stores it as `INTEGER`. Add to `src/WorldEcon.Persistence/Conversions/ValueConverters.cs` (append a new class in that existing file):
```csharp
public sealed class MoneyConverter() : ValueConverter<WorldEcon.SharedKernel.Money, long>(m => m.Units, v => new WorldEcon.SharedKernel.Money(v));
```
(If the existing file already has the needed `using WorldEcon.SharedKernel;`, use the short name `Money` instead of the fully-qualified name.)

- [ ] **Step 4: Wire DbSets + converters into `WorldDbContext`**

Edit `src/WorldEcon.Persistence/WorldDbContext.cs`:
- Add `using WorldEcon.Domain.Economy;` if not present.
- Add DbSets:
```csharp
    public DbSet<Good> Goods => Set<Good>();
    public DbSet<Stockpile> Stockpiles => Set<Stockpile>();
    public DbSet<Shop> Shops => Set<Shop>();
```
- In `ConfigureConventions`, add the three economy ID converters alongside the geography ones:
```csharp
        b.Properties<GoodId>().HaveConversion<GoodIdConverter>();
        b.Properties<StockpileId>().HaveConversion<StockpileIdConverter>();
        b.Properties<ShopId>().HaveConversion<ShopIdConverter>();
```
(`OnModelCreating` already applies all `IEntityTypeConfiguration`s from the assembly, so the new configs are picked up automatically.)

- [ ] **Step 5: Generate the migration**
```bash
dotnet dotnet-ef migrations add AddEconomy --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence
```
Then `dotnet build` to confirm it compiles. (If the EF tool invocation differs, use the form that worked in Plan 1b: `dotnet dotnet-ef ...` after `dotnet tool restore`.)

- [ ] **Step 6: Commit**
```bash
git add src/WorldEcon.Persistence/Conversions src/WorldEcon.Persistence/Configurations/GoodConfiguration.cs src/WorldEcon.Persistence/Configurations/StockpileConfiguration.cs src/WorldEcon.Persistence/Configurations/ShopConfiguration.cs src/WorldEcon.Persistence/WorldDbContext.cs src/WorldEcon.Persistence/Migrations
git commit -m "feat: persist Good/Stockpile/Shop (configs, MoneyConverter, AddEconomy migration)"
```

---

## Task 7: Economy repositories + round-trip

**Files:**
- Create: `src/WorldEcon.Domain/Economy/IGoodRepository.cs`, `IShopRepository.cs`, `IStockpileRepository.cs`
- Create: `src/WorldEcon.Persistence/Repositories/GoodRepository.cs`, `ShopRepository.cs`, `StockpileRepository.cs`
- Test: `tests/WorldEcon.Persistence.Tests.Unit/EconomyRepositoryTests.cs`

> List reads order in memory by `Id.Value` (determinism). `IStockpileRepository.GetByOwnerAndGoodAsync` is the key lookup the price/margin query uses.

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Persistence.Tests.Unit/EconomyRepositoryTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Repositories;

namespace WorldEcon.Persistence.Tests.Unit;

public class EconomyRepositoryTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task Good_Shop_Stockpile_RoundTrip_AndKeyedLookups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            var continent = Continent.Create(world.Id, "C").Value;
            var country = Country.Create(world.Id, continent.Id, "Co").Value;
            var region = Region.Create(world.Id, country.Id, "R").Value;
            var settlement = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 0, 0, 50_000).Value;
            var good = Good.Create(world.Id, "Health Potion", GoodCategory.Potion, new Money(5000), "vial", SizeClass.Small, 0, false).Value;
            var shop = Shop.Create(world.Id, settlement.Id, "The Sundries", 2000, new Money(10_000)).Value;
            var stock = Stockpile.CreateForShop(world.Id, shop.Id, good.Id, 25, new Money(4000)).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.Add(continent);
                ctx.Countries.Add(country);
                ctx.Regions.Add(region);
                ctx.Settlements.Add(settlement);
                ctx.Goods.Add(good);
                ctx.Shops.Add(shop);
                ctx.Stockpiles.Add(stock);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var goods = new GoodRepository(ctx);
                var shops = new ShopRepository(ctx);
                var stockpiles = new StockpileRepository(ctx);

                (await goods.GetAsync(good.Id))!.Name.Should().Be("Health Potion");

                var shopsInTown = await shops.ListBySettlementAsync(settlement.Id);
                shopsInTown.Should().ContainSingle(s => s.Id == shop.Id);

                var sp = await stockpiles.GetByOwnerAndGoodAsync(StockpileOwnerKind.Shop, shop.Id.Value, good.Id);
                sp.Should().NotBeNull();
                sp!.Quantity.Should().Be(25);
                sp.CostBasis.Should().Be(new Money(4000));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit`
Expected: compile failure.

- [ ] **Step 3: Interfaces (Domain)**

Create `src/WorldEcon.Domain/Economy/IGoodRepository.cs`:
```csharp
namespace WorldEcon.Domain.Economy;

public interface IGoodRepository
{
    Task<Good?> GetAsync(GoodId id);
    Task<IReadOnlyList<Good>> ListByWorldAsync(Geography.WorldId worldId);
    Task AddAsync(Good good);
}
```

Create `src/WorldEcon.Domain/Economy/IShopRepository.cs`:
```csharp
using WorldEcon.Domain.Geography;

namespace WorldEcon.Domain.Economy;

public interface IShopRepository
{
    Task<Shop?> GetAsync(ShopId id);
    Task<IReadOnlyList<Shop>> ListBySettlementAsync(SettlementId settlementId);
    Task AddAsync(Shop shop);
}
```

Create `src/WorldEcon.Domain/Economy/IStockpileRepository.cs`:
```csharp
namespace WorldEcon.Domain.Economy;

public interface IStockpileRepository
{
    Task<Stockpile?> GetByOwnerAndGoodAsync(StockpileOwnerKind ownerKind, Guid ownerId, GoodId goodId);
    Task AddAsync(Stockpile stockpile);
}
```

- [ ] **Step 4: Implementations (Persistence)**

Create `src/WorldEcon.Persistence/Repositories/GoodRepository.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class GoodRepository(WorldDbContext context) : IGoodRepository
{
    public Task<Good?> GetAsync(GoodId id) => context.Goods.FirstOrDefaultAsync(g => g.Id == id);

    public async Task<IReadOnlyList<Good>> ListByWorldAsync(WorldId worldId)
    {
        var list = await context.Goods.Where(g => g.WorldId == worldId).ToListAsync();
        return list.OrderBy(g => g.Id.Value).ToList();
    }

    public async Task AddAsync(Good good) => await context.Goods.AddAsync(good);
}
```

Create `src/WorldEcon.Persistence/Repositories/ShopRepository.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class ShopRepository(WorldDbContext context) : IShopRepository
{
    public Task<Shop?> GetAsync(ShopId id) => context.Shops.FirstOrDefaultAsync(s => s.Id == id);

    public async Task<IReadOnlyList<Shop>> ListBySettlementAsync(SettlementId settlementId)
    {
        var list = await context.Shops.Where(s => s.SettlementId == settlementId).ToListAsync();
        return list.OrderBy(s => s.Id.Value).ToList();
    }

    public async Task AddAsync(Shop shop) => await context.Shops.AddAsync(shop);
}
```

Create `src/WorldEcon.Persistence/Repositories/StockpileRepository.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Persistence.Repositories;

public sealed class StockpileRepository(WorldDbContext context) : IStockpileRepository
{
    public Task<Stockpile?> GetByOwnerAndGoodAsync(StockpileOwnerKind ownerKind, Guid ownerId, GoodId goodId)
        => context.Stockpiles.FirstOrDefaultAsync(
            s => s.OwnerKind == ownerKind && s.OwnerId == ownerId && s.GoodId == goodId);

    public async Task AddAsync(Stockpile stockpile) => await context.Stockpiles.AddAsync(stockpile);
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit`
Expected: all pass. (If the `s.OwnerKind == ownerKind` enum-to-string comparison or `s.GoodId == goodId` converted-id comparison fails to translate to SQL, report the exact error — both should translate, as enum-as-string and converted-id equality did in Plan 1b.)

- [ ] **Step 6: Commit**
```bash
git add src/WorldEcon.Domain/Economy/IGoodRepository.cs src/WorldEcon.Domain/Economy/IShopRepository.cs src/WorldEcon.Domain/Economy/IStockpileRepository.cs src/WorldEcon.Persistence/Repositories/GoodRepository.cs src/WorldEcon.Persistence/Repositories/ShopRepository.cs src/WorldEcon.Persistence/Repositories/StockpileRepository.cs tests/WorldEcon.Persistence.Tests.Unit/EconomyRepositoryTests.cs
git commit -m "feat: add economy repositories with keyed stockpile lookup"
```

---

## Task 8: Price/margin query (Application)

**Files:**
- Create: `src/WorldEcon.Application/Queries/PriceMarginQuery.cs`
- Test: `tests/WorldEcon.Application.Tests.Unit/PriceMarginQueryTests.cs`

> The headline DM read. For a `(world, settlement, good)`, list every shop in that settlement that stocks the good, with sale price, stock, cost basis, and margin. Composes the Domain repository interfaces — EF-free. "Next shipment" is intentionally omitted (stage 3). Result ordering is deterministic (by shop name, then id).

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Application.Tests.Unit/PriceMarginQueryTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Repositories;

namespace WorldEcon.Application.Tests.Unit;

public class PriceMarginQueryTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task Run_ReturnsShopsStockingTheGood_WithMargin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            var continent = Continent.Create(world.Id, "C").Value;
            var country = Country.Create(world.Id, continent.Id, "Co").Value;
            var region = Region.Create(world.Id, country.Id, "R").Value;
            var town = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 0, 0, 50_000).Value;
            var potion = Good.Create(world.Id, "Health Potion", GoodCategory.Potion, new Money(5000), "vial", SizeClass.Small, 0, false).Value;

            var sundries = Shop.Create(world.Id, town.Id, "The Sundries", 2000, new Money(10_000)).Value; // 20%
            var apothecary = Shop.Create(world.Id, town.Id, "Apothecary", 5000, new Money(10_000)).Value; // 50%
            var emptyStall = Shop.Create(world.Id, town.Id, "Empty Stall", 1000, new Money(10_000)).Value; // stocks nothing

            var sundriesStock = Stockpile.CreateForShop(world.Id, sundries.Id, potion.Id, 25, new Money(4000)).Value;
            var apothStock = Stockpile.CreateForShop(world.Id, apothecary.Id, potion.Id, 10, new Money(4000)).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.Add(continent);
                ctx.Countries.Add(country);
                ctx.Regions.Add(region);
                ctx.Settlements.Add(town);
                ctx.Goods.Add(potion);
                ctx.Shops.AddRange(sundries, apothecary, emptyStall);
                ctx.Stockpiles.AddRange(sundriesStock, apothStock);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var query = new PriceMarginQuery(new ShopRepository(ctx), new StockpileRepository(ctx), new GoodRepository(ctx));
                var result = await query.RunAsync(world.Id, town.Id, potion.Id);

                result.IsError.Should().BeFalse();
                var value = result.Value;

                value.GoodName.Should().Be("Health Potion");
                value.Shops.Should().HaveCount(2); // Empty Stall excluded (no stockpile)

                // Ordered by shop name: "Apothecary" before "The Sundries"
                value.Shops.Select(s => s.ShopName).Should().Equal("Apothecary", "The Sundries");

                var apoth = value.Shops.Single(s => s.ShopName == "Apothecary");
                apoth.Stock.Should().Be(10);
                apoth.UnitCostBasis.Should().Be(new Money(4000));
                apoth.SalePrice.Should().Be(new Money(6000));    // 4000 * 1.5
                apoth.MarginAbs.Should().Be(new Money(2000));
                apoth.MarginBp.Should().Be(5000);

                var sundries2 = value.Shops.Single(s => s.ShopName == "The Sundries");
                sundries2.SalePrice.Should().Be(new Money(4800)); // 4000 * 1.2
                sundries2.MarginAbs.Should().Be(new Money(800));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Run_UnknownGood_IsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                await ctx.SaveChangesAsync();
            }
            await using (var ctx = NewContextOnFile(path))
            {
                var query = new PriceMarginQuery(new ShopRepository(ctx), new StockpileRepository(ctx), new GoodRepository(ctx));
                var result = await query.RunAsync(world.Id, SettlementId.New(), GoodId.New());
                result.IsError.Should().BeTrue();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Application.Tests.Unit`
Expected: compile failure.

- [ ] **Step 3: Implement**

Create `src/WorldEcon.Application/Queries/PriceMarginQuery.cs`:
```csharp
using ErrorOr;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Queries;

/// <summary>One shop's offer for the queried good. NextShipment is omitted until stage 3 (caravans/batches).</summary>
public sealed record ShopPriceLine(
    ShopId ShopId,
    string ShopName,
    long Stock,
    Money UnitCostBasis,
    Money SalePrice,
    Money MarginAbs,
    int MarginBp);

public sealed record PriceMarginResult(
    GoodId GoodId,
    string GoodName,
    SettlementId SettlementId,
    IReadOnlyList<ShopPriceLine> Shops);

public interface IPriceMarginQuery
{
    Task<ErrorOr<PriceMarginResult>> RunAsync(WorldId worldId, SettlementId settlementId, GoodId goodId);
}

public sealed class PriceMarginQuery(IShopRepository shops, IStockpileRepository stockpiles, IGoodRepository goods)
    : IPriceMarginQuery
{
    public async Task<ErrorOr<PriceMarginResult>> RunAsync(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var good = await goods.GetAsync(goodId);
        if (good is null)
            return Error.NotFound("good.notfound", "Good not found.");

        var shopsInTown = await shops.ListBySettlementAsync(settlementId);

        var lines = new List<ShopPriceLine>();
        foreach (var shop in shopsInTown)
        {
            var stock = await stockpiles.GetByOwnerAndGoodAsync(StockpileOwnerKind.Shop, shop.Id.Value, goodId);
            if (stock is null || stock.Quantity <= 0)
                continue;

            var quote = shop.Quote(stock.CostBasis);
            lines.Add(new ShopPriceLine(
                shop.Id, shop.Name, stock.Quantity, stock.CostBasis,
                quote.SalePrice, quote.MarginAbs, quote.MarginBp));
        }

        // Deterministic display order: by shop name, then id.
        var ordered = lines
            .OrderBy(l => l.ShopName, StringComparer.Ordinal)
            .ThenBy(l => l.ShopId.Value)
            .ToList();

        return new PriceMarginResult(good.Id, good.Name, settlementId, ordered);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Application.Tests.Unit`
Expected: all pass. (If `Error.NotFound` differs in ErrorOr 2.x, use the installed API and report.)

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.Application/Queries/PriceMarginQuery.cs tests/WorldEcon.Application.Tests.Unit/PriceMarginQueryTests.cs
git commit -m "feat: add price/margin query (good+settlement -> shops with sale price, stock, margin)"
```

---

## Task 9: Remove the placeholder + final verification

**Files:**
- Delete: `src/WorldEcon.Application/AssemblyMarker.cs` (the project now has real types)

- [ ] **Step 1: Remove the placeholder marker**
```bash
git rm src/WorldEcon.Application/AssemblyMarker.cs
```

- [ ] **Step 2: Full build + all test projects**

Run:
```bash
dotnet build
dotnet run --project tests/WorldEcon.SharedKernel.Tests.Unit
dotnet run --project tests/WorldEcon.Simulation.Tests.Unit
dotnet run --project tests/WorldEcon.Domain.Tests.Unit
dotnet run --project tests/WorldEcon.Persistence.Tests.Unit
dotnet run --project tests/WorldEcon.Application.Tests.Unit
```
Expected: build warning-clean; all five test projects green.

- [ ] **Step 3: Commit**
```bash
git add -A
git commit -m "chore: remove Application placeholder marker after real types added"
```

- [ ] **Step 4: Confirm clean tree**

Run: `git status`
Expected: nothing to commit (the `design-time.db` from EF is gitignored via `*.db`).

---

## What this unlocks
- The **price/margin query** — the core DM read ("what does a health potion cost in Hammerfell, and what's the margin?") works against a persisted world.
- `WorldEcon.Application` exists as the home for use-cases/queries that the UI (Plan 1c) and later stages call.
- Weighted-average cost basis + the `ICostBasisValuation` seam — the valuation substrate that trade (stage 3) and party effects (stage 4) feed.
- The Economy aggregate established the same ID/entity/config/migration/repo pattern for the remaining aggregates (Demographics, Agents, Jurisdiction).

## Carry-forward notes
- **Plan 2b — supply/demand market pricing:** implement `FixedMath.PowBp` (fixed-point ln/exp for fractional exponents) and the `scarcity^elasticity` market-price curve; add `PricingParameters` (elasticity, multiplier clamps) on `World`; derive demand from population × per-good consumption (then refine with `Race`/`SettlementDemographic`). Settlement-market stockpiles become a priced supply source there.
- **Next shipment** in the query result: add when caravans (stage 3 trade) and work orders (stage 3 production) exist.
- **Overflow policy:** `Money` arithmetic in `Stockpile`/`Shop`/valuation is still unchecked; revisit the `Money`-vs-`FixedMath` overflow-policy consistency (carry-forward from 1a/1b) — `WeightedAverageValuation.Blend`'s `existingQty * existingUnitBasis.Units` is the first place large products accumulate.
- **Source-gen IDs:** the per-ID converter count is now 9; this is the refactor point flagged in 1b before it doubles again.
- **DI registration:** when the UI/host wires things up, add an `AddWorldEconApplication()` / `AddWorldEconPersistence()` extension (per spec §2.1 convention) registering repositories + queries.
