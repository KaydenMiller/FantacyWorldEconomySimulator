# Shop Economy — Phase 1: Shop Substrate — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Re-base the economy so all inventory is owned by shops (each with its own per-good cost basis), every producer (production node / resource endowment) has a producer-shop, every settlement has a public-market shop, and "the market" becomes an aggregate read-model — preserving today's behavior, with **no retail money** (Phase 2).

**Architecture:** Retire the `StockpileOwnerKind.SettlementMarket` pool. Stockpiles become shop-owned. A new `ShopMarket` engine helper (with the existing `.Local`-merge pattern) replaces the per-phase `GetOrCreateMarketStockpile`/`LoadMarketStockpiles*` helpers: it get-or-creates producer/public-market shops, aggregates supply per `(settlement, good)`, and withdraws/deposits across a settlement's shops in stable id order. Producer and public-market shops are created **lazily** on first use (mirroring how `GetOrCreateMarketStockpile` lazily created market stockpiles), so the migration only re-owns existing stock.

**Tech Stack:** C#/.NET 10, EF Core 10 + SQLite, ErrorOr, TUnit + FluentAssertions 7.x, Terminal.Gui 2.4.7. Tests run with `dotnet run --project <testproject> -c Release` (NOT `dotnet test`).

**Spec:** `docs/superpowers/specs/2026-06-22-shop-economy-phase1-substrate-design.md`

---

## Conventions (read once)

- Tests: `dotnet run --project tests/WorldEcon.<Project>.Tests.Unit -c Release`. TUnit `[Test] public async Task`. First "run to see it fail" usually fails at **build** (missing type) — that's the expected red.
- Build: `dotnet build src/WorldEcon.<Project>` ends `0 Error(s)`. Solution is **warnings-as-errors** (no unused usings).
- Strongly-typed IDs: `readonly record struct XId(Guid Value) : IStronglyTypedId`. Nullable typed IDs (`ShopId?`) are fine and translate for equality.
- Aggregates: private parameterless EF ctor; private full ctor; `static ErrorOr<X> Create(...)`; `{ get; private set; }` props; mutators are methods.
- EF: configs in `src/WorldEcon.Persistence/Configurations/`; `Money`→`MoneyConverter`; enums→`HasConversion<string>()`; `b.Ignore(x => x.DomainEvents)`.
- SQL translation: equality on value-converted IDs and `WorldId`/`OwnerId` Guids translate; range/ORDER BY on value-converted types do **not** — sort in memory after `ToListAsync()`.
- Migration command: `dotnet dotnet-ef migrations add <Name> --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence`. Confirm no drift with `dotnet dotnet-ef migrations has-pending-model-changes --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence`.
- Temp DB in tests:
  ```csharp
  var path = Path.Combine(Path.GetTempPath(), $"shop-{Guid.NewGuid():N}.db");
  await using var db = new WorldDbContext(
      new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
  await db.Database.MigrateAsync();
  // ... finally File.Delete(path);
  ```
- Engine tests reuse `tests/WorldEcon.Engine.Tests.Unit/LogTestWorld.cs` (seeds continent→country→region→settlement) and the production-seeding pattern in `ProductionPhaseTests`/`LogEventServiceTests`.

---

## File Structure

**Domain (modify):**
- `src/WorldEcon.Domain/Economy/Enums.cs` — add `ShopKind`; annotate `SettlementMarket` as retired.
- `src/WorldEcon.Domain/Economy/Shop.cs` — add `Kind`; `Create` defaults + a producer/public-market factory path.
- `src/WorldEcon.Domain/Economy/ProductionNode.cs` — add `ProducerShopId? ProducerShopId` + `AssignProducerShop(ShopId)`.
- `src/WorldEcon.Domain/Economy/ResourceEndowment.cs` — same `ProducerShopId` + setter.

**Persistence (modify/create):**
- `src/WorldEcon.Persistence/Configurations/ShopConfiguration.cs` — map `Kind`.
- `src/WorldEcon.Persistence/Configurations/ProductionNodeConfiguration.cs` / `ResourceEndowmentConfiguration.cs` — index `ProducerShopId` is optional; the typed-id converter already exists (`ShopId`).
- A migration `..._ShopSubstrate.cs` (generated + hand-edited re-own SQL).

**Engine (create/modify):**
- `src/WorldEcon.Engine/ShopMarket.cs` — **new** central helper (get-or-create shops, aggregate supply, withdraw-across-shops).
- `src/WorldEcon.Engine/Phases/ProductionPhase.cs`, `ConsumptionPhase.cs`, `PricingPhase.cs`, `TradePhase.cs` — re-point onto `ShopMarket`.
- `src/WorldEcon.Engine/Actions/LogEventService.cs` — `AdjustMarketStockAsync` targets the public-market shop.

**Seeding (modify):**
- `src/WorldEcon.Cli/DemoSeeder.cs` — producer shops for the smithy node + ore endowment; a public-market shop per settlement; tag existing shops `Retail`.
- `src/WorldEcon.Seeding/SeedImporter.cs` — JSON `market` section → the settlement's public-market shop; producer shops for nodes/endowments.

**UI/CLI (modify):**
- `src/WorldEcon.Tui/Navigation/Navigator.cs` — the city **Market** category becomes a marketplace board (per-shop offers, Min Price/Price); shop drill unchanged.
- `src/WorldEcon.Tui/Shell/TuiShell.cs` — a **sort** key for the table.
- `src/WorldEcon.Cli/CommandRunner.cs` — `CmdStock` aggregates over shops.

---

## Task 1: Domain — `ShopKind`, `Shop.Kind`, producer-shop links

