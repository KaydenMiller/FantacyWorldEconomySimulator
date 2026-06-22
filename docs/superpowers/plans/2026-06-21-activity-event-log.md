# Activity / Event Log Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every entity (continent → country → region → settlement → merchant/shop/factory) a queryable log of the events relevant at its level, with magnitude-driven upward propagation, magnitude-tier retention, on-demand summaries, and a regex filter — unifying the existing `DmAction` audit log into a single `LogEvent` stream.

**Architecture:** A new `LogEvent` aggregate plus a `LogEventScope` join table that **materializes visibility at write time** (one scope row per level an event is visible at). A central `LogEventEmitter` (Engine) computes magnitude + target scopes and is the only writer; phases call it via `SimulationContext.Log`. Reads are a single indexed lookup on `(ScopeKind, ScopeId)` ordered by a denormalized `long Sequence` (sidestepping the repo's known limitation that value-converted typed IDs don't translate in SQL `WHERE`/`ORDER BY`). Retention prunes by magnitude-tier age at the end of each advance; summaries are computed on demand.

**Tech Stack:** C#/.NET 10, EF Core 10 + SQLite, ErrorOr, TUnit + FluentAssertions 7.x, Terminal.Gui 2.4.7. Tests run with `dotnet run --project <testproject>` (NOT `dotnet test` — VSTest is removed on .NET 10).

**Spec:** `docs/superpowers/specs/2026-06-21-activity-event-log-design.md`

---

## Conventions (read once before starting)

