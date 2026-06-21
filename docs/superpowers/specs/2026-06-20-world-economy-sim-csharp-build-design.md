# World Economy Simulation — C# Build Spec

**Status:** Build specification (implementation blueprint).
**Derived from:** `world-economy-sim-spec.md` (the design spec / decision log). Where this document says "§N", it refers to that source spec unless prefixed with "Build §".
**Scope of this document:** the concrete C#/.NET technical blueprint a coding agent builds from, covering **MVP roadmap stages 1–5** (data model + world editor, static economy, tick engine, party effects, Kanka import) plus the **architectural seams** for deferred features. Factions (§10) and Events (§11) are **OPEN** in the source spec and are **not built** here — only their seam interfaces exist.
**Last updated:** 2026-06-20

---

## Build §0. How to read this

- The source spec decides *what* and *why*. This document decides *how* in C#: solution layout, conventions, concrete interfaces/entities, the engine and persistence design, and a test plan.
- Code shown is illustrative of the intended shape (signatures, names, patterns), not final implementation.
- Conventions mirror the author's existing `projekter_backend` solution so the two codebases feel the same, with deliberate divergences called out in Build §2.4.

---

## Build §1. Tech stack

| Concern | Choice |
|---|---|
| Language / runtime | C# / **.NET 10**, `nullable enable`, `ImplicitUsings enable`, file-scoped namespaces |
| Simulation core | UI-agnostic class libraries (no EF, no UI deps) |
| Persistence | **EF Core + SQLite** (local, file-based, offline) |
| UI | **Avalonia** (cross-platform), **MVVM** via **CommunityToolkit.Mvvm** |
| DI | `Microsoft.Extensions.DependencyInjection`; per-module `AddXxx()` extension methods |
| Domain results | **`ErrorOr<T>`** |
| Tests | **TUnit + FluentAssertions** |
| Pathfinding | hand-rolled Dijkstra/A\* (QuikGraph acceptable later) |
| Determinism math | integer / fixed-point only in the sim path (no `double`, no `decimal` in sim state) |
| RNG | in-house version-pinned PRNG (PCG / xoshiro256\*\*), **not** `System.Random` |

Central package management via `Directory.Packages.props`; shared build props via `Directory.Build.props` (TargetFramework, Nullable, LangVersion).

---

## Build §2. Conventions (house style)

### 2.1 Adopted from `projekter_backend`
- **Modular layering** per bounded context: `Domain` / `Application` / `Infrastructure` / `Contracts`. (We collapse some for a desktop app — see Build §3.)
- **Strongly-typed IDs** as source-generated `readonly partial record struct` with a textual prefix (e.g. `settlement_…`). One `IdDefinitions.cs` declares them all.
- **EF Core Fluent API only** (`IEntityTypeConfiguration<T>`), no data annotations driving the schema.
- **Domain base classes**: `Entity<TId>`, `AggregateRoot<TId>` with in-process domain events; `ErrorOr<T>` returns from domain methods.
- **Per-module DI extension methods** (`AddWorldEconCore()`, `AddWorldEconPersistence()`), no assembly scanning.
- **Extension-method DTO mapping** (`ToResponse()` / `ToEntity()`), no AutoMapper.
- **Source-generated logging** (`[LoggerMessage]`).
- **Test naming**: `XxxTests` classes, `Method_State_Expected()` methods, FluentAssertions.

### 2.2 Strongly-typed IDs
```csharp
[GenerateId("settlement_")] public readonly partial record struct SettlementId;
[GenerateId("good_")]       public readonly partial record struct GoodId;
[GenerateId("route_")]      public readonly partial record struct RouteId;
[GenerateId("agent_")]      public readonly partial record struct AgentId;
// … one per aggregate
```
EF converts each via a generated `ValueConverter` registered in `ConfigureConventions`.

### 2.3 Money & quantities — fixed-point integers
All quantities are integers (§7.4). Money is a fixed-point integer value type to keep arithmetic deterministic and exact:
```csharp
/// Money stored as an integer number of the smallest currency unit (e.g. copper).
/// Display/denominations (gp/sp/cp) are a presentation concern, never used in sim math.
public readonly record struct Money(long Units) {
    public static readonly Money Zero = new(0);
    public static Money operator +(Money a, Money b) => new(a.Units + b.Units);
    public static Money operator -(Money a, Money b) => new(a.Units - b.Units);
    public static Money operator *(Money a, long q)   => new(a.Units * q);
    // Division uses explicit rounding helpers (see Build §4.4) — no implicit truncation in sim logic.
}
```
Percentages, elasticities, and markups are represented as integer **basis points** (1 bp = 1/10000) or fixed-point `Q` values, never `double`. A shared `FixedMath` helper provides deterministic mul/div/pow-by-integer with explicit rounding.