**Files:**
- Modify: `src/WorldEcon.Domain/Economy/Enums.cs`, `Shop.cs`, `ProductionNode.cs`, `ResourceEndowment.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/ShopSubstrateTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Domain.Tests.Unit/ShopSubstrateTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit;

public class ShopSubstrateTests
{
    private static readonly WorldId World = WorldId.New();
    private static readonly SettlementId Settle = SettlementId.New();

    [Test]
    public void Shop_DefaultKind_IsRetail()
    {
        var shop = Shop.Create(World, Settle, "The Sundries", 2000, new Money(100)).Value;
        shop.Kind.Should().Be(ShopKind.Retail);
    }

    [Test]
    public void Shop_CreateVendor_SetsKind()
    {
        var pub = Shop.CreateVendor(World, Settle, "Town Market", ShopKind.PublicMarket).Value;
        pub.Kind.Should().Be(ShopKind.PublicMarket);
        pub.MarkupBp.Should().Be(0);
        pub.Till.Should().Be(Money.Zero);
    }

    [Test]
    public void ProductionNode_AssignProducerShop_Sets()
    {
        var node = ProductionNode.Create(World, Settle, RecipeId.New(), FacilityType.Smithy, 1).Value;
        node.ProducerShopId.Should().BeNull();
        var shopId = ShopId.New();
        node.AssignProducerShop(shopId);
        node.ProducerShopId.Should().Be(shopId);
    }

    [Test]
    public void ResourceEndowment_AssignProducerShop_Sets()
    {
        var e = ResourceEndowment.Create(World, Settle, GoodId.New(), 30).Value;
        e.ProducerShopId.Should().BeNull();
        var shopId = ShopId.New();
        e.AssignProducerShop(shopId);
        e.ProducerShopId.Should().Be(shopId);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: build failure — `ShopKind`/`CreateVendor`/`AssignProducerShop`/`ProducerShopId` don't exist.

- [ ] **Step 3: Implement**

In `Enums.cs`, add and annotate:
```csharp
/// <summary>What role a shop plays. Producer = a node/endowment's storefront (holds its output);
/// Retail = sells to townsfolk; PublicMarket = the town catch-all (imports, seeded stock).</summary>
public enum ShopKind { Retail = 0, Producer = 1, PublicMarket = 2 }
```
And change the `StockpileOwnerKind` doc comment to:
```csharp
/// <summary>What kind of entity owns a stockpile. SettlementMarket is RETIRED (Phase 1: all economy
/// inventory is shop-owned); kept only so historic enum values still parse. Agent is reserved.</summary>
public enum StockpileOwnerKind { SettlementMarket = 0, Shop = 1, Agent = 2 }
```

In `Shop.cs`, add the `Kind` field and a vendor factory. Replace the class body's field list + ctor + `Create` with:
```csharp
    public WorldId WorldId { get; }
    public SettlementId SettlementId { get; private set; }
    public string Name { get; private set; }
    public int MarkupBp { get; private set; }
    public Money Till { get; private set; }
    public ShopKind Kind { get; private set; }

    private Shop() : base(default) { Name = null!; } // EF

    private Shop(ShopId id, WorldId worldId, SettlementId settlementId, string name, int markupBp,
        Money till, ShopKind kind) : base(id)
    {
        WorldId = worldId;
        SettlementId = settlementId;
        Name = name;
        MarkupBp = markupBp;
        Till = till;
        Kind = kind;
    }

    public static ErrorOr<Shop> Create(WorldId worldId, SettlementId settlementId, string name, int markupBp, Money till)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("shop.name.blank", "Shop name must not be blank.");
        if (markupBp < 0)
            return Error.Validation("shop.markup.negative", "Markup must not be negative.");
        if (till.IsNegative)
            return Error.Validation("shop.till.negative", "Till must not be negative.");

        return new Shop(ShopId.New(), worldId, settlementId, name.Trim(), markupBp, till, ShopKind.Retail);
    }

    /// <summary>Creates a non-retail vendor (Producer or PublicMarket) with no markup and an empty till.
    /// Phase 1: Till/Markup are dormant; these vendors only hold inventory.</summary>
    public static ErrorOr<Shop> CreateVendor(WorldId worldId, SettlementId settlementId, string name, ShopKind kind)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("shop.name.blank", "Shop name must not be blank.");

        return new Shop(ShopId.New(), worldId, settlementId, name.Trim(), 0, Money.Zero, kind);
    }
```
(Leave the existing `Quote(...)` method as-is.)

In `ProductionNode.cs`, add the field + setter:
```csharp
    public ShopId? ProducerShopId { get; private set; }
    public void AssignProducerShop(ShopId shopId) => ProducerShopId = shopId;
```

In `ResourceEndowment.cs`, add the same:
```csharp
    public ShopId? ProducerShopId { get; private set; }
    public void AssignProducerShop(ShopId shopId) => ProducerShopId = shopId;
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Domain/Economy/Enums.cs src/WorldEcon.Domain/Economy/Shop.cs \
        src/WorldEcon.Domain/Economy/ProductionNode.cs src/WorldEcon.Domain/Economy/ResourceEndowment.cs \
        tests/WorldEcon.Domain.Tests.Unit/ShopSubstrateTests.cs
git commit -m "feat(economy): ShopKind + Shop.Kind, producer-shop links on node/endowment"
```

---

## Task 2: Persistence — EF config + migration (schema + re-own SQL)

**Files:**
- Modify: `src/WorldEcon.Persistence/Configurations/ShopConfiguration.cs`
- Create: `src/WorldEcon.Persistence/Migrations/<timestamp>_ShopSubstrate.cs` (generated, then edited)
- Test: `tests/WorldEcon.Persistence.Tests.Unit/ShopSubstrateMigrationTests.cs`

- [ ] **Step 1: Map the new columns**

In `ShopConfiguration.cs`, after the `Till` line add:
```csharp
        b.Property(x => x.Kind).HasConversion<string>();
        b.HasIndex(x => new { x.SettlementId, x.Kind });
```
`ProducerShopId` on the node/endowment is a `ShopId?`; the global `ShopId` converter already handles it, so EF maps it automatically. Add a helpful index in `ProductionNodeConfiguration.cs` and `ResourceEndowmentConfiguration.cs`:
```csharp
        b.HasIndex(x => x.ProducerShopId);
```

- [ ] **Step 2: Generate the migration**

Run:
```bash
dotnet dotnet-ef migrations add ShopSubstrate \
  --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence
```
Expected: a migration whose `Up` adds `Kind` to `shops` and `ProducerShopId` to `production_nodes` and `resource_endowments`, plus the new indexes.

- [ ] **Step 3: Hand-edit the migration**

- Make the `Kind` AddColumn default existing shops to Retail. Find the `AddColumn<string>(name: "Kind", table: "shops", ...)` and set `nullable: false, defaultValue: "Retail"`.
- After all `AddColumn`/`CreateIndex` calls in `Up`, append the data migration that re-owns existing `SettlementMarket` stockpiles into a per-settlement public-market shop:
```csharp
            // Phase 1 substrate: retire the SettlementMarket pool. For each settlement that holds
            // market stockpiles, create a PublicMarket shop and re-own its market stock to it
            // (conserving quantity, cost basis, and market price). Producer shops are created lazily
            // by the engine on first production, so they are not created here.
            migrationBuilder.Sql(@"
INSERT INTO shops (Id, WorldId, SettlementId, Name, MarkupBp, Till, Kind)
SELECT
  lower(
    substr(hex(randomblob(4)),1,8) || '-' ||
    substr(hex(randomblob(2)),1,4) || '-4' ||
    substr(hex(randomblob(2)),2,3) || '-' ||
    substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)),2,3) || '-' ||
    substr(hex(randomblob(6)),1,12)
  ),
  WorldId, OwnerId, 'Town Market', 0, 0, 'PublicMarket'