- **Strongly-typed IDs:** `public readonly record struct XId(Guid Value) : IStronglyTypedId { public static XId New() => new(Guid.NewGuid()); }`
- **Aggregates:** `sealed class : AggregateRoot<XId>`, a `private X() : base(default)` EF ctor (assign non-nullable refs to `null!`), a private full ctor, and a `public static ErrorOr<X> Create(...)` factory with validation. Properties are `{ get; private set; }`. Always `b.Ignore(x => x.DomainEvents)` in the EF config.
- **ID value converters:** add `public sealed class XIdConverter() : ValueConverter<XId, Guid>(v => v.Value, g => new XId(g));` and register it in `WorldDbContext.ConfigureConventions`.
- **EF config:** `IEntityTypeConfiguration<X>`, snake_case `ToTable`, `HasConversion<TickConverter>()` for `Tick`, `HasConversion<string>()` for enums, `HasIndex(...)`, `Ignore(DomainEvents)`.
- **SQL-translatable filters:** equality on a value-converted ID (`e.WorldId == worldId`) translates; range comparison (`<`) and `ORDER BY` on a converted type do NOT — filter/sort those in memory after `ToListAsync()`, or use a plain `long` column.
- **Test running:** `dotnet run --project tests/WorldEcon.<Project>.Tests.Unit -c Release`. TUnit tests are `[Test] public async Task ...`. The first "run to see it fail" step typically fails at **build** (referenced type doesn't exist yet) — that is the expected red.
- **Build check:** `dotnet build src/WorldEcon.<Project>` should end `0 Error(s)`. The solution is warnings-as-errors.
- **Temp DB in tests:** 
  ```csharp
  var path = Path.Combine(Path.GetTempPath(), $"log-{Guid.NewGuid():N}.db");
  await using var db = new WorldDbContext(
      new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
  await db.Database.MigrateAsync();
  // ... ; File.Delete(path) in a finally
  ```

---

## File Structure

**Create (Domain — `src/WorldEcon.Domain/Logging/`):**
- `Ids.cs` — `LogEventId`, `LogEventScopeId`.
- `Enums.cs` — `LogMagnitude`, `LogScopeKind`, `LogEventType`.
- `LogEvent.cs` — the append-only event aggregate.
- `LogEventScope.cs` — visibility row aggregate.

**Create (Persistence):**
- `Configurations/LogEventConfiguration.cs`, `Configurations/LogEventScopeConfiguration.cs`.
- A migration `..._AddLogEvents.cs` (generated, then hand-edited for the DmAction data copy).

**Create (Engine — `src/WorldEcon.Engine/Logging/`):**
- `LogMagnitudePolicy.cs` — default magnitudes, level floors, per-type overrides, visibility test.
- `AncestorResolver.cs` — settlement → region → country? → continent(s), cached.
- `LogEventEmitter.cs` — sequence assignment + scope materialization (the only writer).

**Create (Application — `src/WorldEcon.Application/Logging/`):**
- `LogQueryService.cs` — read a scope's log (newest-first, paged), optional regex.
- `ScopeSummary.cs` + `SummaryService.cs` — on-demand summary.

**Modify:**
- `src/WorldEcon.Persistence/Conversions/StronglyTypedIdConverters.cs` (+2 converters).
- `src/WorldEcon.Persistence/WorldDbContext.cs` (register converters; swap `DmActions` set for `LogEvents`/`LogEventScopes`).
- `src/WorldEcon.Engine/SimulationContext.cs` (add `Log` emitter + sequence init).
- `src/WorldEcon.Engine/TickEngine.cs` (retention pass before final save).
- `src/WorldEcon.Engine/Actions/DmActionService.cs` → rename/rework to `LogEventService.cs`.
- The 5 phase files under `src/WorldEcon.Engine/Phases/` (emit calls).
- `src/WorldEcon.Cli/CommandRunner.cs` (`log`, `summary`; update `buy`/`adjust`/`disable`/`enable`/`actions`).
- `src/WorldEcon.Tui/Navigation/{Navigator,INavigator,NavView}.cs`, `src/WorldEcon.Tui/Shell/TuiShell.cs`, `src/WorldEcon.Tui/Actions/*` (the party actions).

**Delete:**
- `src/WorldEcon.Domain/Actions/DmAction.cs`, `DmActionKind.cs`, `Ids.cs` (DmActionId), `src/WorldEcon.Persistence/Configurations/DmActionConfiguration.cs`, and the `DmActionId` converter.

---

## Task 1: Domain — IDs and enums

**Files:**
- Create: `src/WorldEcon.Domain/Logging/Ids.cs`
- Create: `src/WorldEcon.Domain/Logging/Enums.cs`

- [ ] **Step 1: Create the IDs**

`src/WorldEcon.Domain/Logging/Ids.cs`:
```csharp
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Logging;

public readonly record struct LogEventId(Guid Value) : IStronglyTypedId { public static LogEventId New() => new(Guid.NewGuid()); }
public readonly record struct LogEventScopeId(Guid Value) : IStronglyTypedId { public static LogEventScopeId New() => new(Guid.NewGuid()); }
```

- [ ] **Step 2: Create the enums**

`src/WorldEcon.Domain/Logging/Enums.cs`:
```csharp
namespace WorldEcon.Domain.Logging;

/// <summary>Ordered severity. Higher tiers propagate to higher levels and are retained longer.</summary>
public enum LogMagnitude { Routine = 0, Notable = 1, Major = 2, Historic = 3 }

/// <summary>The kind of entity a log event originates at or is visible to.</summary>
public enum LogScopeKind { World = 0, Continent = 1, Country = 2, Region = 3, Settlement = 4, Merchant = 5, Shop = 6, Factory = 7 }

/// <summary>What happened. Drives default magnitude and per-type propagation overrides.</summary>
public enum LogEventType
{
    Trade = 0, MerchantArrived = 1, MerchantDeparted = 2, MerchantGained = 3, MerchantLost = 4,
    ProductionChanged = 5, Stockout = 6, Spoilage = 7, Restock = 8,
    SettlementFounded = 9, SettlementRuined = 10, ClaimChanged = 11, RouteOpened = 12, RouteClosed = 13,
    PartyAction = 14,
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/WorldEcon.Domain`
Expected: `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add src/WorldEcon.Domain/Logging/Ids.cs src/WorldEcon.Domain/Logging/Enums.cs
git commit -m "feat(logging): add LogEvent ids and enums"
```

---

## Task 2: Domain — LogEvent aggregate

**Files:**
- Create: `src/WorldEcon.Domain/Logging/LogEvent.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/LogEventTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Domain.Tests.Unit/LogEventTests.cs`:
```csharp
using ErrorOr;
using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit;

public class LogEventTests
{
    private static readonly WorldId World = WorldId.New();

    [Test]
    public void Create_WithValidArgs_Succeeds()
    {
        var result = LogEvent.Create(World, sequence: 0, new Tick(10), LogEventType.Trade,
            LogMagnitude.Routine, LogScopeKind.Shop, Guid.NewGuid(), isPlayerAction: false,
            payloadJson: "{}", message: "Sold 3 potions");

        result.IsError.Should().BeFalse();
        result.Value.Message.Should().Be("Sold 3 potions");
        result.Value.Sequence.Should().Be(0);
        result.Value.IsPlayerAction.Should().BeFalse();
    }

    [Test]
    public void Create_WithNegativeSequence_Fails()
    {
        var result = LogEvent.Create(World, sequence: -1, new Tick(10), LogEventType.Trade,
            LogMagnitude.Routine, LogScopeKind.Shop, Guid.NewGuid(), false, "{}", "x");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("logevent.sequence.negative");
    }

    [Test]
    public void Create_WithBlankMessage_Fails()
    {
        var result = LogEvent.Create(World, 0, new Tick(10), LogEventType.Trade,
            LogMagnitude.Routine, LogScopeKind.Shop, Guid.NewGuid(), false, "{}", "  ");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("logevent.message.blank");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: build failure — `LogEvent` does not exist.

- [ ] **Step 3: Implement LogEvent**

`src/WorldEcon.Domain/Logging/LogEvent.cs`:
```csharp
using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Logging;

/// <summary>An append-only, scoped record of something that happened in the world. Visibility at each
/// level is materialized separately as <see cref="LogEventScope"/> rows.</summary>
public sealed class LogEvent : AggregateRoot<LogEventId>
{
    public WorldId WorldId { get; }
    public long Sequence { get; private set; }            // monotonic per world; deterministic ordering
    public Tick OccurredTick { get; private set; }
    public LogEventType Type { get; private set; }
    public LogMagnitude Magnitude { get; private set; }
    public LogScopeKind OriginKind { get; private set; }
    public Guid OriginId { get; private set; }            // raw id of the originating entity
    public bool IsPlayerAction { get; private set; }      // DM/party origin; never pruned
    public string PayloadJson { get; private set; }       // structured details (who/qty/price/...)
    public string Message { get; private set; }           // human-readable
    public DateTimeOffset RecordedAtUtc { get; private set; }

    private LogEvent() : base(default) { PayloadJson = null!; Message = null!; } // EF

    private LogEvent(LogEventId id, WorldId worldId, long sequence, Tick occurredTick, LogEventType type,
        LogMagnitude magnitude, LogScopeKind originKind, Guid originId, bool isPlayerAction,
        string payloadJson, string message, DateTimeOffset recordedAtUtc) : base(id)
    {
        WorldId = worldId;
        Sequence = sequence;
        OccurredTick = occurredTick;
        Type = type;
        Magnitude = magnitude;
        OriginKind = originKind;
        OriginId = originId;
        IsPlayerAction = isPlayerAction;
        PayloadJson = payloadJson;
        Message = message;
        RecordedAtUtc = recordedAtUtc;
    }

    public static ErrorOr<LogEvent> Create(WorldId worldId, long sequence, Tick occurredTick,
        LogEventType type, LogMagnitude magnitude, LogScopeKind originKind, Guid originId,
        bool isPlayerAction, string payloadJson, string message)
        => Create(worldId, sequence, occurredTick, type, magnitude, originKind, originId,
            isPlayerAction, payloadJson, message, DateTimeOffset.UtcNow);

    public static ErrorOr<LogEvent> Create(WorldId worldId, long sequence, Tick occurredTick,
        LogEventType type, LogMagnitude magnitude, LogScopeKind originKind, Guid originId,
        bool isPlayerAction, string payloadJson, string message, DateTimeOffset recordedAtUtc)
    {
        if (sequence < 0)
            return Error.Validation("logevent.sequence.negative", "Sequence must not be negative.");
        if (payloadJson is null)
            return Error.Validation("logevent.payload.null", "Payload JSON must not be null.");
        if (string.IsNullOrWhiteSpace(message))
            return Error.Validation("logevent.message.blank", "Message must not be blank.");

        return new LogEvent(LogEventId.New(), worldId, sequence, occurredTick, type, magnitude,
            originKind, originId, isPlayerAction, payloadJson, message.Trim(), recordedAtUtc);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Domain/Logging/LogEvent.cs tests/WorldEcon.Domain.Tests.Unit/LogEventTests.cs
git commit -m "feat(logging): LogEvent aggregate with validating factory"
```

---

## Task 3: Domain — LogEventScope aggregate

**Files:**
- Create: `src/WorldEcon.Domain/Logging/LogEventScope.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/LogEventScopeTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Domain.Tests.Unit/LogEventScopeTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;

namespace WorldEcon.Domain.Tests.Unit;

public class LogEventScopeTests
{
    [Test]
    public void Create_Succeeds()
    {
        var ev = LogEventId.New();
        var scope = LogEventScope.Create(WorldId.New(), ev, LogScopeKind.Settlement, Guid.NewGuid(), sequence: 7);

        scope.IsError.Should().BeFalse();
        scope.Value.LogEventId.Should().Be(ev);
        scope.Value.ScopeKind.Should().Be(LogScopeKind.Settlement);
        scope.Value.Sequence.Should().Be(7);
    }

    [Test]
    public void Create_WithNegativeSequence_Fails()
    {
        var scope = LogEventScope.Create(WorldId.New(), LogEventId.New(), LogScopeKind.Settlement, Guid.NewGuid(), -1);
        scope.IsError.Should().BeTrue();
        scope.FirstError.Code.Should().Be("logeventscope.sequence.negative");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: build failure — `LogEventScope` does not exist.

- [ ] **Step 3: Implement LogEventScope**

`src/WorldEcon.Domain/Logging/LogEventScope.cs`:
```csharp
using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Logging;

/// <summary>One row per level a <see cref="LogEvent"/> is visible at (origin + qualifying ancestors).
/// <see cref="Sequence"/> is denormalized from the event so the hot read can ORDER BY a plain long.</summary>
public sealed class LogEventScope : AggregateRoot<LogEventScopeId>
{
    public WorldId WorldId { get; }
    public LogEventId LogEventId { get; private set; }
    public LogScopeKind ScopeKind { get; private set; }
    public Guid ScopeId { get; private set; }   // raw id of the entity this event is visible to
    public long Sequence { get; private set; }  // == owning event's Sequence

    private LogEventScope() : base(default) { } // EF

    private LogEventScope(LogEventScopeId id, WorldId worldId, LogEventId logEventId,
        LogScopeKind scopeKind, Guid scopeId, long sequence) : base(id)
    {
        WorldId = worldId;
        LogEventId = logEventId;
        ScopeKind = scopeKind;
        ScopeId = scopeId;
        Sequence = sequence;
    }

    public static ErrorOr<LogEventScope> Create(WorldId worldId, LogEventId logEventId,
        LogScopeKind scopeKind, Guid scopeId, long sequence)
    {
        if (sequence < 0)
            return Error.Validation("logeventscope.sequence.negative", "Sequence must not be negative.");

        return new LogEventScope(LogEventScopeId.New(), worldId, logEventId, scopeKind, scopeId, sequence);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Domain/Logging/LogEventScope.cs tests/WorldEcon.Domain.Tests.Unit/LogEventScopeTests.cs
git commit -m "feat(logging): LogEventScope visibility row"
```

---

## Task 4: Persistence — converters, configs, DbContext wiring

**Files:**
- Modify: `src/WorldEcon.Persistence/Conversions/StronglyTypedIdConverters.cs`
- Create: `src/WorldEcon.Persistence/Configurations/LogEventConfiguration.cs`
- Create: `src/WorldEcon.Persistence/Configurations/LogEventScopeConfiguration.cs`
- Modify: `src/WorldEcon.Persistence/WorldDbContext.cs`

This task wires the model but defers the migration (Task 5). It will not compile-run a DB test yet (no migration), only build.

- [ ] **Step 1: Add the ID converters**

Append to `src/WorldEcon.Persistence/Conversions/StronglyTypedIdConverters.cs` (add `using WorldEcon.Domain.Logging;` at the top alongside the existing usings):
```csharp
public sealed class LogEventIdConverter() : ValueConverter<LogEventId, Guid>(v => v.Value, g => new LogEventId(g));
public sealed class LogEventScopeIdConverter() : ValueConverter<LogEventScopeId, Guid>(v => v.Value, g => new LogEventScopeId(g));
```

- [ ] **Step 2: Add the EF configurations**

`src/WorldEcon.Persistence/Configurations/LogEventConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class LogEventConfiguration : IEntityTypeConfiguration<LogEvent>
{
    public void Configure(EntityTypeBuilder<LogEvent> b)
    {
        b.ToTable("log_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.OccurredTick).HasConversion<TickConverter>();
        b.Property(x => x.Type).HasConversion<string>();
        b.Property(x => x.Magnitude).HasConversion<string>();
        b.Property(x => x.OriginKind).HasConversion<string>();
        b.Property(x => x.Message).IsRequired();
        b.Property(x => x.PayloadJson).IsRequired();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => new { x.WorldId, x.Sequence });
        b.Ignore(x => x.DomainEvents);
    }
}
```

`src/WorldEcon.Persistence/Configurations/LogEventScopeConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Logging;

namespace WorldEcon.Persistence.Configurations;

public sealed class LogEventScopeConfiguration : IEntityTypeConfiguration<LogEventScope>
{
    public void Configure(EntityTypeBuilder<LogEventScope> b)
    {
        b.ToTable("log_event_scopes");
        b.HasKey(x => x.Id);
        b.Property(x => x.ScopeKind).HasConversion<string>();
        b.HasIndex(x => x.LogEventId);
        // The hot read: events visible at a given scope, newest first.
        b.HasIndex(x => new { x.ScopeKind, x.ScopeId, x.Sequence });
        b.Ignore(x => x.DomainEvents);
    }
}
```

- [ ] **Step 3: Wire the DbContext**

In `src/WorldEcon.Persistence/WorldDbContext.cs`:

Add `using WorldEcon.Domain.Logging;` near the other usings.

Replace the line `public DbSet<DmAction> DmActions => Set<DmAction>();` with:
```csharp
    public DbSet<LogEvent> LogEvents => Set<LogEvent>();
    public DbSet<LogEventScope> LogEventScopes => Set<LogEventScope>();
```

In `ConfigureConventions`, replace `b.Properties<DmActionId>().HaveConversion<DmActionIdConverter>();` with:
```csharp
        b.Properties<LogEventId>().HaveConversion<LogEventIdConverter>();
        b.Properties<LogEventScopeId>().HaveConversion<LogEventScopeIdConverter>();
```

Remove the now-dangling `using WorldEcon.Domain.Actions;` only if nothing else in the file uses it (the DmAction DbSet was the only user — remove it).

- [ ] **Step 4: Delete the DmAction model + config**

```bash
git rm src/WorldEcon.Domain/Actions/DmAction.cs src/WorldEcon.Domain/Actions/DmActionKind.cs \
       src/WorldEcon.Domain/Actions/Ids.cs \
       src/WorldEcon.Persistence/Configurations/DmActionConfiguration.cs
```
Then remove the `DmActionIdConverter` line from `src/WorldEcon.Persistence/Conversions/StronglyTypedIdConverters.cs`.

> NOTE: This breaks `DmActionService.cs` and its callers; they are fixed in Task 5 and Task 10. The build will be red until then — that is expected and bounded.

- [ ] **Step 5: Build the persistence project**

Run: `dotnet build src/WorldEcon.Persistence`
Expected: errors ONLY in `WorldEcon.Engine` (DmActionService) — `WorldEcon.Domain` and `WorldEcon.Persistence` themselves compile. If `WorldEcon.Persistence` reports errors about `DmAction`, you missed a reference; fix it. (Engine errors are addressed next.)

- [ ] **Step 6: Commit (WIP — build is intentionally red in Engine)**

```bash
git add -A
git commit -m "feat(logging): persistence model for LogEvent/LogEventScope; remove DmAction model"
```

---

## Task 5: Persistence — migration with DmAction → LogEvent data copy

**Files:**
- Create: `src/WorldEcon.Persistence/Migrations/<timestamp>_AddLogEvents.cs` (generated, then edited)
- Test: `tests/WorldEcon.Persistence.Tests.Unit/LogEventMigrationTests.cs`

> Engine still won't build (DmActionService). EF migration generation only needs `WorldEcon.Persistence` to build, which it does. If `dotnet-ef` insists on building the whole solution, temporarily comment out the body of `DmActionService.RecordAsync` is NOT needed — `dotnet-ef` builds only the target + startup project (`WorldEcon.Persistence`). Proceed.

- [ ] **Step 1: Generate the migration**

Run:
```bash
dotnet dotnet-ef migrations add AddLogEvents \
  --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence
```
Expected: a new file `src/WorldEcon.Persistence/Migrations/<timestamp>_AddLogEvents.cs` whose `Up` **creates** `log_events` and `log_event_scopes` and **drops** `dm_actions`.

- [ ] **Step 2: Insert the data-copy SQL**

Open the generated migration. In `Up`, find the `CreateTable(name: "log_events", ...)` and `CreateTable(name: "log_event_scopes", ...)` calls and the `DropTable(name: "dm_actions")` call. **After both `CreateTable` calls and BEFORE `DropTable("dm_actions")`**, insert:

```csharp
            // Fold the legacy DM/party audit log into the unified LogEvent stream. Historical actions
            // become Major, player-action events at World scope (precise per-settlement scope for old
            // actions is not reconstructable from ArgsJson; new actions get proper scoping going forward).
            migrationBuilder.Sql(@"
INSERT INTO log_events (Id, WorldId, Sequence, OccurredTick, Type, Magnitude, OriginKind, OriginId, IsPlayerAction, PayloadJson, Message, RecordedAtUtc)
SELECT Id, WorldId, Sequence, AppliedTick, 'PartyAction', 'Major', 'World', WorldId, 1, ArgsJson, Description, RecordedAtUtc
FROM dm_actions;");

            migrationBuilder.Sql(@"
INSERT INTO log_event_scopes (Id, WorldId, LogEventId, ScopeKind, ScopeId, Sequence)
SELECT
  lower(
    substr(hex(randomblob(4)),1,8) || '-' ||
    substr(hex(randomblob(2)),1,4) || '-4' ||
    substr(hex(randomblob(2)),2,3) || '-' ||
    substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)),2,3) || '-' ||
    substr(hex(randomblob(6)),1,12)
  ),
  WorldId, Id, 'World', WorldId, Sequence
FROM dm_actions;");
```

> If EF ordered `DropTable("dm_actions")` BEFORE the `CreateTable` calls, move it so the two `Sql` blocks run while `dm_actions` still exists and `log_events`/`log_event_scopes` already exist. The required order is: create log_events → create log_event_scopes → (the two Sql copies) → drop dm_actions.

Leave `Down` as generated (it will drop the new tables and recreate `dm_actions` empty — acceptable; this is a forward-only data migration).

- [ ] **Step 3: Write the migration test**

`tests/WorldEcon.Persistence.Tests.Unit/LogEventMigrationTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;

namespace WorldEcon.Persistence.Tests.Unit;

public class LogEventMigrationTests
{
    [Test]
    public async Task Migrate_CreatesLogTables_AndRoundTripsAnEvent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logmig-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            await db.Database.MigrateAsync();

            var worldId = new WorldEcon.Domain.Geography.WorldId(Guid.NewGuid());
            var ev = LogEvent.Create(worldId, 0, new WorldEcon.SharedKernel.Tick(5),
                LogEventType.Trade, LogMagnitude.Routine, LogScopeKind.Shop, Guid.NewGuid(),
                false, "{}", "round trip").Value;
            db.LogEvents.Add(ev);
            db.LogEventScopes.Add(LogEventScope.Create(worldId, ev.Id, LogScopeKind.Shop, ev.OriginId, 0).Value);
            await db.SaveChangesAsync();

            (await db.LogEvents.CountAsync()).Should().Be(1);
            (await db.LogEventScopes.CountAsync()).Should().Be(1);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 4: Run the test**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit -c Release`
Expected: `Passed!` (this project does not reference Engine, so the still-broken `DmActionService` doesn't block it).

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Persistence/Migrations tests/WorldEcon.Persistence.Tests.Unit/LogEventMigrationTests.cs
git commit -m "feat(logging): AddLogEvents migration with DmAction data copy"
```

---

## Task 6: Engine — magnitude/floor/override policy

**Files:**
- Create: `src/WorldEcon.Engine/Logging/LogMagnitudePolicy.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/LogMagnitudePolicyTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/LogMagnitudePolicyTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;

namespace WorldEcon.Engine.Tests.Unit;

public class LogMagnitudePolicyTests
{
    [Test]
    public void DefaultMagnitude_MapsTypes()
    {
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.Trade).Should().Be(LogMagnitude.Routine);
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.MerchantLost).Should().Be(LogMagnitude.Notable);
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.ClaimChanged).Should().Be(LogMagnitude.Major);
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.SettlementRuined).Should().Be(LogMagnitude.Historic);
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.PartyAction).Should().Be(LogMagnitude.Major);
    }

    [Test]
    public void Visible_ByMagnitudeFloor()
    {
        // Historic clears every floor.
        LogMagnitudePolicy.Visible(LogEventType.SettlementRuined, LogMagnitude.Historic, LogScopeKind.Continent).Should().BeTrue();
        // Notable clears Settlement but not Region.
        LogMagnitudePolicy.Visible(LogEventType.MerchantLost, LogMagnitude.Notable, LogScopeKind.Settlement).Should().BeTrue();
        LogMagnitudePolicy.Visible(LogEventType.MerchantLost, LogMagnitude.Notable, LogScopeKind.Region).Should().BeFalse();
    }

    [Test]
    public void Visible_RespectsOverrides()
    {
        // Force: ClaimChanged visible at Country/Continent regardless of magnitude.
        LogMagnitudePolicy.Visible(LogEventType.ClaimChanged, LogMagnitude.Routine, LogScopeKind.Country).Should().BeTrue();
        // Cap: Restock never leaves the shop, even if magnitude were high.
        LogMagnitudePolicy.Visible(LogEventType.Restock, LogMagnitude.Historic, LogScopeKind.Settlement).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: build failure — `LogMagnitudePolicy` does not exist (and likely still the DmActionService error; ignore that — the policy type is what we're after; if the project can't build at all due to DmActionService, that is fine, it counts as red).

- [ ] **Step 3: Implement the policy**

`src/WorldEcon.Engine/Logging/LogMagnitudePolicy.cs`:
```csharp
using WorldEcon.Domain.Logging;

namespace WorldEcon.Engine.Logging;

/// <summary>Pure rules for how events surface: default magnitude per type, the visibility floor per
/// level, and per-type overrides. Tunable here now; promote to world/config params later.</summary>
public static class LogMagnitudePolicy
{
    public static LogMagnitude DefaultMagnitude(LogEventType type) => type switch
    {
        LogEventType.Trade or LogEventType.Restock or LogEventType.Spoilage => LogMagnitude.Routine,
        LogEventType.MerchantArrived or LogEventType.MerchantDeparted
            or LogEventType.MerchantGained or LogEventType.MerchantLost
            or LogEventType.ProductionChanged or LogEventType.Stockout => LogMagnitude.Notable,
        LogEventType.RouteOpened or LogEventType.RouteClosed or LogEventType.ClaimChanged => LogMagnitude.Major,
        LogEventType.SettlementFounded or LogEventType.SettlementRuined => LogMagnitude.Historic,
        LogEventType.PartyAction => LogMagnitude.Major,
        _ => LogMagnitude.Notable,
    };

    /// <summary>Lowest magnitude that surfaces at a given level (before per-type overrides).</summary>
    public static LogMagnitude FloorFor(LogScopeKind level) => level switch
    {
        LogScopeKind.Shop or LogScopeKind.Factory or LogScopeKind.Merchant => LogMagnitude.Routine,
        LogScopeKind.Settlement => LogMagnitude.Notable,
        LogScopeKind.Region or LogScopeKind.Country => LogMagnitude.Major,
        LogScopeKind.Continent or LogScopeKind.World => LogMagnitude.Historic,
        _ => LogMagnitude.Historic,
    };

    /// <summary>Whether an event of (type, magnitude) surfaces at <paramref name="level"/>, applying
    /// per-type force/cap overrides. The ORIGIN scope is always written by the emitter regardless of
    /// this method.</summary>
    public static bool Visible(LogEventType type, LogMagnitude magnitude, LogScopeKind level)
    {
        // Cap override: a restock never leaves the originating shop.
        if (type == LogEventType.Restock && level != LogScopeKind.Shop)
            return false;

        // Force override: claim changes are always visible at country and continent.
        if (type == LogEventType.ClaimChanged && (level == LogScopeKind.Country || level == LogScopeKind.Continent))
            return true;

        return (int)magnitude >= (int)FloorFor(level);
    }
}
```

- [ ] **Step 4: Run to verify the policy tests pass**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: still red IF `DmActionService` hasn't been fixed yet. To unblock the Engine test project for this and later tasks, do Task 10's `LogEventService` swap is NOT a prerequisite — instead, temporarily neutralize the broken file: it is cleanest to proceed to Task 7–9 then Task 10, building Engine once at Task 10. **However**, so this policy is verified now, run the build of just the file's dependencies is not possible in isolation. Therefore: defer running this test to the end of Task 9 (when Engine compiles). Mark this step done once the code is written.

> Practical note: Tasks 6–9 add Engine code while `DmActionService` is still broken. Engine will first compile at the end of **Task 10**. Run the Engine test project after Task 10; expect all of Tasks 6–10's tests green together. Keep committing per task (red Engine build is acceptable mid-feature, matching Task 4's note).

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Logging/LogMagnitudePolicy.cs tests/WorldEcon.Engine.Tests.Unit/LogMagnitudePolicyTests.cs
git commit -m "feat(logging): magnitude/floor/override policy"
```

---

## Task 7: Engine — ancestor resolver

**Files:**
- Create: `src/WorldEcon.Engine/Logging/AncestorResolver.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/LogTestWorld.cs` (shared seeding helper)
- Test: `tests/WorldEcon.Engine.Tests.Unit/AncestorResolverTests.cs`

- [ ] **Step 1: Create the shared seeding helper**

`tests/WorldEcon.Engine.Tests.Unit/LogTestWorld.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

/// <summary>Spins up a temp SQLite world with one continent → country → region → settlement, for
/// exercising the logging pipeline end-to-end.</summary>
public static class LogTestWorld
{
    public sealed record Seeded(string Path, WorldDbContext Db, World World,
        Continent Continent, Country Country, Region Region, Settlement Settlement);

    public static async Task<Seeded> CreateAsync()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"logeng-{Guid.NewGuid():N}.db");
        var db = new WorldDbContext(
            new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
        await db.Database.MigrateAsync();

        var world = World.Create("Test", seed: 1, CalendarDefinition.Default).Value;
        db.Worlds.Add(world);

        var continent = Continent.Create(world.Id, "Praxus").Value;
        var country = Country.Create(world.Id, "Thaloria").Value;
        var region = Region.Create(world.Id, "The Reach", country.Id, RegionKind.Land).Value;
        var settlement = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 0, 0, 50_000).Value;
        db.Continents.Add(continent);
        db.Countries.Add(country);
        db.Regions.Add(region);
        db.Settlements.Add(settlement);
        db.RegionContinents.Add(RegionContinent.Create(world.Id, region.Id, continent.Id).Value);
        await db.SaveChangesAsync();

        return new Seeded(path, db, world, continent, country, region, settlement);
    }

    public static async Task DisposeAsync(Seeded s)
    {
        await s.Db.DisposeAsync();
        System.IO.File.Delete(s.Path);
    }
}
```

> Verify the factory signatures against the actual domain types before running: `World.Create`, `Continent.Create`, `Country.Create`, `Region.Create(worldId, name, CountryId?, RegionKind)`, `Settlement.Create(worldId, regionId, name, SettlementType, x, y, population)`, `RegionContinent.Create(worldId, regionId, continentId)`. Adjust argument lists if a signature differs (e.g. a required parameter), keeping the same entities. These are the only place test setup depends on exact ctors.

- [ ] **Step 2: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/AncestorResolverTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;

namespace WorldEcon.Engine.Tests.Unit;

public class AncestorResolverTests
{
    [Test]
    public async Task AncestorsOf_Settlement_ReturnsSettlementRegionCountryContinent()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var resolver = new AncestorResolver(s.Db, s.World.Id);
            var chain = await resolver.AncestorsOf(s.Settlement.Id);

            chain.Should().Contain((LogScopeKind.Settlement, s.Settlement.Id.Value));
            chain.Should().Contain((LogScopeKind.Region, s.Region.Id.Value));
            chain.Should().Contain((LogScopeKind.Country, s.Country.Id.Value));
            chain.Should().Contain((LogScopeKind.Continent, s.Continent.Id.Value));
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 3: Implement the resolver**

`src/WorldEcon.Engine/Logging/AncestorResolver.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;

