# Activity / Event Log — Design Spec

**Date:** 2026-06-21
**Status:** Approved (brainstorming complete; ready for implementation plan)
**Project:** WorldEcon — C#/.NET 10 world-economy simulation (`/home/kayden/workspaces/dnd`)

## Goal

Give every location/entity (continent, country, region, city/town, merchant, shop, factory) its
own queryable log of the events that *matter at that level*. A shop cares that an item was bought
or sold, to whom, and for how much; a city cares that production fell or that it gained/lost a
merchant — not about individual shop trades; large events (a settlement falling to ruin) propagate
upward by their magnitude so a country and even a continent see them.

## Summary of Decisions (from brainstorming)

1. **Surfacing model:** magnitude-driven by default, with per-event-type overrides for exceptions.
2. **Propagation:** events propagate *upward* to ancestor levels whose magnitude floor they clear
   (plus type overrides). Higher levels do **not** see lower-level detail by default.
3. **Visibility storage:** materialized at **write time** (a `LogEventScope` join row per visible
   level) — not computed at read time. High-frequency Routine events get exactly one scope row.
4. **Retention:** by magnitude tier, each tier with a max in-world age; Major/Historic and all
   player/DM events are kept forever. Archival/compression is explicitly out of scope (later).
5. **Summaries:** on-demand only — a computed report over a scope + window. Not stored, not
   configured per-location.
6. **Query (v1):** k9s-style `/` regex over message text. Structured filters deferred.
7. **Unification:** one `LogEvent` stream; the existing `DmAction` audit log is folded in
   (DM/party actions become `IsPlayerAction` events, never pruned).

## Non-Goals (v1)

- Stored/scheduled periodic summaries or per-location summary config (summaries are on-demand).
- Structured query filters (type/magnitude/time/counterparty) and any query DSL — regex only.
- Archival/compression of pruned events.
- Claim/contested-based extra visibility (a ruined contested city showing under *both* claimant
  countries) — noted as a future enhancement.
- Geography-edit events (settlement founded/renamed via forms) — those arrive with the forms
  feature; v1 wires the events the engine/CLI can already produce.

---

## Architecture & Data Model

A new aggregate **`LogEvent`** in `WorldEcon.Domain/Logging` is the single append-only stream.
Visibility is materialized via **`LogEventScope`** (one row per level the event is visible at).
The existing `DmAction` is removed and folded into `LogEvent`.

```
LogEvent (AggregateRoot<LogEventId>)
  Id              LogEventId      -- typed, hand-written (matches existing ID pattern)
  WorldId         WorldId
  Sequence        long            -- monotonic per world; deterministic ordering & tie-break
  OccurredTick    Tick            -- in-world time the event happened
  RecordedAtUtc   DateTimeOffset  -- real-world timestamp (supplied by caller)
  Type            LogEventType    -- enum, see taxonomy below
  Magnitude       LogMagnitude    -- Routine | Notable | Major | Historic
  OriginKind      LogScopeKind    -- Continent|Country|Region|Settlement|Merchant|Shop|Factory|World
  OriginId        Guid            -- raw Guid (polymorphic; deliberately NOT a typed id)
  IsPlayerAction  bool            -- DM/party origin; never pruned
  PayloadJson     string          -- structured details (who/qty/price/...); carries DmAction.ArgsJson role
  Message         string          -- human-readable

LogEventScope (AggregateRoot<LogEventScopeId>)
  Id              LogEventScopeId
  LogEventId      LogEventId      -- FK, cascade delete
  ScopeKind       LogScopeKind
  ScopeId         Guid            -- raw Guid (the entity this event is visible to)
  Sequence        long            -- denormalized from LogEvent for SQL ORDER BY (avoids typed-id translation)
  -- INDEX (ScopeKind, ScopeId, Sequence DESC)
```

### Why raw `Guid` for scope/origin ids

`ScopeId` and `OriginId` reference *any* entity kind (shop, settlement, country, …), so a typed id
won't do. Storing them as raw `Guid` (with `Sequence` as `long` and enums as `int`) keeps the hot
read fully SQL-translatable:

```
SELECT ... FROM LogEventScope
WHERE ScopeKind = @k AND ScopeId = @id
ORDER BY Sequence DESC
LIMIT @n
```

This avoids the in-memory ancestry traversal that read-time visibility would force, given the
codebase's known limitation that value-converted typed ids don't translate in SQL `WHERE`/`ORDER BY`.
Regex filtering is applied in memory on the fetched page (v1).

### Enums

```
LogMagnitude : Routine, Notable, Major, Historic            -- ordered

LogScopeKind : World, Continent, Country, Region, Settlement, Merchant, Shop, Factory

LogEventType (initial set; extensible):
  Trade, MerchantArrived, MerchantDeparted, MerchantGained, MerchantLost,
  ProductionChanged, Stockout, Spoilage, Restock,
  SettlementFounded, SettlementRuined, ClaimChanged, RouteOpened, RouteClosed,
  PartyAction
```

