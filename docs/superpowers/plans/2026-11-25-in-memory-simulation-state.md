# In-memory simulation state Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the simulated world in memory while a world is open, so phases issue no SQL during a step, and write it to SQLite only at the saves the user set.

**Architecture:** A `WorldState` in `WorldEcon.Engine` holds every simulated EF entity of one world, tracked by one long-lived `WorldDbContext`, with indexes the phases read. `SimulationContext` carries it; `ShopMarket`, the phases, `LogEventEmitter`, `AncestorResolver`, `MoneyLedger` and `TravelKnowledgeService` read and add through it. `EngineRun` stops saving after every boundary and saves at run end and every 4 simulated months. A `SimulationSession` in `WorldEcon.Application` owns the context, state and transactions for an open world; GUI runs and reads go through it.

**Tech Stack:** C# / .NET 10, EF Core with SQLite, TUnit + FluentAssertions, Avalonia (GUI view models only).

**Spec:** `docs/superpowers/specs/2026-11-25-in-memory-simulation-state-design.md`; Tasks P1–P5 also `docs/superpowers/specs/2026-11-25-price-chart-design.md`

## Global Constraints

- `dotnet build WorldEcon.slnx` finishes with 0 warnings and 0 errors, in Debug and in Release.
- Tests run in Release: `dotnet run --project tests/<Project> -c Release`.
- No commits, no branches, no worktrees (user rule): each task ends with its changes left uncommitted for the user's review.
- No SQL during a step, for every phase: a command issued while a phase runs fails when the step guard is on (spec 5.2).
- Saves: at run end, and every 4 simulated calendar months from the run's start tick, at the first boundary at or after it (spec 6).
- A save is: log pruning, `DetectChanges`, `SaveChangesAsync`, commit, then `BEGIN IMMEDIATE` for the rest of the run (spec 6).
- A run that joined a caller's transaction never commits; it saves inside that transaction (spec 6).
- A failed save or a failed phase rolls back to the previous save, and the in-memory state is reloaded from the file (spec 6; this plan's ruling, see Task 10).
- No new randomness outside `RngStreams`; lists phases visit keep the order they visit today (ascending id value) (spec 5.6).
- Writing style in code comments and docs (user rules): no bare magnitude claims, no flourishes, acronyms expanded on first use.

## Review Focus

1. **A DM change while the world is open but no run is running** (GUI node rename): the in-memory `RouteNode` must show the new name and the next run must not lose it. Test in Task 11.
2. **A run killed or failing after a 4-month save:** the file is at that save, the GUI clock shows that save's tick, and the next run starts from a state reloaded from the file, not the half-run memory. Tests in Tasks 8 and 10.
3. **A step button pressed many times in a row (1-minute steps):** each is a run with a save; no world reload between them, and memory does not grow without bound. Tests in Tasks 9 and 10.
4. **Rows added in one phase and read by a later phase in the same boundary** (a work order created then read, a caravan dispatched then listed): must be visible through `WorldState` immediately. Test in Task 2 and kept by `RunEquivalenceTests`.
5. **A world file opened by another program while the GUI holds it open:** other readers see the last save, never a half-written one. Test in Task 10.

Price chart (Tasks P1–P5):

6. **A good with no market in a settlement on some days** (stock runs out, merchants trade at base value): gaps or wick-only candles, never an invented body. Tests in Tasks P1 and P2.
7. **A settlement under two picked countries, or under a disputed claim:** counted once; disputed not counted. Test in Task P2.
8. **Pressing Play with a chart window open:** the chart keeps its data, Refresh is disabled, no read runs during the run. Test in Task P5.

## Checkpoints for the user

Execution stops at each checkpoint so the user can try the result before the next tasks start. Tasks marked **internal** have no effect the user can see on their own; their tests are the evidence.

| Checkpoint | After tasks | What the user can try |
|---|---|---|
| A. Shop lookups from memory | 1–4 (1, 2, 3 internal) | Debug `advance 1M`: Pricing and Production SQL counts fall; Release `advance 1y` time against the baseline 21.4 s; results still look the same |
| B. Phases free of SQL | 5–7 | Debug `advance 1M`: 0 SQL commands in every phase row; Release `advance 1y` time |
| C. Saves every 4 months | 8–9 | Debug `advance 1y`: 3 saves; stop a long advance part-way and see where the file is; memory stays flat over a long advance |
| C2. Prices in the CLI | P1–P3 | `prices <db> <good>` after an advance: daily candles, week and month candles, filters by city, region, country, no country, sale kind |
| D. GUI on the session | 10–12 | The app: step buttons with no reload pause, city panel between runs, rename a node while paused then play, close or kill during a long jump and reopen |
| D2. Price chart | P4–P5 | The app: box select on the map (Shift+drag, Ctrl+Shift+drag, Esc), the chart window with zoom, candle sizes, hover, scopes and Refresh between runs |
| E. Measurement | 13 | The after numbers in the baseline report; a `dotnet-trace` of your own |

---

### Task 1 (internal): Step guard for SQL during a phase

**Files:**
- Create: `src/WorldEcon.Engine/Diagnostics/StepSqlGuard.cs`
- Modify: `src/WorldEcon.Engine/EngineRun.cs` (mark phase execution)
- Test: `tests/WorldEcon.Engine.Tests.Unit/StepSqlGuardTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class StepScope { public bool InPhase { get; internal set; } }`; `SimulationContext.Step` (a `StepScope`, one per context).
  - `EngineRun` sets `ctx.Step.InPhase = true` around each `phase.ExecuteAsync` call (in a `try/finally`).
  - `public static class StepSqlGuard { public static DbContextOptionsBuilder AddTo(DbContextOptionsBuilder options, StepScope scope); }`: adds a `DbCommandInterceptor` that throws `InvalidOperationException("SQL during a step: <command text, first 200 chars>")` from every `…Executing`/`…ExecutingAsync` override when `scope.InPhase` is true.
- Because the interceptor needs the scope before the context exists, `StepScope` is created first and passed to `SimulationContext.LoadAsync(db, worldId, StepScope? scope = null)`.

- [ ] **Step 1: Write the failing tests** `Phase_issuing_sql_fails_when_guard_is_on` (a phase calling `ctx.Db.Worlds.CountAsync()`; advance 1 day; expect `InvalidOperationException` whose message starts with `"SQL during a step"`) and `Sql_outside_phases_is_allowed` (loading the world and the run's saves succeed with the guard on and a phase that does no SQL).
- [ ] **Step 2: Run and see both fail** — `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release -- --treenode-filter "/*/*/StepSqlGuardTests/*"` → compile errors (types missing).
- [ ] **Step 3: Implement** `StepScope`, `StepSqlGuard`, the `LoadAsync` parameter, and the `InPhase` marking in `EngineRun.StepToAsync`.
- [ ] **Step 4: Run** the same filter → both pass; then the whole Engine test project → all pass (the guard is off by default).
- [ ] **Step 5: Stop** — changes left uncommitted for review.

### Task 2 (internal): `WorldState` and its loader

**Files:**
- Create: `src/WorldEcon.Engine/State/WorldState.cs`, `src/WorldEcon.Engine/State/WorldStateLoader.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/WorldStateTests.cs`

**Interfaces:**
- Produces `public sealed class WorldState` with:
  - Read-only lists, each sorted by id value: `Settlements`, `Goods`, `Recipes`, `ProductionNodes`, `ResourceEndowments`, `Shops`, `Consumers` (`RepresentativeConsumer`), `Merchants` (`RepresentativeMerchant`), `Routes`, `RouteNodes`, `VehicleClasses`, `PoliticalRegions`, `PoliticalRegionContinents`.
  - Lookups: `Settlement SettlementById(SettlementId)`, `Good GoodById(GoodId)`, `Recipe RecipeById(RecipeId)`, `ProductionNode NodeById(ProductionNodeId)`, `Shop? ShopById(ShopId)`, `RepresentativeMerchant? MerchantById(MerchantId)`, `Route RouteById(RouteId)`, `VehicleClass? VehicleByName(string)`.
  - Indexes: `IReadOnlyList<Shop> ShopsIn(SettlementId)`; `IReadOnlyList<Stockpile> ShopStockpiles(SettlementId, GoodId)` (ordered by owner id, then id, as `ShopMarket.StockpilesForGood` orders today); `Stockpile? StockpileInShop(ShopId, GoodId)`; `IReadOnlyList<Stockpile> AllShopStockpiles` (id order); `IReadOnlyList<ProductionNode> NodesIn(SettlementId)`; `IReadOnlyList<WorkOrder> IncompleteWorkOrders` (id order); `IReadOnlyList<Caravan> UndeliveredCaravans`, `DeliveredNotHomeCaravans` (id order); `IReadOnlyList<ShopPriceBelief> PriceBeliefs`; `TravelKnowledge? Knowledge(SettlementId knower, SettlementId from, SettlementId to, string vehicleClass)` and `IReadOnlyList<TravelKnowledge> KnowledgeFromSeat(SettlementId seat)`.
  - Adds, each inserting into the right lists and indexes and calling the matching `DbSet.Add`: `AddShop`, `AddStockpile`, `AddConsumer`, `AddMerchant`, `AddWorkOrder`, `AddCaravan`, `AddPriceBelief`, `AddTravelKnowledge`.
  - `void Refresh(Caravan)` and `void Refresh(WorkOrder)`: move an entity between indexes after its state changed (delivered, home reported, completed).
  - `WorldDbContext Db` (the tracking context) and `World World`.
- Produces `public static class WorldStateLoader { public static Task<WorldState> LoadAsync(WorldDbContext db, World world, CancellationToken ct = default); }` — tracked queries for every list above; work orders only `!Completed`; caravans only `!Delivered || !HomeReported`; travel knowledge all rows. Index keys use the same fields the current queries filter on (`ShopMarket.cs`: `OwnerKind == Shop`, `OwnerId`, `GoodId`; `TravelKnowledgeService.cs`: knower, from, to, vehicle class); read those queries before writing the keys. Id parameter types are the existing Domain id types (`RecipeId`, `ProductionNodeId`, the merchant id type in `RepresentativeMerchant`).

- [ ] **Step 1: Write the failing tests** on the Engine tests' seeded world (use the world builder the existing phase tests use, for example the one in `ProductionPhaseTests`):
  - `Loads_every_simulated_entity_in_id_order` — counts equal `db.<Set>.CountAsync()` per set; each list is sorted by id value.
  - `ShopStockpiles_matches_the_StockpilesForGood_query` — for every settlement × good, the index returns the same ids in the same order as today's `ShopMarket.StockpilesForGood` on a fresh context.
  - `Added_entities_appear_in_indexes_and_the_tracker` — `AddStockpile` then `ShopStockpiles(...)` contains it and `db.Entry(x).State == Added`; same for `AddWorkOrder` / `IncompleteWorkOrders` and `AddCaravan` / `UndeliveredCaravans`.
  - `Refresh_moves_a_delivered_caravan` — after the caravan is marked delivered and `Refresh` runs, it leaves `UndeliveredCaravans` and joins `DeliveredNotHomeCaravans`.
  - `Loading_issues_no_sql_per_entity` — with `RunProfile` interceptors, `LoadAsync` issues one command per entity set loaded (18 sets: the 13 lists, plus stockpiles, shop price beliefs, work orders, caravans, travel knowledge; merchant trips and caravan legs are owned collections loaded with their owners).
- [ ] **Step 2: Run and see them fail** — `--treenode-filter "/*/*/WorldStateTests/*"` → compile errors.
- [ ] **Step 3: Implement** `WorldState` and `WorldStateLoader`. Index storage is the builder's call (spec 10) as long as returned lists keep the order above.
- [ ] **Step 4: Run** the filter → pass; whole Engine project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Task 3 (internal): `SimulationContext` carries `WorldState`; adds go through it; log, ancestors and ledger read it

**Files:**
- Modify: `src/WorldEcon.Engine/SimulationContext.cs`, `src/WorldEcon.Engine/Logging/LogEventEmitter.cs`, `src/WorldEcon.Engine/Logging/AncestorResolver.cs`, `src/WorldEcon.Engine/Ledger/MoneyLedger.cs`, every phase in `src/WorldEcon.Engine/Phases/` (adds only), `src/WorldEcon.Engine/ShopMarket.cs` (adds only), `src/WorldEcon.Engine/Trade/TravelKnowledgeService.cs` (add only)
- Test: `tests/WorldEcon.Engine.Tests.Unit/LogEventEmitterTests.cs`, `AncestorResolverTests.cs`, `MoneyLedgerTests.cs` (extend)

**Interfaces:**
- Consumes: `WorldState`, `WorldStateLoader.LoadAsync` (Task 2); `StepScope` (Task 1).
- Produces:
  - `SimulationContext.State` (`WorldState`); `SimulationContext.LoadAsync(WorldDbContext db, WorldId worldId, StepScope? scope = null)` now loads the state too.
  - `LogEventEmitter` loads its next sequence number in its constructor path (`SimulationContext.LoadAsync`), not on first emit.
  - `AncestorResolver(WorldState state)` answers from `state.Settlements`, `state.PoliticalRegions`, `state.PoliticalRegionContinents`.
  - `MoneyLedger` total supply from `state.Consumers`, `state.Shops`, `state.Merchants`; its snapshot sequence loaded once at context load.
  - Every `ctx.Db.<Set>.Add(...)` in phases, `ShopMarket` and `TravelKnowledgeService.Report` replaced by the matching `ctx.State.Add…` (reads unchanged in this task, so the "DB ∪ Local" merges still find the added rows).

- [ ] **Step 1: Write the failing tests**: `Emitting_after_load_issues_no_sql` (with the step guard on and `InPhase` true, `EmitAsync` for a settlement with a region and continent succeeds and writes the same scope rows as before); `Ledger_snapshot_issues_no_sql_for_totals` (guard on; `SnapshotAsync` succeeds; totals equal a fresh-context sum).
- [ ] **Step 2: Run and see them fail** with `"SQL during a step"`.
- [ ] **Step 3: Implement** the interfaces above.
- [ ] **Step 4: Run** the new tests → pass; whole Engine project → pass; `tests/WorldEcon.Application.Tests.Unit` → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Task 4: `ShopMarket` on `WorldState`

**Files:**
- Modify: `src/WorldEcon.Engine/ShopMarket.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/ShopMarketTests.cs`

**Interfaces:**
- Consumes: `WorldState` indexes and adds (Task 2).
- Produces (synchronous; callers drop their `await`): `List<Shop> ShopsIn(SimulationContext, SettlementId)`, `Shop GetOrCreatePublicMarketShop(SimulationContext, SettlementId)`, `Shop GetOrCreateProducerShop(SimulationContext, ProductionNode, string)`, `Shop GetOrCreateProducerShop(SimulationContext, ResourceEndowment, string)`, `List<Stockpile> StockpilesForGood(SimulationContext, SettlementId, GoodId)`, `long TotalSupply(SimulationContext, SettlementId, GoodId)`, `Stockpile StockpileInShop(SimulationContext, ShopId, GoodId)`, `long WithdrawAcrossShops(SimulationContext, SettlementId, GoodId, long)`. Same results and orders as today.

- [ ] **Step 1: Add the test** `ShopMarket_issues_no_sql` (guard on, `InPhase` true, each method called once on the seeded world) and convert the existing `ShopMarketTests` to the synchronous signatures.
- [ ] **Step 2: Run and see it fail** (compile errors, then `"SQL during a step"`).
- [ ] **Step 3: Implement** on the indexes; update the callers in the phases to the synchronous calls.
- [ ] **Step 4: Run** `ShopMarketTests` → pass; whole Engine project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Checkpoint A: shop lookups from memory (stop for the user)

Build both configurations first (`dotnet build src/WorldEcon.Cli` and `dotnet build src/WorldEcon.Cli -c Release`). Commands, on fresh copies of a seed-42 world (`dotnet src/WorldEcon.Cli/bin/Release/net10.0/WorldEcon.Cli.dll new <db> --seed 42`):

- `dotnet src/WorldEcon.Cli/bin/Debug/net10.0/WorldEcon.Cli.dll advance <copy> 1M` → the `RunProfile` table. Against the baseline month (Production 9,876 SQL commands, Pricing 3,000), Pricing and Production fall because `StockpilesForGood` and `ShopsIn` no longer query.
- `time dotnet src/WorldEcon.Cli/bin/Release/net10.0/WorldEcon.Cli.dll advance <copy> 1y` → compare with 22.2 s (baseline, including 1.2 s startup).
- `stock <copy> <settlement>` and `money <copy>` after the advance → the same kind of output as before; numbers differ run to run today (ids are random), so compare shape, not values.

### Task 5: Consumer, merchant-spawn, perishability, pricing and price-discovery phases read `WorldState`

**Files:**
- Modify: `src/WorldEcon.Engine/Phases/ConsumerIncomePhase.cs`, `ConsumerSpawnPhase.cs`, `MerchantSpawnPhase.cs`, `PerishabilityPhase.cs`, `PricingPhase.cs`, `PriceDiscoveryPhase.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/PhaseSqlFreeTests.cs` (new); existing tests for these phases unchanged

**Interfaces:**
- Consumes: `WorldState` (Task 2), synchronous `ShopMarket` (Task 4).
- Produces: no `ctx.Db` use in these six phases; their "DB ∪ Local" helper methods removed.

- [ ] **Step 1: Write the failing test** `Phase_runs_without_sql` parameterised over these six phases: run each alone for 8 days (covering one weekly boundary) on the seeded world with the step guard on.
- [ ] **Step 2: Run and see it fail** for each phase with `"SQL during a step"`.
- [ ] **Step 3: Implement**: replace each query with the `WorldState` list or index, keeping the existing `OrderBy` on id value where it sets visit order.
- [ ] **Step 4: Run** `PhaseSqlFreeTests` → pass for the six; existing tests for these phases (`ConsumerSpawnIncomeTests`, `MerchantSpawnTests`, `ConsumptionPerishabilityTests`, `PricingPhaseTests`, `PricingOnShopsTests`, `PriceDiscoveryTests`) → pass; whole Engine project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Task 6: Production phase reads `WorldState`

**Files:**
- Modify: `src/WorldEcon.Engine/Phases/ProductionPhase.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/PhaseSqlFreeTests.cs` (add `ProductionPhase`)

**Interfaces:**
- Consumes: `state.GoodById`, `RecipeById`, `NodeById`, `ResourceEndowments`, `NodesIn` / `ProductionNodes`, `IncompleteWorkOrders`, `AddWorkOrder`, `Refresh(WorkOrder)` (Task 2); synchronous `ShopMarket` (Task 4).
- Produces: no `ctx.Db` use in `ProductionPhase`; `LoadIncompleteWorkOrders` removed; completing a work order calls `state.Refresh(order)`.

- [ ] **Step 1: Add** `ProductionPhase` to `Phase_runs_without_sql`.
- [ ] **Step 2: Run and see it fail** with `"SQL during a step"`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `PhaseSqlFreeTests`, `ProductionPhaseTests`, `ProductionOnShopsTests`, `ProductionAdvanceTests`, `SubstrateGranularityTests` → pass; whole Engine project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Task 7: Trade phase and travel knowledge read `WorldState`

**Files:**
- Modify: `src/WorldEcon.Engine/Phases/TradePhase.cs`, `src/WorldEcon.Engine/Trade/TravelKnowledgeService.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/PhaseSqlFreeTests.cs` (add `TradePhase`)

**Interfaces:**
- Consumes: `state.Goods`, `Settlements`, `RouteNodes`, `Routes`, `RouteById`, `VehicleClasses`, `VehicleByName`, `Merchants`, `MerchantById`, `UndeliveredCaravans`, `DeliveredNotHomeCaravans`, `AddCaravan`, `Refresh(Caravan)`, `Knowledge`, `KnowledgeFromSeat`, `AddTravelKnowledge` (Task 2).
- Produces:
  - `TravelKnowledgeService.Report(WorldState state, WorldId world, SettlementId knower, TripRecord trip)` → `bool` and `TravelKnowledgeService.FromSeat(WorldState state, SettlementId seat)` → `List<TravelKnowledge>`; the `WorldDbContext` overloads stay for callers outside runs if any remain (check with a search; delete them if none).
  - No `ctx.Db` use in `TradePhase`; the "world has no road graph" check reads `state.RouteNodes` and `state.Routes`.

- [ ] **Step 1: Add** `TradePhase` to `Phase_runs_without_sql` (seeded world with a road graph, for example `RoadGraphFixture`; 30 days so caravans depart, arrive and report home).
- [ ] **Step 2: Run and see it fail** with `"SQL during a step"`.
- [ ] **Step 3: Implement.** Delivering a caravan and marking it home reported each call `state.Refresh(caravan)`.
- [ ] **Step 4: Run** `PhaseSqlFreeTests`, `TradePhaseTests`, `TradeOnShopsTests`, `TradeHaulageTests`, `TravelGraphTradeTests` → pass; whole Engine project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Checkpoint B: phases free of SQL (stop for the user)

- Debug `advance 1M` on a fresh copy → every phase row shows 0 SQL commands; the remaining SQL is the saves (still one per boundary until Task 8).
- Release `advance 1y` timed as in Checkpoint A.
- `caravans <copy>` and `merchants <copy>` → caravans still depart, arrive and report home.

### Task 8: `EngineRun` saves at run end and every 4 simulated months

**Files:**
- Modify: `src/WorldEcon.Engine/EngineRun.cs`, `src/WorldEcon.Engine/TickEngine.cs`, `src/WorldEcon.Engine/Logging/LogRetention.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/EngineRunTests.cs`, `tests/WorldEcon.Cli.Tests.Unit/AdvanceKillTests.cs`, `tests/WorldEcon.Application.Tests.Unit/RunEquivalenceTests.cs`

**Interfaces:**
- Consumes: `SimulationContext.State` (Task 3).
- Produces:
  - `EngineRun.StepToAsync`: runs phases and monthly money snapshots; no `SaveAndClearAsync`; after each boundary, if `boundary >= NextSaveTick`, calls `SaveAsync()`.
  - `EngineRun.SaveAsync()` (public): `LogRetention.PruneAsync`, `DetectChanges`, `SaveChangesAsync`; when the run owns its transaction: commit, then begin a new one (`BeginTransactionAsync`, which issues `BEGIN IMMEDIATE` on this provider as today). Sets `LastSavedTick`.
  - `EngineRun.NextSaveTick` = the run's start tick plus 4 calendar months (`CalendarSystem`), then 4 months after each save; `EngineRun.LastSavedTick` (`Tick`, the start tick until the first save).
  - `EngineRun.EndAsync()`: end-of-run money snapshot, then `SaveAsync()` semantics, without starting a new transaction. When a periodic save falls due at the boundary equal to the step's target, `StepToAsync` does not save there; the next `StepToAsync` (at its first boundary) or `EndAsync` saves instead, so a run ending on a due tick saves once.
  - `DisposeAsync` of a run not ended: rolls back to the last save (the open transaction only).
  - The tracker is never cleared; `World` stays attached.
- `LogRetention.PruneAsync` runs at saves only; it may query SQL (saves are outside phases).

- [ ] **Step 1: Write the failing tests**:
  - `Saves_every_four_months_and_at_end` — a 1-year advance from tick 0 on the default calendar (360 days, 12 months of 30 days) with `RunProfile`: `Saves` equals 3 (periodic saves at day 120 and day 240, then the end-of-run save at day 360; a periodic save due at the run's end tick is skipped because `EndAsync` saves). After a dispose without `EndAsync` following a 5-month step, a fresh context reads the tick of the day-120 save.
  - Rewrite `AddedRows_VisibleAtNextBoundary_SameForStepsAndOneAdvance` to check visibility through `ctx.State` (a phase adds a stockpile through `State.AddStockpile`; the next boundary's phase sees it in `ShopStockpiles`), keeping the "steps equal one advance" assertion.
  - `AdvanceKillTests.KilledAdvance_LeavesPreRunTick` → rename `KilledAdvance_LeavesLastSave`: a killed 6-month advance leaves the file at the 4-month save tick; a killed 2-month advance leaves the pre-run tick.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Engine, Cli and Application test projects → pass (`RunEquivalenceTests` must pass unchanged).
- [ ] **Step 5: Stop** — uncommitted for review.

### Task 9: Drop finished rows after a save

**Files:**
- Modify: `src/WorldEcon.Engine/State/WorldState.cs`, `src/WorldEcon.Engine/EngineRun.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/WorldStateTests.cs`

**Interfaces:**
- Produces: `WorldState.ReleaseFinished()`, called by `EngineRun.SaveAsync` after a successful `SaveChangesAsync`: detaches from the tracker and drops from `WorldState` completed work orders, caravans delivered and home reported, and all saved `LogEvent`, `LogEventScope`, `MoneyLedgerSnapshot` and `MoneyLedgerLine` entries. No phase reads these after they finish (check each phase before adding an entity kind to the list).

- [ ] **Step 1: Write the failing test** `Tracker_does_not_grow_across_saves`: three consecutive 4-month runs on the seeded world; tracked entity count after each `EndAsync` (`db.ChangeTracker.Entries().Count()`) is within 10% of the count after the first.
- [ ] **Step 2: Run and see it fail** (count grows with log events and work orders).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the test and the whole Engine project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Checkpoint C: saves every 4 months (stop for the user)

- Debug `advance 1y` on a fresh copy → `SaveChanges: 3 calls`.
- Release `advance 1y` timed as before.
- Start a Release `advance 2y` and stop it with Ctrl+C after about half its expected time; `advance <copy> 1` then prints the current tick: it is a 4-month save tick (a multiple of 172,800 ticks), not the start.
- While a Release `advance 5y` runs, watch its memory (`top -p $(pgrep -f WorldEcon.Cli)`): resident memory levels off after the first save instead of rising for the whole run.

### Task P1: Daily price bars recorded in memory and saved

Spec: `docs/superpowers/specs/2026-11-25-price-chart-design.md` sections 5.1–5.4.

**Files:**
- Create: `src/WorldEcon.Domain/Economy/DailyPriceBar.cs`, `src/WorldEcon.Domain/Economy/SaleKind.cs`, `src/WorldEcon.Persistence/Configurations/DailyPriceBarConfiguration.cs`, `src/WorldEcon.Engine/Pricing/PriceBarRecorder.cs`, `src/WorldEcon.Engine/Pricing/MarketPrices.cs`, a migration `AddDailyPriceBars` in `src/WorldEcon.Persistence/Migrations/` (generated with the repository's `dotnet-ef` local tool, as the existing migrations were)
- Modify: `src/WorldEcon.Persistence/WorldDbContext.cs`, `src/WorldEcon.Engine/State/WorldState.cs`, `src/WorldEcon.Engine/EngineRun.cs`, `src/WorldEcon.Engine/Phases/PriceDiscoveryPhase.cs`, `src/WorldEcon.Engine/Phases/TradePhase.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/DailyPriceBarTests.cs`, `tests/WorldEcon.Engine.Tests.Unit/PriceBarRecorderTests.cs`, `tests/WorldEcon.Persistence.Tests.Unit/` (the existing migration test, if one checks the model snapshot)

**Interfaces:**
- Consumes: `WorldState` (Task 2), `ShopStockpiles(SettlementId, GoodId)` (Task 2), `EngineRun.SaveAsync` and `WorldState.ReleaseFinished` (Tasks 8, 9), `StepSqlGuard` (Task 1).
- Produces:
  - `public enum SaleKind { Retail = 0, MerchantBuy = 1, MerchantSell = 2 }`.
  - `DailyPriceBar : AggregateRoot<DailyPriceBarId>` with `WorldId`, `SettlementId`, `GoodId`, `int Day`, `Money? Open`, `Money? Close`, and per `SaleKind`: `Money? Low`, `Money? High`, `long Volume`, `Money Turnover` (read through `SaleStats Stats(SaleKind)`); `static DailyPriceBar Start(WorldId, SettlementId, GoodId, int day, Money? open)`; `void RecordSale(SaleKind, Money unitPrice, long quantity, Money paid)` (rejects quantity ≤ 0 and negative prices); `void Finish(Money? close)`. EF column layout is the builder's call (spec 11); unique index on (`SettlementId`, `GoodId`, `Day`), index on (`WorldId`, `GoodId`, `Day`).
  - `public static class MarketPrices { public static Money? Of(WorldState state, SettlementId settlement, GoodId good); }`: first positive `MarketPrice` in `state.ShopStockpiles(settlement, good)` order, else null. `TradePhase.SeatRefPrice` and the seat-supply loop (today's lines 166 and 359) call it and keep their `good.BaseValue` fallback on null.
  - `public sealed class PriceBarRecorder` held as `WorldState.PriceBars`: `bool Enabled` (default true; set false only by the measurement harness in Task 13); `void OpenDay(int day)` (takes `MarketPrices.Of` for every settlement × good, keeps the non-null ones as opens); `void Record(SettlementId, GoodId, SaleKind, Money unitPrice, long quantity, Money paid)`; `void CloseDay()` (takes closes, makes a `DailyPriceBar` for every pair with an open, a close or a sale, and adds them through `WorldState.AddPriceBar`). No SQL in any method.
  - `EngineRun` calls `OpenDay` before the first daily phase of a day boundary and `CloseDay` after the last one (the builder may use phases with extreme `Order` values instead; spec 11).
  - Calls to `Record`: `PriceDiscoveryPhase` per cleared unit (`Retail`, the ask, 1, the ask); `TradePhase` dispatch (`MerchantBuy`, `chosen.SeatPrice`, `quantity`, `purchase` after the capital cap); `TradePhase` delivery (`MerchantSell` at the destination, `destPrice`, `caravan.Quantity`, `caravan.Quantity × destPrice`).
  - `WorldState.ReleaseFinished()` also drops saved `DailyPriceBar` entries.
  - `WorldStateLoader` does not load `DailyPriceBar` rows.

- [ ] **Step 1: Write the failing tests**:
  - `DailyPriceBarTests`: `RecordSale_tracks_low_high_volume_turnover_per_kind` (two retail sales at 12 and 9, one merchant buy at 20: retail Low 9, High 12, Volume 2, Turnover 21; merchant buy Low = High = 20; merchant sell Volume 0, Low and High null); `RecordSale_rejects_nonpositive_quantity`.
  - `PriceBarRecorderTests` on the Engine tests' seeded world:
    - `Retail_volume_matches_units_sold` — a 1-day advance; for each (settlement, consumed good), the bar's retail Volume equals the quantity in that day's "Sold N …" log events for that settlement and good.
    - `Open_equals_previous_close` — a 3-day advance; for every pair with bars on consecutive days, day d+1 `Open` equals day d `Close`.
    - `Merchant_volumes_match_caravans` — a 30-day advance; total `MerchantBuy` Volume equals the sum of `Quantity` over caravans created in the run; total `MerchantSell` Volume equals the sum over caravans delivered in the run.
    - `Capped_purchase_records_money_paid` — a merchant with capital below `quantity × SeatPrice`: the bar's merchant-buy Turnover equals the capital spent, High equals `SeatPrice`.
    - `No_price_and_no_sales_writes_no_row` — a settlement with no stockpile of a good: no bar for that pair.
    - `Recording_issues_no_sql` — a 2-day advance with `StepSqlGuard` on succeeds.
    - `Bars_are_saved_then_released` — a 5-month advance: the file holds one bar per (pair, day) recorded; after the 4-month save the in-memory tracker holds no `DailyPriceBar` older than that save.
- [ ] **Step 2: Run and see them fail** — `--treenode-filter "/*/*/PriceBarRecorderTests/*"` and `"/*/*/DailyPriceBarTests/*"` → compile errors.
- [ ] **Step 3: Implement** the entity, configuration, migration, recorder, `MarketPrices` and the calls.
- [ ] **Step 4: Run** both filters → pass; Domain, Engine and Persistence test projects → pass; `RunEquivalenceTests` (Application) → pass unchanged.
- [ ] **Step 5: Stop** — uncommitted for review.

### Task P2: Price scopes and candles

Spec sections 6 and the scope rules there.

**Files:**
- Create: `src/WorldEcon.Application/Prices/PriceScope.cs`, `src/WorldEcon.Application/Prices/PriceScopeResolver.cs`, `src/WorldEcon.Application/Prices/CandleBuilder.cs`, `src/WorldEcon.Application/Queries/PriceSeriesQuery.cs`
- Test: `tests/WorldEcon.Application.Tests.Unit/CandleBuilderTests.cs`, `PriceScopeResolverTests.cs`, `PriceSeriesQueryTests.cs`

**Interfaces:**
- Consumes: `DailyPriceBar`, `SaleKind` (Task P1); `CalendarSystem` (`ToDate`, `WeekdayIndex`).
- Produces:
  - `public enum CandleSize { Day, Week, Month, Year }`; `public enum SaleKindFilter { All, Retail, MerchantBuy, MerchantSell }`.
  - `public sealed record PriceScope(IReadOnlyCollection<Guid> SettlementIds, IReadOnlyCollection<Guid> RegionIds, IReadOnlyCollection<Guid> CountryIds, bool NoCountry, bool World)`.
  - `PriceScopeResolver.ResolveAsync(WorldDbContext db, WorldId world, PriceScope scope, CancellationToken ct) → Task<IReadOnlySet<SettlementId>>`: union of the picks; country = regions with that `CountryId` plus regions and settlements under a `TerritorialClaim` of kind `Controls` by it; `Disputes` not counted; no country = settlements no country includes.
  - `public sealed record Candle(int StartDay, int EndDay, Money? Open, Money? High, Money? Low, Money? Close, Money? SaleLow, Money? SaleHigh, long Volume, Money Turnover, int CityCount)`.
  - `CandleBuilder.Build(IEnumerable<DailyPriceBar> bars, SaleKindFilter kind, CandleSize size, CalendarSystem calendar) → IReadOnlyList<Candle>`: pure; step 1 across settlements per day, step 2 over periods, drawn High/Low widened to Open/Close (spec 6).
  - `PriceSeriesQuery.RunAsync(WorldDbContext db, WorldId world, GoodId good, IReadOnlySet<SettlementId> settlements, int fromDay, int toDay, CancellationToken ct) → Task<IReadOnlyList<DailyPriceBar>>`: saved bars from SQL, filtered to the scope, no tracking. Every run ends with a save, so between runs the file holds every bar.

- [ ] **Step 1: Write the failing tests** with hand-built bars and the default calendar:
  - `Cross_city_open_close_are_volume_weighted` (city A open 10 volume 3, city B open 20 volume 1 → open 13 (12.5 rounded half away from zero)).
  - `Zero_volume_day_uses_plain_average_of_priced_cities`.
  - `Wick_covers_open_and_close` (open 15, sale high 12 → High 15, SaleHigh 12).
  - `Week_month_year_periods_follow_the_calendar` (bars on 40 consecutive days: Week candles start where `WeekdayIndex` is 0; Month candles break on day 30; one Year candle).
  - `Period_open_is_first_open_and_close_is_last_close`.
  - `Empty_periods_are_omitted`.
  - `Kind_filter_uses_that_kinds_stats_only`.
  - `PriceScopeResolverTests`: `Country_includes_primary_and_controlled_not_disputed`; `Two_countries_count_a_settlement_once`; `No_country_is_settlements_no_country_includes`.
  - `PriceSeriesQueryTests`: `Returns_only_bars_in_scope_and_range` (bars for 3 settlements over 5 days; a scope of 2 settlements and days 2–3 → 4 bars).
- [ ] **Step 2: Run and see them fail** (compile errors).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Application test project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Task P3: `prices` CLI command

Spec section 9.

**Files:**
- Modify: `src/WorldEcon.Cli/CommandRunner.cs` (new `"prices"` case and `CmdPrices`, plus its help text)
- Test: `tests/WorldEcon.Cli.Tests.Unit/PricesCommandTests.cs`

**Interfaces:**
- Consumes: `PriceScopeResolver`, `PriceSeriesQuery`, `CandleBuilder` (Task P2).
- Produces: `prices <db> <good> [--city <name>]… [--region <name>]… [--country <name>]… [--no-country] [--kind all|retail|buy|sell] [--candle day|week|month|year] [--from <date>] [--to <date>]`. No scope option = whole world. Dates parse as the existing date arguments do (`advance` targets). Output: one line per candle with period (world calendar), open, high, low, close, sale low, sale high (world currency format; `-` for null), volume, cities. Unknown good, city, region or country → error message and non-zero exit, as other commands do.

- [ ] **Step 1: Write the failing tests**: `Prints_one_row_per_day_after_a_3_day_advance`; `Week_candles_group_days`; `Unknown_good_is_an_error`; `City_option_limits_the_scope` (the row's city count is 1).
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Cli test project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Checkpoint C2: prices in the CLI (stop for the user)

On a fresh copy of the seed-42 world:

- `advance <copy> 2M`, then `prices <copy> <a consumed good, for example Bread>`: one row per day for 60 days; open of each day equals the close of the day before; high ≥ open and close ≥ low.
- `prices <copy> Bread --candle week` and `--candle month`: fewer rows, same overall high and low.
- `prices <copy> Bread --city <name>`: cities column 1. `--kind retail` against `--kind buy`: different high and low.
- `prices <copy> <a raw good, for example Iron ore>`: narrow candles (formula price and merchant trades only).
- `--country <name>` and `--no-country`: city counts add up to the whole-world count when every settlement is in at most one country.

### Task 10: `SimulationSession` for an open world

**Files:**
- Create: `src/WorldEcon.Application/Simulation/SimulationSession.cs`
- Modify: `src/WorldEcon.Application/Simulation/SimulationRunner.cs`, `src/WorldEcon.Application/Simulation/ISimulationRun.cs`, `src/WorldEcon.Application/Queries/CaravanPositionsQuery.cs`, `src/WorldEcon.Application/Queries/SettlementDetailsQuery.cs`
- Test: `tests/WorldEcon.Application.Tests.Unit/SimulationSessionTests.cs` (new), `SimulationRunnerTests.cs`, `CaravanPositionsQueryTests.cs`, `SettlementDetailsQueryTests.cs`

**Interfaces:**
- Consumes: `SimulationContext.LoadAsync` (Task 3), `EngineRun` (Task 8), `StepSqlGuard` (Task 1).
- Produces `public sealed class SimulationSession : IAsyncDisposable`:
  - `static Task<ErrorOr<SimulationSession>> OpenAsync(string dbPath, CancellationToken ct = default)` (same file checks and errors as `SimulationRunner.OpenAsync` today: `NoWorld`, `NotWal`, `Busy`); loads the world once.
  - `Task<ErrorOr<ISimulationRun>> BeginRunAsync(CancellationToken ct = default)`: one run at a time (`InvalidOperationException` if one is open); the run uses the session's context and state.
  - `long CurrentTick`, `long LastSavedTick`.
  - Reads from memory, usable between runs and (through the run's gate) during one: `Task<ErrorOr<CaravanPositions>> CaravanPositionsAsync(CancellationToken)`, `Task<ErrorOr<SettlementDetails>> SettlementDetailsAsync(Guid, CancellationToken)`, `Task<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>> SettlementListAsync(CancellationToken)`.
  - `Task<ErrorOr<T>> ApplyDmChangeAsync<T>(Func<WorldDbContext, WorldState, CancellationToken, Task<ErrorOr<T>>> change, CancellationToken ct = default)`: refused while a run is open; otherwise runs the change on the session's context and state, then saves in its own transaction.
  - After a run fails (phase or save throws) or is disposed without ending: the session rolls back, disposes its context, and reloads state from the file before the next run or read. `LastSavedTick` is the tick reloaded.
  - In Debug builds (`#if DEBUG`) the session adds `StepSqlGuard` to its context options.
- `SimulationRunner` becomes the run object `BeginRunAsync` returns (its own `OpenAsync(string)` is removed; update callers).
- `CaravanPositionsQuery.Run(WorldState state, WorldDbContext db, ...)` and `SettlementDetailsQuery.Run(WorldState state, WorldDbContext db, ...)`: dynamic data (caravans, stock, prices, merchants) from `state`; static geometry (`RouteTiles`, `VoyagePaths`, `Roads`) may query `db`. The `dbPath` overloads stay for callers with no session (CLI, TUI).

- [ ] **Step 1: Write the failing tests**:
  - `Steps_in_a_row_do_not_reload` — open a session, run ten 1-minute runs (`BeginRunAsync`, `StepToAsync(tick+1)`, `EndAsync`), with `RunProfile`: the loader's SELECT commands run once (at open) — count SQL commands issued outside saves after open as zero.
  - `Reads_between_runs_come_from_memory` — after a run ends, `SettlementDetailsAsync` returns the same stock as a fresh-file query, and issues no SQL for stock and prices.
  - `Failed_run_reloads_from_last_save` — a phase that throws after a 4-month save: `LastSavedTick` equals the save tick; the next `SettlementDetailsAsync` matches a fresh-file query at that tick.
  - `Other_readers_see_last_save` — while a run is open past a save, a second connection reads the saved tick, not the run's current tick.
  - `DM_change_refused_during_a_run` and `DM_change_between_runs_is_saved_and_visible` (a stock adjustment via `ApplyDmChangeAsync`; visible in `SettlementDetailsAsync` and in a fresh-file query).
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.** Ruling (recorded here, not in the spec): reloading after a failure is the only way to keep memory equal to the file once the transaction rolls back; the spec's "rolls back to the previous save" requires it.
- [ ] **Step 4: Run** the Application test project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Task 11: GUI on the session

**Files:**
- Modify: `src/WorldEcon.Gui/ViewModels/WorldSession.cs`, `TimeControlsViewModel.cs`, `TimeController.cs`, `CityPanelViewModel.cs`, `MainViewModel.cs` (caravan refresh), `NodeNameEditor.cs`
- Test: `tests/WorldEcon.Gui.Tests.Unit/TimeControllerTests.cs`, `TimeControlsViewModelTests.cs`, `CityPanelTests.cs`, `NodeNameEditorTests.cs`, `RunRoutingTests.cs`, `FakeRun.cs`

**Interfaces:**
- Consumes: `SimulationSession` (Task 10); `RouteNodeRenameService.RenameAsync(WorldDbContext, string, string?, CancellationToken)` (exists).
- Produces:
  - `WorldSession.Simulation` (`SimulationSession?`), opened on first use of the time bar or of a panel read, disposed when the world closes.
  - `TimeControlsViewModel` opens runs with `session.Simulation.BeginRunAsync`.
  - `TimeController` fail path: the clock (`DisplayTick` and its base) resets to the session's `LastSavedTick`, not the run's start tick.
  - Caravan refresh and city panel read `session.Simulation` when it is open (between runs too), the file otherwise. `CityPanelViewModel.CanRefresh` stays false while a run is open.
  - `NodeNameEditor` saves through `session.Simulation.ApplyDmChangeAsync((db, state, ct) => RouteNodeRenameService.RenameAsync(db, id, name, ct))` when the session is open; the in-memory `RouteNode` then holds the new name.
- [ ] **Step 1: Write the failing tests**: `Failed_run_resets_clock_to_last_save` (TimeController with a fake run reporting a save tick); `Rename_with_session_open_updates_memory_and_file` (NodeNameEditor; then a run starts and ends, and the name is still the new one in memory and in a fresh-file read); `City_panel_reads_session_between_runs`.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Gui test project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Task 12: Route upgrade and node rename logged as DM changes

**Files:**
- Modify: `src/WorldEcon.Engine/Trade/RouteUpgradeService.cs`, `src/WorldEcon.Application/Routes/RouteNodeRenameService.cs`
- Test: the existing route upgrade and rename test files (find with `grep -rl "RouteUpgradeService\|RouteNodeRenameService" tests`)

**Interfaces:**
- Produces: the `RouteUpgraded` log event emitted with `isPlayerAction: true`; a rename emits a player-action log event if it does not already (spec 8: every DM change is recorded as one). Use an existing `LogEventType` if one fits a rename; otherwise add `RouteNodeRenamed`.

- [ ] **Step 1: Write the failing tests** `Route_upgrade_is_logged_as_a_player_action` and `Node_rename_is_logged_as_a_player_action`.
- [ ] **Step 2: Run and see it fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the test's project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Checkpoint D: GUI on the session (stop for the user)

In the app, on a world created from seed 42:

- Press the 1-minute step button ten times: each press finishes without a pause to load the world.
- Open a city panel between runs: stock and prices match what the last run ended with.
- Pause, rename a route node, press Play for a day, pause: the new name is still shown; close and reopen the world: still the new name.
- Jump 2 years, kill the app part-way (for example `pkill -f WorldEcon.Gui`), reopen: the clock shows a 4-month save tick.
- CLI: `log <db> world <world name> --regex Renamed` (or the rename event type Task 12 uses) lists the rename as a player action.

### Task P4: Box selection on the map

Spec section 7.

**Files:**
- Create: `src/WorldEcon.Gui/ViewModels/SettlementSelection.cs`
- Modify: `src/WorldEcon.Gui/Views/MapView.cs` (pointer handling, box drawing), `src/WorldEcon.Gui/ViewModels/MapViewModel.cs` (selection, highlight in the overlay), `src/WorldEcon.Gui/Views/MainWindow.axaml(.cs)` (Esc, counter, **Price chart** button), `src/WorldEcon.Gui/ViewModels/MainViewModel.cs`
- Test: `tests/WorldEcon.Gui.Tests.Unit/SettlementSelectionTests.cs`, `MapSelectionTests.cs`

**Interfaces:**
- Consumes: `TimeControlsViewModel` run state (whether a run is open; Task 11).
- Produces:
  - `public enum BoxSelectMode { Replace, Toggle }`.
  - `SettlementSelection`: `IReadOnlySet<Guid> Ids`, `int Count`, `void Apply(IEnumerable<Guid> inBox, BoxSelectMode mode)` (Replace: exactly `inBox`; Toggle: flips each id in `inBox`), `void Clear()`, `event Changed`.
  - `MapViewModel.SettlementsInScreenBox(Rect box) → IReadOnlyList<Guid>`: settlements whose projected screen position is inside `box`, on the visible side of the globe only.
  - `MapView`: mode decided by modifiers at press: Shift → Replace, Ctrl+Shift → Toggle, none → pan (unchanged). The box is drawn while dragging. A Shift press with no drag applies an empty box (Replace → clears).
  - `MainWindow` Esc: clears the selection when `Count > 0`; otherwise today's behaviour.
  - `MainViewModel.CanOpenPriceChart` = no run open; the **Price chart** button binds to it. Counter text "N cities selected", hidden at 0.

- [ ] **Step 1: Write the failing tests**: `Replace_drops_earlier_selection`; `Toggle_removes_selected_and_adds_unselected`; `Empty_replace_clears`; `Far_side_settlements_are_not_in_the_box` (a settlement behind the globe at a screen point inside the box is excluded); `Esc_clears_selection_before_going_back` (first Esc clears, second Esc does today's action); `Price_chart_disabled_during_a_run`.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Gui test project → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Task P5: Price chart window

Spec section 8.

**Files:**
- Create: `src/WorldEcon.Gui/ViewModels/PriceChartViewModel.cs`, `src/WorldEcon.Gui/ViewModels/ChartViewport.cs`, `src/WorldEcon.Gui/Views/PriceChartControl.cs` (Skia), `src/WorldEcon.Gui/Views/PriceChartView.axaml(.cs)`
- Modify: `src/WorldEcon.Application/Simulation/SimulationSession.cs` (`PriceSeriesAsync`), `src/WorldEcon.Gui/ViewModels/PanelWindows.cs` (open a chart window), `src/WorldEcon.Gui/ViewModels/MainViewModel.cs`
- Test: `tests/WorldEcon.Gui.Tests.Unit/PriceChartViewModelTests.cs`, `ChartViewportTests.cs`, `PriceChartControlTests.cs` (headless); `tests/WorldEcon.Application.Tests.Unit/SimulationSessionTests.cs` (extend)

**Interfaces:**
- Consumes: `SimulationSession` (Task 10), `PriceScopeResolver`, `PriceSeriesQuery`, `CandleBuilder` (Task P2), `SettlementSelection` (Task P4).
- Produces:
  - `SimulationSession.PriceSeriesAsync(GoodId good, PriceScope scope, SaleKindFilter kind, CandleSize size, int fromDay, int toDay, CancellationToken ct) → Task<ErrorOr<IReadOnlyList<Candle>>>`: refused (an `ErrorOr` error, not an exception) while a run is open; otherwise reads the file through the session's context (every run ended with a save).
  - `ChartViewport` (pure): `int FirstDay`, `int LastDay`; `ChartViewport ZoomAt(double pointerFraction, double factor)` keeps the day under the pointer fixed; `ChartViewport Pan(int days)`; `static ChartViewport Fit(int firstDay, int lastDay)`; `CandleSize AutoSize()` = the smallest size with at most 200 candles visible.
  - `PriceChartViewModel`: scope (fixed copy of the map selection when opened from it, or regions, countries, no country, world), `Good`, `Kind`, `CandleSizeChoice` (Auto/Day/Week/Month/Year), `Viewport`, `Candles`, `RefreshCommand`, `CanRefresh` (false while a run is open), hover text built from a `Candle` (dates in the world calendar, open, close, wick high/low, sale high/low in the world currency, volume, turnover, cities).
  - `PriceChartControl`: draws candles (body open→close in two theme colours, wick low→high; no sales → body only; no open/close → wick only), the volume strip, price and date axes; wheel = `ZoomAt`, drag = `Pan`, double-click = `Fit`; reports the hovered candle.

- [ ] **Step 1: Write the failing tests**:
  - `ChartViewportTests`: `ZoomAt_keeps_pointer_day_fixed`; `AutoSize_keeps_200_or_fewer_candles` (a 3-year view → Week; a 2-month view → Day; a 20-year view → Month).
  - `PriceChartViewModelTests`: `Opened_from_selection_keeps_its_settlements` (the map selection changes after opening; the chart scope does not); `Refresh_disabled_during_a_run`; `Hover_text_lists_both_high_low_pairs`.
  - `PriceChartControlTests` (Avalonia headless): `Renders_empty_gap_and_normal_data_without_error`.
  - `SimulationSessionTests`: `PriceSeries_refused_during_a_run`; `PriceSeries_after_a_run_covers_every_day` (after a 10-day run and `EndAsync`, day candles for a priced good number 10).
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Gui and Application test projects → pass.
- [ ] **Step 5: Stop** — uncommitted for review.

### Checkpoint D2: price chart (stop for the user)

In the app, on a fresh copy of the seed-42 world advanced 1 year (CLI `advance <copy> 1y`, then open it):

- Shift+drag a box over 2–3 cities: they highlight and the counter shows the count. Shift+drag another box: only the new cities are selected. Ctrl+Shift+drag over a mix: selected ones drop out, others join. Esc clears; Esc again goes back to the globe.
- Press **Price chart**: pick a consumed good. Candles for the year; zoom with the wheel down to day candles and out to month candles (Auto); set the candle size by hand.
- Hover a candle: dates, open, close, wick and sale high/low, volume, cities.
- Switch the scope to a country, to "No country" and to the whole world; switch the sale kind.
- Press Play: the **Price chart** button and the open chart's Refresh are disabled; the chart does not change. Pause, press Refresh: the new days appear.

### Task 13: Debug guard in the CLI, whole-suite check and the after measurement

**Files:**
- Modify: `src/WorldEcon.Cli/CommandRunner.cs` (`CmdAdvance`: add `StepSqlGuard` under `#if DEBUG`, next to the existing `RunProfile` wiring)
- Modify: `docs/research/reports/Simulation speed baseline 2026-11-25.md` (add an "After in-memory state" section)

- [ ] **Step 1:** `dotnet build WorldEcon.slnx` and `dotnet build WorldEcon.slnx -c Release` → 0 warnings, 0 errors.
- [ ] **Step 2:** run every test project in `tests/` in Release → all pass; list any failure by name in the report.
- [ ] **Step 3:** Debug `advance 1M` on a copy of the seed-42 world → completes (no `"SQL during a step"`); its `RunProfile` table shows 0 SQL commands for every phase, and 1 save.
- [ ] **Step 4:** Release `advance 1y` on a fresh copy of the seed-42 world (`new <db> --seed 42`), timed as in the baseline report; time to open the world reported separately; a `dotnet-trace` of a 3-month Release advance summarised in the baseline's two tables.
- [ ] **Step 4b:** recording cost: the same Release 1-year advance through the scratch timing harness with `WorldState.PriceBars.Enabled = false`, against the run with it on; report the difference in ms per simulated day and the price bar rows written (price chart spec, section 2).
- [ ] **Step 5:** write the after numbers next to the baseline (per simulated day, per simulated year, per settlement per day, shares by library and by method).
- [ ] **Step 6: Stop** — uncommitted for review.

### Checkpoint E: measurement (stop for the user)

- Read the "After in-memory state" section of `docs/research/reports/Simulation speed baseline 2026-11-25.md`.
- Optional: `dotnet tool restore`, then `dotnet dotnet-trace collect --format Speedscope -o <file>.nettrace -- dotnet src/WorldEcon.Cli/bin/Release/net10.0/WorldEcon.Cli.dll advance <copy> 3M`, and open the `.speedscope.json` at speedscope.app.