namespace WorldEcon.Engine.Logging;

/// <summary>Resolves a settlement's scope chain (Settlement → Region → Country? → Continent(s)) for
/// the emitter, cached per instance (one instance per advance / per service call).</summary>
public sealed class AncestorResolver
{
    private readonly WorldDbContext _db;
    private readonly WorldId _worldId;
    private readonly Dictionary<Guid, IReadOnlyList<(LogScopeKind Kind, Guid Id)>> _cache = new();

    public AncestorResolver(WorldDbContext db, WorldId worldId)
    {
        _db = db;
        _worldId = worldId;
    }

    public async Task<IReadOnlyList<(LogScopeKind Kind, Guid Id)>> AncestorsOf(SettlementId settlementId)
    {
        if (_cache.TryGetValue(settlementId.Value, out var cached))
            return cached;

        var chain = new List<(LogScopeKind, Guid)> { (LogScopeKind.Settlement, settlementId.Value) };

        var settlement = await _db.Settlements.FirstOrDefaultAsync(x => x.Id == settlementId);
        if (settlement is not null)
        {
            var region = await _db.Regions.FirstOrDefaultAsync(r => r.Id == settlement.RegionId);
            if (region is not null)
            {
                chain.Add((LogScopeKind.Region, region.Id.Value));
                if (region.CountryId is { } cid)
                    chain.Add((LogScopeKind.Country, cid.Value));

                // A region may span several continents (geography v2 m2m).
                var continentIds = (await _db.RegionContinents
                        .Where(rc => rc.WorldId == _worldId && rc.RegionId == region.Id)
                        .ToListAsync())
                    .Select(rc => rc.ContinentId.Value)
                    .OrderBy(g => g);
                foreach (var contId in continentIds)
                    chain.Add((LogScopeKind.Continent, contId));
            }
        }

        _cache[settlementId.Value] = chain;
        return chain;
    }
}
```

- [ ] **Step 4: (Deferred run)** Engine compiles at Task 10; this test runs green then. Mark done once written.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Logging/AncestorResolver.cs tests/WorldEcon.Engine.Tests.Unit/LogTestWorld.cs tests/WorldEcon.Engine.Tests.Unit/AncestorResolverTests.cs
git commit -m "feat(logging): settlement ancestor resolver"
```

