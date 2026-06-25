# Overnight session — decisions & plan (2026-06-25 → morning)

Autonomous work session. The author is asleep; I'm making decisions per the guidelines below, recording them here, and executing a prioritized plan easiest-wins-first. **This file is the morning review log** — decisions up top, plan in the middle, a running progress log at the bottom.

## Guiding principles (from the author)
1. **Measure first** — if a decision needs data, measure before choosing.
2. **Realism** — maximize realism (weight, size/volume, cost, **wear-and-tear on tools**, etc.). If a thing adds realism, include it *unless* it significantly slows a calculation. "Significant" ≈ more than ~2× the cost of that calculation (e.g. a 0.1 s/day calc going to 0.2 s/day → defer).
3. **Performance** — after realism, as fast as possible. **Hard ceiling: a 1-year run must finish in < 5 minutes.** Anything over → question whether it's worth it.
4. **Queryable** — if the DM can't see/interact with it, it doesn't exist. Expose everything exposable: logs, filters, pricing, destinations, arrival times, birth/death counts, items produced (lifetime & recent), items consumed (lifetime & recent). **No silent deletions** — a caravan eaten by wolves that vanishes silently is useless; it must surface as a queryable event.

## Decisions made (applying the guidelines)

### Money-supply ledger (the in-flight feature)
- **Scope:** instrument-only (author-confirmed before bed). Track every money flow categorized as Faucet/Sink/Transfer; do not change economic behavior; expose the (currently hidden) trade leaks rather than fix them.
- **Snapshot cadence:** **monthly** (configurable), plus one at the end of every advance. → *Queryable* (regular time series the DM can watch); cheap (per guideline 3).
- **Surfacing:** **both** CLI `money` *and* a TUI `:money` view. → guideline 4 (expose everything); cost is trivial.
- **Total money supply:** derived (Σ Consumer.Budget + Σ Shop.Till + Σ Merchant.Capital) — authoritative, no drift.
- **Conservation invariant:** Δsupply == faucets − sinks (transfers net to zero); discrepancy stored on each snapshot (0 = correct; nonzero = an untracked flow). Hard assertion in the test suite; recorded (not thrown) at runtime.
- **Recording seam:** a `MoneyLedger` accumulator on `SimulationContext`, mirroring the existing `ctx.Log` emitter. Phases record at the four flow sites (allowance, retail sale, merchant purchase, merchant sale).

### Cross-cutting (recorded for later, per guideline 2 realism)
- **Tool wear-and-tear** (new realism item the author raised): tools/equipment degrade with use and must be replaced (a mine wears out pickaxes). This creates real demand for tools (closes a loop) and a consumption sink. Added to the roadmap; *not* built tonight (it's a new mechanic, not an easy win). Cheap when built (per-facility usage counter → periodic replacement order).
- **No silent deletions (guideline 4):** any entity the sim removes (delivered caravans, dead population, spoiled goods, a future ambushed caravan) must leave a queryable log event first. I'll audit existing removals as part of the queryability work.

### Branch
- Continue on **`feat/tui-forms`** (the author's established working branch; already holds forms + rich seed + perf + TUI fixes, all unmerged). Avoids multi-branch juggling overnight. Each feature is its own commit(s) with clear messages for morning review.

## Plan of action (execute top-down; easiest wins that serve the goals first)

1. **[measure]** Baseline a 1-year advance (guideline 1+3) → decide how urgent the perf work is. *(running)*
2. **Money-supply ledger** — the agreed foundation; it's also a queryability win and unblocks taxes/upkeep/labor. Spec → plan → build (domain entity + EF + migration + `ctx.Money` emitter + call sites + CLI `money` + TUI `:money` + tests) → live-validate (CLI + tmux) → commit.
3. **Performance: Production/Pricing/Trade N+1 cleanup** — mechanical (I have the demand-phase template), creates headroom under the 5-min/1-year ceiling as mechanics accumulate. Re-measure before/after (guideline 1). Build → test → re-measure → commit.
4. **Queryability counters** — items **produced** and **consumed**, lifetime + recent, per good/settlement, surfaced in CLI + TUI; plus an audit that nothing is silently deleted. (Guideline 4 explicit asks.)
5. *(stretch, likely beyond tonight)* begin weight/dim-weight groundwork (goods gain mass+volume fields) since it's a prerequisite for transport realism and is low-risk additive data.

Each item: build → automated tests green → **live-validate over the terminal** (the author's hard rule: a capability isn't real until driven live) → commit → update `docs/capabilities-and-roadmap.md` status → append to the progress log below.

## Progress log
- _(started)_ kicked off the 1-year baseline measurement; writing decisions + plan.
- **Measurement (guideline 1):** 1-year advance (518,400 ticks) on the demo world = **~63 s** — well under the 5-min ceiling (~5× headroom). ⇒ performance acceptable now; ledger (realism/queryability) goes first; N+1 perf cleanup is headroom, not urgent.
- **Money-supply ledger spec** written → `docs/superpowers/specs/2026-06-25-money-supply-ledger-design.md`. Starting implementation.
