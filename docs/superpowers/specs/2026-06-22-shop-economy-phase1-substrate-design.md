# Shop Economy — Phase 1: Shop Substrate (Design Spec)

**Date:** 2026-06-22
**Status:** Approved (brainstorming complete; ready for implementation plan)
**Project:** WorldEcon — C#/.NET 10 world-economy simulation (`/home/kayden/workspaces/dnd`)

## Why this exists

Today the simulation runs on a `SettlementMarket` stockpile pool: one anonymous per-good
inventory per town that production fills, population consumes from (free), pricing prices, and
merchants trade. Shops exist (`Shop` with inventory, `Till`, `MarkupBp`) but are **disconnected
islands** — nothing flows between the market pool and the shops, `Shop.Till` is never touched, and
the build spec's intent (shops sell at `cost_basis × (1+markup)`, hold a till that can run dry) was
never implemented.

The chosen target model is **"shops all the way down"**: every vendor — including each producer —
is a shop that holds its own inventory at its **own cost basis**, and the "market" becomes an
**aggregate read-model** over all shops rather than a container. This fixes the core unrealism that
a shared market pool forces every seller to share one cost basis and one price.

This is a foundational re-architecture, so it is **decomposed into phases**. **This spec covers
Phase 1 only: the substrate swap, behavior-preserving, with no retail money.** Money (tills,
per-shop retail pricing, consumer agents) is Phase 2.

## Phase roadmap (context; only Phase 1 is in scope here)

1. **Phase 1 — Shop substrate (THIS SPEC).** Goods live in shops; the market becomes an aggregate
   view; per-shop cost bases exist. Behavior preserved (free population consumption, merchants as
   today, same pricing formula). No tills/markup/retail money.
2. **Phase 2 — Demand side & retail money.** Introduce `RepresentativeConsumer` (population-spawned
   cohort agents, parallel to `RepresentativeMerchant`, holding a budget and needs like food); they
   buy from shops to meet needs within budget; tills cycle; per-shop retail pricing (`cost+markup`)
   so shop prices diverge. **Open question deferred to Phase 2:** where consumer money comes from —
   a periodic allowance per cohort vs real wages from production labor (the income/wage loop the
   build spec avoided by keeping population unwalleted). Possibly its own sub-phase.
3. **Phase 3 — Industrial demand & merchant trade over shops; transient caravan stalls** (the
   "farmers market" dynamic: a visiting caravan sets up a temporary vendor stall in the PublicMarket,
   local shops/consumers buy from it during its dwell time, then it leaves with unsold cargo).
4. **Phase 4 — Production profitability throttle** (producers slow/stop when their shop accumulates
   unsold stock at a loss; closes the glut loop that currently drives prices to the floor).
5. **Later — Happiness → luxury demand** (met needs / surplus budget raise willingness to spend on
   luxury goods).

## Phase 1 goals & non-goals

**Goals**
- All economy inventory is owned by **shops**; `SettlementMarket` is retired.
- Each `ProductionNode` and each `ResourceEndowment` has a **producer-shop**; their output lands
  there at production cost basis (so per-shop cost bases are real from day one).
- Each settlement has one **PublicMarket** shop (catch-all for imports + seeded non-producer goods).
- A **`MarketView`** read-model aggregates supply + a reference price per `(settlement, good)`.
- Production, pricing, consumption, industrial demand, and trade are re-pointed onto shops +
  `MarketView`, **preserving current behavior**.
- The TUI market screen becomes a **marketplace board** listing every shop's offer.

**Non-goals (Phase 2+)**
- No retail money: `Shop.Till` and `Shop.MarkupBp` stay dormant; population still consumes free;
  shops do not yet buy/restock or charge a markup.
- No `RepresentativeConsumer`, no per-shop divergent retail pricing, no wage/income loop.
- No transient caravan stalls (Phase 3); no production throttle (Phase 4).
- No happiness/luxury (later).

---

## Section 1 — Model

### `Shop` becomes a generalized settlement vendor
`Shop` (in `WorldEcon.Domain.Economy`) already has `WorldId`, `SettlementId`, `Name`, `MarkupBp`,
`Till`, `Quote(...)`. Add exactly one field:
- **`ShopKind Kind`** — `enum ShopKind { Producer, Retail, PublicMarket }`.

The **link is a forward reference from the producer to its shop**, not a back-reference on the shop:
`ProductionNode` and `ResourceEndowment` each gain a **`ShopId ProducerShopId`** pointing at their
producer-shop. This is what the deposit logic needs (given a node/endowment, deposit into *its*
shop), and it keeps `Shop` itself free of producer-specific fields. A `Producer`-kind shop is
identified by `Kind`; its owning node/endowment is found by the node/endowment that references it.