### Folding in `DmAction` (churn)

- Remove `DmAction` / `DmActionKind`; replace `DmActionService` with `LogEventService` that writes
  `IsPlayerAction = true` events (carrying the old `ArgsJson` as `PayloadJson`).
- Replace `DmActionConfiguration` with `LogEventConfiguration` + `LogEventScopeConfiguration`.
- `WorldDbContext.DmActions` → `LogEvents` (+ `LogEventScopes`).
- EF migration: create the new tables, **copy existing `DmActions` rows** into `LogEvent`
  (`Type = PartyAction`, `Magnitude = Major`, `IsPlayerAction = true`, origin from the action's
  target, one `LogEventScope` at the target) then drop the `DmActions` table.
- Update the few CLI/TUI/test references (e.g. `ActionTests` asserting `DmActions.CountAsync()`).

---

## Emission & Propagation

A central **`LogEventEmitter`** (in `WorldEcon.Engine`) is the only writer. Phases call it; it
computes visibility and writes the `LogEvent` plus its `LogEventScope` rows.

### Magnitude

Each `LogEventType` has a **default magnitude**. The calling phase may bump it per-instance (e.g. an
unusually large trade → Notable). Defaults (initial):

| Type | Default magnitude |
|---|---|
| Trade, Restock, Spoilage | Routine |
| MerchantArrived, MerchantDeparted, ProductionChanged, Stockout, MerchantGained, MerchantLost | Notable |
| RouteOpened, RouteClosed, ClaimChanged | Major |
| SettlementFounded, SettlementRuined | Historic |
| PartyAction | Major (player action; never pruned) |

### Level floors

Defaults (tunable constants now; config later):

| Scope level | Floor |
|---|---|
| Shop, Factory, Merchant (leaf) | Routine |
| Settlement | Notable |
| Region | Major |
| Country | Major |
| Continent | Historic |
| World | Historic |

### Propagation rule

1. The **origin** scope is always written.
2. For each **ancestor** scope of the origin (settlement → region → primary country → continent(s),
   resolved from already-loaded geography), write a `LogEventScope` **iff** the event's magnitude
   `>=` that level's floor.
3. **Per-type overrides** (independent of magnitude):
   - *Force-raise:* `ClaimChanged` is always visible at Country and above.
   - *Cap:* `Restock` never leaves the originating shop.

Worked examples:
- `SettlementRuined` = Historic → clears Region/Country/Continent floors → visible at the settlement,
  its region, its country, and its continent(s).
- `MerchantLost` = Notable → clears Settlement floor only → the city sees it; the region does not.
- `Trade` = Routine → clears no ancestor floor → exactly one scope row (the shop). This is the
  high-frequency path, so fan-out is reserved for rare big events.

### Ancestor resolution

A per-advance cache built from already-loaded geography maps an origin entity to its scope chain
(`Shop/Factory/Merchant → Settlement → Region → primary Country (if set) → Continent(s)`). A region
may belong to multiple continents (geography v2 `RegionContinent` m2m), so a high-magnitude event can
fan out to several continents. Claim/contested-based extra ancestors are a future enhancement.

### Sequence assignment

`Sequence` is assigned in emission order within an advance (phase order is deterministic), continuing
from the world's current max `Sequence`. A counter lives on `SimulationContext`, initialized from the
DB max for the world at load. This yields stable, deterministic ordering and tie-breaks.

### Emission points (v1)

| Phase / source | Event(s) | Default scope / magnitude |
|---|---|---|
| `TradePhase` | shop sale/purchase (`Trade`) | Shop / Routine |
| `TradePhase` | caravan dispatch/arrival (`MerchantDeparted`/`MerchantArrived`) | Merchant / Notable |
| `ProductionPhase` | output produced (`ProductionChanged` on threshold) | Settlement / Notable |
| `ProductionPhase` | input stockout (`Stockout`) | Settlement / Notable |
| `ConsumptionPhase` | shortage/stockout (`Stockout`) | Settlement / Notable |
| `MerchantSpawnPhase` | merchant gained/retired (`MerchantGained`/`MerchantLost`) | Settlement / Notable |
| `PerishabilityPhase` | spoilage (`Spoilage`) | Shop or Settlement / Routine |
| `LogEventService` (party/DM) | `PartyAction` | acted-on entity / Major, `IsPlayerAction` |

Structural events (`SettlementFounded/Ruined`, `ClaimChanged`, `RouteOpened/Closed`) are emitted from
the engine/CLI commands that can already cause them; form-driven geography edits emit them once the
forms feature lands.