FROM (SELECT DISTINCT WorldId, OwnerId FROM stockpiles WHERE OwnerKind = 'SettlementMarket');");

            migrationBuilder.Sql(@"
UPDATE stockpiles
SET OwnerId = (SELECT s.Id FROM shops s WHERE s.Kind = 'PublicMarket' AND s.SettlementId = stockpiles.OwnerId),
    OwnerKind = 'Shop'
WHERE OwnerKind = 'SettlementMarket';");
```
> Note: `StockpileOwnerKind` is stored as a string (`HasConversion<string>()`), so the literals are `'SettlementMarket'` / `'Shop'`. `Shop.Kind`/`Till`/`MarkupBp` columns exist by this point (added earlier in the same `Up`). Leave `Down` as generated.

- [ ] **Step 4: Confirm no model drift**

Run: `dotnet dotnet-ef migrations has-pending-model-changes --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence`
Expected: "No changes have been made to the model since the last migration."

- [ ] **Step 5: Write the conservation test**

`tests/WorldEcon.Persistence.Tests.Unit/ShopSubstrateMigrationTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Persistence.Tests.Unit;

public class ShopSubstrateMigrationTests
{
    [Test]
    public async Task Migration_ReownsSettlementMarketStock_IntoPublicMarketShop_Conserving()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shopmig-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            // Migrate to just BEFORE ShopSubstrate. Use the migration immediately prior
            // (AddWorldCurrency); adjust the id if the chain changes.
            await migrator.MigrateAsync("20260622073704_AddWorldCurrency");

            var worldId = Guid.NewGuid();
            var settlementId = Guid.NewGuid();
            var goodId = Guid.NewGuid();
            // Insert a SettlementMarket stockpile via raw SQL (pre-substrate shape).
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO stockpiles (Id, WorldId, OwnerKind, OwnerId, GoodId, Quantity, CostBasis, MarketPrice) " +
                "VALUES ({0},{1},'SettlementMarket',{2},{3},{4},{5},{6})",
                Guid.NewGuid(), worldId, settlementId, goodId, 77L, 20L, 18L);

            // Run ShopSubstrate.
            await migrator.MigrateAsync(); // to latest

            var sp = await db.Stockpiles.SingleAsync();
            sp.OwnerKind.Should().Be(StockpileOwnerKind.Shop);
            sp.Quantity.Should().Be(77);
            sp.CostBasis.Should().Be(new Money(20));
            sp.MarketPrice.Should().Be(new Money(18));

            var shop = await db.Shops.SingleAsync(s => s.Kind == ShopKind.PublicMarket);
            shop.SettlementId.Value.Should().Be(settlementId);
            sp.OwnerId.Should().Be(shop.Id.Value);
        }
        finally { File.Delete(path); }
    }
}
```
> If the migration immediately before `ShopSubstrate` is not `20260622073704_AddWorldCurrency`, use the actual prior migration id (check `src/WorldEcon.Persistence/Migrations/`).

- [ ] **Step 6: Run the test**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 7: Commit**

```bash
git add src/WorldEcon.Persistence tests/WorldEcon.Persistence.Tests.Unit/ShopSubstrateMigrationTests.cs
git commit -m "feat(economy): ShopSubstrate migration (Kind, ProducerShopId, re-own market stock)"
```

---

## Task 3: Engine — `ShopMarket` helper (the market as a service)

This is the heart of the re-base. It replaces the per-phase `GetOrCreateMarketStockpile` / `LoadMarketStockpiles*` helpers with one shop-aware service using the `.Local`-merge pattern.

**Files:**
- Create: `src/WorldEcon.Engine/ShopMarket.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/ShopMarketTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/ShopMarketTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ShopMarketTests
{
    [Test]
    public async Task PublicMarketShop_IsCreatedOnceAndReused()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            var a = await ShopMarket.GetOrCreatePublicMarketShop(sim, s.Settlement.Id);
            var b = await ShopMarket.GetOrCreatePublicMarketShop(sim, s.Settlement.Id);
            a.Id.Should().Be(b.Id);
            a.Kind.Should().Be(ShopKind.PublicMarket);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task WithdrawAcrossShops_DepletesInOrder_AndReportsTaken()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var good = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, 0, true, 0).Value;
            s.Db.Goods.Add(good);
            // Two shops in the settlement, 30 + 30 of the good.
            var shopA = Shop.Create(s.World.Id, s.Settlement.Id, "A", 0, Money.Zero).Value;
            var shopB = Shop.Create(s.World.Id, s.Settlement.Id, "B", 0, Money.Zero).Value;
            s.Db.Shops.AddRange(shopA, shopB);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shopA.Id, good.Id, 30, new Money(10)).Value);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shopB.Id, good.Id, 30, new Money(10)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            long taken = await ShopMarket.WithdrawAcrossShops(sim, s.Settlement.Id, good.Id, 50);
            taken.Should().Be(50);
            (await ShopMarket.TotalSupply(sim, s.Settlement.Id, good.Id)).Should().Be(10);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: build failure — `ShopMarket` does not exist.

- [ ] **Step 3: Implement `ShopMarket`**

`src/WorldEcon.Engine/ShopMarket.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine;

/// <summary>
/// The shop-based market substrate. All economy inventory is shop-owned; "the market" is the
/// aggregate over a settlement's shops. This helper get-or-creates the per-node/endowment producer
/// shops and the per-settlement public-market shop (lazily), aggregates supply, and
/// deposits/withdraws across a settlement's shops in stable id order. Uses the established
/// <c>.Local</c>-merge pattern so within-advance unsaved changes are visible before SaveChanges.
/// </summary>
public static class ShopMarket
{
    // ---- shops ---------------------------------------------------------------------------------

    /// <summary>All shops in a settlement (DB ∪ Local), deduped by id, in stable id order.</summary>
    public static async Task<List<Shop>> ShopsIn(SimulationContext ctx, SettlementId settlementId)
    {
        var worldId = ctx.World.Id;
        var fromDb = await ctx.Db.Shops
            .Where(sh => sh.WorldId == worldId && sh.SettlementId == settlementId)
            .ToListAsync();
        var byId = fromDb.ToDictionary(sh => sh.Id);
        foreach (var local in ctx.Db.Shops.Local.Where(sh => sh.WorldId == worldId && sh.SettlementId == settlementId))
            byId[local.Id] = local;
        return byId.Values.OrderBy(sh => sh.Id.Value).ToList();
    }

    public static async Task<Shop> GetOrCreatePublicMarketShop(SimulationContext ctx, SettlementId settlementId)
    {
        var existing = (await ShopsIn(ctx, settlementId)).FirstOrDefault(sh => sh.Kind == ShopKind.PublicMarket);
        if (existing is not null)
            return existing;
        var created = Shop.CreateVendor(ctx.World.Id, settlementId, "Town Market", ShopKind.PublicMarket).Value;
        ctx.Db.Shops.Add(created);
        return created;
    }

    /// <summary>Producer shop fronting a node; created and linked (node.ProducerShopId) on first call.</summary>
    public static async Task<Shop> GetOrCreateProducerShop(SimulationContext ctx, ProductionNode node, string name)
    {
        if (node.ProducerShopId is { } id)
        {
            var shop = await FindShop(ctx, id);
            if (shop is not null)
                return shop;
        }
        var created = Shop.CreateVendor(ctx.World.Id, node.SettlementId, name, ShopKind.Producer).Value;
        ctx.Db.Shops.Add(created);
        node.AssignProducerShop(created.Id);
        return created;
    }

    /// <summary>Producer shop fronting an endowment (the "mine"); created and linked on first call.</summary>
    public static async Task<Shop> GetOrCreateProducerShop(SimulationContext ctx, ResourceEndowment endowment, string name)
    {
        if (endowment.ProducerShopId is { } id)
        {
            var shop = await FindShop(ctx, id);
            if (shop is not null)
                return shop;
        }
        var created = Shop.CreateVendor(ctx.World.Id, endowment.SettlementId, name, ShopKind.Producer).Value;
        ctx.Db.Shops.Add(created);
        endowment.AssignProducerShop(created.Id);
        return created;
    }

    private static async Task<Shop?> FindShop(SimulationContext ctx, ShopId id)
    {
        var local = ctx.Db.Shops.Local.FirstOrDefault(sh => sh.Id == id);
        if (local is not null)
            return local;
        return await ctx.Db.Shops.FirstOrDefaultAsync(sh => sh.Id == id);
    }

    // ---- stockpiles ----------------------------------------------------------------------------

    /// <summary>All shop stockpiles of a good in a settlement (DB ∪ Local), in stable shop-id then
    /// stockpile-id order (so depletion is deterministic).</summary>
    public static async Task<List<Stockpile>> StockpilesForGood(SimulationContext ctx, SettlementId settlementId, GoodId goodId)
    {
        var shopIds = (await ShopsIn(ctx, settlementId)).ToDictionary(sh => sh.Id.Value, sh => sh.Id.Value);
        var worldId = ctx.World.Id;
        var fromDb = await ctx.Db.Stockpiles
            .Where(s => s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop && s.GoodId == goodId)
            .ToListAsync();
        var byId = fromDb.ToDictionary(s => s.Id);
        foreach (var local in ctx.Db.Stockpiles.Local.Where(s =>
            s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop && s.GoodId == goodId))
            byId[local.Id] = local;
        return byId.Values
            .Where(s => shopIds.ContainsKey(s.OwnerId))
            .OrderBy(s => s.OwnerId).ThenBy(s => s.Id.Value)
            .ToList();
    }

    public static async Task<long> TotalSupply(SimulationContext ctx, SettlementId settlementId, GoodId goodId)
        => (await StockpilesForGood(ctx, settlementId, goodId)).Sum(s => s.Quantity);

    /// <summary>Get-or-create the stockpile for a good inside one shop.</summary>
    public static async Task<Stockpile> StockpileInShop(SimulationContext ctx, ShopId shopId, GoodId goodId)
    {
        var local = ctx.Db.Stockpiles.Local.FirstOrDefault(s =>
            s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == shopId.Value && s.GoodId == goodId);
        if (local is not null)
            return local;
        var existing = await ctx.Db.Stockpiles.FirstOrDefaultAsync(s =>
            s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == shopId.Value && s.GoodId == goodId);
        if (existing is not null)
            return existing;
        var created = Stockpile.CreateForShop(ctx.World.Id, shopId, goodId, 0, Money.Zero).Value;
        ctx.Db.Stockpiles.Add(created);
        return created;
    }

    /// <summary>Withdraw up to <paramref name="quantity"/> across a settlement's shops in id order.
    /// Returns the amount actually taken (≤ quantity).</summary>
    public static async Task<long> WithdrawAcrossShops(SimulationContext ctx, SettlementId settlementId, GoodId goodId, long quantity)
    {
        long remaining = quantity;
        long taken = 0;
        foreach (var stock in await StockpilesForGood(ctx, settlementId, goodId))
        {
            if (remaining <= 0) break;
            if (stock.Quantity <= 0) continue;
            long take = Math.Min(remaining, stock.Quantity);
            stock.Withdraw(take).OrThrow("shop-market withdraw");
            remaining -= take;
            taken += take;
        }
        return taken;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/ShopMarket.cs tests/WorldEcon.Engine.Tests.Unit/ShopMarketTests.cs
git commit -m "feat(economy): ShopMarket helper (shop-based market substrate)"
```