---

## Task 8: Engine — LogEventEmitter

**Files:**
- Create: `src/WorldEcon.Engine/Logging/LogEventEmitter.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/LogEventEmitterTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/LogEventEmitterTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class LogEventEmitterTests
{
    [Test]
    public async Task Emit_RoutineTrade_WritesOnlyOriginScope()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var emitter = new LogEventEmitter(s.Db, s.World.Id);
            await emitter.EmitAsync(LogEventType.Trade, "Sold 3 potions", new Tick(10),
                LogScopeKind.Shop, Guid.NewGuid(), s.Settlement.Id);
            await s.Db.SaveChangesAsync();

            (await s.Db.LogEvents.CountAsync()).Should().Be(1);
            // Routine clears no ancestor floor → exactly one scope row (the shop).
            (await s.Db.LogEventScopes.CountAsync()).Should().Be(1);
            (await s.Db.LogEventScopes.FirstAsync()).ScopeKind.Should().Be(LogScopeKind.Shop);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Emit_HistoricSettlementRuined_FansOutToContinentAndWorld()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var emitter = new LogEventEmitter(s.Db, s.World.Id);
            await emitter.EmitAsync(LogEventType.SettlementRuined, "Hammerfell fell to ruin", new Tick(20),
                LogScopeKind.Settlement, s.Settlement.Id.Value, s.Settlement.Id);
            await s.Db.SaveChangesAsync();

            var kinds = (await s.Db.LogEventScopes.ToListAsync()).Select(x => x.ScopeKind).ToHashSet();
            kinds.Should().Contain(new[]
            {
                LogScopeKind.Settlement, LogScopeKind.Region, LogScopeKind.Country,
                LogScopeKind.Continent, LogScopeKind.World,
            });
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Emit_AssignsMonotonicSequenceContinuingFromMax()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var emitter = new LogEventEmitter(s.Db, s.World.Id);
            await emitter.EmitAsync(LogEventType.Trade, "a", new Tick(1), LogScopeKind.Shop, Guid.NewGuid(), s.Settlement.Id);
            await emitter.EmitAsync(LogEventType.Trade, "b", new Tick(2), LogScopeKind.Shop, Guid.NewGuid(), s.Settlement.Id);
            await s.Db.SaveChangesAsync();

            var seqs = (await s.Db.LogEvents.ToListAsync()).Select(e => e.Sequence).OrderBy(x => x).ToList();
            seqs.Should().Equal(0, 1);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 2: (Red is deferred — Engine compiles at Task 10.)** Write the test, do not run yet.

- [ ] **Step 3: Implement the emitter**

`src/WorldEcon.Engine/Logging/LogEventEmitter.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Logging;

/// <summary>The only writer of <see cref="LogEvent"/>s. Assigns a monotonic per-world sequence,
/// computes target scopes (origin + qualifying ancestors per <see cref="LogMagnitudePolicy"/>), and
/// adds the tracked rows. The caller saves (the engine once per advance; the party service immediately).</summary>
public sealed class LogEventEmitter
{
    private readonly WorldDbContext _db;
    private readonly WorldId _worldId;
    private readonly AncestorResolver _resolver;
    private long _nextSequence;
    private bool _seqLoaded;

    public LogEventEmitter(WorldDbContext db, WorldId worldId)
    {
        _db = db;
        _worldId = worldId;
        _resolver = new AncestorResolver(db, worldId);
    }

    public async Task<LogEvent> EmitAsync(LogEventType type, string message, Tick tick,
        LogScopeKind originKind, Guid originId, SettlementId? settlement = null,
        LogMagnitude? magnitude = null, bool isPlayerAction = false, string payloadJson = "{}")
    {
        var mag = magnitude ?? LogMagnitudePolicy.DefaultMagnitude(type);
        await EnsureSequenceLoaded();
        long seq = _nextSequence++;

        var ev = LogEvent.Create(_worldId, seq, tick, type, mag, originKind, originId,
            isPlayerAction, payloadJson, message).Value;
        _db.LogEvents.Add(ev);

        // Origin scope is always written.
        AddScope(ev, originKind, originId, seq);

        // Ancestor scopes that clear the magnitude floor (or are forced by a per-type override).
        if (settlement is { } sid)
        {
            foreach (var (akind, aid) in await _resolver.AncestorsOf(sid))
            {
                if (akind == originKind && aid == originId)
                    continue; // origin already added
                if (LogMagnitudePolicy.Visible(type, mag, akind))
                    AddScope(ev, akind, aid, seq);
            }
        }

        // World scope, when the event is visible at World and didn't originate there.
        if (originKind != LogScopeKind.World && LogMagnitudePolicy.Visible(type, mag, LogScopeKind.World))
            AddScope(ev, LogScopeKind.World, _worldId.Value, seq);

        return ev;
    }

    private void AddScope(LogEvent ev, LogScopeKind kind, Guid id, long seq)
        => _db.LogEventScopes.Add(LogEventScope.Create(_worldId, ev.Id, kind, id, seq).Value);

    private async Task EnsureSequenceLoaded()
    {
        if (_seqLoaded)
            return;
        long max = await _db.LogEvents
            .Where(e => e.WorldId == _worldId)
            .Select(e => (long?)e.Sequence)
            .MaxAsync() ?? -1;
        _nextSequence = max + 1;
        _seqLoaded = true;
    }
}
```

- [ ] **Step 4: (Deferred run — Task 10.)** Mark done once written.

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Engine/Logging/LogEventEmitter.cs tests/WorldEcon.Engine.Tests.Unit/LogEventEmitterTests.cs
git commit -m "feat(logging): LogEventEmitter (sequence + scope materialization)"
```

---

## Task 9: Engine — expose the emitter on SimulationContext

**Files:**
- Modify: `src/WorldEcon.Engine/SimulationContext.cs`

- [ ] **Step 1: Add the emitter to the context**

In `src/WorldEcon.Engine/SimulationContext.cs`, add `using WorldEcon.Engine.Logging;`, then:

Change the ctor and add the property:
```csharp
    private SimulationContext(WorldDbContext db, World world, IRngStreams rng, CalendarSystem calendar)
    {
        Db = db;
        World = world;
        Rng = rng;
        Calendar = calendar;
        Log = new LogEventEmitter(db, world.Id);
    }

    public WorldDbContext Db { get; }
    public World World { get; }
    public IRngStreams Rng { get; }
    public CalendarSystem Calendar { get; }

    /// <summary>The log-event writer for this advance. Phases emit through this; the engine's single
    /// end-of-advance SaveChanges persists the rows.</summary>
    public LogEventEmitter Log { get; }
```
(The `LoadAsync` body is unchanged — it already calls the private ctor.)

- [ ] **Step 2: Build check is deferred to Task 10** (Engine still has the broken `DmActionService`). Mark done once written.

- [ ] **Step 3: Commit**

```bash
git add src/WorldEcon.Engine/SimulationContext.cs
git commit -m "feat(logging): expose LogEventEmitter as SimulationContext.Log"
```

---

## Task 10: Engine — replace DmActionService with LogEventService; fix callers

**Files:**
- Create: `src/WorldEcon.Engine/Actions/LogEventService.cs` (replaces `DmActionService.cs`)
- Delete: `src/WorldEcon.Engine/Actions/DmActionService.cs`
- Modify: `src/WorldEcon.Cli/CommandRunner.cs`
- Modify: `src/WorldEcon.Tui/Actions/BuyOutAction.cs`, `DisableProductionAction.cs`, `EnableProductionAction.cs`
- Modify: `tests/WorldEcon.Tui.Tests.Unit/ActionTests.cs`

This task makes the whole solution compile again and runs the Engine tests from Tasks 6–10.

- [ ] **Step 1: Create LogEventService**

