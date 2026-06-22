# Shop Economy — Phase 2: Demand Side & Retail Money (Design Spec)

**Date:** 2026-06-22
**Status:** Approved (brainstorming complete; ready for implementation plan)
**Project:** WorldEcon — C#/.NET 10 world-economy simulation (`/home/kayden/workspaces/dnd`)

## Why this exists

Phase 1 put all inventory in shops and made "the market" an aggregate view, but the **demand side is
still free and unwalleted**: today's `ConsumptionPhase` makes population eat goods for free, and
`Shop.Till` / `Shop.MarkupBp` are dormant. Phase 2 introduces the **walleted demand side**: a
representative **consumer** agent (the mirror of `RepresentativeMerchant`) that holds a budget and
needs, **buys** from shops to meet those needs, pays the shop's till, and pays a **scarcity-flexing
retail price**. This activates the dormant `Till`/`Markup` and finally produces the per-shop "sold X
to townsfolk for Y" events the activity log was designed for.

This is one phase of the shop-economy re-architecture. **In scope:** consumer agents, allowance
income (behind a swappable seam), tiered needs, budget-constrained cheapest-first buying, retail
pricing, shop tills, per-shop sale + unmet-need logging, UI. **Explicitly deferred:** retail
restocking / wholesale→retail money flow (Phase 3), the wage/labor income loop (a later phase that
swaps the income seam), happiness → luxury demand (later), demographics-varied needs (later).

## Phase roadmap (context; only Phase 2 is in scope here)

1. **Phase 1 — Shop substrate.** DONE (on master).
2. **Phase 2 — Demand side & retail money (THIS SPEC).** Consumers buy from shops with budgets;
   tiered needs; scarcity-flexing retail pricing; tills credited; allowance income behind a seam.
3. **Phase 3 — Industrial demand & merchant trade over shops; retail restocking (wholesale→retail
   money flow); transient caravan stalls (farmers-market).**
4. **Phase 4 — Production profitability throttle** (closes the glut loop).
5. **Later — wage/labor income loop** (closes the money loop by replacing the allowance seam);
   happiness → luxury demand; demographics-varied needs.

## Phase 2 goals & non-goals

**Goals**
- A `RepresentativeConsumer` aggregate (Seat, `Size`, `Budget`) spawned per population, parallel to
  `RepresentativeMerchant`.
- Allowance income each period via a swappable `IConsumerIncome` strategy.
- Tiered per-good needs (`NeedTier` on `Good`); consumers buy in tier-priority order within budget.
- A `ConsumerDemandPhase` **replacing** the free `ConsumptionPhase`: consumers buy from any shop
  selling what they need, cheapest-retail-price first, within budget.
- Scarcity-flexing retail price: `cost × (1 + markup × scarcityMult)`.
- Shops earn into `Till` on sales (`Shop.CreditTill`).
- Logging: per-shop sale `Trade` events (Shop scope) + unmet-need shortfall events (Settlement scope);
  the old `Consumed` event is retired.
- Consumers surfaced in the TUI/CLI; the marketplace board's price becomes the retail price.

**Non-goals (later phases)**
- No retail restocking / wholesale→retail flow; shops are not refilled (Phase 3). Population stays
  fed because **producer shops keep producing**; pure retail shops may run empty.
- No wage/labor loop — income is an allowance faucet with no sink (tills accumulate). The seam allows
  the wage loop to replace it later.
- No happiness, no luxury-willingness, no demographic variation in needs (all consumers share the
  same per-capita basket).

---

## Section 1 — The consumer agent & spawn

### `RepresentativeConsumer` (new aggregate, `WorldEcon.Domain.Economy`)
Mirrors `RepresentativeMerchant`:
```
RepresentativeConsumer (AggregateRoot<ConsumerId>)
  WorldId   WorldId
  Seat      SettlementId
  Size      long       -- number of people this agent represents (e.g. 1000)
  Budget    Money      -- spendable cash
  Spend(Money)         -- guard: no debt, no negative (mirrors merchant)
  Earn(Money)          -- add income / refund (mirrors merchant)
```
New typed id `ConsumerId`. `Create(worldId, seat, size, budget)` validates `Size >= 1`, `Budget`
non-negative.

### Spawn
`ConsumerSpawnPhase` (weekly, mirrors `MerchantSpawnPhase`, stable settlement id order) ensures each
settlement has `floor(population / DefaultConsumerSize)` consumers seated there (spawn-only; surplus
never retired). `DefaultConsumerSize` is a tunable constant (default **1000**), so a 50k settlement
gets ~50 consumers each representing 1000 people; sum of `Size` ≈ population. New consumers spawn with
`Budget = Money.Zero` (the income phase funds them). `DemoSeeder` pre-seeds initial consumers (like it
pre-seeds merchants) so day 1 has demand.

---

## Section 2 — Income (allowance behind a seam)