---

## Task 4: Engine — re-point ProductionPhase onto shops

Raw extraction → the endowment's producer-shop; batch outputs → the node's producer-shop; input sourcing → across the settlement's shops.

**Files:**
- Modify: `src/WorldEcon.Engine/Phases/ProductionPhase.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/ProductionOnShopsTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/ProductionOnShopsTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ProductionOnShopsTests
{
    [Test]
    public async Task RawExtraction_DepositsIntoEndowmentProducerShop()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var ore = Good.Create(s.World.Id, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            s.Db.Goods.Add(ore);
            var endow = ResourceEndowment.Create(s.World.Id, s.Settlement.Id, ore.Id, 30).Value;
            s.Db.ResourceEndowments.Add(endow);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            // No SettlementMarket stockpiles exist anymore; ore lives in a Producer shop.
            (await s.Db.Stockpiles.CountAsync(x => x.OwnerKind == StockpileOwnerKind.SettlementMarket)).Should().Be(0);
            var producerShop = await s.Db.Shops.SingleAsync(x => x.Kind == ShopKind.Producer);
            var oreStock = await s.Db.Stockpiles.SingleAsync(x => x.OwnerId == producerShop.Id.Value && x.GoodId == ore.Id);
            oreStock.Quantity.Should().BeGreaterThan(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: FAIL — ore still goes to a `SettlementMarket` stockpile (count != 0).

- [ ] **Step 3: Re-point ProductionPhase**

In `src/WorldEcon.Engine/Phases/ProductionPhase.cs`:

(a) **Raw extraction** (step 1 loop). Replace the body of the endowment loop:
```csharp
        foreach (var endow in endowments.OrderBy(e => e.Id.Value))
        {
            var good = await ctx.Db.Goods.FirstAsync(g => g.Id == endow.GoodId);
            var shop = await ShopMarket.GetOrCreateProducerShop(ctx, endow, $"{settlementNames[endow.SettlementId]} Mine");
            var stock = await ShopMarket.StockpileInShop(ctx, shop.Id, endow.GoodId);
            stock.Deposit(endow.Abundance, good.BaseValue, _valuation);
        }
```
> `settlementNames` must be built **before** the extraction loop (it currently builds it later for the completion log). Move the `settlementNames` dictionary build (`await ctx.Db.Settlements...ToDictionary(s => s.Id, s => s.Name)`) to the top of `ExecuteAsync`, after `worldId`. Use `settlementNames.TryGetValue(endow.SettlementId, out var n) ? n : "Mine"` to be null-safe.

(b) **Batch completion outputs.** In the completion loop, replace
```csharp
                var market = await GetOrCreateMarketStockpile(ctx, node.SettlementId, o.Good);
                market.Deposit(o.Quantity, perUnit, _valuation);
```
with
```csharp
                var nodeShop = await ShopMarket.GetOrCreateProducerShop(ctx, node,
                    $"{(settlementNames.TryGetValue(node.SettlementId, out var sn) ? sn : "Factory")} {node.Facility}");
                var outStock = await ShopMarket.StockpileInShop(ctx, nodeShop.Id, o.Good);
                outStock.Deposit(o.Quantity, perUnit, _valuation);
```

(c) **Input sourcing** (step 3, start new batches). Replace the input availability + reservation block. The current code does `GetOrCreateMarketStockpile` per input and `stock.Withdraw(line.Quantity)`. Replace with aggregate sourcing across the settlement's shops:
```csharp
            // Check inputs are available across the settlement's shops.
            bool allAvailable = true;
            foreach (var line in inputs)
            {
                if (await ShopMarket.TotalSupply(ctx, node.SettlementId, line.Good) < line.Quantity)
                {
                    allAvailable = false;
                    break;
                }
            }
            if (!allAvailable)
                continue;

            long committed = 0;
            foreach (var line in inputs)
            {
                // Weighted cost of what we withdraw, sourced across shops in id order.
                var sources = await ShopMarket.StockpilesForGood(ctx, node.SettlementId, line.Good);
                long need = line.Quantity;
                foreach (var src in sources)
                {
                    if (need <= 0) break;
                    if (src.Quantity <= 0) continue;
                    long take = Math.Min(need, src.Quantity);
                    committed += take * src.CostBasis.Units;
                    src.Withdraw(take).OrThrow("production input reservation");
                    need -= take;
                }
            }
```
Remove the now-unused `inputStocks` local and the old per-input `GetOrCreateMarketStockpile` loop. **Delete** the private `GetOrCreateMarketStockpile` method from `ProductionPhase` (it referenced `SettlementMarket`).

> The committed-cost computation now sums each withdrawn lot's source cost basis (more accurate than before, where one market pool had a single cost basis). This is a deliberate, behavior-preserving improvement consistent with the per-shop cost-basis model.

Add `using` for `WorldEcon.Engine;` is unnecessary (same namespace `WorldEcon.Engine.Phases` — `ShopMarket` is in `WorldEcon.Engine`; add `using WorldEcon.Engine;`? No — `WorldEcon.Engine.Phases` does not auto-see `WorldEcon.Engine`. Add `using WorldEcon.Engine;` is wrong since it's the parent namespace; in C# the parent namespace IS visible from a child namespace, so `ShopMarket` resolves without a using. Confirm by building.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `Passed!` (the new test + existing production tests — but note existing production/determinism tests that asserted SettlementMarket stockpiles will now FAIL; those are updated in Task 10. If a pre-existing test references `StockpileOwnerKind.SettlementMarket` and breaks here, leave it red and note it; Task 10 sweeps them).

> If too many existing tests break to tell signal from noise, jump to Task 10's test-sweep guidance, fix the references to query shops, then return. Practically: do Tasks 4–9 (production/pricing/consumption/trade/seeding) then Task 10 fixes all stale `SettlementMarket` test references together.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Phases/ProductionPhase.cs tests/WorldEcon.Engine.Tests.Unit/ProductionOnShopsTests.cs
git commit -m "feat(economy): production/extraction deposit into producer-shops"
```