`Till` and `MarkupBp` remain on the `Shop` model but are **unused in Phase 1**.

### All inventory moves under shops
`StockpileOwnerKind` is currently `{ SettlementMarket = 0, Shop = 1, Agent = 2 }`. Phase 1 **retires
`SettlementMarket`**: every economy `Stockpile` becomes `OwnerKind.Shop` with `OwnerId` = a
`ShopId`. Each good keeps its own weighted-average `CostBasis` per shop (already supported).
`Stockpile.MarketPrice` stays per-stockpile (the pricing phase writes the town reference price to
each shop's copy in Phase 1; per-shop divergence is Phase 2).

> The `Agent` owner kind is untouched (reserved). Only `SettlementMarket` is retired.

### Per-settlement shop population
After Phase 1, a settlement's shops are:
- One **`Producer`** shop per `ProductionNode` — fronts that node; receives its output.
- One **`Producer`** shop per `ResourceEndowment` — the "mine/farm"; receives its raw extraction.
- The existing **`Retail`** shops (e.g. The Sundries) — same inventory, reclassified `Retail`.
- One **`PublicMarket`** shop — holds migrated old market-pool stock, seeded non-producer goods, and
  caravan imports.

---

## Section 2 — `MarketView` (the market as a read-model)

The market stops being a container and becomes a **read-model**. For a `(settlement, good)` it
aggregates, across all shops in that settlement **in deterministic shop-id order**:
- **total quantity** on offer, and
- a **reference price** (the town supply/demand price the pricing phase computes).

It is the single thing every "market" consumer reads: pricing, consumption, industrial sourcing,
merchant trade, and the UI. Two usage modes share the aggregation:
- **Engine phases** need the `.Local`-merge pattern (union DB rows with `ctx.Db.Stockpiles.Local`,
  dedup by id) so within-advance unsaved changes are visible — matching the existing phase pattern.
- **UI/CLI** need a plain read.

The shared aggregation logic lives in one place (a `MarketView` service/helper) usable both ways.
In Phase 1 the reference price is **uniform** across a settlement's shops for a given good (one town
price). In Phase 2 each shop prices independently and the "market price" becomes the **cheapest**
shop offering the good.

---

## Section 3 — Re-pointed phases (behavior-preserving, no retail money)

Each phase moves off the retired `SettlementMarket` pool onto shops + `MarketView`. Behavior is
preserved. The only money rule is **no *retail* money is introduced** — population stays free, tills
and markup stay dormant. Existing **merchant** money (`Earn`/`Spend`) is unchanged (it is not retail
money).

- **Production & raw extraction.** Outputs deposit into the **producer-shop** of the node/endowment
  that produced them, at production cost basis. (`ResourceEndowment` raw extraction → that
  endowment's producer-shop.) `GetOrCreateMarketStockpile`-style helpers become
  "get-or-create the producer-shop's stockpile for this good."
- **Pricing.** Computes the town reference price per `(settlement, good)` from **aggregate** supply
  via `MarketView`, using the **unchanged** formula
  `market_price = base_value × clamp(scarcity^elasticity, min_mult, max_mult)` with
  `scarcity = demand / max(supply, 1)` and `demand = population consumption + industrial input demand`
  (as today). It writes the result to **each** shop's `Stockpile.MarketPrice` for that good (uniform
  in Phase 1).
- **Consumption.** Population demand (`= population × ConsumptionPerCapitaBp`, as today) depletes the
  settlement's shops' stock of the good in **deterministic shop-id order** until met or dry. Still
  **free** (no money). Emits the existing `Consumed` and `Stockout` log events. *(This is a
  placeholder that the Phase 2 `RepresentativeConsumer` replaces.)*
- **Industrial demand.** Production nodes withdraw inputs across the settlement's shops (deterministic
  shop-id order), committing the **source shops' cost basis** as the work order's input cost
  (preserving cost-basis flow).
- **Trade.** Merchants survey reachable settlements via `MarketView` (aggregate supply + reference
  price), buy exports by withdrawing from the **seat** settlement's shops, and deposit imports into
  the **destination** settlement's **PublicMarket** shop. Merchant money preserved.
  *(Transient caravan stalls are Phase 3.)*
- **Perishability.** Already iterates all stockpiles (now all shop-owned) — unchanged; still logs
  per-shop `Spoilage`.
- **Party/DM actions.** `LogEventService.AdjustMarketStockAsync` (the `adjust` command) targets the
  settlement's **PublicMarket** shop (the natural successor to "the settlement market");
  `BuyFromShopsAsync` already targets shops. `SetSettlementProductionDisabledAsync` unchanged.