`IConsumerIncome` strategy (interface) with one method conceptually: *grant this period's income to a
consumer*. Phase 2 ships **`AllowanceIncome`**: `income = Size × PerCapitaAllowance` (tunable;
calibrated so a consumer can afford its Essential tier with margin). A `ConsumerIncomePhase`
(configurable cadence; **default weekly** — a "paycheck") iterates consumers in id order and grants
income via the configured strategy (`consumer.Earn(income)`). Budgets therefore ebb (spent over the
week) and flow (refilled weekly) — a consumer can run short late in a period (→ unmet needs).

**The seam:** the engine resolves the active `IConsumerIncome` (default `AllowanceIncome`). A future
wage/labor phase provides a `WageIncome` implementation (income derived from production labor) without
changing the consumer, needs, or buying mechanics. Money enters the model here; the loop's closure is
deferred.

---

## Section 3 — Needs (tiered, per-good)

Add `NeedTier` to `Good`:
```
enum NeedTier { Essential = 0, Standard = 1, Comfort = 2 }   -- lower buys first
```
A good's per-capita demand rate stays `ConsumptionPerCapitaBp` (the existing field). A consumer's
**daily demand** for good G = `Size × G.ConsumptionPerCapitaBp` (basis-points math via `FixedMath`).
Only goods with `ConsumptionPerCapitaBp > 0` are needs. Goods are grouped by `NeedTier`; consumers
satisfy needs **in tier order** (all Essential goods, then Standard, then Comfort), so essentials
always precede comforts and a budget-limited consumer never reaches Comfort.

`NeedTier` is a property of the **good** (a universal per-capita basket); per-consumer/demographic
needs profiles are deferred. Existing consumables (Bread) become `Essential`. The demo seed gains a
couple of non-food goods across `Standard`/`Comfort` with consumption rates to exercise tiering.

---

## Section 4 — Buying behavior (`ConsumerDemandPhase`, replaces `ConsumptionPhase`)

`ConsumerDemandPhase` (daily; takes `ConsumptionPhase`'s Order 20 slot; the old `ConsumptionPhase` is
**removed**). For each settlement (stable id order):

1. Compute, per consumable good in the settlement, the **scarcity multiplier** for retail pricing (see
   Section 5) from the day's **total** consumer demand vs current aggregate shop supply.
2. For each **consumer** (stable id order):
   - For each `NeedTier` in order (Essential → Standard → Comfort):
     - For each good in that tier (stable id order) with `ConsumptionPerCapitaBp > 0`:
       - `demand = Size × ConsumptionPerCapitaBp`.
       - List the settlement's shops holding the good, sorted by **retail price ascending** (tie-break
         shop id). Buy from cheapest first: per shop, `affordable = Budget / retailPrice`,
         `take = min(remainingDemand, shopStock, affordable)`. If `take > 0`:
         `stock.Withdraw(take)`, `consumer.Spend(retailPrice × take)`, `shop.CreditTill(retailPrice ×
         take)`, accumulate the per-shop sale for logging.
       - Move to the next cheaper shop until demand met, budget exhausted, or no stock.
       - If demand for a good is not fully met (no stock anywhere, or budget ran out), record an
         **unmet need** for that good.

Total demand ≈ today's free-consumption demand (`Σ Size ≈ population`), now money-mediated. Producer
shops (which keep producing) sustain Essential supply; retail shops sell what they have.

> A consumer that exhausts its budget mid-tier stops buying entirely for the rest of the day (it has
> no money). This is intentional — it models a poor consumer that can't afford the full basket.

---

## Section 5 — Scarcity-flexing retail pricing