---

## Task 5: Engine — re-point PricingPhase onto the aggregate

**Files:**
- Modify: `src/WorldEcon.Engine/Phases/PricingPhase.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/PricingOnShopsTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/PricingOnShopsTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class PricingOnShopsTests
{
    [Test]
    public async Task Pricing_WritesReferencePrice_ToEachShopStockpile()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var good = Good.Create(s.World.Id, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            s.Db.Goods.Add(good);
            var shopA = Shop.Create(s.World.Id, s.Settlement.Id, "A", 0, Money.Zero).Value;
            var shopB = Shop.Create(s.World.Id, s.Settlement.Id, "B", 0, Money.Zero).Value;
            s.Db.Shops.AddRange(shopA, shopB);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shopA.Id, good.Id, 100, new Money(20)).Value);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shopB.Id, good.Id, 100, new Money(20)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var prices = (await s.Db.Stockpiles.Where(x => x.GoodId == good.Id).ToListAsync())
                .Select(x => x.MarketPrice.Units).Distinct().ToList();
            prices.Should().HaveCount(1);          // uniform town reference price in Phase 1
            prices[0].Should().BeGreaterThan(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: FAIL (pricing still keyed to SettlementMarket; shop stockpiles keep `MarketPrice = 0`).

- [ ] **Step 3: Re-point PricingPhase**

In `src/WorldEcon.Engine/Phases/PricingPhase.cs`, replace the `foreach (var sp in LoadMarketStockpiles(...))` loop and the `LoadMarketStockpiles` helper. The new shape: iterate settlements × goods, compute the reference price from aggregate supply, write it to every shop stockpile of that good in the settlement.
```csharp
        foreach (var settlement in settlements.Values.OrderBy(s => s.Id.Value))
        {
            foreach (var good in goods.Values.OrderBy(g => g.Id.Value))
            {
                var stocks = await ShopMarket.StockpilesForGood(ctx, settlement.Id, good.Id);
                if (stocks.Count == 0)
                    continue;

                long popDemand = FixedMath.MulBp(settlement.Population, good.ConsumptionPerCapitaBp);
                long industrialDemand = 0;
                foreach (var node in nodes)
                {
                    if (node.SettlementId != settlement.Id || node.Disabled) continue;
                    if (!recipes.TryGetValue(node.RecipeId, out var recipe)) continue;
                    foreach (var line in recipe.Inputs)
                        if (line.Good == good.Id)
                            industrialDemand += line.Quantity;
                }

                long demand = popDemand + industrialDemand;
                long supply = Math.Max(stocks.Sum(s => s.Quantity), 1);
                long scarcityBp = FixedMath.DivRound(demand * FixedMath.BpScale, supply);
                long multBp = Math.Clamp(
                    FixedMath.PowBpInt(scarcityBp, ctx.World.ElasticityExponent),
                    ctx.World.MinPriceMultBp, ctx.World.MaxPriceMultBp);
                var price = new Money(FixedMath.MulBp(good.BaseValue.Units, multBp));

                foreach (var sp in stocks)
                    sp.SetMarketPrice(price);
            }
        }
```
Delete the old `LoadMarketStockpiles` method. Keep the `settlements`/`goods`/`recipes`/`nodes` dictionaries already loaded at the top of `ExecuteAsync` (they exist).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: the new pricing test passes (ignore stale SettlementMarket tests until Task 10).

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Phases/PricingPhase.cs tests/WorldEcon.Engine.Tests.Unit/PricingOnShopsTests.cs
git commit -m "feat(economy): pricing reads aggregate shop supply, writes per-shop reference price"
```

---

## Task 6: Engine — re-point ConsumptionPhase onto shops

**Files:**
- Modify: `src/WorldEcon.Engine/Phases/ConsumptionPhase.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/ConsumptionOnShopsTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/ConsumptionOnShopsTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumptionOnShopsTests
{
    [Test]
    public async Task Consumption_DepletesShopStock_AndEmitsConsumed()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var grain = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, 0, true, consumptionPerCapitaBp: 1000).Value;
            s.Db.Goods.Add(grain);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 0, Money.Zero).Value;
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, grain.Id, 200, new Money(10)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var remaining = (await s.Db.Stockpiles.SingleAsync(x => x.GoodId == grain.Id)).Quantity;
            remaining.Should().BeLessThan(200);
            (await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.Consumed)).Should().BeGreaterThan(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: FAIL (consumption still queries SettlementMarket; shop grain untouched).

- [ ] **Step 3: Re-point ConsumptionPhase**

Replace the `foreach (var stock in LoadMarketStockpiles(...))` loop with a settlement × consumable-good loop using `ShopMarket`. Replace the loop body and delete `LoadMarketStockpiles`:
```csharp
        foreach (var settlement in settlements.Values.OrderBy(s => s.Id.Value))
        {
            foreach (var good in consumable.Values.OrderBy(g => g.Id.Value))
            {
                long demand = FixedMath.MulBp(settlement.Population, good.ConsumptionPerCapitaBp);
                if (demand <= 0)
                    continue;
                long consumed = await ShopMarket.WithdrawAcrossShops(ctx, settlement.Id, good.Id, demand);

                if (consumed > 0)
                    await ctx.Log.EmitAsync(LogEventType.Consumed,
                        $"Population consumed {consumed} {good.Name} in {settlement.Name}", tick,
                        LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);

                if (consumed < demand)
                    await ctx.Log.EmitAsync(LogEventType.Stockout,
                        $"{good.Name} ran short of demand in {settlement.Name} (ate {consumed} of {demand} needed)", tick,
                        LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);
            }
        }
```
> `settlements` is already a `Dictionary<SettlementId, Settlement>` and `consumable` a `Dictionary<GoodId, Good>` at the top of the method. Keep them. (The `Consumed`/`Stockout` emission matches the current behavior and messages.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: the consumption test passes.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Phases/ConsumptionPhase.cs tests/WorldEcon.Engine.Tests.Unit/ConsumptionOnShopsTests.cs
git commit -m "feat(economy): consumption depletes across a settlement's shops"
```

---

## Task 7: Engine — re-point TradePhase onto shops

Buy exports across the seat settlement's shops; deposit imports into the destination's public-market shop; survey via aggregate price.

**Files:**
- Modify: `src/WorldEcon.Engine/Phases/TradePhase.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/TradeOnShopsTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/TradeOnShopsTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class TradeOnShopsTests
{
    [Test]
    public async Task Trade_NoLongerUsesSettlementMarket()
    {
        // Full DemoSeeder-style worlds are exercised by CLI smoke; here we assert the invariant
        // that after a multi-day advance, zero SettlementMarket stockpiles exist (everything shop-owned).
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var ore = Good.Create(s.World.Id, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            s.Db.Goods.Add(ore);
            s.Db.ResourceEndowments.Add(ResourceEndowment.Create(s.World.Id, s.Settlement.Id, ore.Id, 30).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerWeek);

            (await s.Db.Stockpiles.CountAsync(x => x.OwnerKind == StockpileOwnerKind.SettlementMarket)).Should().Be(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: FAIL (trade's `GetOrCreateMarketStockpile` creates a `SettlementMarket` stockpile on import).

- [ ] **Step 3: Re-point TradePhase**

In `src/WorldEcon.Engine/Phases/TradePhase.cs`, replace the four market-stockpile helpers' usages:

(a) **Delivery (Step A):** replace
```csharp
            var destStock = await GetOrCreateMarketStockpile(ctx, caravan.DestinationId, caravan.GoodId);
            destStock.Deposit(caravan.Quantity, caravan.UnitCostBasis, _valuation);
            long destPrice = destStock.MarketPrice.Units > 0
                ? destStock.MarketPrice.Units
                : goods[caravan.GoodId].BaseValue.Units;