`src/WorldEcon.Engine/Actions/LogEventService.cs` (port the three effect methods from the old `DmActionService`, recording a `LogEvent` via the emitter instead of a `DmAction`):
```csharp
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Actions;

/// <summary>Applies party/DM effects to the live world (the <see cref="WorldDbContext"/> is
/// authoritative state) and records a player-action <see cref="LogEvent"/> for each. Effects are
/// deterministic: entities iterate in stable id order. The service mutates tracked entities, emits the
/// event, and saves once.</summary>
public sealed class LogEventService
{
    private readonly WorldDbContext _db;
    private readonly ICostBasisValuation _valuation;

    public LogEventService(WorldDbContext db, ICostBasisValuation? valuation = null)
    {
        _db = db;
        _valuation = valuation ?? new WeightedAverageValuation();
    }

    public async Task<ErrorOr<LogEvent>> BuyFromShopsAsync(
        WorldId worldId, SettlementId settlementId, GoodId goodId, long quantity, DateTimeOffset recordedAtUtc)
    {
        if (quantity < 1)
            return Error.Validation("party.buy.quantity", "Quantity must be at least 1.");

        var world = await LoadWorld(worldId);
        if (world.IsError)
            return world.Errors;

        var good = await _db.Goods.FirstOrDefaultAsync(g => g.WorldId == worldId && g.Id == goodId);
        if (good is null)
            return Error.NotFound("party.good.notfound", "Good not found.");

        var settlement = await _db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId);

        var shops = (await _db.Shops
                .Where(s => s.WorldId == worldId && s.SettlementId == settlementId)
                .ToListAsync())
            .OrderBy(s => s.Id.Value)
            .ToList();

        long remaining = quantity;
        long bought = 0;
        foreach (var shop in shops)
        {
            if (remaining <= 0)
                break;
            var stock = await _db.Stockpiles.FirstOrDefaultAsync(s =>
                s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop
                && s.OwnerId == shop.Id.Value && s.GoodId == goodId);
            if (stock is null || stock.Quantity <= 0)
                continue;
            long take = Math.Min(remaining, stock.Quantity);
            stock.Withdraw(take).OrThrow("party buy-from-shops withdraw");
            remaining -= take;
            bought += take;
        }

        var payload = JsonSerializer.Serialize(new
        { settlementId = settlementId.Value, goodId = goodId.Value, requested = quantity, bought });
        var message = $"Party bought {bought}x {good.Name} from shops in {Name(settlement, settlementId)}";

        return await EmitParty(world.Value, settlementId, message, payload, recordedAtUtc);
    }

    public async Task<ErrorOr<LogEvent>> AdjustMarketStockAsync(
        WorldId worldId, SettlementId settlementId, GoodId goodId, long delta, DateTimeOffset recordedAtUtc)
    {
        if (delta == 0)
            return Error.Validation("party.adjust.delta", "Delta must not be zero.");

        var world = await LoadWorld(worldId);
        if (world.IsError)
            return world.Errors;

        var good = await _db.Goods.FirstOrDefaultAsync(g => g.WorldId == worldId && g.Id == goodId);
        if (good is null)
            return Error.NotFound("party.good.notfound", "Good not found.");

        var settlement = await _db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId);

        long applied;
        if (delta > 0)
        {
            var stock = await GetOrCreateMarketStockpile(worldId, settlementId, goodId);
            stock.Deposit(delta, good.BaseValue, _valuation);
            applied = delta;
        }
        else
        {
            var stock = await FindMarketStockpile(worldId, settlementId, goodId);
            long withdraw = Math.Min(-delta, stock?.Quantity ?? 0);
            if (withdraw > 0)
                stock!.Withdraw(withdraw).OrThrow("party market-stock adjustment withdraw");
            applied = -withdraw;
        }

        var payload = JsonSerializer.Serialize(new
        { settlementId = settlementId.Value, goodId = goodId.Value, delta, applied });
        var message = $"Party adjusted {good.Name} market stock in {Name(settlement, settlementId)} by {delta}";

        return await EmitParty(world.Value, settlementId, message, payload, recordedAtUtc);
    }

    public async Task<ErrorOr<LogEvent>> SetSettlementProductionDisabledAsync(
        WorldId worldId, SettlementId settlementId, bool disabled, DateTimeOffset recordedAtUtc)
    {
        var world = await LoadWorld(worldId);
        if (world.IsError)
            return world.Errors;

        var settlement = await _db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId);

        var nodes = (await _db.ProductionNodes
                .Where(n => n.WorldId == worldId && n.SettlementId == settlementId)
                .ToListAsync())
            .OrderBy(n => n.Id.Value)
            .ToList();

        foreach (var node in nodes)
        {
            if (disabled) node.Disable();
            else node.Enable();
        }

        var payload = JsonSerializer.Serialize(new
        { settlementId = settlementId.Value, disabled, nodeCount = nodes.Count });
        var verb = disabled ? "disabled" : "restored";
        var message = $"Party {verb} production in {Name(settlement, settlementId)} ({nodes.Count} facilities)";

        return await EmitParty(world.Value, settlementId, message, payload, recordedAtUtc);
    }

    private async Task<ErrorOr<LogEvent>> EmitParty(
        World world, SettlementId settlementId, string message, string payloadJson, DateTimeOffset recordedAtUtc)
    {
        var emitter = new LogEventEmitter(_db, world.Id);
        var ev = await emitter.EmitAsync(LogEventType.PartyAction, message, world.CurrentTick,
            LogScopeKind.Settlement, settlementId.Value, settlementId,
            magnitude: LogMagnitude.Major, isPlayerAction: true, payloadJson: payloadJson);
        await _db.SaveChangesAsync();
        _ = recordedAtUtc; // RecordedAtUtc is stamped by LogEvent.Create(UtcNow); kept for signature parity
        return ev;
    }

    private async Task<ErrorOr<World>> LoadWorld(WorldId worldId)
    {
        var world = await _db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId);
        return world is null ? Error.NotFound("party.world.notfound", "World not found.") : world;
    }

    private static string Name(Settlement? settlement, SettlementId id) => settlement?.Name ?? id.Value.ToString();

    private async Task<Stockpile?> FindMarketStockpile(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var local = _db.Stockpiles.Local.FirstOrDefault(s =>
            s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.OwnerId == settlementId.Value && s.GoodId == goodId);
        if (local is not null)
            return local;
        return await _db.Stockpiles.FirstOrDefaultAsync(s =>
            s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlementId.Value && s.GoodId == goodId);
    }

    private async Task<Stockpile> GetOrCreateMarketStockpile(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var existing = await FindMarketStockpile(worldId, settlementId, goodId);
        if (existing is not null)
            return existing;
        var created = Stockpile.Create(
            worldId, StockpileOwnerKind.SettlementMarket, settlementId.Value, goodId, 0, Money.Zero).Value;
        _db.Stockpiles.Add(created);
        return created;
    }
}
```

- [ ] **Step 2: Delete the old service**

```bash
git rm src/WorldEcon.Engine/Actions/DmActionService.cs
```

- [ ] **Step 3: Fix CLI callers**

In `src/WorldEcon.Cli/CommandRunner.cs`, replace each `new DmActionService(ctx)` with `new LogEventService(ctx)`, replace the method call `SetSettlementProductionDisabledAsync` usage stays the same name, and update result usage from `.Description` to `.Message`. Concretely, in `CmdBuy`, `CmdAdjust`, and `CmdSetDisabled`, the success print line changes from:
```csharp
Console.WriteLine(result.Value.Description);
```
to:
```csharp
Console.WriteLine(result.Value.Message);
```
and the constructor line changes to `var svc = new LogEventService(ctx);` (or inline `new LogEventService(ctx)`). Also update the `using WorldEcon.Engine.Actions;` (unchanged namespace) — no new using needed. Remove any `using WorldEcon.Domain.Actions;` left in the file.

For `CmdActions` (which listed `DmActions`): repoint it to list the unified log. Replace its body's query with:
```csharp
        var events = (await ctx.LogEvents.Where(e => e.IsPlayerAction).ToListAsync())
            .OrderBy(e => e.Sequence)
            .ToList();
        Console.WriteLine("Party/DM actions:");
        Console.WriteLine();
        if (events.Count == 0) { Console.WriteLine("  (none)"); return 0; }
        foreach (var e in events)
            Console.WriteLine($"  #{e.Sequence,-4} tick {e.OccurredTick.Value,-8} {e.Message}");
        return 0;
```
(Add `using WorldEcon.Domain.Logging;` to the file if needed for the lambda; `IsPlayerAction`/`Sequence`/`Message` are on `LogEvent`.)

- [ ] **Step 4: Fix TUI actions**

In `src/WorldEcon.Tui/Actions/BuyOutAction.cs`: change `new DmActionService(ctx.Db)` → `new LogEventService(ctx.Db)` and `result.Value.Description` → `result.Value.Message`. Update the `using WorldEcon.Engine.Actions;` (unchanged).

In `src/WorldEcon.Tui/Actions/DisableProductionAction.cs` and `EnableProductionAction.cs`: change `new DmActionService(ctx.Db)` → `new LogEventService(ctx.Db)`, keep the call to `SetSettlementProductionDisabledAsync(...)`, and change `result.Value.Description` → `result.Value.Message` wherever the success message is shown.

- [ ] **Step 5: Fix ActionTests**

In `tests/WorldEcon.Tui.Tests.Unit/ActionTests.cs`, the `BuyOutAction` test asserts `(await fresh.DmActions.CountAsync()).Should().Be(1);`. Replace with an assertion on the unified log:
```csharp
                (await fresh.LogEvents.CountAsync(e => e.IsPlayerAction)).Should().Be(1);
```
Add `using WorldEcon.Domain.Logging;` if needed. (The `AdvanceAction` test is unaffected.)

- [ ] **Step 6: Build the whole solution**

Run: `dotnet build`
Expected: `0 Error(s)`. If `WorldEcon.Cli`/`WorldEcon.Tui` still reference `DmActionService` or `.Description`, fix the remaining spots.

- [ ] **Step 7: Run the Engine and TUI test suites (Tasks 6–10 go green together)**

Run:
```bash
dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release
dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release
```
Expected: `Passed!` for both — `LogMagnitudePolicyTests`, `AncestorResolverTests`, `LogEventEmitterTests`, and the updated `ActionTests`.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(logging): LogEventService replaces DmActionService; party actions emit LogEvents"
```

---

## Task 11: Engine — emit events from phases

**Files:**
- Modify: `src/WorldEcon.Engine/Phases/TradePhase.cs`
- Modify: `src/WorldEcon.Engine/Phases/ProductionPhase.cs`
- Modify: `src/WorldEcon.Engine/Phases/ConsumptionPhase.cs`
- Modify: `src/WorldEcon.Engine/Phases/MerchantSpawnPhase.cs`
- Modify: `src/WorldEcon.Engine/Phases/PerishabilityPhase.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/PhaseLoggingTests.cs`

Each phase calls `ctx.Log.EmitAsync(...)`. Add `using WorldEcon.Domain.Logging;` to each phase file.

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/PhaseLoggingTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class PhaseLoggingTests
{
    [Test]
    public async Task MerchantSpawn_EmitsMerchantGained_VisibleAtSettlement()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            // Settlement population 50000 → at least one merchant spawns on the weekly cadence.
            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerWeek);

            var gained = await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.MerchantGained);
            gained.Should().BeGreaterThan(0);

            // It surfaces at the settlement (Notable clears the Settlement floor).
            var anyEventId = (await s.Db.LogEvents.FirstAsync(e => e.Type == LogEventType.MerchantGained)).Id;
            var scopeKinds = (await s.Db.LogEventScopes.Where(x => x.LogEventId == anyEventId).ToListAsync())
                .Select(x => x.ScopeKind).ToHashSet();
            scopeKinds.Should().Contain(LogScopeKind.Settlement);
            scopeKinds.Should().NotContain(LogScopeKind.Region); // Notable does not reach Region
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: FAIL — `MerchantGained` count is 0 (no emission wired yet).

- [ ] **Step 3: Emit from MerchantSpawnPhase**

In `src/WorldEcon.Engine/Phases/MerchantSpawnPhase.cs`, in the spawn loop, after `ctx.Db.Merchants.Add(merchant);`, add:
```csharp
                await ctx.Log.EmitAsync(LogEventType.MerchantGained,
                    $"A new merchant set up in {settlement.Name}", tick,
                    LogScopeKind.Merchant, merchant.Id.Value, settlement.Id);
```

- [ ] **Step 4: Emit from TradePhase**

In `src/WorldEcon.Engine/Phases/TradePhase.cs`:

In Step A (delivery), after `merchant?.Earn(new Money(caravan.Quantity * destPrice));` and `caravan.MarkDelivered();`, add (guard on merchant for the seat lookup):
```csharp
    await ctx.Log.EmitAsync(LogEventType.MerchantArrived,
        $"Caravan delivered {caravan.Quantity} units (good {caravan.GoodId.Value})", tick,
        LogScopeKind.Merchant, caravan.OwnerId.Value,
        merchant?.Seat);
```

In Step B (dispatch), after `ctx.Db.Caravans.Add(newCaravan); inFlight.Add(newCaravan);`, add:
```csharp
    await ctx.Log.EmitAsync(LogEventType.MerchantDeparted,
        $"Caravan dispatched {quantity} units toward settlement {bestDest.Settlement.Value}", tick,
        LogScopeKind.Merchant, merchant.Id.Value, merchant.Seat);
```

> `merchant.Seat` is a `SettlementId`; passing it (or a nullable one in Step A) supplies the ancestor chain. Notable magnitude → these surface at the merchant and its seat settlement, not above.

- [ ] **Step 5: Emit from ProductionPhase**

In `src/WorldEcon.Engine/Phases/ProductionPhase.cs`, in the completion loop, after the outputs `market.Deposit(...)` loop and `workOrder.MarkComplete();`, add:
```csharp
        await ctx.Log.EmitAsync(LogEventType.ProductionChanged,
            $"Production completed at a facility in {(await ctx.Db.Settlements.FirstAsync(x => x.Id == node.SettlementId)).Name}",
            tick, LogScopeKind.Factory, node.Id.Value, node.SettlementId);
