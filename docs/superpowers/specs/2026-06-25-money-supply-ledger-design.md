# Money-Supply Ledger — Design

**Status:** Built + live-validated. **Post-review revisions (2026-06-25, author walked the 8 decisions):** (1) the ledger tracks **faucets & sinks only** — the `RetailSale` *transfer* was dropped (it's a *supply* ledger; conserved place-to-place movement belongs to the future **geographic money-flow** feature). (2) TUI command renamed to **`:ledger`** (CLI is `money`/`ledger`). (3) Party gold is *not* money-neutral after all → a future **Party Treasury & DM money-injection** subsystem (wallets + DM injection that feeds the ledger + warn-before-large-injection/backup). Cadence (monthly+end), conservation (record + fail tests), derived total supply, the `ctx.Money` emitter, and the snapshot+lines model were all confirmed. Original scope: instrument-only.

**Goal:** Make every flow of currency in the world **explicit, categorized, conserved-checkable, and queryable** — without changing economic behavior. This is the foundation for taxes/tariffs/upkeep sinks and the labor/wage loop (wages without a sink ledger = runaway inflation), and it directly serves the "if the DM can't see it, it doesn't exist" principle.

**Architecture:** A `MoneyLedger` accumulator on `SimulationContext` (sibling to the existing `ctx.Log` emitter) records each money flow under a named **channel** classified as **Faucet / Sink / Transfer**. The engine writes a **monthly `MoneyLedgerSnapshot`** (plus one at end of each advance) capturing the derived **total money supply**, the period's per-channel totals, the **net delta**, and a **discrepancy** (which must be 0). CLI `money` and TUI `:money` surface it.

**Tech:** C#/.NET 10, EF Core + SQLite, mirrors the `LogEvent`/`LogEventScope` parent+lines pattern.

---

## Money model (discovered)
Currency stocks: `RepresentativeConsumer.Budget`, `Shop.Till`, `RepresentativeMerchant.Capital`. **Total money supply = their sum** (derived, authoritative — never a separate running total that can drift).

Flows today (the four call sites):
| Channel | Kind | Site |
|---|---|---|
| `ConsumerAllowance` | Faucet | `ConsumerIncomePhase` (`consumer.Earn`) |
| `RetailSale` | Transfer | `ConsumerDemandPhase` (`consumer.Spend`→`shop.CreditTill`) |
| `MerchantPurchase` | Sink | `TradePhase` buy-at-source (`merchant.Spend`, source shop not credited) |
| `MerchantSale` | Faucet | `TradePhase` sell-on-delivery (`merchant.Earn`, nobody debited) |

The two trade channels are the *currently-hidden leaks*; the ledger makes them visible (it does **not** fix them — that's deferred wholesale-money work).

## Components

### Domain
- `enum MoneyFlowKind { Faucet, Sink, Transfer }`
- `enum MoneyChannel { ConsumerAllowance, RetailSale, MerchantPurchase, MerchantSale }` (more added as taxes/upkeep/etc. arrive).
- `MoneyChannels.KindOf(MoneyChannel)` — static classifier (Allowance→Faucet, RetailSale→Transfer, MerchantPurchase→Sink, MerchantSale→Faucet).
- `MoneyLedgerSnapshot : AggregateRoot<MoneyLedgerSnapshotId>` — `WorldId`, `Sequence` (monotonic per world), `Tick`, `TotalSupply` (Money), `NetDelta` (Money; faucets−sinks for the period), `Discrepancy` (Money; `(supply − prevSupply) − netDelta`, expected 0), and child `Lines`.
- `MoneyLedgerLine` — `WorldId`, `SnapshotId`, `Channel`, `Kind`, `Amount` (Money; the period total for that channel). One row per channel that had nonzero flow in the period.

### Persistence
- EF configs for both, mirroring `LogEvent`/`LogEventScope` (explicit `WorldId` property; enums stored as strings; `Money`↔long converter; cascade FK snapshot→lines). New migration.

### Engine — `MoneyLedger` (on `SimulationContext` as `ctx.Money`)
- `Record(MoneyChannel channel, long amount)` — accumulates `amount` into an in-memory per-channel dictionary for the current period. Called at the four sites.
- `SnapshotAsync(SimulationContext ctx, Tick tick)`:
  1. `totalSupply` = Σ stocks (load `Budget`/`Till`/`Capital` per-world and sum in memory — value-converted types don't SQL-aggregate; monthly cadence + small N → cheap).
  2. `netDelta` = Σ(faucet amounts) − Σ(sink amounts) from the accumulator.
  3. `discrepancy` = `_lastSupply` is set ? `(totalSupply − _lastSupply) − netDelta` : 0.
  4. Build snapshot + nonzero lines; assign `Sequence` (monotonic, loaded like the log emitter does).
  5. `_db.MoneyLedgerSnapshots.Add(...)`; set `_lastSupply = totalSupply`; clear the accumulator; remember `_lastSnapshotTick`.
  6. Skip if `tick == _lastSnapshotTick` (no double-snapshot when end-of-advance lands on a month boundary).
- In-memory state (`_period` dict, `_lastSupply`, `_lastSnapshotTick`, `_nextSequence`) survives the engine's per-batch `ChangeTracker.Clear()` (it's not tracked), exactly like `LogEventEmitter`'s sequence field.

### TickEngine integration
- Snapshot at each **month boundary** (`tick % ticksPerMonth == 0`, from the world calendar) and once at **end of advance**. Recording happens continuously in the phases; snapshots roll up + reset.

### Surfacing (queryability — guideline 4)
- **CLI `money <dbPath>`:** latest snapshot (total supply, per-channel Faucet/Sink/Transfer breakdown, net delta, discrepancy) + a short history table (supply & net delta over recent snapshots).
- **TUI `:money`:** a root view showing the latest breakdown and a history list; drilling a snapshot shows its lines. Added to the navigator roots.

## Conservation invariant
For every snapshot after the first: `Δ(total supply) == faucets − sinks` (transfers net to zero across the two stocks they move between). The stored `Discrepancy` is 0 in a correct model; **nonzero ⇒ an untracked money flow (a bug)**. Enforced as a **hard assertion in the test suite**; recorded (not thrown) at runtime so a leak surfaces rather than crashing a session.

## Determinism & performance
- No RNG; snapshots are a deterministic function of state + recorded flows. Per-channel totals are **granularity-independent** (advance(N) totals == Σ over k×advance(N/k)).
- Cost: 4 integer adds/day + one stock-sum per month (~12/year over dozens of entities). Negligible vs. the ~63 s/year baseline; well within the 2× rule.

## Testing
- **Domain:** `MoneyChannels.KindOf` classification.
- **Engine integration:** advance the seeded world; assert (a) a snapshot exists, (b) `TotalSupply == manual Σ stocks`, (c) `Discrepancy == 0` (conservation), (d) `ConsumerAllowance` faucet > 0 and the trade channels appear with the right kinds.
- **Granularity:** total faucets/sinks over a fixed span are identical whether advanced in one step or several.
- **Live validation (author's hard rule):** drive `money` (CLI) and `:money` (TUI, tmux) on an advanced world; confirm the breakdown reads correctly.

## Out of scope (deferred)
Fixing trade to be conserved; tax/tariff/upkeep/link-fee sinks (channels added when those features land); party-gold injection; rich charting; MV=PQ price-level validation (needs price discovery).