```
with
```csharp
            var destShop = await ShopMarket.GetOrCreatePublicMarketShop(ctx, caravan.DestinationId);
            var destStock = await ShopMarket.StockpileInShop(ctx, destShop.Id, caravan.GoodId);
            destStock.Deposit(caravan.Quantity, caravan.UnitCostBasis, _valuation);
            long destPrice = destStock.MarketPrice.Units > 0
                ? destStock.MarketPrice.Units
                : goods[caravan.GoodId].BaseValue.Units;
```

(b) **Survey seat stocks (Step B):** replace the entire seat-survey + best-pick block (from `var seatStocks = (await LoadMarketStockpilesAt(...))` through the `bestSeatStock.Withdraw(quantity)...merchant.Spend(...)` lines) with a per-good aggregate over the seat settlement's shops that enumerates the goods on offer, picks the most profitable (good, destination), and buys across shops:
```csharp
            // Goods available at the seat (aggregate across shops).
            var seatSupply = new List<(GoodId Good, long Qty, long Price)>();
            foreach (var good in goods.Values.OrderBy(g => g.Id.Value))
            {
                var stocks = await ShopMarket.StockpilesForGood(ctx, merchant.Seat, good.Id);
                long qty = stocks.Sum(x => x.Quantity);
                if (qty <= 0) continue;
                long price = stocks.Select(x => x.MarketPrice.Units).FirstOrDefault(p => p > 0);
                if (price == 0) price = good.BaseValue.Units;
                seatSupply.Add((good.Id, qty, price));
            }
            if (seatSupply.Count == 0)
                continue;

            GoodId bestGoodId = default;
            ReachableSettlement? bestDest = null;
            long bestProfit = long.MinValue, bestSeatPrice = 0, bestSeatQty = 0;
            foreach (var offer in seatSupply)
            {
                foreach (var dest in reachable.OrderBy(d => d.Settlement.Value))
                {
                    long destPrice = await SeatRefPrice(ctx, dest.Settlement, offer.Good, goods[offer.Good]);
                    long transportPerUnit = dest.Distance * TransportCostUnitsPerDistance;
                    long profitPerUnit = destPrice - offer.Price - transportPerUnit;
                    bool better = profitPerUnit > bestProfit
                        || (profitPerUnit == bestProfit && bestDest is not null
                            && IsBetterTieBreak(offer.Good, dest, bestGoodId, bestDest));
                    if (better)
                    {
                        bestProfit = profitPerUnit; bestGoodId = offer.Good; bestDest = dest;
                        bestSeatPrice = offer.Price; bestSeatQty = offer.Qty;
                    }
                }
            }
            if (bestDest is null || bestProfit <= 0)
                continue;

            long affordable = bestSeatPrice == 0 ? merchant.CargoCapacity : merchant.Capital.Units / bestSeatPrice;
            long quantity = Math.Min(merchant.CargoCapacity, Math.Min(bestSeatQty, affordable));
            if (quantity < 1)
                continue;

            long got = await ShopMarket.WithdrawAcrossShops(ctx, merchant.Seat, bestGoodId, quantity);
            quantity = got; // in case of a race with same-tick depletion
            if (quantity < 1)
                continue;
            merchant.Spend(new Money(quantity * bestSeatPrice));
```
And add a small private helper for the destination reference price:
```csharp
    private static async Task<long> SeatRefPrice(SimulationContext ctx, SettlementId settlement, GoodId goodId, Good good)
    {
        var stocks = await ShopMarket.StockpilesForGood(ctx, settlement, goodId);
        long price = stocks.Select(x => x.MarketPrice.Units).FirstOrDefault(p => p > 0);
        return price > 0 ? price : good.BaseValue.Units;
    }
```
Update the caravan-dispatch tail to use `bestGoodId`/`quantity` (it already does) and keep the `MerchantDeparted` log.

(c) **Delete** the now-unused private helpers `LoadMarketStockpilesAt`, `FindMarketStockpile`, and `GetOrCreateMarketStockpile` from `TradePhase`. Keep `LoadUndeliveredCaravans` and `FindMerchant`.

> This is the most intricate phase rewrite. Keep the determinism tie-breaks (`IsBetterTieBreak`, ordering `reachable` by `Settlement.Value`, goods by `Id.Value`). The merchant money behavior (Spend on dispatch, Earn on delivery) is unchanged.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: the trade invariant test passes.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Phases/TradePhase.cs tests/WorldEcon.Engine.Tests.Unit/TradeOnShopsTests.cs
git commit -m "feat(economy): trade buys across seat shops, deposits imports to public-market shop"
```

---

## Task 8: Engine — party `adjust` targets the public-market shop

**Files:**
- Modify: `src/WorldEcon.Engine/Actions/LogEventService.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/LogEventServiceTests.cs` (extend)

`LogEventService` runs outside an advance (no `SimulationContext`), so it can't use `ShopMarket` (which takes `SimulationContext`). Add small local get-or-create logic mirroring `ShopMarket` but over the bare `WorldDbContext`.

- [ ] **Step 1: Write the failing test**

Add to `tests/WorldEcon.Engine.Tests.Unit/LogEventServiceTests.cs`:
```csharp
    [Test]
    public async Task AdjustMarketStock_TargetsPublicMarketShop()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var good = WorldEcon.Domain.Economy.Good.Create(s.World.Id, "Salt",
                WorldEcon.Domain.Economy.GoodCategory.Food, new WorldEcon.SharedKernel.Money(5), "bag",
                WorldEcon.Domain.Economy.SizeClass.Small, 0, true, 0).Value;
            s.Db.Goods.Add(good);
            await s.Db.SaveChangesAsync();

            var result = await new WorldEcon.Engine.Actions.LogEventService(s.Db)
                .AdjustMarketStockAsync(s.World.Id, s.Settlement.Id, good.Id, 50, DateTimeOffset.UtcNow);
            result.IsError.Should().BeFalse();

            var pub = await s.Db.Shops.SingleAsync(x => x.Kind == WorldEcon.Domain.Economy.ShopKind.PublicMarket);
            var stock = await s.Db.Stockpiles.SingleAsync(x => x.OwnerId == pub.Id.Value && x.GoodId == good.Id);
            stock.Quantity.Should().Be(50);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: FAIL (adjust still creates/uses a `SettlementMarket` stockpile).

- [ ] **Step 3: Re-point the adjust helpers**

In `LogEventService.cs`, replace `FindMarketStockpile` and `GetOrCreateMarketStockpile` to target the settlement's public-market shop:
```csharp
    private async Task<Shop> GetOrCreatePublicMarketShop(WorldId worldId, SettlementId settlementId)
    {
        var local = _db.Shops.Local.FirstOrDefault(sh =>
            sh.SettlementId == settlementId && sh.Kind == ShopKind.PublicMarket);
        if (local is not null) return local;
        var existing = await _db.Shops.FirstOrDefaultAsync(sh =>
            sh.WorldId == worldId && sh.SettlementId == settlementId && sh.Kind == ShopKind.PublicMarket);
        if (existing is not null) return existing;
        var created = Shop.CreateVendor(worldId, settlementId, "Town Market", ShopKind.PublicMarket).Value;
        _db.Shops.Add(created);
        return created;
    }

    private async Task<Stockpile?> FindMarketStockpile(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var pub = await GetOrCreatePublicMarketShop(worldId, settlementId);
        var local = _db.Stockpiles.Local.FirstOrDefault(s =>
            s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == pub.Id.Value && s.GoodId == goodId);
        if (local is not null) return local;
        return await _db.Stockpiles.FirstOrDefaultAsync(s =>
            s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop
            && s.OwnerId == pub.Id.Value && s.GoodId == goodId);
    }

    private async Task<Stockpile> GetOrCreateMarketStockpile(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var existing = await FindMarketStockpile(worldId, settlementId, goodId);
        if (existing is not null) return existing;
        var pub = await GetOrCreatePublicMarketShop(worldId, settlementId);
        var created = Stockpile.CreateForShop(worldId, pub.Id, goodId, 0, Money.Zero).Value;
        _db.Stockpiles.Add(created);
        return created;
    }