```

> The `node` is already in scope from the completion loop (`var node = await ctx.Db.ProductionNodes.First...`). Reuse it; do not re-query if already loaded. If `node` is not in scope at that point, fetch it as shown.

- [ ] **Step 6: Emit from ConsumptionPhase (stockout)**

In `src/WorldEcon.Engine/Phases/ConsumptionPhase.cs`, inside the loop where `consume` is computed, replace the final `if (consume > 0) stock.Withdraw(...)` block with stockout detection:
```csharp
            if (consume > 0)
                stock.Withdraw(consume).OrThrow("population consumption");

            if (consume < demand)
                await ctx.Log.EmitAsync(LogEventType.Stockout,
                    $"{good.Name} ran short of demand in {settlement.Name}", tick,
                    LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);
```

- [ ] **Step 7: Emit from PerishabilityPhase (spoilage, market stock only)**

In `src/WorldEcon.Engine/Phases/PerishabilityPhase.cs`, after `if (loss > 0) stock.Withdraw(loss)...`, add a settlement-scoped spoilage event for market-owned stock (shop spoilage deferred — note it):
```csharp
            if (loss > 0 && stock.OwnerKind == StockpileOwnerKind.SettlementMarket)
                await ctx.Log.EmitAsync(LogEventType.Spoilage,
                    $"{good.Name} spoiled in a market ({loss} units)", tick,
                    LogScopeKind.Settlement, stock.OwnerId, new SettlementId(stock.OwnerId));
```
Add `using WorldEcon.Domain.Geography;` if not already present (it is, per the file header) and `using WorldEcon.Domain.Logging;`.

> The phase signature already provides `tick`; the loops here previously ignored it. That's fine.

- [ ] **Step 8: Run the test**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 9: Run the full Engine suite to confirm no regressions**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `Passed!` (existing determinism/phase tests still green; emitting log rows doesn't change economic state).

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(logging): emit events from production/trade/consumption/spawn/perishability phases"
```

---

## Task 12: Engine — retention pruning

**Files:**
- Create: `src/WorldEcon.Engine/Logging/LogRetention.cs`
- Modify: `src/WorldEcon.Engine/TickEngine.cs`
- Test: `tests/WorldEcon.Engine.Tests.Unit/LogRetentionTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Engine.Tests.Unit/LogRetentionTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class LogRetentionTests
{
    [Test]
    public async Task Prune_DropsOldRoutine_KeepsMajorAndPlayer()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var origin = Guid.NewGuid();
            var emitter = new LogEventEmitter(s.Db, s.World.Id);
            await emitter.EmitAsync(LogEventType.Trade, "old routine", new Tick(0),
                LogScopeKind.Shop, origin, s.Settlement.Id);                                   // Routine
            await emitter.EmitAsync(LogEventType.SettlementRuined, "historic", new Tick(0),
                LogScopeKind.Settlement, s.Settlement.Id.Value, s.Settlement.Id);             // Historic (never)
            await emitter.EmitAsync(LogEventType.PartyAction, "player", new Tick(0),
                LogScopeKind.Settlement, s.Settlement.Id.Value, s.Settlement.Id,
                magnitude: LogMagnitude.Routine, isPlayerAction: true);                        // player → never
            await s.Db.SaveChangesAsync();

            // Now well past the Routine age (90 in-world days).
            long now = 200 * Tick.DefaultMinutesPerDay;
            await LogRetention.PruneAsync(s.Db, s.World.Id, new Tick(now));
            await s.Db.SaveChangesAsync();

            var types = (await s.Db.LogEvents.ToListAsync()).Select(e => e.Type).ToHashSet();
            types.Should().NotContain(LogEventType.Trade);          // pruned
            types.Should().Contain(LogEventType.SettlementRuined);  // historic kept
            types.Should().Contain(LogEventType.PartyAction);       // player kept
            // Scope rows for the pruned event are gone too.
            (await s.Db.LogEventScopes.CountAsync()).Should().Be(
                await s.Db.LogEventScopes.CountAsync(x => x.ScopeId != origin || x.ScopeKind != LogScopeKind.Shop));
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Prune_IsGranularityIndependent()
    {
        async Task<int> SurvivorsAfter(int chunks, long perChunk)
        {
            var s = await LogTestWorld.CreateAsync();
            try
            {
                var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
                var engine = new TickEngine(StandardPhases.All());
                for (int i = 0; i < chunks; i++)
                    await engine.AdvanceAsync(sim, perChunk);
                return await s.Db.LogEvents.CountAsync();
            }
            finally { await LogTestWorld.DisposeAsync(s); }
        }

        long oneYear = 360 * Tick.DefaultMinutesPerDay;
        int single = await SurvivorsAfter(1, oneYear);
        int split = await SurvivorsAfter(2, oneYear / 2);
        split.Should().Be(single);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: build failure — `LogRetention` does not exist.

- [ ] **Step 3: Implement retention**

`src/WorldEcon.Engine/Logging/LogRetention.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Logging;

/// <summary>Deletes engine-generated events past their magnitude-tier age. Player actions and
/// Major/Historic events are never pruned. The cutoff is age-relative-to-now and deletions are
/// monotonic, so pruning is granularity-independent (advance(N) == k×advance(N/k) surviving set).</summary>
public static class LogRetention
{
    // Tunable now; promote to world/config params later.
    public static readonly long RoutineMaxAgeTicks = 90 * Tick.DefaultMinutesPerDay;    // 90 in-world days
    public static readonly long NotableMaxAgeTicks = 5 * 360 * Tick.DefaultMinutesPerDay; // 5 in-world years (360-day year)

    /// <summary>Marks prunable events (and their scope rows) for deletion on the tracked context.
    /// The caller's SaveChanges persists the removal.</summary>
    public static async Task PruneAsync(WorldDbContext db, WorldId worldId, Tick now)
    {
        long routineCutoff = now.Value - RoutineMaxAgeTicks;
        long notableCutoff = now.Value - NotableMaxAgeTicks;

        // WorldId equality + bool translate in SQL; the tick range check is value-converted, so do it
        // in memory after materializing the (bounded, retention-capped) candidate set.
        var candidates = await db.LogEvents
            .Where(e => e.WorldId == worldId && !e.IsPlayerAction)
            .ToListAsync();

        var prunable = candidates.Where(e => e.Magnitude switch
        {
            LogMagnitude.Routine => e.OccurredTick.Value < routineCutoff,
            LogMagnitude.Notable => e.OccurredTick.Value < notableCutoff,
            _ => false, // Major / Historic never pruned
        }).ToList();

        if (prunable.Count == 0)
            return;

        var prunableSeqs = prunable.Select(e => e.Sequence).ToHashSet();
        var scopes = await db.LogEventScopes
            .Where(x => x.WorldId == worldId && prunableSeqs.Contains(x.Sequence))
            .ToListAsync();

        db.LogEventScopes.RemoveRange(scopes);
        db.LogEvents.RemoveRange(prunable);
    }
}
```

- [ ] **Step 4: Call it from the engine**

In `src/WorldEcon.Engine/TickEngine.cs`, add `using WorldEcon.Engine.Logging;`, then in `AdvanceAsync`, after the `for` loop and **before** `await ctx.Db.SaveChangesAsync();`, insert:
```csharp
        await LogRetention.PruneAsync(ctx.Db, ctx.World.Id, ctx.World.CurrentTick);
```

- [ ] **Step 5: Run the test**

Run: `dotnet run --project tests/WorldEcon.Engine.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(logging): magnitude-tier retention pruning at end of advance"
```

---

## Task 13: Application — log query + on-demand summary

**Files:**
- Create: `src/WorldEcon.Application/Logging/LogQueryService.cs`
- Create: `src/WorldEcon.Application/Logging/ScopeSummary.cs`
- Create: `src/WorldEcon.Application/Logging/SummaryService.cs`
- Test: `tests/WorldEcon.Application.Tests.Unit/LogQueryServiceTests.cs`

> Confirm the Application project's root namespace (it hosts `PriceMarginQuery`). If it is `WorldEcon.Application`, use `WorldEcon.Application.Logging` as below. The project must reference `WorldEcon.Domain` and `WorldEcon.Persistence` (it already does for the price query).

- [ ] **Step 1: Write the failing test**

`tests/WorldEcon.Application.Tests.Unit/LogQueryServiceTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Logging;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Tests.Unit;

public class LogQueryServiceTests
{
    [Test]
    public async Task Query_ReturnsScopeEvents_NewestFirst_WithRegex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logq-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            await db.Database.MigrateAsync();

            var worldId = new WorldId(Guid.NewGuid());
            var shop = Guid.NewGuid();
            void Add(long seq, string msg)
            {
                var ev = LogEvent.Create(worldId, seq, new Tick(seq), LogEventType.Trade,
                    LogMagnitude.Routine, LogScopeKind.Shop, shop, false, "{}", msg).Value;
                db.LogEvents.Add(ev);
                db.LogEventScopes.Add(LogEventScope.Create(worldId, ev.Id, LogScopeKind.Shop, shop, seq).Value);
            }
            Add(0, "Sold 3 potions to Bob");
            Add(1, "Bought 5 iron from Alice");
            Add(2, "Sold 1 potion to Carol");
            await db.SaveChangesAsync();

            var svc = new LogQueryService(db);
            var all = await svc.QueryAsync(worldId, LogScopeKind.Shop, shop, regex: null, limit: 10);
            all.Select(e => e.Sequence).Should().Equal(2, 1, 0); // newest first

            var potions = await svc.QueryAsync(worldId, LogScopeKind.Shop, shop, regex: "potion", limit: 10);
            potions.Should().OnlyContain(e => e.Message.Contains("potion"));
            potions.Should().HaveCount(2);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Application.Tests.Unit -c Release`
Expected: build failure — `LogQueryService` does not exist.

- [ ] **Step 3: Implement LogQueryService**

`src/WorldEcon.Application/Logging/LogQueryService.cs`:
```csharp
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;

namespace WorldEcon.Application.Logging;

/// <summary>Reads the log visible at a scope, newest first. The scope read is a single indexed lookup
/// on (ScopeKind, ScopeId) ordered by the denormalized long Sequence; regex filtering (v1) is applied
/// in memory on the fetched page.</summary>
public sealed class LogQueryService
{
    private readonly WorldDbContext _db;

    public LogQueryService(WorldDbContext db) => _db = db;

