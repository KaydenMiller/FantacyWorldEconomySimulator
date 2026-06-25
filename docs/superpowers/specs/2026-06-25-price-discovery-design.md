# Price Discovery (retail slice) — Design

> **For agentic workers:** the implementation plan follows via superpowers:writing-plans.

**Goal:** Replace formula-based retail pricing with **emergent prices** that arise from real supply meeting real demand: each shop holds an evolving **price belief**, consumers bid a **per-unit demand curve**, and a **double auction** clears each market daily. Price becomes an *output* of "what's available + how much is needed," exactly the project's economic thesis. This is the keystone the rest of the economy (wholesale, geographic flow, labor) builds on.

**Architecture:** A new daily `PriceDiscoveryPhase` runs, per (settlement, consumed-good): shops post asks drawn deterministically from their belief bands; consumers post per-unit bids along their demand curves; a double auction matches them; trades execute (existing money flows); the day's clearing price is stored as the good's market price; and each shop's belief band updates from whether it sold. Non-consumed (industrial/raw) goods keep the existing formula price until the wholesale slice.

**Scope:** **Retail only** — consumers ↔ shops. Merchants stay on their current trade logic but read the new *emergent* clearing price. Merchants becoming direct bidders is the next (wholesale) slice.

**Tech:** C#/.NET 10, EF Core + SQLite. Determinism is a hard constraint; performance budget is a 1-year advance < 5 min (currently ~63 s).

---

## Cross-cutting decisions (from the brainstorm)
- **Determinism via hash, not RNG state.** The one "random" choice — which price within its band a shop offers — is a **deterministic hash** of `(worldSeed, shopId, goodId, tick)`. Same inputs → same offer, always. **Granularity-invariant by construction** (depends on the tick, not on how the advance is chunked); no persisted RNG.
- **Per-unit demand curve** for consumers (not a single reservation price).
- **Knobs are tunable now:** the belief-update fractions and the per-good willingness peak are persisted, DM-editable settings.
- **Naming:** names are spelled out — no abbreviations like `Bp` (basis points stays the integer fixed-point representation, `10000` = 1.0, but the word is spelled out in identifiers). See `naming-spell-out-no-abbreviations` memory.

---

## Components

### 1. Domain — new/changed types
- **`ShopPriceBelief`** (new aggregate, per shop + good): `Low` and `High` (`Money`) — the belief band the shop offers within. Created lazily the first time a shop prices a good. Persisted (new table + migration). Invariant: `1 ≤ Low ≤ High`.
- **`Good.PeakWillingnessMultipleBasisPoints`** (new field): how much a consumer will pay for their *most-needed (first)* unit, as a multiple of the good's base value (`40000` = 4×). Willingness declines linearly to `10000` (1× base) at the consumer's desired quantity. **Defaulted by need tier** at creation (Essential `40000`, Standard `18000`, Comfort `13000` — tunable), DM-editable. Migration adds the column with a tier-derived backfill.
- **`World`** gains three persisted, DM-editable belief-tuning settings (basis points): `BeliefNarrowFractionBasisPoints` (default `1000` = 10%), `BeliefWidenFractionBasisPoints` (default `1000`), `BeliefShiftFractionBasisPoints` (default `2000` = 20%). Migration backfills defaults.
- **`DeterministicHash`** (new public helper in `WorldEcon.Simulation.Random`): exposes a SplitMix64-based mix of several `ulong`/`Guid` inputs → a uniform `ulong`, and a helper to map it into an inclusive integer range. (`SplitMix64` is currently `internal`; the new helper is the public surface.)

### 2. Bootstrapping a belief band
When a shop first needs a band for a good: `Low = round(baseValue × 0.8)`, `High = round(baseValue × 1.2)` (clamped to `Low ≥ 1`). Anchoring on the stable base value keeps new markets sane and lets bands converge toward the true clearing price within a few days.

### 3. Deterministic in-band ask
A shop's ask for a good on a given tick:
```
span      = High - Low
offset    = DeterministicHash.RangeInclusive(worldSeed, shopId, goodId, tick, 0, span)
askPrice  = Low + offset            // uniform integer in [Low, High], deterministic
```
All-integer, no floats, no RNG. Different shops → different offsets → price dispersion; same shop next day (tick changed) → different offset → day-to-day variation.