```
Add `using WorldEcon.Domain.Economy;` if not present (for `Shop`/`ShopKind`). The `BuyFromShopsAsync`/`SetSettlementProductionDisabledAsync` methods already operate over shops/nodes and are unchanged.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Actions/LogEventService.cs tests/WorldEcon.Engine.Tests.Unit/LogEventServiceTests.cs
git commit -m "feat(economy): party adjust targets the settlement public-market shop"
```

---

## Task 9: Seeding — producer & public-market shops

**Files:**
- Modify: `src/WorldEcon.Cli/DemoSeeder.cs`
- Modify: `src/WorldEcon.Seeding/SeedImporter.cs`

The engine creates producer/public-market shops lazily, so seeding does **not strictly need** to pre-create them. But the `new`-world experience is better if the demo world already shows the structure, and the JSON `market` section must land somewhere. Minimal, correct changes:

- [ ] **Step 1: DemoSeeder — route the seeded smithy/ore via producer-shops is optional; the only required change is the JSON-importer market routing (next step). For the demo, leave production lazy (first advance creates the producer shops). No code change required in `DemoSeeder.cs` unless you want pre-seeded producer shops.** Verify by smoke test in Step 3.

- [ ] **Step 2: SeedImporter — route the JSON `market` section into a public-market shop**

In `src/WorldEcon.Seeding/SeedImporter.cs`, replace the "Settlement market stockpiles (owner = the settlement itself)" block:
```csharp
        // Settlement market stockpiles → the settlement's public-market shop (substrate: no
        // SettlementMarket pool). Created once per settlement that declares a market.
        var marketEntries = NonNull(s.Market).ToList();
        if (marketEntries.Count > 0)
        {
            var pub = Unwrap(Shop.CreateVendor(worldId, settlementId, "Town Market", ShopKind.PublicMarket));
            db.Shops.Add(pub);
            foreach (var m in marketEntries)
            {
                var stockpile = Unwrap(Stockpile.CreateForShop(
                    worldId, pub.Id, ResolveGood(goodsByName, m.Good, $"market in '{s.Name}'"),
                    m.Quantity, new Money(m.UnitCostBasis)));
                db.Stockpiles.Add(stockpile);
            }
        }
```
Add `using WorldEcon.Domain.Economy;` if `ShopKind` isn't already imported (it is — `Shop`/`Stockpile` are used).

- [ ] **Step 3: Smoke test (new world + advance)**

```bash
rm -f /tmp/shop.db
dotnet run --project src/WorldEcon.Cli -c Release -- new /tmp/shop.db
dotnet run --project src/WorldEcon.Cli -c Release -- advance /tmp/shop.db 1w
# No SettlementMarket stockpiles should remain; producer + public-market shops exist:
command -v sqlite3 >/dev/null && sqlite3 /tmp/shop.db \
  "SELECT (SELECT COUNT(*) FROM stockpiles WHERE OwnerKind='SettlementMarket') AS market_pool,
          (SELECT COUNT(*) FROM shops WHERE Kind='Producer') AS producer_shops,
          (SELECT COUNT(*) FROM shops WHERE Kind='PublicMarket') AS public_markets;"
rm -f /tmp/shop.db
```
Expected: `market_pool = 0`, `producer_shops >= 1`, `public_markets >= 1`.

- [ ] **Step 4: Commit**

```bash
git add src/WorldEcon.Seeding/SeedImporter.cs
git commit -m "feat(economy): seed JSON market section into a public-market shop"
```

---

## Task 10: Sweep stale `SettlementMarket` references; full green

**Files:**
- Modify: any remaining production/test code referencing `StockpileOwnerKind.SettlementMarket`.

- [ ] **Step 1: Find all remaining references**

Run:
```bash
grep -rn "SettlementMarket" src tests --include=*.cs | grep -vE "obj/|bin/"
```
Expected remaining hits: the `Enums.cs` definition + its doc comment (keep); **`PerishabilityPhase.cs`** (a now-dead `OwnerKind == SettlementMarket` log branch); and any **tests** that seeded or asserted `SettlementMarket` stockpiles (these need updating to shops).

- [ ] **Step 1b: Remove the dead `SettlementMarket` branch in `PerishabilityPhase`**

After the substrate swap, every stockpile is shop-owned, so the `if (stock.OwnerKind == StockpileOwnerKind.SettlementMarket) ... "spoiled in a market"` branch is unreachable. Delete that branch; keep only the `else if (stock.OwnerKind == Shop && shopsByOwnerId.TryGetValue(...))` branch (which now also covers public-market shop spoilage as "spoiled at Town Market"). Simplify the `else if` to an `if`. Verify spoilage still logs via the existing `Perishability_ShopOwnedStock_EmitsSpoilage_ScopedToShop` test.

- [ ] **Step 2: Update each stale test**

For every test that created a `Stockpile.Create(..., StockpileOwnerKind.SettlementMarket, settlementId.Value, ...)` or asserted on SettlementMarket stockpiles, change it to create a `Shop` and a shop-owned stockpile (`Stockpile.CreateForShop(world, shop.Id, good, qty, cost)`), and assert over shop-owned stock / `ShopMarket.TotalSupply`. Use the `LogTestWorld` + shop pattern from Task 3's test. (Likely affected: older `ProductionPhaseTests`, `ConsumptionPhaseTests`/stockout tests, `PricingPhaseTests`, `TradePhaseTests`, and the determinism tests.)

- [ ] **Step 3: Update `CmdStock` (CLI) to aggregate over shops**