    public async Task<IReadOnlyList<LogEvent>> QueryAsync(
        WorldId worldId, LogScopeKind scopeKind, Guid scopeId, string? regex, int limit)
    {
        // Pull more than `limit` when a regex will thin the page, so we still return up to `limit` matches.
        int fetch = regex is null ? limit : Math.Max(limit * 8, limit);

        var scopeRows = await _db.LogEventScopes
            .Where(x => x.WorldId == worldId && x.ScopeKind == scopeKind && x.ScopeId == scopeId)
            .OrderByDescending(x => x.Sequence)
            .Take(fetch)
            .ToListAsync();

        if (scopeRows.Count == 0)
            return [];

        var seqs = scopeRows.Select(x => x.Sequence).ToHashSet();
        var events = (await _db.LogEvents
                .Where(e => e.WorldId == worldId && seqs.Contains(e.Sequence))
                .ToListAsync())
            .OrderByDescending(e => e.Sequence)
            .AsEnumerable();

        if (regex is not null)
        {
            var rx = new Regex(regex, RegexOptions.IgnoreCase);
            events = events.Where(e => rx.IsMatch(e.Message));
        }

        return events.Take(limit).ToList();
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Application.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Implement the summary DTO + service (with a test)**

Append to `tests/WorldEcon.Application.Tests.Unit/LogQueryServiceTests.cs` a second test class file `tests/WorldEcon.Application.Tests.Unit/SummaryServiceTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Logging;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Tests.Unit;

public class SummaryServiceTests
{
    [Test]
    public async Task Summarize_CountsByTypeInWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logsum-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            await db.Database.MigrateAsync();

            var worldId = new WorldId(Guid.NewGuid());
            var settle = Guid.NewGuid();
            void Add(long seq, LogEventType type, long tick)
            {
                var ev = LogEvent.Create(worldId, seq, new Tick(tick), type, LogMagnitude.Notable,
                    LogScopeKind.Settlement, settle, false, "{}", $"{type} @ {tick}").Value;
                db.LogEvents.Add(ev);
                db.LogEventScopes.Add(LogEventScope.Create(worldId, ev.Id, LogScopeKind.Settlement, settle, seq).Value);
            }
            Add(0, LogEventType.Stockout, 10);
            Add(1, LogEventType.Stockout, 20);
            Add(2, LogEventType.ProductionChanged, 30);
            Add(3, LogEventType.Stockout, 5000); // outside window
            await db.SaveChangesAsync();

            var svc = new SummaryService(db);
            var summary = await svc.SummarizeAsync(worldId, LogScopeKind.Settlement, settle,
                from: new Tick(0), to: new Tick(100));

            summary.TotalEvents.Should().Be(3);
            summary.CountByType[LogEventType.Stockout].Should().Be(2);
            summary.CountByType[LogEventType.ProductionChanged].Should().Be(1);
        }
        finally { File.Delete(path); }
    }
}
```

`src/WorldEcon.Application/Logging/ScopeSummary.cs`:
```csharp
using WorldEcon.Domain.Logging;

namespace WorldEcon.Application.Logging;

/// <summary>An on-demand rollup of the events visible at a scope within a tick window.</summary>
public sealed record ScopeSummary(
    LogScopeKind ScopeKind,
    Guid ScopeId,
    long FromTick,
    long ToTick,
    int TotalEvents,
    IReadOnlyDictionary<LogEventType, int> CountByType,
    IReadOnlyList<LogEvent> Notable);   // Major+ events in the window, newest first
```

`src/WorldEcon.Application/Logging/SummaryService.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Logging;

/// <summary>Computes an on-demand <see cref="ScopeSummary"/> from the surviving log. A window older
/// than retention shows only what survived (documented behavior).</summary>
public sealed class SummaryService
{
    private readonly WorldDbContext _db;

    public SummaryService(WorldDbContext db) => _db = db;

    public async Task<ScopeSummary> SummarizeAsync(
        WorldId worldId, LogScopeKind scopeKind, Guid scopeId, Tick from, Tick to)
    {
        var scopeSeqs = (await _db.LogEventScopes
                .Where(x => x.WorldId == worldId && x.ScopeKind == scopeKind && x.ScopeId == scopeId)
                .Select(x => x.Sequence)
                .ToListAsync())
            .ToHashSet();

        // Tick range is value-converted → filter in memory.
        var events = (await _db.LogEvents
                .Where(e => e.WorldId == worldId && scopeSeqs.Contains(e.Sequence))
                .ToListAsync())
            .Where(e => e.OccurredTick.Value >= from.Value && e.OccurredTick.Value <= to.Value)
            .ToList();

        var countByType = events
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        var notable = events
            .Where(e => (int)e.Magnitude >= (int)LogMagnitude.Major)
            .OrderByDescending(e => e.Sequence)
            .ToList();

        return new ScopeSummary(scopeKind, scopeId, from.Value, to.Value, events.Count, countByType, notable);
    }
}
```

- [ ] **Step 6: Run to verify both Application tests pass**

Run: `dotnet run --project tests/WorldEcon.Application.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 7: Commit**

```bash
git add src/WorldEcon.Application/Logging tests/WorldEcon.Application.Tests.Unit/LogQueryServiceTests.cs tests/WorldEcon.Application.Tests.Unit/SummaryServiceTests.cs
git commit -m "feat(logging): LogQueryService + on-demand SummaryService"
```

---

## Task 14: CLI — `log` and `summary` commands

**Files:**
- Modify: `src/WorldEcon.Cli/CommandRunner.cs`
- Test: `tests/WorldEcon.Cli.Tests.Unit/LogCommandTests.cs` (create if the CLI test project exists; otherwise skip the automated test and verify manually as in Step 4)

> If `tests/WorldEcon.Cli.Tests.Unit` does not exist, do Steps 1–2 as a manual check instead of an automated test (the CLI is thin glue over the tested `LogQueryService`/`SummaryService`).

- [ ] **Step 1: Add the command routes**

In `src/WorldEcon.Cli/CommandRunner.cs`, add to the `switch` in `RunAsync`:
```csharp
            "log" => await CmdLog(args),
            "summary" => await CmdSummary(args),
```
And add `using WorldEcon.Application.Logging;`, `using WorldEcon.Domain.Logging;`.

- [ ] **Step 2: Implement the handlers**

Add to `CommandRunner.cs`. These resolve a scope by `<kind> <name>` (kind ∈ continent/country/region/city/settlement), then call the services. (Merchant/shop/factory scoping by name is awkward; v1 CLI supports the named geography scopes and `world`.)
```csharp
    // ---- log <dbPath> <kind> <name> [--regex <pattern>] [--limit <n>] ----
    private static async Task<int> CmdLog(string[] args)
    {
        if (args.Length < 4)
            return MissingArgs("log <dbPath> <world|continent|country|region|city> <name> [--regex <p>] [--limit <n>]");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();
        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null) { Console.Error.WriteLine("Error: no world found."); return 1; }

        var (kind, id) = await ResolveScope(ctx, world.Id, args[2], args[3]);
        if (id is null) { Console.Error.WriteLine($"Error: {args[2]} '{args[3]}' not found."); return 1; }

        string? regex = OptValue(args, "--regex");
        int limit = int.TryParse(OptValue(args, "--limit"), out var n) ? n : 50;

        var events = await new LogQueryService(ctx).QueryAsync(world.Id, kind, id.Value, regex, limit);
        Console.WriteLine($"Log for {args[2]} '{args[3]}' (newest first):");
        Console.WriteLine();
        if (events.Count == 0) { Console.WriteLine("  (no events)"); return 0; }
        foreach (var e in events)
            Console.WriteLine($"  tick {e.OccurredTick.Value,-8} {e.Magnitude,-8} {e.Type,-18} {e.Message}");
        return 0;
    }

    // ---- summary <dbPath> <kind> <name> [--from <tick>] [--to <tick>] ----
    private static async Task<int> CmdSummary(string[] args)
    {
        if (args.Length < 4)
            return MissingArgs("summary <dbPath> <world|continent|country|region|city> <name> [--from <tick>] [--to <tick>]");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();
        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null) { Console.Error.WriteLine("Error: no world found."); return 1; }

        var (kind, id) = await ResolveScope(ctx, world.Id, args[2], args[3]);
        if (id is null) { Console.Error.WriteLine($"Error: {args[2]} '{args[3]}' not found."); return 1; }

        long from = long.TryParse(OptValue(args, "--from"), out var f) ? f : 0;
        long to = long.TryParse(OptValue(args, "--to"), out var t) ? t : world.CurrentTick.Value;

        var sum = await new SummaryService(ctx).SummarizeAsync(world.Id, kind, id.Value,
            new WorldEcon.SharedKernel.Tick(from), new WorldEcon.SharedKernel.Tick(to));

        Console.WriteLine($"Summary for {args[2]} '{args[3]}' over ticks {from}..{to}:");
        Console.WriteLine($"  total events: {sum.TotalEvents}");
        foreach (var kv in sum.CountByType.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
            Console.WriteLine($"    {kv.Key,-18} {kv.Value}");
        if (sum.Notable.Count > 0)
        {
            Console.WriteLine("  notable:");
            foreach (var e in sum.Notable)
                Console.WriteLine($"    tick {e.OccurredTick.Value,-8} {e.Type,-18} {e.Message}");
        }
        return 0;
    }