### 2.4 Deliberate divergences from `projekter_backend`
- **No MassTransit / RabbitMQ / transactional outbox.** Single-process desktop app; domain events dispatch **in-process** via an EF `SaveChanges` interceptor (the backend's `DomainEventDispatcherInterceptor` pattern, minus the broker).
- **SQLite + integer in-world time**, not Postgres + NodaTime `Instant`. NodaTime cannot model the custom in-world calendar (its `CalendarSystem` is sealed/non-extensible), and sim time is integer ticks, not wall-clock. Real-world save metadata uses plain `DateTimeOffset.UtcNow`.
- **One application-level `WorldDbContext`** (not one DbContext per module) — a desktop save is a single coherent world; cross-aggregate queries are common (the price/margin view joins shops, stockpiles, caravans).

---

## Build §3. Solution & project layout

```
WorldEcon.sln
src/
  WorldEcon.SharedKernel        # Entity<TId>, AggregateRoot<TId>, IDomainEvent, IPin,
                                #   strongly-typed-id source generator, Money, FixedMath, ErrorOr glue
  WorldEcon.Domain              # all sim entities + value objects by aggregate. NO EF, NO UI.
  WorldEcon.Simulation          # tick engine, phases, cadences, scheduler, RNG streams,
                                #   pricing/production/trade/pathfinding logic, calendar. NO EF, NO UI.
  WorldEcon.Persistence         # EF Core + SQLite: WorldDbContext, Fluent configs, migrations,
                                #   repositories, projection writer, snapshot/branch/compare services
  WorldEcon.Application         # use-cases: world editing, queries (price/margin), DM actions,
                                #   simulation orchestration (advance N ticks), Kanka import
  WorldEcon.Seeding             # JSON/YAML seed format load/save; ISeedSource; Kanka REST client
  WorldEcon.App                 # Avalonia UI (MVVM). Thin. Depends on Application only.
tests/
  WorldEcon.Domain.Tests.Unit
  WorldEcon.Simulation.Tests.Unit       # economic invariants + determinism
  WorldEcon.Persistence.Tests.Unit      # snapshot/branch/compare, projection round-trip, migrations
  WorldEcon.Application.Tests.Unit
```

**Dependency flow** (enforced by project references):
```
App → Application → { Simulation, Persistence, Seeding } → Domain → SharedKernel
                         Simulation → Domain (only)
                         Persistence → Domain (+ EF)
```
The **core** = `Domain` + `Simulation`: depends on neither EF nor UI, so it is unit-testable in isolation and the deferred features slot in behind seams without touching the UI or storage (source §3.1, §3.4).

**Why `Simulation` is separate from `Domain`:** `Domain` holds state + invariants (pure data and aggregate rules); `Simulation` holds the *behavior over time* (the tick loop, pricing math, agent strategies). Keeping them apart means the determinism/RNG/scheduler machinery never leaks into entity definitions, and the engine can be tested against hand-built domain fixtures with no persistence.

---

## Build §4. Determinism & state architecture (the backbone)

This is the most important section. The architecture is a **hybrid**: action-log source-of-truth (event-sourcing) + SQLite as a rebuildable queryable projection + an in-memory integer tick loop. It satisfies source §3.3 (determinism, snapshots, branch/compare) and §14.3 (pins).

### 4.1 The three layers

1. **Source of truth = world seed + ordered action log.**
   World state is a pure function of `(seed, orderedActionLog, rulesetVersion)`. The only inputs to the world are:
   - the immutable **world seed** + **`CalendarDefinition`** + initial **seed data**,
   - an append-only, totally-ordered log of **`DmAction`** entries (party effects, §13) and **`AdvanceTime`** entries.
   Pins/overrides (§14.3) are entries in this log, re-asserted as per-tick constraints (Build §4.5).

2. **Engine = in-memory deterministic tick loop.**
   The active world is held in RAM as domain POCOs (`WorldState`) for a fast tick loop using integer/fixed-point math and version-pinned per-subsystem RNG streams. This is the Memory-Image pattern used *correctly* — backed by the log, never the persistence format.

3. **Persistence/query = SQLite as a rebuildable read-model (projection).**
   After each tick batch, the engine **synchronously** projects `WorldState` into SQLite so the DM gets full ad-hoc relational queries (the price/margin view, §15). The SQLite file is **disposable** — always reconstructible from the log. Single-process + synchronous projection ⇒ no eventual-consistency complexity.

### 4.2 Snapshots, branches, compare

- **Snapshot** = a consistent file-copy of the world DB at a tick, taken with **`VACUUM INTO 'snapshot.db'`** (portable default; produces a compacted, transactionally consistent copy). A snapshot stores the materialized projection **plus** the log position and each RNG stream's position. Loading a snapshot = open the file; **no replay**.
- **Branch** = fork the log (O(1): record `parentSnapshotId + forkTick`) + clone the nearest snapshot DB, then append divergent `DmAction`s and run forward. This is Fowler's Parallel Model.
- **Compare** = row-level diff between two branch DBs via **`sqldiff`** / the SQLite **session/changeset** extension, surfaced as an economic diff ("granary city: grain −4,200, bread +38%, 2 caravans rerouted"). Requires every table to have an explicit primary key (we enforce this).
- **Replay bound:** snapshots are taken every *N* ticks (configurable) or when replay would exceed a latency budget; a snapshot is a disposable optimization that must equal a from-log replay to that tick (a built-in integrity check).

Interfaces:
```csharp
public interface ISnapshotService {
    Task<SnapshotId> CaptureAsync(WorldId world, Tick at, CancellationToken ct);
    Task RestoreAsync(SnapshotId snapshot, CancellationToken ct);
}
public interface IBranchService {
    Task<WorldId> BranchAsync(SnapshotId from, string label, CancellationToken ct);
}
public interface ICompareService {
    Task<WorldDiff> CompareAsync(SnapshotId a, SnapshotId b, CancellationToken ct);
}
```

### 4.3 Determinism rulebook (mandatory, applies everywhere)
1. **Integer / fixed-point only** in the sim path. No `double`/`float` for any state that affects outcomes; no `decimal` in hot sim math. (Float arithmetic is not portably reproducible.)
2. **In-house version-pinned PRNG**, never `new Random(seed)` (its sequence is not stable across .NET versions). **Separate RNG streams per subsystem** (pricing, production, trade, events) so adding a draw in one subsystem doesn't shift another's sequence; persist each stream position in snapshots.
3. **No order-dependent nondeterminism.** Never drive logic or RNG draws from `Dictionary`/hash iteration order. Iterate by an explicit, stable sort key (typically the entity's strongly-typed Id). Every EF query whose result order affects state carries an explicit, fully-unique `OrderBy`.
4. **Reproduction key = `(seed, rulesetVersion)`.** Stamp every save with the engine/ruleset version; an old save replays identically only under its original ruleset (or via an explicit upcaster). Integration-test loading + replaying real old saves on every release.
5. **Build determinism caveat documented:** guarantees hold against an identical binary on the same machine; this is acceptable for a single-user desktop tool.

### 4.4 Rounding policy
All sim divisions go through explicit helpers (`FixedMath.DivFloor`, `DivRound`, `MulDiv`) with a single documented rounding mode (round-half-to-even by default), so price/cost computations are reproducible and auditable. No implicit integer truncation in economic logic.

### 4.5 Pins / overrides (§14.3)
```csharp
public interface IPin {
    PinTarget Target { get; }        // (entity, property) e.g. shop price, border state, trader position
    bool AppliesAt(Tick tick);       // pins can be permanent or time-bounded
    void Enforce(WorldState state);  // re-asserts the locked value/state this tick
}
```
Pins are applied as the **final step of every tick** (after all subsystems), guaranteeing a pinned value is the value the DM sees regardless of simulation drift. Pins are created from `DmAction`s, so they live in the deterministic log.

---

## Build §5. Time & calendar model

### 5.1 Base tick = one in-world minute
World time is `long TotalMinutesSinceEpoch` (a `Tick`). int64 range is effectively unbounded.

| Unit | Ticks |
|---|---|
| minute | 1 |
| hour | 60 |
| day | 1,440 |
| week | 10,080 |
| month / year | computed from `CalendarDefinition` (not a fixed constant) |

- **Default DM advance step = 10 ticks (10 minutes)**, configurable; the DM may advance any tick count ("advance 1 hour / 3 days / to next dawn").
- `Tick` is a value type with helpers (`AddMinutes`, `AddDays`) and comparisons.

### 5.2 Configurable calendar
The calendar is **data-driven** so any world (Gregorian, 12×30, etc.) is just config:
```csharp
public sealed record CalendarDefinition(
    int MinutesPerHour,                 // default 60
    int HoursPerDay,                    // default 24
    IReadOnlyList<MonthDef> Months,     // default: 12 × { Days = 30 }  → 360-day year
    IReadOnlyList<string> Weekdays,     // default: 7 placeholder names
    CalendarDate Epoch,                 // what Tick 0 maps to (sets "what day/year it is")
    string EraLabel,                    // e.g. "DR"
    LeapRule LeapRule,                  // default None
    IReadOnlyList<SeasonDef> Seasons);  // month/day ranges → seasonality (§7.8)

public sealed record MonthDef(string Name, int Days);
public readonly record struct CalendarDate(int Year, int Month, int Day, int Hour, int Minute);
```

**Default for this world:** 12 months × 30 days (**360-day year**), 7-day week, 24h/60min, **placeholder names** (`Month 1…12`, `Day 1…7`) renamed later. `LeapRule = None`.

### 5.3 `CalendarSystem` (in-house, no NodaTime)
A small, integer-only, deterministic converter built from a `CalendarDefinition`:
```csharp
public sealed class CalendarSystem {
    public CalendarSystem(CalendarDefinition def);
    public CalendarDate ToDate(Tick tick);
    public Tick ToTick(CalendarDate date);
    public int WeekdayIndex(Tick tick);
    public SeasonDef SeasonAt(Tick tick);
}
```
Implementation: minutes → (days, intraday) by division; days → (year, dayOfYear) via `daysPerYear` (sum of month lengths, with optional leap rule); dayOfYear → (month, day) via a cumulative-offset table; weekday via modulo over `Weekdays.Count`. Uniform 30-day months make this nearly pure division/modulo. This class is a primary determinism test target (round-trip `ToTick(ToDate(t)) == t` across ranges).

> Rationale captured in design discussion: NodaTime's `CalendarSystem` is sealed and not third-party-extensible (real-world calendars only), so a custom fantasy calendar must be in-house. The integer-minute timeline needs no date library, and owning the calendar keeps it deterministic and culture/timezone-free.

### 5.4 Cadences & scheduler
- **Cadences** (subsystem run frequency, in ticks, configurable world params): pricing & trade **daily** (1,440), merchant recompute **weekly** (10,080), seasonal checks on **month boundaries**. Gated by `tick % cadence == 0`, so minute-resolution time costs nothing for coarse subsystems.
- **Scheduler**: long-running effects (caravans, production batches, "detained" agent states) register a **completion `Tick`** and resolve exactly then — no per-tick polling. Travel time = `distance ÷ speed` in integer ticks, so partial-day travel falls out naturally (a 320-minute caravan arrives mid-next-morning).
```csharp
public interface ITickScheduler {
    void Schedule(Tick at, ScheduledEffect effect);
    IReadOnlyList<ScheduledEffect> DueAt(Tick tick);   // returned in a deterministic stable order
}
```

---

## Build §6. Domain model

Organized by aggregate (source §4). All entities derive from `Entity<TId>` / `AggregateRoot<TId>`; all quantities integer; all money `Money`.

### 6.1 SharedKernel base types
```csharp
public abstract class Entity<TId> where TId : struct, IEquatable<TId> {
    public TId Id { get; }
    protected Entity(TId id) => Id = id;
}
public abstract class AggregateRoot<TId> : Entity<TId> where TId : struct, IEquatable<TId> {
    private readonly List<IDomainEvent> _events = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _events.AsReadOnly();
    protected void Raise(IDomainEvent e) => _events.Add(e);
    public void ClearEvents() => _events.Clear();
}
```
Audit fields use sim `Tick` (in-world time) where meaningful; real-world save timestamps live on the `World`/snapshot rows as `DateTimeOffset`.

### 6.2 Geography (source §4.1, §9)
- `World` — root: seed, `CalendarDefinition`, current `Tick`, global parameters, rulesetVersion.
- `Continent`, `Country`, `Region` — jurisdictions (Country/Region can declare laws).
- `Settlement` — name, type, **display coordinates**, population, parent region/country/continent, demographics, stockpile, production nodes, shops.
- `Route` — **directed edge**: from, to, distance, terrain, danger, `RouteCategory { Land, ShippingLane }`. Authoring tool creates both directions by default with shared properties; special roads override per-direction (source §9.2).

### 6.3 Demographics (source §4.2, §7.7)
- `Race` — name + per-industry productivity modifiers (integer basis points).
- `SettlementDemographic` — (settlement, race, percentage) → consumption mix + labor productivity.

### 6.4 Economy (source §4.3, §6, §7)
- `Good` — name, category, **base unit**, **size/volume class**, **shelf-life** (perishability, in ticks), **divisible/discrete** flag.
- `Stockpile` — (owner, good, quantity, **weighted-average cost basis** as `Money`). Owner = settlement market / shop / agent. Enforces non-negative quantity (invariant).
- `ProductionNode` — (settlement, facility type, recipes, throughput cap). Pulls inputs from / deposits outputs to local stockpile; is a **buyer** (industrial demand, §7.6).
- `Recipe` — inputs list → outputs list, labor cost, ticks-to-produce, required facility type, **seasonal profile** (§7.8). Same good may appear in both lists (engine nets it).
- `WorkOrder` (Batch) — in-progress run: startTick → completeTick, committed inputs.
- `Shop` — (settlement, inventory, markup policy). Sells at `cost_basis × (1 + markup)`; holds a **till** (`Money`) and can run dry (§6.3).
- `Caravan` — origin, destination, path, departTick, arriveTick, owning agent, cargo (goods + cost basis).
- `ResourceEndowment` — (settlement, raw good, abundance) → gates & scales raw extraction (§7.5).

### 6.5 Agents (source §4.4, §8)
- `IMerchantAgent` — shared contract (Build §7.1).
- `RepresentativeMerchant` — seated in a settlement; population-spawned (§8.1).
- `HeroicTrader` — authored individual; ability list; movement pattern; legality disposition.

### 6.6 Jurisdiction & rules (source §4.5, §12)
- `LegalityRule` — (jurisdiction, good) → `{ Legal, Restricted, Banned }` + enforcement profile.
- `BorderControl` — `{ Open, Restricted, Sealed }` on a frontier / crossing edges.
- `AccessPermission` — (agent, jurisdiction) grant; gates sealed/restricted borders & faction markets.

### 6.7 Authoring metadata (source §4.8)
Every authored value carries `Provenance { Authored, Derived }` + optional pin reference, so the model always knows DM canon vs. simulation-evolved state.

### 6.8 Seams only (NOT built): Factions (§4.6/§10), Events (§4.7/§11)
Define the seam interfaces (Build §7) and leave the systems unimplemented. Do not invent faction/event mechanics — they are OPEN in the source spec.

---

## Build §7. Extension seams (source §3.2)

These interfaces exist from day one so deferred features slot in without refactoring.

### 7.1 Trading strategy
```csharp
public interface IMerchantAgent {
    AgentId Id { get; }
    SettlementId Seat { get; }
    Money Capital { get; }
    CargoHold Cargo { get; }
    TraderDisposition Disposition { get; }     // law-abiding vs smuggler + risk tolerance (§12.5)
}
public interface ITradingStrategy {
    // Survey reachable markets, decide caravans by expected profit vs transport + risk (§8). 
    IReadOnlyList<TradeDecision> Decide(IMerchantAgent agent, MarketView view, IRng rng);
}
```
Frictions (§8.2) are parameters on the agent/strategy: limited reach, capital/capacity caps, risk aversion, lag. Reps and heroic traders share this contract.

### 7.2 Trader abilities (heroic traders, §8.5)
```csharp
public interface ITraderAbility {
    // Each ability hooks one phase of the trade loop and bends one friction; parameterized.
    void OnSurvey(SurveyContext ctx);     // e.g. clairvoyance widens reach
    void OnRoute(RouteContext ctx);       // e.g. teleport/flight ignore graph/terrain
    void OnCapacity(CapacityContext ctx); // e.g. bag of holding raises cap
    void OnRisk(RiskContext ctx);         // e.g. stealth discounts edge danger
    void OnPrice(PriceContext ctx);       // e.g. master haggler improves spread
}
```
A heroic trader = a normal agent + a list of composable `ITraderAbility` instances, each parameterized (so dozens of named NPCs need no per-character code).

### 7.3 Enforcement (abstract risk-cost now; entities deferred, §12.3/§12.4)
```csharp
public interface IEnforcementThreat {
    JurisdictionRef Source { get; }
    int DetectionChanceBp { get; }        // basis points
    EnforcementReach Reach { get; }
    Consequence Resolve(IMerchantAgent agent, IRng rng);  // arrest/detain, confiscate, exclude, fine
}
```
A smuggler faces the **union** of applicable threats (no precedence collapsing, §12.3). The "detained" agent state is a first-class agent status reused here. Simulated enforcer *entities* are deferred behind this seam.

### 7.4 Cost-basis valuation
```csharp
public interface ICostBasisValuation {
    Money OnDeposit(Stockpile target, int qty, Money incomingCostBasis);  // weighted-average now
    Money AllocateByproduct(Money totalInputCost, IReadOnlyList<OutputLine> outputs); // §7.3
}
```
Weighted-average implementation now; per-lot/FIFO promotable later behind this interface.

### 7.5 Faction / Event seams (OPEN — declare, do not implement)
```csharp
public interface IJurisdiction { /* legality + market-access surface (§10 decided bits) */ }
public interface IWorldEvent  { /* time-bounded modifiers on production/routes/demand (§11) */ }
```
These compile and are referenced by enforcement/border code, but have no concrete implementations in the MVP.

---

## Build §8. Simulation engine (source §5)

### 8.1 Tick loop
```csharp
public interface ITickEngine {
    void Advance(WorldState state, int ticks, IActionLog log);
}
```
Each tick, in **fixed order** (source §5.1), gated by cadence where noted:
1. **Production** — advance batches; deposit completed outputs; start new batches if inputs committed (§7).
2. **Consumption** — population demand (by demographics) + **industrial demand** (production-node inputs) draw from local stock (§7.6).
3. **Pricing** *(daily cadence)* — recompute market/shop prices from supply vs demand + markup (§6.2).
4. **Trade** *(daily cadence)* — agents survey reachable markets, decide caravans, depart; scheduler advances in-flight caravans; arrivals deposit cargo (§8).
5. **Events** — `IWorldEvent` modifiers *(seam only; no-op in MVP)*.
6. **DM actions** — logged party effects resolved against current state (§12, §13).
7. **Pins** — re-assert all applicable pins (Build §4.5).

Subsystem cadences (Build §5.4) mean most minute-ticks only run production/consumption/scheduler-due-effects + pins; pricing/trade fire on day boundaries.

### 8.2 RNG
```csharp
public interface IRng { ulong NextULong(); int NextInt(int maxExclusive); }
public interface IRngStreams {
    IRng For(RngStream stream);   // Pricing, Production, Trade, Events — independent streams
}
```
Streams seeded deterministically from the world seed + a stream constant; positions persisted in snapshots.

### 8.3 "Lag is a feature" (source §5.2)
Production batches and caravan travel span many ticks via the scheduler — do not collapse. This is what makes shocks ripple rather than teleport.

---

## Build §9. Economy & pricing (source §6)

### 9.1 Pricing (integer/fixed-point form of §6.2)
```
scarcity_bp   = demand * 10000 / max(supply, 1)
mult_bp       = clamp( FixedMath.PowBp(scarcity_bp, elasticity_bp), min_mult_bp, max_mult_bp )
market_price  = FixedMath.MulDivBp(base_value, mult_bp)
shop_price    = cost_basis + FixedMath.MulBp(cost_basis, markup_bp)   // markup flexes w/ scarcity & competition
```
`elasticity_bp`, `min_mult_bp`, `max_mult_bp`, markup ranges are tunable **world parameters** (OPEN values in source §6.2; expose as config). Demand = population consumption + industrial input needs (§7.6).

### 9.2 Money model (source §6.3)
Merchants & shops hold finite `Money` tills and pay for goods (can run dry). Population is abstract demand from demographics — not walleted.

### 9.3 Cost basis & margin (source §6.4 — first-class)
- Every stockpile tracks weighted-average cost basis via `ICostBasisValuation`.
- **Margin is a first-class read**: for any shop/good return `{ salePrice, costBasis, marginAbs, marginBp }`. Surfaced directly in the price/margin view (§15).
- Byproduct cost allocation (§7.3): `primary_cost = total_input_cost − Σ recovered_byproduct_value`; multi-primary split by relative sale value.

---

## Build §10. Production (source §7)

- **Recipes** = inputs list → outputs list + labor + ticks + facility (§7.1). Gross-committed inputs (fully reserved to start a batch), recovered outputs, **implicit waste** (mass not conserved), same good allowed both sides (netted on settlement) (§7.2).
- **Integer quantities + per-good base unit** (§7.4); divisible/discrete flag enforces whole-unit outputs.
- **Resource endowments** gate & scale raw extraction (§7.5).
- **Industrial demand**: production nodes bid for inputs locally, raising scarcity (§7.6).
- **Labor**: per-settlement soft pool from population; production competes for it; demographic productivity modifiers apply (§7.7).
- **Seasonality**: per-recipe seasonal profile vs the calendar; **hook everywhere, populate agriculture first**; mines/smithies year-round (§7.8).
- **Perishability**: per-good shelf-life; stockpiles decay over ticks → food stays regional, metals/luxuries travel (§7.9).
- **Deferred (seams only):** resource depletion, quality tiers (§7.10).

---

## Build §11. Trade & agents (source §8)

- **Representative merchants** seated per settlement; count `max(1, floor(pop / ratio))`, `ratio` default **10,000** (world param) × optional per-settlement multiplier; recomputed **weekly** as a soft target (spawn/retire on threshold crossing); **in-flight caravans finish before retirement** (§8.1).
- **Frictions** (§8.2) bound every agent so each closes only a *fraction* of a price gap per tick.
- A **small number of regional reps**, not one global trader (§8.3).
- **Caravans** carry goods + cost basis along graph paths; arrivals blend cost basis via weighted-average; failed/ambushed caravans originate supply shocks (§8.4).
- **Heroic traders** (§8.5–8.6): authored, additive; composable parameterized abilities (Build §7.2); autonomous-by-default with DM pins; movement patterns `{ Seated, Circuit, Opportunistic }`; require good size/volume class; seeded from Kanka Characters.

---

## Build §12. Geography & travel (source §9)

- Directed road graph stored as **relational node/edge tables** (Settlements, Routes) in SQLite — no graph DB.
- **In-memory adjacency built at load**, cached, invalidated on road edit. Coordinates display-only; travel time from the graph (distance ÷ speed → integer ticks).
- **Danger is a live per-edge weight** (events change it); applied as a live modifier over distance-based paths, not baked into a cached path.
- **Pathfinding**: hand-rolled Dijkstra/A\* weaving in danger + merchant risk-aversion; QuikGraph acceptable later.
```csharp
public interface IPathfinder {
    Path? FindPath(SettlementId from, SettlementId to, IEdgeWeight weight, AgentAccess access);
}
```
`AgentAccess` makes sealed crossing edges impassable for agents lacking `AccessPermission` (per-agent closure, §12.2).
- **Inter-continental shipping lanes** = `RouteCategory.ShippingLane`: long, danger-prone, few chokepoint ports (§9.4).

---

## Build §13. Contraband, borders & enforcement (source §12)

- **Legality overlay** (§12.1): `(jurisdiction, good) → {Legal, Restricted, Banned}`, all four jurisdiction levels can declare. Illegal = supply suppressor + price multiplier + risk premium → fat illegal margin in the margin display. Support both underground production nodes and smuggling-in.
- **Borders** (§12.2): `BorderControl {Open, Restricted, Sealed}`; per-agent access via `AccessPermission`; sealed regions still simulate internally (prices float free; opening ⇒ arbitrage shock); seed-and-pin closure.
- **Enforcement** (§12.3): **stacking independent threats** via `IEnforcementThreat` (no precedence). Consequence by jurisdiction **type**: Country = arrest → **detained** N ticks + confiscate (+ optional fine/bounty); City = local confiscation/fine/expulsion; Region = admin-middle; Faction = exclusion (revoke `AccessPermission` + reputation hit). "Detained" agent state is first-class.
- **Trader legality disposition** (§12.5): law-abiding vs willing-to-smuggle + risk-tolerance scalar on the agent.
- **Free-port rule** (§12.6, PROPOSED): local permission lowers (not erases) a broader threat's local detection chance. Build to this; make it a toggle so the alternative (full local override) is configurable.
- **Enforcement entities deferred** (§12.4) behind `IEnforcementThreat`.

---

## Build §14. Persistence (source §14.1)

- **`WorldDbContext`** (one per world/save file), EF Core + SQLite, Fluent configs (`IEntityTypeConfiguration<T>` per aggregate), strongly-typed-id converters in `ConfigureConventions`, enums as strings, **every table has an explicit PK** (required for `sqldiff`/changesets).
- **Migrations** via EF Core, timestamp-named.
- **Repositories** per aggregate (`ISettlementRepository`, …), not generic `IRepository<T>`.
- **Projection writer** (`IWorldProjection`) writes `WorldState` → SQLite synchronously after a tick batch.
- **Snapshot/branch/compare** services (Build §4.2): `VACUUM INTO`, file clone, `sqldiff`/session changesets.
- **Domain events** dispatched via a `SaveChanges` interceptor (in-process).
- **Action log** persisted as an append-only, ordered table (`DmActionLog`) that is part of the deterministic record; it survives in every snapshot and is the source of truth on replay.
- **SQLite hygiene**: WAL mode with checkpoint before file-copy snapshots; periodic `VACUUM` of the working DB; pin a known-good SQLite version.

---

## Build §15. Application layer & DM interaction (source §13, §15)

Use-cases as handlers (records in / `ErrorOr<T>` out), invoked by the UI:
- **World editing** — CRUD continents/countries/regions/settlements/routes/races/demographics/goods/recipes/shops.
- **Price/margin query** — the core DM read: `(good, settlement) → [ shop { price, stock, margin(sale/cost/%), nextShipment } ]`, where nextShipment = next inbound caravan or completing batch. **Works at roadmap stage 2** (static economy), before the tick engine.
- **DM actions** (§13) — the only channel for party effects; each appended to the log and resolved deterministically:
  - "party bought N of good in shop" → decrement stock → supply drops → price rises until restock.
  - "party destroyed stockpile / disabled node."
  - "party opened/sealed border" → flip `BorderControl`.
  - The party is mechanically just another agent pulling/pushing local supply.
```csharp
public interface IDmActionHandler {
    ErrorOr<Success> Apply(DmAction action, WorldState state, IActionLog log);
}
```
- **Simulation orchestration** — `AdvanceAsync(int ticks)`, snapshot, branch, compare; each `AdvanceTime`/`DmAction` is a logged entry.

---

## Build §16. Seeding & Kanka import (source §14.1–14.2)

- **Seed format**: JSON/YAML for towns, routes, races, demographics, factions, goods, shops, starting stockpiles/prices, plus the `CalendarDefinition` and world seed. The author's campaign is just *a* seed world.
```csharp
public interface ISeedSource { Task<SeedWorld> LoadAsync(CancellationToken ct); }
```
- **Kanka import** — built as two layers behind `ISeedSource`, **structural first, economic second** (§14.2):
  1. **Structural map**: Kanka Location → Settlement, Organisation → Faction (seam), location tree → region/continent hierarchy, Character → HeroicTrader candidate.
  2. **Economic extraction**: read canon prices/stock from structured custom attributes/notes.
  - **Target the REST API** (live re-sync by campaign ID) with the file-based seed format as the local fallback. The HTTP client is layered on top of `ISeedSource`; the JSON/YAML loader ships first so import is testable without network.

---

## Build §17. UI (Avalonia MVVM, source §15 — phased)

Thin MVVM (CommunityToolkit.Mvvm: `[ObservableProperty]`, `[RelayCommand]`); ViewModels depend on Application use-cases only.
1. **World editor** — author/edit all geography, demographics, economy entities + the `CalendarDefinition`. Persists.
2. **Price/margin query view** — good + settlement → shops with price, stock, margin, next shipment. (Usable at stage 2.)
3. **Map view** — settlements at coordinates, routes as edges; later overlays (danger, faction control, sealed borders).
4. **Simulation controls** — advance N ticks (default step 10 min), pause, snapshot, branch, compare; current in-world date via `CalendarSystem`.
5. **DM action panel** — log party effects.
6. **Agent/trade inspector** — caravans in transit, merchant capital, heroic traders.

Specific widget layouts remain OPEN (source §15).

---

## Build §18. Testing plan (source §3.4)

TUnit + FluentAssertions. The core is deterministic and UI-agnostic ⇒ unit-tested in isolation.

**Economic invariants (must have tests):**
- Stockpile quantity never negative; cost-basis accounting correct on blended deposits.
- Caravan conservation: goods leaving origin == goods arriving (minus modeled loss), no teleport.
- Byproduct cost allocation: worked example `100 iron → plate + 20 iron` yields plate cost basis 8gp (§7.3).
- Margin read = sale − cost; basis-point margin correct.
- Production gross-commit: a batch can't start without full inputs on hand.
- Perishability decays stockpiles on schedule; pins override drift every tick.

**Determinism:**
- Same `(seed, rulesetVersion, actionLog)` ⇒ byte-identical projection (golden-master over a multi-day run).
- Snapshot == from-log replay to the same tick.
- `CalendarSystem` round-trips: `ToTick(ToDate(t)) == t` across multi-year ranges incl. month boundaries.
- RNG stream isolation: adding a draw in one stream doesn't shift another.

**Persistence:**
- Projection round-trip; snapshot/branch produces an independent diverging world; `sqldiff` compare yields expected economic deltas.
- Migration up/down on a representative save; replay of an old save under its pinned ruleset.

---

## Build §19. Roadmap → build mapping (source §16)

| Stage | Deliverable | Projects / types touched |
|---|---|---|
| **1. Data model + world editor** | Entities, persistence, determinism/snapshot scaffolding, seams, calendar | SharedKernel, Domain, Persistence (`WorldDbContext`, migrations, snapshot/branch/compare), Simulation (`CalendarSystem`, RNG, scheduler stubs), App (world editor) |
| **2. Static economy** | Shops, stockpiles, supply/demand pricing, cost basis & margin. **Price/margin query works.** | Domain (Economy), Simulation (pricing, cost-basis), Application (price/margin query), App (query view) |
| **3. Tick engine** | Production (recipes/batches/endowments/industrial demand/labor), consumption, caravans over time | Simulation (tick loop, production, consumption, trade, pathfinding), Persistence (projection), App (sim controls) |
| **4. Party effects** | Logged DM actions rippling into prices | Application (`IDmActionHandler`, action log), App (DM action panel) |
| **5. Kanka import** | Structural layer then economic extraction | Seeding (`ISeedSource`, JSON/YAML, Kanka REST client), Application (import) |

Each stage is independently usable. **Factions (§10) and Events (§11) are OPEN and not built** — only their seams exist (Build §7.5). Deferred features (per-lot cost, enforcement entities, depletion, quality tiers, merchant promotion) have seams only.

---

## Build §20. Open items (inherited from source spec — do not resolve here)
- Factions system (§10) and Events system (§11) — design before building.
- Pricing parameter values (§6.2): `elasticity_bp`, multiplier clamps, markup ranges — exposed as config; tune later.
- Free-port rule (§12.6) and conquest-flips-legality (§10) — PROPOSED; build to the proposed default, keep toggles.
- Kanka attribute audit (§14.2) — concrete economic-extraction mapping pending the author's attribute structure.
- UI widget layouts (§15).