The retail price a consumer pays at shop S for good G:
```
demand        = Σ over the settlement's consumers of (Size × G.ConsumptionPerCapitaBp)   -- today's pressure
supply        = aggregate shop quantity of G in the settlement (>= 1)
scarcityBp    = FixedMath.DivRound(demand × FixedMath.BpScale, supply)
scarcityMult  = clamp( FixedMath.PowBpInt(scarcityBp, World.ElasticityExponent),
                       World.MinPriceMultBp, World.MaxPriceMultBp )   -- same knobs as wholesale pricing
effectiveMarkupBp = FixedMath.MulBp(S.MarkupBp, scarcityMult)         -- dormant per-shop Markup, now active & scarcity-flexed
retailPrice   = cost_basis(S,G) × (1 + effectiveMarkupBp)            -- = costUnits + MulBp(costUnits, effectiveMarkupBp)
```
A glutted good (supply ≫ demand) floors `scarcityMult` → markup shrinks toward 0 → retail ≈ cost. A
scarce good raises markup → retail rises. `scarcityMult` is computed **once per (settlement, good)**
per day in the `ConsumerDemandPhase` (it knows the day's full demand before buying). Cost basis is
per-shop, so retail prices differ across shops for the same good.

The existing **wholesale `MarketPrice`** (`base_value × scarcityMult`, set by `PricingPhase`) is
unchanged and remains the wholesale signal (merchant arbitrage + the board's reference / details).
Retail price is a derived value (cost basis + the scarcity-flexed markup), computed where it's used —
it is **not** persisted on the stockpile (only `MarketPrice`/`CostBasis` are persisted).

---

## Section 6 — Money flow & logging

- **Shop till:** add `Shop.CreditTill(Money amount)` (guards non-negative). Consumer payments credit
  the selling shop's till. (`DebitTill` for restocking arrives in Phase 3.)
- **Per-shop sale logging:** for each shop a consumer buys from, emit a `Trade` `LogEvent` at **Shop
  scope** (Routine; 90-day retention): e.g. *"Sold 12 Bread to townsfolk for 3g 2s at The Sundries."*
  Aggregate per (shop, good) per day where reasonable to avoid one event per consumer (one event per
  shop-good-day capturing total qty + total money is acceptable and keeps volume bounded). This is the
  shop-level sale record the activity log was designed for.
- **Unmet-need logging:** when a settlement's consumers collectively fail to meet demand for a good
  (out of stock or unaffordable), emit a shortfall event at **Settlement scope** (Notable): e.g.
  *"Consumers in Hammerfell couldn't afford/find Bread (needed 250, got 180)."* One per (settlement,
  good) per day.
- **Retire `Consumed`:** the `LogEventType.Consumed` event (free population consumption) is no longer
  emitted — sales now move goods and log at the shop. Keep the enum value for historic parse
  compatibility (mark retired), like `SettlementMarket`.
- **Money note:** allowance is a faucet with no sink in Phase 2; tills accumulate. Deliberate open
  loop, closed by the future wage phase.

---

## Section 7 — Determinism, UI, testing

### Determinism
Consumers, shops, goods, settlements all iterated in stable `Id.Value` order; the per-good retail-price
sort tie-breaks on shop id; income and spawn are deterministic functions of state. The
`advance(N) == k × advance(N/k)` granularity invariant must hold and is regression-tested (income
cadence and demand are deterministic per tick).

### UI
- **Consumers as a resource:** a `:consumers` root and a **Consumers** category under a settlement
  (mirroring merchants), columns `Settlement | Size | Budget`; drill a consumer for details
  (`Size`, `Budget`, `Id`). A CLI `consumers <dbPath>` command mirrors `merchants`.
- **Marketplace board:** the **`Price` column becomes the retail price** (`cost × (1+effectiveMarkup)`,
  what consumers pay); `Min Price` (cost basis) stays. The wholesale `MarketPrice` moves to the good /
  shop **details** view (labeled "Wholesale" or "Market signal"). An optional `Markup` column may be
  added.

### Testing
- Spawn: a settlement gets `floor(population / Size)` consumers; sum of `Size` ≈ population.
- Income: a consumer's `Budget` increases by `Size × PerCapitaAllowance` each income period; the seam
  resolves `AllowanceIncome` by default.
- Tiered buying: a consumer buys all Essential before any Comfort; a budget-limited consumer never
  reaches Comfort.
- Budget constraint: a consumer never spends more than `Budget` (no debt); `Spend` guard holds.
- Cheapest-first: given two shops at different retail prices, the cheaper sells out first.
- Till credited: a shop's `Till` increases by exactly `Σ retailPrice × qty` sold.
- Unmet need: out-of-stock or unaffordable demand emits the settlement shortfall event.
- Scarcity-flex: a scarce good (supply ≪ demand) has a higher retail price than a glutted one
  (supply ≫ demand) at the same base markup/cost.
- Per-shop sale: a `Trade` event at Shop scope is emitted with the right qty/money in its payload.
- Determinism/granularity: `advance(8d)` total state == `8 × advance(1d)` for consumer purchases,
  budgets, and shop stock/till.

---

## Implementation order (suggested for the plan)

1. Domain: `ConsumerId`, `RepresentativeConsumer` (Spend/Earn); `NeedTier` on `Good`;
   `Shop.CreditTill`.
2. Persistence: EF config + migration (consumers table; `Good.NeedTier` column default `Essential`).
3. Engine: `IConsumerIncome` + `AllowanceIncome`; wire the active strategy into the engine.
4. Engine: `ConsumerSpawnPhase` (weekly).
5. Engine: `ConsumerIncomePhase` (weekly).
6. Engine: retail-price helper (`scarcityMult` + `effectiveMarkup` + `retailPrice`) reused by the
   demand phase (and any UI that shows retail price).
7. Engine: `ConsumerDemandPhase` (replaces `ConsumptionPhase`): tiered, budget-constrained,
   cheapest-first buying; till credit; per-shop sale + unmet-need logging; retire `Consumed`.
8. Seeding: `DemoSeeder` pre-seeds consumers + tiered demo goods; `SeedImporter` supports consumers +
   `NeedTier`.
9. UI/CLI: consumers resource + command; marketplace board `Price` = retail price; wholesale moves to
   details.
10. Tests + determinism/granularity regression throughout.
