# WorldEcon — Capabilities & Roadmap

**Living document.** The single source of truth for (a) what the simulation can actually do today, (b) how confident we are in each capability, and (c) where we're going — short and long term. It exists so we plan deliberately instead of stacking features on unverified ground.

_Last updated: 2026-06-24._

---

## Validation policy

A capability is only **real once it has been validated live over the terminal** — actually driven end-to-end in the running CLI or TUI and observed to behave correctly. Compiling, passing unit tests, or reasoning that it "should work" does **not** make it a capability.

Each item carries a status:

- ✅ **Validated** — exercised live over the terminal and observed correct.
- 🔨 **Built** — implemented and automated tests pass, but **not yet** driven live. Treat as provisional.
- 🧭 **Planned** — designed or discussed; not built.
- 💡 **Idea** — raised; not yet designed.

> **Reality check:** most of the codebase is currently 🔨. It has tests and it runs, but only a subset of flows have actually been driven live in a terminal during a working session. Promoting 🔨 → ✅ requires a deliberate **live-validation pass** (see Roadmap → "Validation sweep"). Where an item is marked ✅ below, it was driven live either this session or in an earlier session with tmux evidence recorded in `docs/superpowers/decisions-log.md`.

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

### Activity / event log
- 🔨 Append-only event stream with write-time visibility materialization, magnitude-driven upward propagation, granularity-independent retention, regex query + on-demand summaries.
- ✅ `log <scope> <name>` and `summary` driven live; scoped TUI log view (`l`) validated in an earlier session.

### Snapshots & branching
- 🔨 `VACUUM INTO` snapshot, branch, and in-C# structural compare; TUI `S` action.
- 🔨 **Not driven live this session** — provisional until a validation pass.

### DM / party actions
- 🔨 Buy-out a good, adjust market stock, disable/enable production; all logged as player actions.
- ✅ Buy-out validated end-to-end in an earlier tmux session; `adjust`/`actions` exercised via CLI.

### Interface: CLI (`WorldEcon.Cli`)
- ✅ `new`, `import`, `advance`, `list`, `stock`, `consumers`, `caravans`, `log`, `summary` — all driven live this session with correct output.
- 🔨 `price`, `merchants`, `actions`, `snapshot`, `disable`/`enable` — exist; not all re-exercised this session.

### Interface: TUI (`WorldEcon.Tui`, k9s-style navigator)
- ✅ Drill navigation (Enter/Esc/hjkl), `:` command bar with autocomplete, `/` regex filter, `o` column sort, `d` master-detail view, `q`/`:q` quit — validated live (this + earlier sessions).
- ✅ `n` (new) create-forms: **Good** and **Settlement** driven live end-to-end (fields → validation → save → navigate to the new row).
- 🔨 `n` create-forms for the other 11 entity types (Continent/Country/Region/Route/Shop/Stock/Merchant/Consumer/Endowment/Recipe/ProductionNode) — unit-tested only.
- 🔨 `E` (edit) forms (settlement state, region, merchant capital, shop till) — unit-tested only; **not driven live**.
- ✅ Advance UX: `⏳ working…` busy indicator + input-lock during a background advance + result dialog + header refresh — validated live this session.
- 🔨 Scoped log view, marketplace board, merchant display names ("Caravaneer"), caravan-date formatting — built; some validated in earlier sessions, the recent fixes not yet driven live.

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
5. **Most TUI flows are 🔨, not ✅** — edit-forms, 11 of 13 create-forms, snapshots, and recent display fixes have not been driven live.
6. **Terminology:** raw food crop is "Grain," not "Wheat."

---

## Roadmap

### Short term (next up)
- **Validation sweep** — drive every 🔨 capability live over the terminal and promote the ones that pass to ✅ (and file bugs for the ones that don't). This is the prerequisite for trusting the rest of the list.
- **First-class extraction facilities** (designed in discussion below; spec pending): farms/mines/plantations become proper facilities like factories — multi-good, visible/browsable, correctly labeled — with an **optional depletable reserve** (DM "infinite/stable" toggle; reserves can grow / "discover more ore" / multiple veins) and a **yield knob** for land quality / region density / season. **Workers-required** recorded as a seam (visible/editable) but not yet economically active.
- **Production/Pricing/Trade N+1 cleanup** → sub-second advances (load each settlement's stockpiles once per day, like the demand phase already does).

### Medium term
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
