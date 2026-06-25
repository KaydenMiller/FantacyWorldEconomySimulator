# WorldEcon — Capabilities & Roadmap

**Living document.** The single source of truth for (a) what the simulation can actually do today, (b) how confident we are in each capability, and (c) where we're going — short and long term. It exists so we plan deliberately instead of stacking features on unverified ground.

_Last updated: 2026-06-24 (after a full validation sweep)._

---

## Validation policy

A capability is only **real once it has been validated live over the terminal** — actually driven end-to-end in the running CLI or TUI and observed to behave correctly. Compiling, passing unit tests, or reasoning that it "should work" does **not** make it a capability.

Each item carries a status:

- ✅ **Validated** — exercised live over the terminal and observed correct.
- 🔨 **Built** — implemented and automated tests pass, but **not yet** driven live. Treat as provisional.
- 🧭 **Planned** — designed or discussed; not built.
- 💡 **Idea** — raised; not yet designed.

> **Validation sweep — 2026-06-24.** A full live sweep was run over the terminal (CLI directly, TUI via tmux). **Every capability tested passed — no failures.** All CLI commands, all 13 TUI create-forms (verified by querying the DB after each), all 4 edit-forms, snapshots, scoped log, marketplace board, sort, filter, merchant display names, and caravan date formatting are now ✅. Remaining 🔨 items are ones not yet exercised or genuinely not built. Where an item is marked ✅ below, it was driven live in this sweep or an earlier session with tmux evidence in `docs/superpowers/decisions-log.md`.

---

## Current capabilities

### Geography & world model
- 🔨 Nested/overlapping geography: continents → countries → regions → settlements, with country-optional regions, `RegionKind` (ocean/mountain/…), settlement `State` (active/ruined/abandoned), region containment (m2m), and `TerritorialClaim` (controls/disputes) as a faction precursor.
- 🔨 Routes as directed edges with distance/terrain/danger/category; pathfinding over the settlement graph.
- ✅ Browsing all of the above in the TUI drill-navigator (continents→…→cities and the city category chooser) and the CLI `list`.

### Economy: goods, shops, market
- 🔨 Goods with category, size class, perishability (shelf life), divisibility, per-capita consumption, and need tier (Essential/Standard/Comfort).
- 🔨 Shop substrate: all inventory is shop-owned (Retail / Producer / PublicMarket); each shop holds goods at its own weighted-average cost basis; "the market" is an aggregate read-model over a settlement's shops.
- ✅ Inspecting market/shop stock and per-shop prices live via CLI (`stock`, `price`) and the TUI marketplace board.

### Production & extraction
- 🔨 Recipes (multi-input **and multi-output**), production nodes ("factories") with a facility type and throughput cap, and a work-order system (one batch ≈ one day per node).
- 🔨 Resource endowments = the raw-extraction layer (a settlement+good+abundance). **Known weak spot:** these are a separate, weaker concept than factories — not first-class, not multi-good, invisible in the TUI, and mislabeled "<Settlement> Mine" for everything (including farms and vineyards). See "Known gaps" and the Roadmap.
- ✅ Production observed running live (CLI `advance` then `log`/`stock` show batches completing and stock changing).

### Demand & consumers
- 🔨 Representative consumers (size = people represented, budget, seated at a settlement), spawned to population weekly.
- 🔨 Allowance income (a money **faucet** — the placeholder until the labor/wage loop exists).
- 🔨 Tiered demand: consumers buy Essential→Standard→Comfort, cheapest-retail-first within budget; purchases credit shop tills and emit per-shop sale events; unmet demand emits stockouts; scarcity-flexing retail price.
- ✅ Observed live: `advance` then `consumers` (budgets accumulating), `log city` (sales + stockouts), confirming demand runs.

### Trade & logistics
- 🔨 Representative merchants (seat, capital, cargo capacity, reach), caravans (in-transit → delivered), deterministic trade down price gradients.
- ✅ Observed live: `caravans` shows goods flowing between settlements after `advance`.