### 4. Consumer demand curve (per-unit bids)
For a consumer (`Size`, `Budget`) and a consumed good (base value `P`, peak multiple `W` basis points):
- Desired quantity `Q = Size × ConsumptionPerCapitaBp / 10000` (`ConsumptionPerCapitaBp` is an existing field — left abbreviated per the naming convention's "don't churn existing names mid-feature" rule; new names below are spelled out).
- Willingness for the k-th unit (`k = 1..Q`), in basis points of base value, declines linearly from the peak to 1×:
  ```
  willingness(k) = W - (W - 10000) × (k - 1) / max(Q - 1, 1)
  unitReservation(k) = P × willingness(k) / 10000
  ```
- The consumer submits **one bid per desired unit** at `unitReservation(k)`, highest first, **capped by remaining budget** (it cannot bid for a unit it can't afford).
- **Cross-good budget priority:** the per-good auctions in a settlement run in **need-tier order (Essential → Standard → Comfort), then stable good id**, and each consumer's remaining budget carries across them — so essentials claim budget before luxuries (preserving today's tier-priority behaviour). Desired quantities per representative consumer are small (e.g. bread ≈ 5/day), so this is a handful of bids each.

### 5. `PriceDiscoveryPhase` — the daily auction (Order 20; replaces `ConsumerDemandPhase`)
Per settlement, for each consumed good in tier-then-id order:
1. **Asks:** every shop in the settlement with stock of the good posts one ask at its deterministic in-band price (§3), offering its full stock as quantity.
2. **Bids:** every seated consumer posts its per-unit demand-curve bids (§4), capped by remaining budget.
3. **Clear (double auction):** sort asks ascending by `(price, shopId)`, bids descending by `(price, consumerId)`. Repeatedly match the highest bid against the lowest ask while `bid ≥ ask`: trade one unit at `clearingPrice = (bid + ask) / 2` (integer), deplete that shop's stock, decrement the consumer's budget, credit the shop's till. Stop when the next bid < the next ask, or either side is exhausted; remaining bids are unmet.
4. **Record price:** set `Stockpile.MarketPrice` for that good's stocks in the settlement to the **last (marginal) clearing price** — the emergent market price merchants and the board read.
5. **Money flows:** `consumer.Spend` + `shop.CreditTill` (unchanged). Retail is a conserved transfer → the money-supply ledger is unaffected. Emit the existing per-shop·good `Trade` sale events and a `Stockout` for unmet demand.
6. **Belief update** (§6) for each participating shop.

Determinism: stable sort keys throughout; the only "randomness" is the hash draw. Granularity-invariant.

### 6. Belief update (after each good's auction)
For each shop that posted an ask, with `center = (Low + High) / 2`, world fractions `narrow`/`widen`/`shift` (basis points):
- **Sold ≥ half its offered quantity → narrow** (more confident): `Low += (center − Low) × narrow / 10000`; `High −= (High − center) × narrow / 10000`.
- **Otherwise (sold less / didn't sell) → widen + shift toward reality:** `High += (High − Low) × widen / 10000`; then if the good cleared at a price `C`, shift the band toward `C` (`Low += (C − Low) × shift/10000`, same for `High`); if nothing cleared, shift the band **down** toward base value by `shift` (the shop lowers expectations to find a buyer).
- Re-clamp `1 ≤ Low ≤ High`.

These are the Doran-Parberry-style gentle updates (small fractions → orbits equilibrium without thrashing). The fractions are world knobs (§1).

### 7. `PricingPhase` partition + merchant bridge
- **`PricingPhase` (Order 40) now prices only NON-consumed goods** (`ConsumptionPerCapitaBp == 0` — raw/industrial inputs like ore, coal, grain, flour, ingots) via the existing formula. Consumed goods are priced by the auction; no overwrite conflict, order-independent.
- **`TradePhase` (Order 50) is unchanged** — it reads `Stockpile.MarketPrice`, which is now the emergent clearing price for consumed goods (and the formula price for industrial goods). Merchants immediately trade on emergent prices. They become direct auction bidders in the wholesale slice.
- `RetailPricing` (cost × markup × scarcity) is **superseded** for consumed goods (its caller, `ConsumerDemandPhase`, is removed). Left in place only if still referenced; otherwise deleted.

### 8. Surfacing
- The TUI marketplace board's **Price** column shows the good's current market price (now the emergent clearing price for consumed goods); **Min Price** stays the per-shop cost basis. No new view; just the price source changes.
- A shop's belief band is available in the shop **details** view (Low/High per good) so the DM can see what a shop "thinks" a good is worth — queryability.

---

## Data flow (one settlement-day, one consumed good)
```
Production deposits stock → shops hold goods
PriceDiscoveryPhase:
  shops ask (hash draw within belief band)   ─┐
  consumers bid (per-unit demand curve)       ├─► double auction clears
                                              ─┘     → trades (spend/credit), stock depletes
                                                     → MarketPrice = marginal clearing price
                                                     → Trade + Stockout log events
                                                     → each shop's belief band narrows/widens
TradePhase: merchants move goods down the emergent price gradient
```

## Persistence / migrations
- New table `shop_price_beliefs` (id, world id, shop id, good id, low, high) + EF config + id converter (spelled-out converter name) + DbSet.
- `goods` gains `PeakWillingnessMultipleBasisPoints` (tier-derived backfill).
- `worlds` gains the three belief-fraction columns (default backfill).
- Mirror the established config patterns (Money converter, explicit world id where there's no navigation, `Ignore(DomainEvents)`).

## Determinism & performance
- **Determinism:** hash draws + stable sort keys; no RNG state; belief bands evolve deterministically from trade outcomes. Granularity invariant: a year advanced in one step equals the same year in monthly steps (the existing invariant tests are extended to cover prices/beliefs).
- **Performance:** one auction per (settlement, consumed-good) per day over small participant sets (a few shops, population/1000 consumers each with a few unit-bids). Expected well within budget; **measure before/after** (guideline: 1-yr < 5 min, currently ~63 s). If it bites, the fallback is clearing against the *aggregate* settlement demand curve instead of per-unit bids (same result, fewer objects).

## Testing
- **Domain:** demand-curve willingness math (peak at unit 1, 1× at unit Q, monotonic decline); belief-update math (narrow on success, widen+shift on failure, clamps); `DeterministicHash` (stable, uniform-ish, range-correct).
- **Engine integration:** advance the seeded world and assert —
  - prices **disperse** across shops (not one identical number) and **move** over time;
  - a **supply shock** raises the clearing price and the belief bands follow;
  - **elasticity:** when a good is made scarce/expensive, an **elastic** good's quantity sold collapses while an **inelastic** good's persists (its high-willingness units keep clearing);
  - **money is still conserved** (retail = transfer; ledger discrepancy stays 0);
  - **granularity invariance** holds for prices/beliefs.
- **Live validation (hard rule):** drive CLI + TUI on an advanced world; confirm dispersed, reactive prices and visible belief bands.

## Out of scope (deferred)
- **Merchant direct bidding** (the wholesale slice) — merchants stay on the emergent clearing price for now.
- Speculation / expectations / cobweb cycles; geographic money-flow; party treasury; per-good willingness *floor* as a second knob (v1 fixes the floor at 1× base).
- Bringing industrial/raw goods fully into the auction (they keep the formula price until wholesale).

## Risks
- **Convergence/oscillation:** if belief fractions are too large, prices oscillate. Mitigation: gentle defaults (10–20%) from the literature; they're tunable; tests assert prices settle into a band rather than thrash.
- **Cold-start dead markets:** a good no shop stocks and no consumer can afford never trades. Mitigation: bootstrap bands on base value; seed stock exists; stockouts surface it to the DM.
- **Performance at many consumers:** per-unit bids scale with consumer count × small Q. Mitigation: representative-consumer aggregation keeps counts modest; aggregate-curve fallback documented.