---

## Retention (pruning)

A deterministic pruning pass runs at the **end of each advance**. An event is deleted iff:

```
!IsPlayerAction  AND  (World.CurrentTick - OccurredTick) > maxAge[Magnitude]
```

`LogEventScope` rows cascade-delete with their event.

| Magnitude | Max in-world age |
|---|---|
| Routine | 90 in-world days |
| Notable | 5 in-world years |
| Major | ∞ (never) |
| Historic | ∞ (never) |
| *(any, IsPlayerAction)* | ∞ (never) |

Ages are constants now, configurable later.

### Granularity independence

The cutoff is *age relative to the current tick*, and deletions are monotonic, so the surviving set
after `advance(2880)` equals that after `2×advance(1440)` (each intermediate prune is a subset of the
final one). This preserves the existing "advance N == k × advance(N/k)" invariant for the log itself.

### Invariant: logs never feed back into the simulation

Phases must **never read** the log to make simulation decisions. The log is write-only observability.
Therefore pruning (and the log generally) cannot affect simulation determinism, regardless of cadence.

---

## On-Demand Summaries

A read-only **`SummaryService`**. Input `(scopeKind, scopeId, fromTick, toTick)`; output a
`ScopeSummary` DTO aggregating the LogEvents visible at that scope in the window:

- counts by `LogEventType`
- net production change
- merchants gained / lost
- total trade volume (qty) and value (money)
- stockout count (and the goods involved)
- list of Major+ events in the window

Surfaced two ways:
1. The `advance` command optionally prints a "summary since last advance" for a chosen scope
   (default = world).
2. An explicit summary command/action for any scope + window ("since last", "last N days", or a range).

Summaries are computed from surviving events. **Caveat (documented behavior):** a window old enough
to have been pruned shows only what survived retention. This is acceptable because summaries target
recent windows.

---

## Query & TUI Surface

Additive to the existing k9s-style shell (`WorldEcon.Tui`) and `Navigator`:

- **`l` (log) action** on any row (continent/country/region/city/town/merchant/shop) opens a Log
  view scoped to that entity. Columns: **Time** (in-world date + tick) · **Mag** · **Type** ·
  **Message**, newest-first, paged.
- **`/` regex filter** (k9s-style) over the message text of the current log view (in-memory on the
  fetched page). Structured filters deferred.
- **`:log`** root → world-scoped events.
- **Summary** entry point (`:summary` or an action key) prompts for scope + window and shows a
  `ScopeSummary`.

### CLI parity

- `log <scope> [--since <when>] [--regex <pattern>]`
- `summary <scope> [--from <when>] [--to <when>]`

(`<scope>` identifies an entity; `<when>` accepts in-world date or tick.)

---

## Determinism & Testing

- New typed ids `LogEventId` / `LogEventScopeId` (hand-written, matching the existing pattern). Noted
  under the pending source-gen-ID refactor. `OriginId`/`ScopeId` stay raw `Guid` deliberately.
- `Sequence` makes ordering deterministic; logs never feed back into the sim (asserted invariant).

### Tests

- **Emission per phase:** a `Trade` writes exactly one Shop-scoped Routine event; a `SettlementRuined`
  writes a Historic event visible at the continent.
- **Propagation floors:** magnitude → correct set of `LogEventScope` rows (origin + qualifying
  ancestors); per-type override force-raise (`ClaimChanged` at Country+) and cap (`Restock` shop-only).
- **Retention:** events past their tier age are pruned, Major/Historic and player events survive, and
  `advance(2880)` surviving set == `2×advance(1440)` surviving set (granularity independence).
- **Summary aggregation:** counts/totals/net-change correct over a window for a scope.
- **Regex filter:** filters the rendered log rows.
- **DmAction migration:** existing DM actions become `IsPlayerAction` LogEvents after migration.
- **TUI smoke:** `l` opens a scoped log view; `/` filters it.

---

## Implementation Order (suggested for the plan)

1. Domain: `LogEvent` + `LogEventScope` aggregates, enums, typed ids, factories.
2. Persistence: EF configs, DbSets, migration (incl. `DmAction` → `LogEvent` data copy + drop).
3. Engine: `LogEventEmitter` (magnitude defaults, floors, overrides, ancestor cache, sequence) +
   `SimulationContext` sequence counter; replace `DmActionService` with `LogEventService`.
4. Engine: wire emission into each phase (Trade, Production, Consumption, MerchantSpawn,
   Perishability) and structural events.
5. Engine: retention pruning pass at end of advance.
6. Application: `SummaryService` + `ScopeSummary`.
7. CLI: `log` and `summary` commands.
8. TUI: `l` log view, `/` regex filter, `:log` root, summary entry point.
9. Tests throughout (TDD per task).
