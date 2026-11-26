# In-memory simulation state with periodic saves

Date: 2026-11-25. Status: design, awaiting the user's approval. Uncommitted. Builder check done; its open question answered by the user (keep the world loaded).

## Contents

1. [Intent](#1-intent)
2. [Goals, non-goals, anti-goals](#2-goals-non-goals-anti-goals)
3. [Where the code is today](#3-where-the-code-is-today)
4. [Approaches](#4-approaches)
5. [Design](#5-design)
6. [Saves](#6-saves)
7. [Reads while a run is open](#7-reads-while-a-run-is-open)
8. [DM changes](#8-dm-changes)
9. [Measuring the result](#9-measuring-the-result)
10. [Left to the builder](#10-left-to-the-builder)
11. [Later work this prepares for](#11-later-work-this-prepares-for)

Tags: **(user)** decided by the user; **(proposal, not contested)** proposed by Claude, not yet corrected.

## 1. Intent

The running simulation should reach about 1 simulated year per minute of real time with every system in the living-world overview running (user; overview section 2). The baseline (`docs/research/reports/Simulation speed baseline 2026-11-25.md`) shows about 97% of today's advance time is the database path and the garbage it creates, not simulation logic: on a 3-month Release trace, garbage collection pauses 42%, SQLite 29%, other runtime work (mostly query compilation) 18%, Entity Framework (EF) 7%, our own instructions 2.4%. One simulated year takes 21.4 s with 6 settlements, about 9.9 ms per settlement per simulated day, against a budget of about 164 ms per simulated day for the whole world.

This project keeps the world in memory for the length of a run, so phases stop querying SQLite, and writes it back only at saves. It is the first step toward the target and the base for running city groups in parallel later.

## 2. Goals, non-goals, anti-goals

Goals:

- Phases read and change in-memory objects through indexes; no SQL during a step (proposal, not contested).
- Saves follow the user's rules (section 6).
- Every existing behaviour is kept: the same phases, in the same order and cadence, producing the same kinds of rows (work orders, caravans, log events, ledger lines). Existing tests that check simulation results keep passing, except those whose subject is the save rule changed here (section 6).
- A clear before-and-after measurement against the baseline (section 9).

Non-goals:

- Bit-for-bit determinism. Not set up now (user); this design keeps what allows it later (section 5.6).
- Running phases or cities in parallel. Later work (section 11); this design only avoids blocking it.
- Postgres. Later, for several DMs (user); saves stay EF, so the provider can change.
- New simulation features.

Anti-goals (this would count as a failure even if it ran faster):

- The GUI showing a state that differs from what the run holds (section 7).
- A save that writes part of a world: each save is one transaction.
- A DM change lost after the DM made it (section 8).

## 3. Where the code is today

- `EngineRun.StepToAsync` jumps from boundary to boundary. After each boundary where a phase ran it calls `SaveAndClearAsync`: `DetectChanges`, `SaveChangesAsync`, `ChangeTracker.Clear()`, reattach `World`. The whole run is one transaction (`TickEngine.BeginRunAsync`, SQLite `BEGIN IMMEDIATE`), committed by `EndAsync`.
- Because the tracker is cleared, phases reload what they need from SQLite every boundary: about 1,163 SQL commands per simulated day in the 6-settlement world.
- `ShopMarket` and several phases merge query results with `DbSet.Local` ("DB ∪ Local") so rows added but not yet saved are visible.
- `SimulationRunner` (desktop app) wraps one `EngineRun`. GUI reads during a run (`CaravanPositionsQuery`, `SettlementDetailsQuery`) are SQL queries on the run's own connection, which sees the run's saved but uncommitted rows.
- DM changes (`LogEventService`: buy, adjust stock, disable production) run from the CLI outside any run and save at once.
- Entities reference each other by raw id; there are no EF navigation properties.
- Random numbers come from `RngStreams` (one seeded stream per subsystem); new rows get random ids (`Guid.NewGuid`).

## 4. Approaches

| | A. Keep the EF entities as the in-memory objects (chosen) | B. A separate in-memory model | C. Data-oriented arrays |
|---|---|---|---|
| What it is | The mapped entities stay loaded and tracked for the whole run; an index layer sits beside them; a save is one change scan and one write | Plain simulation classes, loaded from EF at the start and written back as differences at saves | Each kind of entity stored as columns of values, laid out for loops and threads |
| Removes | Per-boundary queries, per-boundary saves, rebuilding the same rows every day | The same, plus EF tracking during the run | The same, with the least memory per entity |
| Cost | One model; phases change from queries to index lookups; EF tracking overhead stays | A second model for about 30 tables, plus mapping | A large rewrite away from domain objects |

Chosen: **A (user)**. B or C later only where a trace after A shows EF tracking or a specific loop as the cost (proposal, not contested).

## 5. Design

### 5.1 World state (proposal, not contested)

A `WorldState` object holds every simulated entity of the world. It is loaded once when the world is opened and stays loaded while the world is open, across runs (user). Ending a run means "save and start a new transaction"; it does not discard `WorldState`. Reloading for every run was rejected (user): each step button press would load the whole world first, a cost that grows with world size.

- Owner (proposal, not contested): an open-world session object (one per open world file, holding the `WorldDbContext`, `WorldState` and the transaction). `SimulationRunner` and `ISimulationRun` become runs on that session instead of each opening their own connection; `TimeController`'s open, commit and fail paths start and end runs on the session.
- The CLI opens a session per command, so a CLI `advance` loads once and saves at the end and every 4 months.

What `WorldState` holds:

- Loaded: settlements, goods, recipes, production nodes, resource endowments, shops, stockpiles, shop price beliefs, consumers, merchants (with trips), caravans (with legs), work orders, routes, route nodes, vehicle classes, travel knowledge, the world row.
- Also loaded: the political region rows `AncestorResolver` reads to scope log events (5.4).
- Not loaded: globe and tile tables, map imagery, worldgen tables (phases do not read them); log events and money ledger rows from before the run (append-only during a run; see 5.4); history rows no phase reads again (completed work orders, caravans home and reported) (proposal, not contested).
- Every loaded entity is tracked by the run's `WorldDbContext` (as today, `AutoDetectChangesEnabled` off).
- Indexes the phases need, built at load and kept current on add: for example shops by settlement, stockpiles by (settlement, good) and by shop, production nodes by settlement, caravans undelivered and delivered-not-home, work orders incomplete by due tick, routes by node.
- Lists that phases visit are kept in the order they visit today (by id value), so behaviour does not change (5.6).

### 5.2 Phases (proposal, not contested)

- `SimulationContext` gains the `WorldState`; phases read and change entities through it and do not use `ctx.Db` queries during a step.
- No SQL during a step is a rule for every phase, not only the standard ones (Claude's call, defence in depth): in Debug builds and tests, an EF command interceptor fails any SQL command issued while a phase runs. Tests whose subject is what SQL sees of rows added mid-run (for example `EngineRunTests.AddedRows_VisibleAtNextBoundary_SameForStepsAndOneAdvance`) are rewritten to check the same through `WorldState`, or retired where their subject no longer exists.
- `ShopMarket` becomes index lookups on `WorldState` (its "DB ∪ Local" merges go away).
- New entities are created through `WorldState` (`Add…` methods), which adds them to the index and to the `DbContext` (`DbSet.Add`), so the next save inserts them.
- Removing entities (log pruning, if it stays) goes through `WorldState` too.

### 5.3 The run loop (proposal, not contested)

- `StepToAsync` no longer saves or clears after each boundary; it runs phases and money snapshots only, then checks whether a save is due (section 6).
- The tracker is never cleared during a run.

### 5.4 Append-only rows (proposal, not contested)

Log events with their scope rows, and money ledger lines and snapshots, are only added during a run. They are added to the `DbContext` as today and written at the next save. They are not indexed in `WorldState` unless a phase reads them back during the run; `LogEventEmitter` keeps its next sequence number in memory, loaded once (as today).

### 5.5 Memory (proposal, not contested)

Rows created during a run stay in memory at least until the next save. After a save, rows that phases will not read again (log events, ledger lines, completed work orders, caravans home and reported) can be detached and dropped from `WorldState` so memory does not grow for the length of a play session. The 6-settlement world created about 10,000 work orders and 18,700 log events per simulated year; at 1,000 settlements, if growth is proportional, that is about 1.7 million work orders and 3.1 million log events per year (not measured).

### 5.6 Keeping determinism possible (user; details proposal, not contested)

- No new source of randomness outside `RngStreams`.
- Indexes and lists keep the visit order phases use today; no iteration over unordered `HashSet` or `Dictionary` where order affects results.
- Seeded entity ids remain the later fix (roadmap "Seeded entity IDs").

## 6. Saves

Rules (user):

- A run is a play session (until pause) or a jump to a date. A step button (1 minute, 1 day, 1 week, 1 month, 1 year, or any increment) is its own run.
- A run saves when it ends (pause, the end of a jump or step, closing the world).
- While a run goes, it saves every 4 simulated months, so a crash loses at most 4 months. A run shorter than that may be lost entirely.
- Saves hold the full state (user): not a change log to replay.

Mechanics (proposal, not contested):

- A save: log pruning (through `WorldState`), `DetectChanges`, `SaveChangesAsync`, commit the transaction, then begin a new transaction for the rest of the run (`BEGIN IMMEDIATE`, so no other writer gets in between).
- A run that joined a transaction the caller opened (tests) does not commit at the 4-month saves; it saves rows inside the caller's transaction and leaves the commit to the caller, as today (builder's call, recorded).
- "Every 4 simulated months" is measured from the run's start tick: a save is due at the first boundary at or after start + 4 months, then every 4 months after (calendar months, using `CalendarSystem`). The end-of-run money snapshot stays at run end only.
- A failed save, or a phase failure, rolls back to the previous save and ends the run with the error shown. The GUI's clock (`TimeController` fail path) resets to the tick of the last save, not to the start of the run.
- `AdvanceKillTests` changes: a killed advance leaves the file at its last save (the start of the advance if it ran under 4 months).

## 7. Reads while a run is open

- While a run is open, the in-memory state is the truth; saved rows can be up to 4 months old (proposal, not contested).
- The run's reads (`ISimulationRun.CaravanPositionsAsync`, `SettlementDetailsAsync`, `SettlementListAsync`) answer from `WorldState`, taking turns with steps as today (`SimulationRunner`'s gate) (proposal, not contested). Parts of those reads that never change during a run (`RouteTiles`, `VoyagePaths`, `Roads`) can still come from SQL.
- Between runs, the GUI reads from the same `WorldState` (user's choice of keeping it loaded; details proposal, not contested), so what it shows never differs from what the next run starts from.
- Reads that do not go through the session (other connections, other programs) see the last save (proposal, not contested).

## 8. DM changes

- Every DM change is saved (user).
- With no run open, DM changes save at once, as today.
- With a run open, a DM change is applied to the run's `WorldState` between steps (through the run's gate) and the run saves right after it (proposal, not contested).
- Scope now (Claude's call): only the no-run path. Today no GUI command makes DM changes during a run; the CLI and TUI (`BuyOutAction`, `DisableProductionAction`, `EnableProductionAction`, `route upgrade`) apply them outside runs. The in-run path is built with the first GUI DM command, not before.
- DM changes are `LogEventService` operations (buy, adjust stock, disable and enable production), `RouteUpgradeService` and `RouteNodeRenameService` (Claude's call). Each is recorded as a player-action log event; `RouteUpgradeService` changes to log `RouteUpgraded` with `isPlayerAction` true. That record is the start of the later replay log.

## 9. Measuring the result

- Release `advance 1y` on a fresh copy of the seed-42 world, against the baseline (21.4 s stepping, about 59 ms per simulated day).
- Time to open a world (load `WorldState` and build indexes), reported separately from stepping.
- The Debug `advance` summary (`RunProfile`) shows SQL commands per phase; after this change phases issue none during steps, and saves are 1 per run plus 1 per 4 simulated months.
- A `dotnet-trace` of a 3-month Release advance, compared with the baseline breakdown.
- No speed target is set for this project alone (user decides after the measurement); the overall target is the overview's.

## 10. Left to the builder

- Exact class and method names (`WorldState`, index names), and whether indexes are dictionaries of lists or sorted collections, as long as visit order is kept.
- How phases are converted: one phase at a time, each keeping its tests green.
- Whether `SimulationContext.Db` stays public for saves and DM changes only.
- Which rows are dropped after a save (5.5), as long as no phase reads them again.

## 11. Later work this prepares for

- City groups processed in parallel inside time windows (overview section 2): `WorldState` is the place to split by group.
- Postgres for several DMs: saves stay EF.
- Price chart (`2026-11-25-price-chart-design.md`, approved): daily price bars are recorded through `WorldState` during a run and saved with the rest; its tasks (P1–P5) and checkpoints (C2, D2) are in this design's plan.
- Seeded entity ids and log-and-replay saves: the DM change log (section 8) and ordered visiting (5.6).