In `src/WorldEcon.Cli/CommandRunner.cs` `CmdStock`, replace the `SettlementMarket` query with an aggregate over the settlement's shop stockpiles, grouped by good:
```csharp
        var shopIds = (await ctx.Shops.Where(sh => sh.SettlementId == settlement.Id).ToListAsync())
            .Select(sh => sh.Id.Value).ToHashSet();
        var stockpiles = (await ctx.Stockpiles
                .Where(s => s.OwnerKind == WorldEcon.Domain.Economy.StockpileOwnerKind.Shop)
                .ToListAsync())
            .Where(s => shopIds.Contains(s.OwnerId))
            .GroupBy(s => s.GoodId)
            .Select(g => new { GoodId = g.Key, Qty = g.Sum(x => x.Quantity), MinCost = g.Min(x => x.CostBasis.Units), Price = g.Select(x => x.MarketPrice.Units).FirstOrDefault(p => p > 0) })
            .OrderBy(x => goodsById.TryGetValue(x.GoodId, out var n) ? n : string.Empty, StringComparer.Ordinal)
            .ToList();
        // ... print Good | Qty | MinPrice(=MinCost) | Price using stockCurrency.Format(new Money(...))
```
Update the header/rows to print `Good | Quantity | Min Price | Price` (Min Price = cheapest cost basis among the settlement's shops for that good). `CmdPrice` already operates over shops (via `PriceMarginQuery`) — leave it.

- [ ] **Step 4: Full build + every suite**

Run:
```bash
dotnet build
for p in SharedKernel Simulation Domain Persistence Application Engine Seeding Tui; do
  echo "=== $p ==="; dotnet run --project tests/WorldEcon.$p.Tests.Unit -c Release 2>&1 | grep -oE "Passed!|Failed!" | head -1
done
```
Expected: `0 Error(s) 0 Warning(s)` and `Passed!` everywhere.

- [ ] **Step 5: Granularity & conservation regression**

Add a test (e.g. `tests/WorldEcon.Engine.Tests.Unit/SubstrateGranularityTests.cs`) seeding an endowment + recipe + node, advancing 8 days as a single chunk vs eight 1-day chunks (fresh identical worlds), and asserting the produced output good's **total shop supply** is equal across both paths (granularity independence on the new substrate). Mirror `ProductionAdvanceTests`. Run the Engine suite; expect `Passed!`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(economy): retire SettlementMarket usage; CLI stock aggregates over shops; regressions"
```

---

## Task 11: UI — marketplace board (per-shop offers) + sortable table

**Files:**
- Modify: `src/WorldEcon.Tui/Navigation/Navigator.cs`
- Modify: `src/WorldEcon.Tui/Shell/TuiShell.cs` (sort key)
- Test: `tests/WorldEcon.Tui.Tests.Unit/MarketBoardTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Tui.Tests.Unit/MarketBoardTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Tui;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Tests.Unit;

public class MarketBoardTests
{
    [Test]
    public async Task MarketBoard_ListsEachShopOffer_WithMinPriceAndPrice()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);
            var hammerfell = await ctx.Settlements.SingleAsync(s => s.Name == "Hammerfell");

            var nav = new Navigator();
            var view = await nav.MarketBoardAsync(hammerfell.Id, tui);

            view.Columns.Should().ContainInOrder("Good", "Category", "Shop", "Qty", "Min Price", "Price");
            // One row per shop offer (Hammerfell seed has shops with potions/bread).
            view.Rows.Should().NotBeEmpty();
        }
        finally { File.Delete(path); }
    }
}
```
> Confirm `TestWorld.SeedTempDbAsync`/`NewContext` exist in the TUI test project (the existing `ActionTests` use them).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release`
Expected: build failure — `Navigator.MarketBoardAsync` does not exist.

- [ ] **Step 3: Add the marketplace board to `Navigator`**

In `Navigator.cs`, add:
```csharp
    /// <summary>Marketplace board for a settlement: one row per shop's offer of each good.</summary>
    public async Task<NavView> MarketBoardAsync(SettlementId settlementId, TuiContext ctx)
    {
        var goodNames = await Lookups.GoodNamesAsync(ctx);
        var goods = (await ctx.Db.Goods.Where(g => g.WorldId == ctx.World.Id).ToListAsync())
            .ToDictionary(g => g.Id);
        var shops = (await ctx.Db.Shops.Where(s => s.SettlementId == settlementId).ToListAsync())
            .ToDictionary(s => s.Id.Value, s => s);
        var stocks = (await ctx.Db.Stockpiles
                .Where(s => s.OwnerKind == StockpileOwnerKind.Shop)
                .ToListAsync())
            .Where(s => shops.ContainsKey(s.OwnerId) && s.Quantity > 0)
            .ToList();

        var rows = stocks
            .Select(s =>
            {
                var shop = shops[s.OwnerId];
                var good = goods.TryGetValue(s.GoodId, out var g) ? g : null;
                return new NavRow(s.Id.Value.ToString(), NavKind.Leaf, new[]
                {
                    good?.Name ?? s.GoodId.Value.ToString(),
                    good?.Category.ToString() ?? "",
                    shop.Name,
                    s.Quantity.ToString(),
                    ctx.FormatMoney(s.CostBasis),   // Min Price = cost basis (break-even)
                    ctx.FormatMoney(s.MarketPrice),
                });
            })
            .OrderBy(r => r.Cells[0], StringComparer.Ordinal).ThenBy(r => r.Cells[2], StringComparer.Ordinal)
            .ToList();

        var name = (await Lookups.SettlementNamesAsync(ctx)).Resolve(settlementId.Value);
        return new NavView($"{name} / Market", ["Good", "Category", "Shop", "Qty", "Min Price", "Price"], rows);
    }
```
Then in `CityCategoryView`, change the `"market"` case to use it:
```csharp
        case "market":
            return await MarketBoardAsync(settlementId, ctx);
```
And in `CityChooserView`, change the market count line to count shop offers (not SettlementMarket):
```csharp
    var shopIdsForMarket = (await ctx.Db.Shops.Where(sh => sh.SettlementId == settlementId).ToListAsync())
        .Select(sh => sh.Id.Value).ToHashSet();
    var market = (await AllStockpiles(ctx)).Count(s => s.OwnerKind == StockpileOwnerKind.Shop && shopIdsForMarket.Contains(s.OwnerId) && s.Quantity > 0);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Add a sort key to the table (`s` cycles sort column)**

In `TuiShell.cs`, add a sort that re-orders the current frame's displayed rows by a chosen column (reusing the per-frame model). Add a field `private int _sortColumn = -1;` and handle a key (use `'o'` for "order"; `s`/`S` are taken by snapshot). In `OnTableKey`, before the action dispatch, add:
```csharp
        if (key.AsRune.Value == 'o')
        {
            var cols = _stack[^1].View.Columns.Count;
            if (cols > 0) { _sortColumn = (_sortColumn + 1) % cols; ApplyTop(); }
            key.Handled = true;
            return;
        }
```
In `GetFilteredRows` (or a new sort step in `ApplyTop`), after filtering, if `_sortColumn >= 0` sort rows by `r => r.Cells[_sortColumn]` (Ordinal). Keep it minimal; reset `_sortColumn = -1` in `PushView`/`SetRoot`/`Back` so each frame starts unsorted. Add `o sort` to the status hints in `RefreshStatus`.

> Number-aware sorting is a nice-to-have; Ordinal string sort is acceptable for Phase 1 (note it in the commit). Verify the build + the TUI suite still pass.

- [ ] **Step 6: tmux smoke**

```bash
rm -f /tmp/mb.db; dotnet run --project src/WorldEcon.Cli -c Release -- new /tmp/mb.db
dotnet run --project src/WorldEcon.Cli -c Release -- advance /tmp/mb.db 1w
tmux kill-session -t mb 2>/dev/null; tmux new-session -d -s mb -x 200 -y 50
tmux send-keys -t mb "dotnet run --project src/WorldEcon.Tui -c Release -- /tmp/mb.db" Enter; sleep 12
tmux send-keys -t mb "Enter"; sleep 1            # drill into Hammerfell
# select the "Market goods" category and drill
tmux send-keys -t mb "j j j Enter"; sleep 2; tmux capture-pane -t mb -p | head -16
tmux send-keys -t mb "q"; tmux kill-session -t mb 2>/dev/null; rm -f /tmp/mb.db
```
Expected: a "… / Market" board with columns Good | Category | Shop | Qty | Min Price | Price and one row per shop offer. Paste the pane.

- [ ] **Step 7: Commit**

```bash
git add src/WorldEcon.Tui tests/WorldEcon.Tui.Tests.Unit/MarketBoardTests.cs
git commit -m "feat(tui): marketplace board (per-shop offers, Min Price/Price) + column sort"
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
- [ ] **No SettlementMarket inventory anywhere:** a fresh `new` + `advance 1w` leaves `market_pool = 0` (Task 9 smoke).
- [ ] **Final code review** (subagent-driven-development final reviewer) against this plan + the spec; then `superpowers:finishing-a-development-branch`.
- [ ] **Update memory + decisions log:** record that Phase 1 (shop substrate) shipped; note the roadmap position (Phase 2 = `RepresentativeConsumer` + tills + income question next).