---

## Section 4 — Migration & seeding

- **EF migration** (existing worlds), forward-only and deterministic:
  1. Create one `PublicMarket` shop per settlement.
  2. Create one `Producer` shop per `ProductionNode` (fronting it) and per `ResourceEndowment`.
  3. **Re-own** every existing `SettlementMarket` stockpile to its settlement's `PublicMarket` shop
     (set `OwnerKind = Shop`, `OwnerId = <publicMarketShopId>`), preserving quantity, cost basis, and
     market price (no inventory loss — a test asserts conservation).
  4. The `SettlementMarket` enum value is retired in code; existing rows are migrated as above so no
     row references it afterward.
  > Producer shops created for already-existing production runs start empty; future production fills
  > them. Existing market stock (including past production output) lands in the PublicMarket shop —
  > acceptable, since Phase 1 preserves aggregate behavior, not per-shop provenance of legacy stock.
- **Seeding** (new worlds): `DemoSeeder` and the JSON seed importer create producer-shops alongside
  nodes/endowments and a PublicMarket shop per settlement, and deposit producer output / retail /
  seed stock into the correct shops.

---

## Section 5 — Determinism & testing

- **Stable ordering.** Every cross-shop operation (consumption depletion, industrial input sourcing,
  trade export buys, pricing write-back) iterates shops in **stable `Id.Value` order**. No behavior
  depends on EF return order. (Value-converted typed IDs don't translate in SQL `ORDER BY`, so order
  in memory after materializing — the established codebase rule.)
- **Granularity invariant preserved.** `advance(N)` must equal `k × advance(N/k)` for production,
  consumption, and pricing over the new substrate (the existing invariant; a regression test guards
  it).
- **Conservation.** The migration must not create or destroy inventory; quantities and cost bases are
  preserved when re-owning to PublicMarket.

**Tests (TDD per the plan):**
- Production/extraction deposits land in the producing node's/endowment's producer-shop (not a pool).
- Consumption depletes across a settlement's shops in id order; `Consumed`/`Stockout` still emitted.
- Pricing computes the reference price from aggregate supply and writes it to each shop's stockpile.
- Industrial demand sources inputs across shops, committing source cost basis.
- Trade deposits imports into the destination PublicMarket shop; merchant money behaves as before.
- `MarketView` aggregation: total supply + reference price per `(settlement, good)` across shops.
- Migration re-owns `SettlementMarket` stock to PublicMarket with conservation (no quantity loss).
- End-to-end: advance a week; conservation/sanity holds; granularity independence holds.

---

## Section 6 — UI (the marketplace board)

- Under a settlement, **Market** becomes a **listing of every offer** — each row is one shop's stock
  of one good — with columns:

  **Good | Category | Shop | Qty | Min Price | Price**

  - **Min Price** = the offering shop's per-unit **cost basis** for that good (its break-even floor;
    below it the shop sells at a loss). Surfaces the existing per-shop `Stockpile.CostBasis` with a
    clearer label.
  - **Price** = the current sale / market reference price for that good.
  - Showing both makes an "underwater" good (market price < cost basis, e.g. an oversupplied glut)
    visible at a glance.
- **Sortable** by any column (good, category, shop, qty, min price, price) via a sort key, and
  **filterable** with the existing `/`. Optionally drill **Market → good-category → offers of that
  type** for a grouped view. (Basic column-sort is new on the market view; it can generalize to other
  tables later.)
- Drilling a single **shop** shows that shop's own inventory (and, in Phase 2, its own prices + till).
- CLI `stock` and `price` read through `MarketView`; the listing surfaces per-shop rows.

---

## Implementation order (suggested for the plan)

1. Domain: `ShopKind` + `Shop` fields (`Kind`, producer-node/endowment link); keep `StockpileOwnerKind`
   but stop using `SettlementMarket`.
2. Persistence: EF config + migration (create shops, re-own market stock to PublicMarket, conservation
   test).
3. `MarketView` aggregation service/helper (engine `.Local`-merge mode + plain UI read), with tests.
4. Engine: re-point Production/extraction → producer-shops.
5. Engine: re-point Pricing → `MarketView` aggregate + write-back to shop stockpiles.
6. Engine: re-point Consumption + industrial demand → shops (deterministic order).
7. Engine: re-point Trade → shops (buy from seat shops, deposit imports to PublicMarket).
8. Party/DM: `adjust` targets PublicMarket shop.
9. Seeding: `DemoSeeder` + JSON importer create producer/public-market shops.
10. UI: marketplace board (columns incl. Min Price), sortable/filterable; CLI via `MarketView`.
11. Determinism/granularity + conservation regression tests throughout.