### Time & determinism
- 🔨 Configurable calendar; advance by unit (`m/h/d/w/M/y`) or raw ticks; deterministic tick engine with fixed phase order + seeded RNG + stable iteration.
- 🔨 Granularity invariant: `advance(N) == k × advance(N/k)` (unit-tested).
- ✅ `advance` driven live across many durations this session; in-world date advances correctly.

### Money & currency
- 🔨 World-configurable currency denominations (default copper/silver/gold/platinum 10:10:10); display-only (Money math is base units).
- ✅ Currency formatting observed live (CLI shows `3g 2s 1c` style; TUI money columns).
- ✅ **Money-supply ledger** — every supply-changing flow categorized **Faucet/Sink** (transfers excluded — it's a *supply* ledger); monthly + end-of-advance snapshots with derived total supply, per-channel breakdown, net delta, and a conservation discrepancy (0 = no untracked flow); surfaced via CLI `money`/`ledger` and TUI `:ledger`. Live-validated; conservation holds. Instrument-only (the trade money leaks are now *visible* channels, not fixed). Spec: `docs/superpowers/specs/2026-06-25-money-supply-ledger-design.md`. **It immediately revealed the demo world inflates ~16k p/month** (allowance + merchant-sale faucets ≫ the merchant-purchase sink; consumers hoard).

### Activity / event log
- 🔨 Append-only event stream with write-time visibility materialization, magnitude-driven upward propagation, granularity-independent retention, regex query + on-demand summaries.
- ✅ `log <scope> <name>` and `summary` driven live; scoped TUI log view (`l`) validated in an earlier session.

### Snapshots & branching
- ✅ `VACUUM INTO` snapshot via CLI (`snapshot`) and TUI (`S` → destination prompt → file written) — both driven live this sweep.
- 🔨 Branch and in-C# structural compare — exist; not exercised this sweep.

### DM / party actions
- ✅ Buy-out, adjust market stock, disable/enable production — all driven live via CLI this sweep and logged (the `actions` list shows them); buy-out also validated in the TUI in an earlier session.

### Interface: CLI (`WorldEcon.Cli`)
- ✅ Entire command surface driven live this sweep with correct output: `new`, `import`, `advance`, `list`, `stock`, `price`, `consumers`, `merchants`, `caravans`, `log`, `summary`, `buy`, `adjust`, `disable`, `enable`, `actions`, `snapshot`.

### Interface: TUI (`WorldEcon.Tui`, k9s-style navigator)
- ✅ Drill navigation (Enter/Esc/hjkl), `:` command bar with autocomplete, `/` regex filter, `o` column sort, `d` master-detail view, `q`/`:q` quit — validated live (this + earlier sessions).
- ✅ All **13** `n` (new) create-forms driven live this sweep and confirmed persisted to the DB: Continent, Country, Region, Settlement, Route, Good, Shop, Stock, Merchant, Consumer, Resource Endowment, Recipe (incl. the input/output line loop), Production Node (facility derived from the chosen recipe).
- ✅ All **4** `E` (edit) forms driven live: settlement state (Active→Ruined, reflected in the list), region kind/country, merchant capital (+/−), shop till.
- ✅ Advance UX: `⏳ working…` busy indicator + input-lock during a background advance + result dialog + header refresh.
- ✅ Scoped log view (`l`), marketplace board (Good│Category│Shop│Qty│Min Price│Price), merchant display names ("Caravaneer"), and caravan Arrive dates (formatted, not raw ticks) — all driven live this sweep.

### Performance
- ✅ Long advances are fast: a 2-month advance dropped from 5+ min to ~9 s (skip-ahead loop + change-tracking discipline + demand-phase N+1 fix). Measured live via CLI timing this session.
- 🔨 Remaining ~0.1 s/day from N+1 in Production/Pricing/Trade (the path to "a few seconds") — not yet done.

### Seed / content
- ✅ Rich 6-settlement demo world (`new`) and the matching JSON sample (`import`) seed and advance correctly — driven live this session.

---

## Known gaps & rough edges (raised, not yet addressed)

1. **Extraction layer is second-class.** Farms/mines/plantations are `ResourceEndowment`s, not facilities: invisible in the TUI, single-good, no depletion, and every extraction shop is mislabeled "<Settlement> Mine." (This is what surfaced the "I see flour required but no farm for wheat" question — the grain farms exist but are hidden and mislabeled.)
2. **No labor.** Consumer income is a free allowance faucet; nothing employs anyone or pays wages, so the money loop isn't closed.
3. **Economy isn't balanced.** Essentials are met; comfort/speciality goods run caravan-limited shortfalls. Intentional for now, but a tuning pass is owed.
4. **Advance still ~0.1 s/day** — fine for months, slow for many years; N+1 in Production/Pricing/Trade remains.
5. ~~Most TUI flows are 🔨, not ✅.~~ **Resolved by the 2026-06-24 validation sweep** — forms (create + edit), snapshots, log, marketplace board, and the recent display fixes were all driven live and pass.
6. **Terminology:** raw food crop is "Grain," not "Wheat."

---

## Roadmap

### Short term (next up)
- **Validation sweep** — drive every 🔨 capability live over the terminal and promote the ones that pass to ✅ (and file bugs for the ones that don't). This is the prerequisite for trusting the rest of the list.
- **First-class extraction facilities** (designed in discussion below; spec pending): farms/mines/plantations become proper facilities like factories — multi-good, visible/browsable, correctly labeled — with an **optional depletable reserve** (DM "infinite/stable" toggle; reserves can grow / "discover more ore" / multiple veins) and a **yield knob** for land quality / region density / season. **Workers-required** recorded as a seam (visible/editable) but not yet economically active.
- **Production/Pricing/Trade N+1 cleanup** → sub-second advances (load each settlement's stockpiles once per day, like the demand phase already does).

### Medium term
- **Party Treasury & DM money injection** (decided 2026-06-25, from the ledger review): model the party as in-world money-holders — **individual member wallets + a collective party purse**. Wallets change by **buying/selling** with shops & merchants (wallet ⇄ till, tracked) and by the **DM adjusting them directly** (a faucet/sink that feeds the supply ledger — so injecting gold and watching a town's economy react is *visible*). **Safety:** before a **large injection**, **warn the DM** (not automatic) to back up — reuses the existing snapshot/branch/fork. New `MoneyChannel`s (party spend/earn, DM injection/removal) plug into the ledger.
- **Geographic money-flow / balance-of-trade** (decided 2026-06-25): track **net money in/out per place** (settlement → region → country → continent) and **net flow between settlements along trade routes** — the basis for a future **trade-route-arrow visualization** ("net coin Hammerfell → Sunport this month"). This is where *transfers* (deliberately excluded from the supply ledger) are tracked, at the geographic level; it also captures party gold flowing into a town.
- **Labor & wages subsystem** — population as a labor supply, employment at facilities, wages → consumer income (replacing the allowance faucet and **closing the money loop**). This is where the "workers" seam comes alive.
- **Retail restocking / wholesale→retail money flow** (shop-economy Phase 3): retail shops buy from producer shops to refill; trade & industrial demand fully over shops; transient caravan market stalls.
- **Production profitability throttle** (Phase 4): producers slow when their shop accumulates unsold stock at a loss — closes the glut loop.

### Long term
- **Seasonality & weather** — deterministic seasonal yield from the calendar; random weather needs RNG-state persistence (currently deferred).
- **Factions (§10) & Events (§11)** — undesigned in the source spec; must be designed before building.
- **Per-batch perishability** (freshness/age tracking instead of a flat daily decay).
- **Seeded entity IDs** — full "same seed ⇒ same world" reproducibility (today determinism is snapshot/branch-based).
- **Happiness → luxury demand; quality tiers; contraband/borders/enforcement; junction nodes.**

---

## Refined economic direction (2026-06-25)

The product target and the design decisions made while reviewing the research below. **This supersedes the looser roadmap above where they conflict.**

### Target
A **full, quantity-driven economy**: real goods are produced, stored, hauled, and consumed in **real amounts**, and **price is an _output_** of "what's actually available + how much is needed + other market factors" — not the thing being simulated. This is a **personal tool for the author** (not a product for others), so it optimizes for **realism over convenience**; creature comforts may be sacrificed, but the two hard constraints below may not. North-star references: **X4: Foundations** and **EVE Online**.

### Cross-cutting constraints (govern every decision)
- **Determinism** — seeded RNG + deterministic tie-breaks; runs are reproducible. (The price-belief mechanic introduces an RNG surface that must be seeded/threaded explicitly.)
- **Performance** — non-negotiable: fast world-gen and fast simulation. Every mechanic below is cheap aggregate/constant math.
- **Closed loops** — every good needs both a producer and a consumer; every unit of money needs both a faucet and a sink. Open loops are the #1 source of unstable, ugly results.

### Locked design decisions
1. **Price discovery — per-shop price-belief intervals, cleared by a universal double auction.** Each shop keeps a `[low, high]` confidence band per good; it offers within the band, narrows it on successful trades, widens/shifts it toward the market on failures. Prices **emerge from real quantity flows** (offers clear or fail against real inventories), giving dispersed, lagged, believable prices and natural haggling ranges. Quantity sim underneath; emergent prices on top. (Per-shop chosen over per-market for realism.)
   - **The double auction is THE clearing mechanism for every market** — anything that prices to buy or sell goes through it. Participants differ only in **how they form a price:** shops & merchants draw from their **belief bands** (ask high / bid low, learning from rejections); **consumers bid a reservation price** derived from need-tier × budget × **elasticity**. A consumer's declining willingness-to-pay per successive unit **is** the demand curve, so elasticity (#6) plugs straight in as the consumer's bid — **retail and wholesale unify under one mechanism**, not two code paths.
   - **Clearing:** sort bids desc / asks asc; match top-to-top while `bid ≥ ask` at the **midpoint** price; trade `min(qty)`; leftovers **rejected** (the signal that drives belief updates). Deterministic tie-break: price, then shop id; belief-price draws use the seeded RNG.
   - **Performance:** runs once per (market, good) per pricing round over a *small* set of **aggregate** participants (a few shops/merchants + the representative consumer[s]) — an O(n log n) sort over a handful of orders. Cheap.
2. **Demand elasticity per good.** Demanded quantity bends with price: **inelastic** staples (grain, salt) keep selling and spike hard in shortage; **elastic** luxuries (wine, silk) see demand collapse. Composes with the existing tier-substitution. Self-correcting price feedback; makes shortages behave believably per good.
3. **Money-supply discipline.** Track total money + an explicit **faucet−sink ledger** and net delta, surfaced to the DM. This is foundational and a **prerequisite for the labor/wage loop** (wages without sinks = runaway inflation). Implicit faucets (consumer allowance today) must be paired with real sinks (taxes, tariffs, fees, upkeep, spoilage-as-value-destruction).
4. **Stability guardrails.** (a) **Shortage-aware** production/restock decisions (bias toward goods where demand>supply) to avoid the zero-profit **death-spiral**; (b) **per-facility upkeep / idleness cost** so chronically unprofitable shops/factories close on their own — natural churn, and doubles as a money sink.
5. **Population dynamics — migration + births/deaths.** A settlement's **provisioning/attractiveness** (need-satisfaction, food weighted heaviest) drives **migration** (under-provisioned places lose people, prosperous ones gain), modeled as an aggregate **migrant flow** (a caravan-like entity that travels routes over real time, subject to the same logistics/hazards) and surfaced as a queryable event. Plus **births/deaths** tied to food/provisioning. Both are **aggregate cohort math** (`pop × rate`, nudged by provisioning) on a **coarse cadence** (monthly/seasonal, not per-tick) → O(settlements), performance-trivial, and more realistic than daily churn. Self-balances populations (relative prosperity via migration; absolute carrying capacity via births/deaths); later feeds labor supply.
6. **Weight & dimensional weight.** Each good has **mass + volume**; per-unit **haul weight = max(mass, volume ÷ dimensionalFactor)** (factor is a world config knob, like currency/calendar). **Both transport and storage are weight/volume-bounded.** Promotes the existing `SizeClass` to explicit mass/volume (D&D liberties for fantasy goods). Consequence: bulky-cheap goods (grain, ore, timber) stay locally produced; dense-valuable goods (ingots, spices, gems) become the long-haul trade — emergent comparative advantage. Makes transport the real binding constraint (the X4/EVE lesson).
7. **Transport modes** — a 5-axis trade-off (capacity in haul-weight · speed · cost · hazard exposure · routing/range), in two families:
   - **Open-graph movers** over the route network: porter-with-a-sack → handcart → small wagon → large wagon → wagon caravan (capacity ↑, speed ↓, risk ↑).
   - **Geography-benders:** **airship** (fast, avoids ground hazards, modest cap, wealth-gated) and **teleport links** like the campaign's **"Light Bridge"** — a first-class **transport-link entity** that's a **single shared channel** between an **exclusively-paired** set of gates (a gate can't bind to a gate already paired elsewhere — a network rule that's moot at 2 gates), with a **charge time** between firings (Light Bridge = **1 hour → 24 firings/day**, `slots/day = ticksPerDay ÷ chargeTicks`), a **weight cap per firing**, **instant** transit on arrival, and a **fee**. The 24 slots are a **shared pool whose direction is allocated by demand** (all one way, 12/12 alternating, or any split). → a scarce, high-premium service and a controllable chokepoint/faction lever.
   - Stargate-style: dial one destination, exclusive lock while active. The link's **charge interval, per-firing capacity, and fee are editable config that can change over time** (the bridge is reinforced, the magic wanes, a war throttles it). This **"charge time → slots/day → weight-per-firing → demand-allocated direction"** is a **reusable capacity-limited-link pattern** (future airship lines with N flights/day, or more gates, reuse it; only the numbers + the avoids-hazards / instant-vs-in-transit flags change).
   - Merchants pick the most profitable **feasible** mode per shipment (landed cost vs. price gradient); **wealth gates access** (own an airship vs. hire a porter); public links are pay-per-use. Scarce link slots are **allocated to the highest-value contending shipment** (deterministic tie-break: value gradient, then id). _Resolved: a **fixed DM-set fee** + scarce demand-allocated slots already yields a high effective premium under load; **emergent fee-from-scarcity** is an opt-in later._
8. **Extraction as first-class facilities.** Farms/mines/plantations become facilities like factories (multi-good via recipes, visible/browsable, correctly labeled — not "Mine" for everything), with **optional DM-controlled depletable reserves** (infinite/stable toggle, growable "discover more ore," multiple veins) and a **yield knob** (land quality / region tree-density / season). **Workers recorded as a seam** (not yet economically active). _Resolved: yield is a **DM knob now**, automatic **seasonal cycle later**; **reserve defaults to infinite** so the happy path is validated before depletion is introduced._
9. **Labor & wages (next major subsystem, built AFTER the money ledger).** Population as a labor supply, employment at facilities, wages → consumer income — replacing the allowance faucet and **closing the money loop**. The "workers" seam comes alive here.

### Query & play surfaces (DM-facing; mostly session-time)
- **"What can players buy here, at what price, and when does it restock?"** — a fast session-time view; *restock* is answered by whether a **real caravan / known source will actually deliver** the good (forward-looking from logistics, not a fudge).
- **Queryable, impact-ranked record of already-happened events** (leverages the existing log **magnitude tiers** Routine/Notable/Major/Historic) so the DM filters impactful from mundane and **builds their own hooks** — the tool exposes facts, it does **not** auto-generate plot. Events occur regardless of party action ("bandits hit caravan X at mile Y on the Z road").
- **Lightweight travel/encounter prompter:** party route + speed → terse, **non-binding** prompts that read sim state (bandits on route, terrain, nearby things) but **never mutate the sim** ("a wolf is stalking you," "something fell off a cart on the path").

### Recommended foundational build ordering
1. Money-supply ledger + sinks/faucets (with upkeep/idleness as the first sinks).
2. Price discovery (per-shop belief intervals) + demand elasticity.
3. Weight/dim-weight + transport modes (makes friction real).
4. Stability guardrails (shortage-aware production) + population migration.
5. First-class extraction facilities (+ depletion, yield).
6. Labor & wages (closes the money loop).
7. Query & travel surfaces (ongoing; partly leverages the existing log).

Rationale: the **money + price foundations** make the later pieces (especially labor) safe and believable; **weight/transport** make the quantity sim feel real; **labor goes last** because it needs the sink ledger to not inflate.

### Open questions (resolve per-feature in specs)
- ~~Population: migration only, or + births/deaths tied to food?~~ **Resolved:** migration **+** births/deaths, both aggregate cohort math on a monthly cadence.
- ~~Extraction yield / reserve default?~~ **Resolved:** yield = DM knob now (seasonal later); reserve defaults to **infinite** for happy-path testing.
- ~~Teleport/airship link fee: fixed DM premium vs. emergent slot-scarcity price?~~ **Resolved:** fixed DM fee + scarce, demand-allocated slots (24/day for the Light Bridge); emergent fee-from-scarcity is an opt-in later.
- ~~Price-belief clearing mechanism?~~ **Resolved:** a **universal double auction** clears every market (retail and wholesale alike); participants differ only in how they price (shops/merchants via belief bands, consumers via elasticity-derived reservation prices). See decision #1.

_All design open-questions are now resolved; remaining detail is per-feature spec work._

### Research basis
Distilled from a four-stream survey (commercial sims, RPG-economy & ABM literature incl. Doran-Parberry, TTRPG systems/tools, open-source sims like BazaarBot) — see `docs/research/2026-06-25-economy-research.md`.

---

## Discussion notes — 2026-06-24 session

Captured so this thread isn't lost:

- **Forms.** Built TUI create-forms (`n` → choose entity → guided prompts) for 13 entity types and edit-forms (`E`) for the mutations the domain exposes. Good + Settlement create validated live; the rest are 🔨.
- **Rich seed.** Replaced the 2-settlement starter with a 6-settlement economy (full grain/metal/textile/drink supply chains); added `SeedConsumer` so imported worlds have day-1 demand.
- **Performance.** Found and fixed the catastrophic advance slowdown (was quadratic via EF change-detection; "2M" = 2 *months* under the old engine took 5+ min, now ~9 s). The remaining N+1 in Production/Pricing/Trade is scoped, not done.
- **TUI responsiveness.** Background ops are now serialized (busy guard) with a working indicator — fixes "interact during an advance → error."
- **Farms/mines direction (this is the seed of the next feature):**
  - Elevate extraction to **first-class facilities** like factories; recipes already support multi-output, so a farm/mine is conceptually a factory whose recipe extracts raw goods.
  - Facilities **require workers** — recorded as a seam now; the full labor/wage loop is the *next* subsystem (user chose "seam now, labor next").
  - **Depletion is optional and DM-controlled:** a mine/vein can run finite or be set infinite/stable; reserves can **grow** ("discover more ore"); "multiple veins" = a larger/adjustable reserve or multiple facilities.
  - **Yield varies:** a farm may produce less in a bad season; land quality and **region density** matter (a timber plantation is a farm kind whose wood output reflects how tree-dense the region is).
  - Guiding principle from the user: **"this is a simulation, so the more knobs for control the better."**
  - Open (deferred to the spec): whether yield variability is a pure DM knob vs. an automatic seasonal cycle; reserve default (infinite vs finite).

---

## How to use this document

- When we **finish** something, update its status here (and only mark ✅ after a live terminal run).
- When we **start** planning a feature, it should already appear under Roadmap; promote it to a spec in `docs/superpowers/specs/` and link it.
- Decisions and their rationale continue to live in `docs/superpowers/decisions-log.md`; this file is the higher-level "where are we / where are we going" view.