    private static string? OptValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static async Task<(LogScopeKind Kind, Guid? Id)> ResolveScope(
        WorldDbContext ctx, WorldId worldId, string kindToken, string name)
    {
        switch (kindToken.ToLowerInvariant())
        {
            case "world":
                return (LogScopeKind.World, worldId.Value);
            case "continent":
                return (LogScopeKind.Continent,
                    (await ctx.Continents.ToListAsync()).FirstOrDefault(x => Eq(x.Name, name))?.Id.Value);
            case "country":
                return (LogScopeKind.Country,
                    (await ctx.Countries.ToListAsync()).FirstOrDefault(x => Eq(x.Name, name))?.Id.Value);
            case "region":
                return (LogScopeKind.Region,
                    (await ctx.Regions.ToListAsync()).FirstOrDefault(x => Eq(x.Name, name))?.Id.Value);
            case "city":
            case "settlement":
                return (LogScopeKind.Settlement,
                    (await ctx.Settlements.ToListAsync()).FirstOrDefault(x => Eq(x.Name, name))?.Id.Value);
            default:
                return (LogScopeKind.World, null);
        }

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
```

> `MissingArgs`, `OpenContext`, and `Unknown` already exist in `CommandRunner.cs` (used by other commands). Update `PrintUsage()` to mention `log` and `summary`.

- [ ] **Step 3: Build**

Run: `dotnet build src/WorldEcon.Cli`
Expected: `0 Error(s)`.

- [ ] **Step 4: Manual smoke test**

```bash
rm -f /tmp/logcli.db
dotnet run --project src/WorldEcon.Cli -c Release -- new /tmp/logcli.db
dotnet run --project src/WorldEcon.Cli -c Release -- advance /tmp/logcli.db 20160   # 2 weeks
dotnet run --project src/WorldEcon.Cli -c Release -- log /tmp/logcli.db city Hammerfell
dotnet run --project src/WorldEcon.Cli -c Release -- summary /tmp/logcli.db world World
rm -f /tmp/logcli.db
```
Expected: `log` prints merchant/production/stockout events for Hammerfell; `summary` prints counts by type. (Settlement name `Hammerfell` matches the `new` seed.)

- [ ] **Step 5: Commit**

```bash
git add src/WorldEcon.Cli/CommandRunner.cs
git commit -m "feat(logging): cli log and summary commands"
```

---

## Task 15: TUI — `l` log view, `/` regex filter, `:log` root, summary

**Files:**
- Modify: `src/WorldEcon.Tui/Navigation/NavView.cs` (add `Log` to `NavKind` if needed — not required; we render via a NavView)
- Modify: `src/WorldEcon.Tui/Navigation/Navigator.cs` (a `LogView` builder + a `LogScopeView`)
- Modify: `src/WorldEcon.Tui/Shell/TuiShell.cs` (the `l` key, the `/` filter, `:log` root, summary entry)
- Test: `tests/WorldEcon.Tui.Tests.Unit/LogNavTests.cs`

The shell already renders any `NavView` into the table; the log is just another `NavView`. The `l` action builds a log NavView for the selected row's entity and pushes it on the stack.

- [ ] **Step 1: Write the failing test (Navigator builds a scoped log view)**

`tests/WorldEcon.Tui.Tests.Unit/LogNavTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Tui;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Tests.Unit;

public class LogNavTests
{
    [Test]
    public async Task LogViewForScope_ListsEventsNewestFirst()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);

            var hammerfell = await ctx.Settlements.SingleAsync(s => s.Name == "Hammerfell");
            var shopId = Guid.NewGuid();
            void Add(long seq, string msg)
            {
                var ev = LogEvent.Create(tui.World.Id, seq, new WorldEcon.SharedKernel.Tick(seq),
                    LogEventType.Trade, LogMagnitude.Routine, LogScopeKind.Settlement,
                    hammerfell.Id.Value, false, "{}", msg).Value;
                ctx.LogEvents.Add(ev);
                ctx.LogEventScopes.Add(LogEventScope.Create(tui.World.Id, ev.Id,
                    LogScopeKind.Settlement, hammerfell.Id.Value, seq).Value);
            }
            Add(0, "first"); Add(1, "second");
            await ctx.SaveChangesAsync();

            var nav = new Navigator();
            var view = await nav.LogViewForScopeAsync(LogScopeKind.Settlement, hammerfell.Id.Value, "Hammerfell", null, tui);

            view.Rows.Should().HaveCount(2);
            view.Rows[0].Cells.Last().Should().Be("second"); // newest first
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release`
Expected: build failure — `Navigator.LogViewForScopeAsync` does not exist.

- [ ] **Step 3: Add the log view builder to Navigator**

In `src/WorldEcon.Tui/Navigation/Navigator.cs`, add `using WorldEcon.Application.Logging;` and `using WorldEcon.Domain.Logging;`, and a public method:
```csharp
    /// <summary>Build a log NavView for an entity scope (newest first), optionally regex-filtered.</summary>
    public async Task<NavView> LogViewForScopeAsync(
        LogScopeKind kind, Guid scopeId, string title, string? regex, TuiContext ctx)
    {
        var events = await new LogQueryService(ctx.Db).QueryAsync(ctx.World.Id, kind, scopeId, regex, limit: 500);
        var rows = events.Select(e => new NavRow(
            e.Id.Value.ToString(),
            NavKind.Leaf,
            new[]
            {
                FormatTick(ctx, e.OccurredTick),
                e.Magnitude.ToString(),
                e.Type.ToString(),
                e.Message,
            })).ToList();
        var suffix = regex is null ? "" : $"  /{regex}/";
        return new NavView($"Log — {title}{suffix}", ["Time", "Mag", "Type", "Message"], rows);
    }

    private static string FormatTick(TuiContext ctx, WorldEcon.SharedKernel.Tick tick)
    {
        var d = ctx.Calendar.ToDate(tick);
        return $"Y{d.Year} M{d.Month} D{d.Day} {d.Hour:D2}:{d.Minute:D2}";
    }
```

> `ctx.Calendar.ToDate(tick)` returns a `CalendarDate` with `Year/Month/Day/Hour/Minute` (same shape `TuiContext.CurrentDateLabel` uses). Reuse those member names.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release`
Expected: `Passed!`.

- [ ] **Step 5: Wire the `l` key, `:log` root, and `/` filter in the shell**

In `src/WorldEcon.Tui/Shell/TuiShell.cs`:

(a) Map a `LogScopeKind` for the selected row's `NavKind`. Add a helper:
```csharp
    private static WorldEcon.Domain.Logging.LogScopeKind? LogScopeFor(NavKind kind) => kind switch
    {
        NavKind.Continent => WorldEcon.Domain.Logging.LogScopeKind.Continent,
        NavKind.Country => WorldEcon.Domain.Logging.LogScopeKind.Country,
        NavKind.Region => WorldEcon.Domain.Logging.LogScopeKind.Region,
        NavKind.City => WorldEcon.Domain.Logging.LogScopeKind.Settlement,
        NavKind.Merchant => WorldEcon.Domain.Logging.LogScopeKind.Merchant,
        NavKind.Shop => WorldEcon.Domain.Logging.LogScopeKind.Shop,
        NavKind.Factory => WorldEcon.Domain.Logging.LogScopeKind.Factory,
        _ => null,
    };
```

(b) In `HandleActionKey(char ch)`, add a case for `'l'` before the global-action lookup:
```csharp
        if (ch == 'l') { ShowLog(); return true; }
```
and implement `ShowLog()` (pushes a log NavView for the selected row onto the stack, reusing the existing `_stack`/`ApplyTop()` machinery):
```csharp
    private void ShowLog()
    {
        var row = SelectedRow;
        if (row is null) return;
        var scope = LogScopeFor(row.Kind);
        if (scope is null) return;
        var title = row.Cells.Count > 0 ? row.Cells[0] : row.Kind.ToString();
        Dispatch(async () =>
        {
            var view = await _nav.LogViewForScopeAsync(scope.Value, Guid.Parse(row.Key), title, _logFilter, _ctx);
            Post(() => { PushView(view); });
        });
    }
```

> Use the shell's existing pattern for pushing a view and refreshing (the same one `DrillSelected` uses — it adds a `NavFrame` to `_stack` and calls `ApplyTop()`). If there is no reusable `PushView`, add:
> ```csharp
>     private void PushView(WorldEcon.Tui.Navigation.NavView view)
>     {
>         _stack.Add(new NavFrame(view, () => Task.FromResult(view)!));
>         ApplyTop();
>     }
> ```
> Match the actual `NavFrame` ctor signature in the file (it stores the view and a reload thunk).

(c) Add a `:log` root: the shell's command bar resolves roots via `_nav.TryResolveRoot`. `Navigator.TryResolveRoot` already maps `"log"` to `"actions"`. Change that mapping so `"log"` opens the world log instead. In `Navigator.cs` `TryResolveRoot`, change the `"actions" or "action" or "log"` arm to two arms:
```csharp
            "actions" or "action" => "actions",
            "log" or "events" => "log",
```
and add a `RootAsync` arm:
```csharp
            "log" => await LogViewForScopeAsync(LogScopeKind.World, ctx.World.Id.Value, "World", null, ctx),
```
and add `"log"` to the `Roots` array for autocomplete.

(d) Add a `/` filter: in the shell's key handler (where `:` opens the command bar), add a `'/'` branch that opens the same prompt bar but, on submit, re-builds the CURRENT log view with the typed regex. Store `_logFilter` (a `string?` field) and, when the top frame is a log view, rebuild via `LogViewForScopeAsync`. Minimal approach — only enable `/` when the current view title starts with `"Log — "`:
```csharp
        if (key.AsRune.Value == '/' && Title.StartsWith("WorldEcon — Log — ", StringComparison.Ordinal))
        {
            key.Handled = true;
            Dispatch(async () =>
            {
                var pattern = await PromptAsync("/", null);
                _logFilter = string.IsNullOrEmpty(pattern) ? null : pattern;
                // Rebuild the top log view in place using its stored scope.
                if (_currentLogScope is { } sc)
                {
                    var view = await _nav.LogViewForScopeAsync(sc.Kind, sc.Id, sc.Title, _logFilter, _ctx);
                    Post(() => { _stack[^1] = new NavFrame(view, () => Task.FromResult(view)!); ApplyTop(); });
                }
            });
            return;
        }
```
with fields:
```csharp
    private string? _logFilter;
    private (WorldEcon.Domain.Logging.LogScopeKind Kind, Guid Id, string Title)? _currentLogScope;
```
and set `_currentLogScope` in `ShowLog()` (and clear `_logFilter`) before building, and in the `:log` root path. Keep this minimal; the regex applies to the message column.

(e) Add `l log` and `/ filter` to the status-bar hints in `RefreshStatus()`:
```csharp
        var hints = new List<string> { ":cmd", "hjkl move", "enter drill", "esc back", "d details", "l log", "/ filter", "a advance", "S snapshot", "? help", "q quit" };
```

> These shell edits depend on the exact private members in `TuiShell.cs` (`_stack`, `NavFrame`, `_nav`, `_ctx`, `Dispatch`, `Post`, `PromptAsync`, `ApplyTop`, `SelectedRow`, `Title`). Read the current file and adapt names precisely; the logic above is the contract.

(f) Add a TUI summary entry. When viewing a log (`_currentLogScope` set), pressing `S` is taken (snapshot); use `:summary` instead. Add a `"summary"` root token in `Navigator.TryResolveRoot` (`"summary" => "summary"`) that, when the command bar runs it, summarizes the **currently-open log scope** (or the world if none) over ticks `0..World.CurrentTick` and shows it via `Ui.ShowMessageAsync`. In `TuiShell`, where root tokens are dispatched, special-case `"summary"`:
```csharp
        if (canonical == "summary")
        {
            var sc = _currentLogScope ?? (WorldEcon.Domain.Logging.LogScopeKind.World, _ctx.World.Id.Value, "World");
            Dispatch(async () =>
            {
                var sum = await new WorldEcon.Application.Logging.SummaryService(_ctx.Db)
                    .SummarizeAsync(_ctx.World.Id, sc.Kind, sc.Id,
                        new WorldEcon.SharedKernel.Tick(0), _ctx.World.CurrentTick);
                var lines = new List<string> { $"Total events: {sum.TotalEvents}" };
                foreach (var kv in sum.CountByType)
                    lines.Add($"{kv.Key}: {kv.Value}");
                await Ui!.ShowMessageAsync($"Summary — {sc.Title}", lines);
            });
            return;
        }
```
Do NOT add `"summary"` to `RootAsync` (it isn't a drillable view); handle it only in the command dispatch above. Add `summary` to the `:` autocomplete `Roots` list.

> Deferred (note, do not build): auto-printing a "summary since last advance" when `a` (advance) runs — the explicit `:summary` (TUI) and `summary` (CLI) commands cover the need for v1.

- [ ] **Step 6: Build and run the TUI test suite**

Run:
```bash
dotnet build src/WorldEcon.Tui
dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release
```
Expected: `0 Error(s)` and `Passed!`.

- [ ] **Step 7: Live smoke test under tmux**

```bash
rm -f /tmp/logtui.db
dotnet run --project src/WorldEcon.Cli -c Release -- new /tmp/logtui.db
dotnet run --project src/WorldEcon.Cli -c Release -- advance /tmp/logtui.db 20160
tmux kill-session -t logt 2>/dev/null; tmux new-session -d -s logt -x 200 -y 50
tmux send-keys -t logt "dotnet run --project src/WorldEcon.Tui -c Release -- /tmp/logtui.db" Enter
sleep 12
# select Hammerfell, press l for its log
tmux send-keys -t logt "l"; sleep 2; tmux capture-pane -t logt -p | head -20
# filter
tmux send-keys -t logt "/"; sleep 1; tmux send-keys -t logt "merchant" Enter; sleep 2; tmux capture-pane -t logt -p | head -20
tmux send-keys -t logt "q"; tmux kill-session -t logt 2>/dev/null; rm -f /tmp/logtui.db
```
Expected: the `l` view shows a "Log — Hammerfell" table with Time/Mag/Type/Message; the `/merchant` filter narrows to merchant rows.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(logging): TUI scoped log view (l), regex filter (/), :log root"
```

---

## Final verification

- [ ] **Run every suite:**
```bash
for p in SharedKernel Simulation Domain Persistence Application Engine Seeding Tui; do
  echo "=== $p ==="; dotnet run --project tests/WorldEcon.$p.Tests.Unit -c Release 2>&1 | grep -E "Passed!|Failed!|error" | head -3
done
```
Expected: `Passed!` for all.

- [ ] **Warnings-as-errors build:** `dotnet build` → `0 Error(s) 0 Warning(s)`.

- [ ] **Dispatch a final code review** (subagent-driven-development Step: final reviewer) against this plan + the spec, then use `superpowers:finishing-a-development-branch`.

- [ ] **Update memory + decisions log:** add the activity/event-log subsystem to `world-econ-sim-project.md` (move it from "Requested, not yet designed" to built), and append a decisions-log entry summarizing write-time visibility materialization, magnitude-tier retention granularity-independence, and the DmAction fold-in.
```
