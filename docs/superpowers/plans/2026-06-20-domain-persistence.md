# Domain & Persistence Implementation Plan (Plan 1b)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make persistence real — model the Geography aggregate, persist a world to EF Core + SQLite with a clean round-trip, and provide deterministic snapshot / branch / compare on the save file.

**Architecture:** Two new UI-agnostic projects — `WorldEcon.Domain` (entities + value objects + invariants; no EF) and `WorldEcon.Persistence` (EF Core + SQLite: `WorldDbContext`, Fluent configs, migrations, repositories, snapshot/branch/compare). The Domain depends only on SharedKernel; Persistence depends on Domain + EF. Geography is the proving vertical for the whole persistence machinery; later aggregates follow the same pattern.

**Tech Stack:** C# / .NET 10, EF Core 10 + SQLite, `ErrorOr<T>` for domain results, strongly-typed `Guid`-backed IDs, TUnit + FluentAssertions. Determinism rules from Plan 1a carry forward (integer/fixed-point; explicit `OrderBy` on queries whose order matters).

**Reference spec:** `docs/superpowers/specs/2026-06-20-world-economy-sim-csharp-build-design.md` (Build §2 conventions, §3 layout, §4 determinism/snapshots, §6 domain model, §14 persistence).

**Builds on:** Plan 1a (`SharedKernel`: `Money`, `FixedMath`, `Tick`, `Calendar.*`; `Simulation`: `CalendarSystem`, RNG). Merged to `master`.

### Scope decisions (read first)
- **Geography only this plan.** Aggregates: `World`, `Continent`, `Country`, `Region`, `Settlement`, `Route`. Economy/Demographics/Agents/Jurisdiction entities are **out of scope** here and added later following the identical pattern.
- **Hand-written strongly-typed IDs**, not a Roslyn source generator (the generator only removes boilerplate; deferred). Each ID is `readonly record struct XId(Guid Value) : IStronglyTypedId`.
- **`compare` is an in-C# structural diff** over loaded aggregates (portable, testable), not the external `sqldiff` CLI. `sqldiff`/session-changeset generalization is deferred.
- **No action log / no engine yet** (Plan 3+). Snapshots here copy the authored DB file; RNG-state-in-snapshot lands when the engine does.
- **Authored IDs use `Guid.NewGuid()`.** That is fine for DM-authored entities (they become fixed seed data). NOTE for later: entities created *during simulation ticks* must derive IDs deterministically from the RNG, not `Guid.NewGuid()`.

---

## File structure

| File | Responsibility |
|---|---|
| `src/WorldEcon.SharedKernel/Domain/IDomainEvent.cs` | Marker for domain events |
| `src/WorldEcon.SharedKernel/Domain/IStronglyTypedId.cs` | Marker interface for typed IDs |
| `src/WorldEcon.SharedKernel/Domain/Provenance.cs` | `Authored` vs `Derived` enum (spec §4.8) |
| `src/WorldEcon.SharedKernel/Domain/Entity.cs` | `Entity<TId>` base (identity equality) |
| `src/WorldEcon.SharedKernel/Domain/AggregateRoot.cs` | `AggregateRoot<TId>` base (domain-event buffer) |
| `src/WorldEcon.Domain/Geography/Ids.cs` | World/Continent/Country/Region/Settlement/Route IDs |
| `src/WorldEcon.Domain/Geography/Enums.cs` | `SettlementType`, `Terrain`, `RouteCategory` |
| `src/WorldEcon.Domain/Geography/World.cs` | World aggregate root |
| `src/WorldEcon.Domain/Geography/Continent.cs`, `Country.cs`, `Region.cs` | Jurisdiction hierarchy |
| `src/WorldEcon.Domain/Geography/Settlement.cs` | Settlement node |
| `src/WorldEcon.Domain/Geography/Route.cs` | Directed edge |
| `src/WorldEcon.Domain/Geography/IWorldRepository.cs` … | Repository interfaces (no EF) |
| `src/WorldEcon.Persistence/WorldDbContext.cs` | The DbContext |
| `src/WorldEcon.Persistence/Conversions/*.cs` | Value converters (typed IDs, Tick, ulong seed, CalendarDefinition JSON) |
| `src/WorldEcon.Persistence/Configurations/*.cs` | `IEntityTypeConfiguration<T>` per entity |
| `src/WorldEcon.Persistence/Repositories/*.cs` | Repository implementations |
| `src/WorldEcon.Persistence/Snapshots/*.cs` | `ISnapshotService`, `IBranchService`, `ICompareService` + impls |
| `src/WorldEcon.Persistence/Migrations/*` | EF migrations |
| `tests/WorldEcon.Domain.Tests.Unit/*` | Entity invariants + base equality |
| `tests/WorldEcon.Persistence.Tests.Unit/*` | Round-trip, repositories, snapshot/branch/compare |

---

## Task 0: Scaffold Domain & Persistence projects

**Files:** new projects + central package additions.

- [ ] **Step 1: Add EF + ErrorOr packages to central management**

Edit `Directory.Packages.props` — add these `<PackageVersion>` entries inside the existing `<ItemGroup>` (keep the existing TUnit + FluentAssertions entries):
```xml
    <PackageVersion Include="ErrorOr" Version="2.0.1" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0" />
```
(If a `10.0.0` EF package is not yet published, use the latest stable `10.0.x` and note the exact version. ErrorOr: use latest 2.x if 2.0.1 is unavailable.)

- [ ] **Step 2: Create `src/WorldEcon.Domain/WorldEcon.Domain.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>WorldEcon.Domain</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ErrorOr" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\WorldEcon.SharedKernel\WorldEcon.SharedKernel.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `src/WorldEcon.Persistence/WorldEcon.Persistence.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>WorldEcon.Persistence</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\WorldEcon.Domain\WorldEcon.Domain.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create the two test projects**

`tests/WorldEcon.Domain.Tests.Unit/WorldEcon.Domain.Tests.Unit.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\WorldEcon.Domain\WorldEcon.Domain.csproj" />
  </ItemGroup>
</Project>
```

`tests/WorldEcon.Persistence.Tests.Unit/WorldEcon.Persistence.Tests.Unit.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\WorldEcon.Persistence\WorldEcon.Persistence.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Register projects in the solution and set up the EF CLI tool**
```bash
dotnet sln add src/WorldEcon.Domain/WorldEcon.Domain.csproj
dotnet sln add src/WorldEcon.Persistence/WorldEcon.Persistence.csproj
dotnet sln add tests/WorldEcon.Domain.Tests.Unit/WorldEcon.Domain.Tests.Unit.csproj
dotnet sln add tests/WorldEcon.Persistence.Tests.Unit/WorldEcon.Persistence.Tests.Unit.csproj
dotnet new tool-manifest
dotnet tool install dotnet-ef
```

- [ ] **Step 6: Build to verify everything restores and compiles**

Run: `dotnet build`
Expected: build succeeds, 0 warnings (warnings-as-errors). No tests yet in the new projects — that's fine.

- [ ] **Step 7: Commit**
```bash
git add -A
git commit -m "chore: scaffold Domain and Persistence projects with EF Core + ErrorOr"
```

---

## Task 1: SharedKernel domain base types

**Files:**
- Create: `src/WorldEcon.SharedKernel/Domain/IDomainEvent.cs`, `IStronglyTypedId.cs`, `Provenance.cs`, `Entity.cs`, `AggregateRoot.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/EntityBaseTests.cs`

> `Entity<TId>` equality is by concrete type + Id (two entities are equal iff same runtime type and same Id). This matters because EF tracks entities by identity and tests assert equality.

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/EntityBaseTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit;

public class EntityBaseTests
{
    private readonly record struct FooId(Guid Value) : IStronglyTypedId;
    private readonly record struct BarId(Guid Value) : IStronglyTypedId;

    private sealed class Foo(FooId id) : Entity<FooId>(id);
    private sealed class Bar(BarId id) : Entity<BarId>(id);

    private sealed class FooAggregate(FooId id) : AggregateRoot<FooId>(id)
    {
        public void DoThing() => Raise(new ThingHappened());
    }
    private sealed record ThingHappened : IDomainEvent;

    [Test]
    public void Entities_WithSameTypeAndId_AreEqual()
    {
        var id = new FooId(Guid.NewGuid());
        new Foo(id).Should().Be(new Foo(id));
    }

    [Test]
    public void Entities_WithDifferentIds_AreNotEqual()
        => new Foo(new FooId(Guid.NewGuid())).Should().NotBe(new Foo(new FooId(Guid.NewGuid())));

    [Test]
    public void RaisedEvents_AreExposed_AndClearable()
    {
        var agg = new FooAggregate(new FooId(Guid.NewGuid()));
        agg.DoThing();
        agg.DomainEvents.Should().ContainSingle();
        agg.ClearDomainEvents();
        agg.DomainEvents.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: compile failure (base types do not exist).

- [ ] **Step 3: Implement the base types**

Create `src/WorldEcon.SharedKernel/Domain/IDomainEvent.cs`:
```csharp
namespace WorldEcon.SharedKernel.Domain;

/// <summary>Marker for an in-process domain event raised by an aggregate.</summary>
public interface IDomainEvent;
```

Create `src/WorldEcon.SharedKernel/Domain/IStronglyTypedId.cs`:
```csharp
namespace WorldEcon.SharedKernel.Domain;

/// <summary>Marker for a strongly-typed, Guid-backed identifier.</summary>
public interface IStronglyTypedId
{
    Guid Value { get; }
}
```

Create `src/WorldEcon.SharedKernel/Domain/Provenance.cs`:
```csharp
namespace WorldEcon.SharedKernel.Domain;

/// <summary>Whether a value is DM canon or simulation-evolved (spec §4.8).</summary>
public enum Provenance
{
    Authored = 0,
    Derived = 1,
}
```

Create `src/WorldEcon.SharedKernel/Domain/Entity.cs`:
```csharp
namespace WorldEcon.SharedKernel.Domain;

/// <summary>Base class for entities with identity equality (same concrete type + same Id).</summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct, IStronglyTypedId
{
    public TId Id { get; }

    protected Entity(TId id) => Id = id;

    public bool Equals(Entity<TId>? other)
        => other is not null && other.GetType() == GetType() && other.Id.Equals(Id);

    public override bool Equals(object? obj) => obj is Entity<TId> e && Equals(e);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
```

Create `src/WorldEcon.SharedKernel/Domain/AggregateRoot.cs`:
```csharp
namespace WorldEcon.SharedKernel.Domain;

/// <summary>An entity that is the root of an aggregate and can raise in-process domain events.</summary>
public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id)
    where TId : struct, IStronglyTypedId
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: all tests pass.

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.SharedKernel/Domain tests/WorldEcon.Domain.Tests.Unit/EntityBaseTests.cs
git commit -m "feat: add domain base types (Entity, AggregateRoot, IDomainEvent, Provenance)"
```

---

## Task 2: Geography IDs & enums

**Files:**
- Create: `src/WorldEcon.Domain/Geography/Ids.cs`, `src/WorldEcon.Domain/Geography/Enums.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/Geography/IdTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/Geography/IdTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class IdTests
{
    [Test]
    public void New_ProducesUniqueNonEmptyIds()
    {
        var a = SettlementId.New();
        var b = SettlementId.New();
        a.Should().NotBe(b);
        a.Value.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void Ids_AreStronglyTyped()
        => WorldId.New().Should().BeAssignableTo<IStronglyTypedId>();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: compile failure (IDs/enums do not exist).

- [ ] **Step 3: Implement IDs and enums**

Create `src/WorldEcon.Domain/Geography/Ids.cs`:
```csharp
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public readonly record struct WorldId(Guid Value) : IStronglyTypedId { public static WorldId New() => new(Guid.NewGuid()); }
public readonly record struct ContinentId(Guid Value) : IStronglyTypedId { public static ContinentId New() => new(Guid.NewGuid()); }
public readonly record struct CountryId(Guid Value) : IStronglyTypedId { public static CountryId New() => new(Guid.NewGuid()); }
public readonly record struct RegionId(Guid Value) : IStronglyTypedId { public static RegionId New() => new(Guid.NewGuid()); }
public readonly record struct SettlementId(Guid Value) : IStronglyTypedId { public static SettlementId New() => new(Guid.NewGuid()); }
public readonly record struct RouteId(Guid Value) : IStronglyTypedId { public static RouteId New() => new(Guid.NewGuid()); }
```

Create `src/WorldEcon.Domain/Geography/Enums.cs`:
```csharp
namespace WorldEcon.Domain.Geography;

public enum SettlementType { Village = 0, Town = 1, City = 2 }

public enum Terrain { Plains = 0, Forest = 1, Mountain = 2, Desert = 3, Coast = 4, Sea = 5 }

public enum RouteCategory { Land = 0, ShippingLane = 1 }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: all tests pass.

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.Domain/Geography/Ids.cs src/WorldEcon.Domain/Geography/Enums.cs tests/WorldEcon.Domain.Tests.Unit/Geography/IdTests.cs
git commit -m "feat: add geography strongly-typed IDs and enums"
```

---

## Task 3: World aggregate + jurisdiction hierarchy

**Files:**
- Create: `src/WorldEcon.Domain/Geography/World.cs`, `Continent.cs`, `Country.cs`, `Region.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/Geography/HierarchyTests.cs`

> Factories return `ErrorOr<T>` and validate inputs. Names must be non-empty/non-whitespace. `World` holds the seed, calendar, current tick, and ruleset version (spec §6.2).

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/Geography/HierarchyTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class HierarchyTests
{
    [Test]
    public void World_Create_SetsFieldsAndStartsAtTickZero()
    {
        var result = World.Create("Aerth", seed: 1234UL, CalendarDefinition.Default, rulesetVersion: "1.0.0");
        result.IsError.Should().BeFalse();
        var w = result.Value;
        w.Name.Should().Be("Aerth");
        w.Seed.Should().Be(1234UL);
        w.CurrentTick.Should().Be(Tick.Zero);
        w.RulesetVersion.Should().Be("1.0.0");
    }

    [Test]
    public void World_Create_RejectsBlankName()
        => World.Create("  ", 1UL, CalendarDefinition.Default, "1.0.0").IsError.Should().BeTrue();

    [Test]
    public void Continent_Create_RejectsBlankName()
        => Continent.Create(WorldId.New(), " ").IsError.Should().BeTrue();

    [Test]
    public void Country_Create_LinksToContinentAndWorld()
    {
        var wid = WorldId.New();
        var cid = ContinentId.New();
        var c = Country.Create(wid, cid, "Highmark").Value;
        c.WorldId.Should().Be(wid);
        c.ContinentId.Should().Be(cid);
        c.Name.Should().Be("Highmark");
    }

    [Test]
    public void Region_Create_LinksToCountryAndWorld()
    {
        var wid = WorldId.New();
        var coid = CountryId.New();
        var r = Region.Create(wid, coid, "The Reach").Value;
        r.WorldId.Should().Be(wid);
        r.CountryId.Should().Be(coid);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: compile failure (types do not exist).

- [ ] **Step 3: Implement the hierarchy**

Create `src/WorldEcon.Domain/Geography/World.cs`:
```csharp
using ErrorOr;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class World : AggregateRoot<WorldId>
{
    public string Name { get; private set; }
    public ulong Seed { get; }
    public CalendarDefinition Calendar { get; }
    public Tick CurrentTick { get; private set; }
    public string RulesetVersion { get; private set; }

    // Constructor used by EF and the factory.
    private World(WorldId id, string name, ulong seed, CalendarDefinition calendar, Tick currentTick, string rulesetVersion)
        : base(id)
    {
        Name = name;
        Seed = seed;
        Calendar = calendar;
        CurrentTick = currentTick;
        RulesetVersion = rulesetVersion;
    }

    public static ErrorOr<World> Create(string name, ulong seed, CalendarDefinition calendar, string rulesetVersion)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("world.name.blank", "World name must not be blank.");
        if (string.IsNullOrWhiteSpace(rulesetVersion))
            return Error.Validation("world.ruleset.blank", "Ruleset version must not be blank.");

        return new World(WorldId.New(), name.Trim(), seed, calendar, Tick.Zero, rulesetVersion);
    }
}
```

Create `src/WorldEcon.Domain/Geography/Continent.cs`:
```csharp
using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class Continent : AggregateRoot<ContinentId>
{
    public WorldId WorldId { get; }
    public string Name { get; private set; }

    private Continent(ContinentId id, WorldId worldId, string name) : base(id)
    {
        WorldId = worldId;
        Name = name;
    }

    public static ErrorOr<Continent> Create(WorldId worldId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("continent.name.blank", "Continent name must not be blank.");
        return new Continent(ContinentId.New(), worldId, name.Trim());
    }
}
```

Create `src/WorldEcon.Domain/Geography/Country.cs`:
```csharp
using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class Country : AggregateRoot<CountryId>
{
    public WorldId WorldId { get; }
    public ContinentId ContinentId { get; }
    public string Name { get; private set; }

    private Country(CountryId id, WorldId worldId, ContinentId continentId, string name) : base(id)
    {
        WorldId = worldId;
        ContinentId = continentId;
        Name = name;
    }

    public static ErrorOr<Country> Create(WorldId worldId, ContinentId continentId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("country.name.blank", "Country name must not be blank.");
        return new Country(CountryId.New(), worldId, continentId, name.Trim());
    }
}
```

Create `src/WorldEcon.Domain/Geography/Region.cs`:
```csharp
using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class Region : AggregateRoot<RegionId>
{
    public WorldId WorldId { get; }
    public CountryId CountryId { get; }
    public string Name { get; private set; }

    private Region(RegionId id, WorldId worldId, CountryId countryId, string name) : base(id)
    {
        WorldId = worldId;
        CountryId = countryId;
        Name = name;
    }

    public static ErrorOr<Region> Create(WorldId worldId, CountryId countryId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("region.name.blank", "Region name must not be blank.");
        return new Region(RegionId.New(), worldId, countryId, name.Trim());
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: all tests pass.

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.Domain/Geography/World.cs src/WorldEcon.Domain/Geography/Continent.cs src/WorldEcon.Domain/Geography/Country.cs src/WorldEcon.Domain/Geography/Region.cs tests/WorldEcon.Domain.Tests.Unit/Geography/HierarchyTests.cs
git commit -m "feat: add World aggregate and jurisdiction hierarchy with validation"
```

---

## Task 4: Settlement & Route

**Files:**
- Create: `src/WorldEcon.Domain/Geography/Settlement.cs`, `Route.cs`
- Test: `tests/WorldEcon.Domain.Tests.Unit/Geography/SettlementAndRouteTests.cs`

> Invariants: population ≥ 0; route distance > 0; danger ≥ 0; a route may not connect a settlement to itself.

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Domain.Tests.Unit/Geography/SettlementAndRouteTests.cs`:
```csharp
using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class SettlementAndRouteTests
{
    [Test]
    public void Settlement_Create_SetsFields_DefaultProvenanceAuthored()
    {
        var s = Settlement.Create(WorldId.New(), RegionId.New(), "Hammerfell",
            SettlementType.City, x: 10, y: 20, population: 50_000).Value;
        s.Name.Should().Be("Hammerfell");
        s.Type.Should().Be(SettlementType.City);
        s.X.Should().Be(10);
        s.Y.Should().Be(20);
        s.Population.Should().Be(50_000);
        s.Provenance.Should().Be(Provenance.Authored);
    }

    [Test]
    public void Settlement_Create_RejectsNegativePopulation()
        => Settlement.Create(WorldId.New(), RegionId.New(), "X", SettlementType.Town, 0, 0, -1)
            .IsError.Should().BeTrue();

    [Test]
    public void Route_Create_SetsDirectedEdge()
    {
        var from = SettlementId.New();
        var to = SettlementId.New();
        var r = Route.Create(WorldId.New(), from, to, distance: 120, Terrain.Plains, danger: 3, RouteCategory.Land).Value;
        r.FromSettlementId.Should().Be(from);
        r.ToSettlementId.Should().Be(to);
        r.Distance.Should().Be(120);
        r.Danger.Should().Be(3);
        r.Category.Should().Be(RouteCategory.Land);
    }

    [Test]
    public void Route_Create_RejectsNonPositiveDistance()
        => Route.Create(WorldId.New(), SettlementId.New(), SettlementId.New(), 0, Terrain.Plains, 0, RouteCategory.Land)
            .IsError.Should().BeTrue();

    [Test]
    public void Route_Create_RejectsSelfLoop()
    {
        var s = SettlementId.New();
        Route.Create(WorldId.New(), s, s, 10, Terrain.Plains, 0, RouteCategory.Land).IsError.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: compile failure.

- [ ] **Step 3: Implement Settlement and Route**

Create `src/WorldEcon.Domain/Geography/Settlement.cs`:
```csharp
using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class Settlement : AggregateRoot<SettlementId>
{
    public WorldId WorldId { get; }
    public RegionId RegionId { get; private set; }
    public string Name { get; private set; }
    public SettlementType Type { get; private set; }
    public int X { get; private set; }              // display coordinate only (spec §9.1)
    public int Y { get; private set; }
    public long Population { get; private set; }
    public Provenance Provenance { get; private set; }

    private Settlement(SettlementId id, WorldId worldId, RegionId regionId, string name,
        SettlementType type, int x, int y, long population, Provenance provenance) : base(id)
    {
        WorldId = worldId;
        RegionId = regionId;
        Name = name;
        Type = type;
        X = x;
        Y = y;
        Population = population;
        Provenance = provenance;
    }

    public static ErrorOr<Settlement> Create(WorldId worldId, RegionId regionId, string name,
        SettlementType type, int x, int y, long population)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("settlement.name.blank", "Settlement name must not be blank.");
        if (population < 0)
            return Error.Validation("settlement.population.negative", "Population must not be negative.");

        return new Settlement(SettlementId.New(), worldId, regionId, name.Trim(),
            type, x, y, population, Provenance.Authored);
    }
}
```

Create `src/WorldEcon.Domain/Geography/Route.cs`:
```csharp
using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>A directed edge between two settlements (spec §9.1–9.2).</summary>
public sealed class Route : AggregateRoot<RouteId>
{
    public WorldId WorldId { get; }
    public SettlementId FromSettlementId { get; private set; }
    public SettlementId ToSettlementId { get; private set; }
    public long Distance { get; private set; }
    public Terrain Terrain { get; private set; }
    public int Danger { get; private set; }
    public RouteCategory Category { get; private set; }

    private Route(RouteId id, WorldId worldId, SettlementId from, SettlementId to,
        long distance, Terrain terrain, int danger, RouteCategory category) : base(id)
    {
        WorldId = worldId;
        FromSettlementId = from;
        ToSettlementId = to;
        Distance = distance;
        Terrain = terrain;
        Danger = danger;
        Category = category;
    }

    public static ErrorOr<Route> Create(WorldId worldId, SettlementId from, SettlementId to,
        long distance, Terrain terrain, int danger, RouteCategory category)
    {
        if (from.Equals(to))
            return Error.Validation("route.selfloop", "A route may not connect a settlement to itself.");
        if (distance <= 0)
            return Error.Validation("route.distance.nonpositive", "Distance must be positive.");
        if (danger < 0)
            return Error.Validation("route.danger.negative", "Danger must not be negative.");

        return new Route(RouteId.New(), worldId, from, to, distance, terrain, danger, category);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Domain.Tests.Unit`
Expected: all tests pass.

- [ ] **Step 5: Commit**
```bash
git add src/WorldEcon.Domain/Geography/Settlement.cs src/WorldEcon.Domain/Geography/Route.cs tests/WorldEcon.Domain.Tests.Unit/Geography/SettlementAndRouteTests.cs
git commit -m "feat: add Settlement and Route entities with invariants"
```

---

## Task 5: WorldDbContext, value converters & configurations

**Files:**
- Create: `src/WorldEcon.Persistence/Conversions/StronglyTypedIdConverters.cs`, `ValueConverters.cs`
- Create: `src/WorldEcon.Persistence/Configurations/*.cs` (one per entity)
- Create: `src/WorldEcon.Persistence/WorldDbContext.cs`
- Create: `src/WorldEcon.Persistence/WorldDbContextFactory.cs` (design-time factory for migrations)
- Test: deferred to Task 6 (the round-trip test exercises this end-to-end)

> SQLite has no unsigned 64-bit and no native Guid; converters bridge these. `CalendarDefinition` is stored as a JSON string. Every typed ID converts to `Guid`. `Tick` converts to `long`. `ulong` Seed bit-casts to `long`.

- [ ] **Step 1: Implement the strongly-typed ID converters**

Create `src/WorldEcon.Persistence/Conversions/StronglyTypedIdConverters.cs`:
```csharp
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Conversions;

public sealed class WorldIdConverter() : ValueConverter<WorldId, Guid>(v => v.Value, g => new WorldId(g));
public sealed class ContinentIdConverter() : ValueConverter<ContinentId, Guid>(v => v.Value, g => new ContinentId(g));
public sealed class CountryIdConverter() : ValueConverter<CountryId, Guid>(v => v.Value, g => new CountryId(g));
public sealed class RegionIdConverter() : ValueConverter<RegionId, Guid>(v => v.Value, g => new RegionId(g));
public sealed class SettlementIdConverter() : ValueConverter<SettlementId, Guid>(v => v.Value, g => new SettlementId(g));
public sealed class RouteIdConverter() : ValueConverter<RouteId, Guid>(v => v.Value, g => new RouteId(g));
```

- [ ] **Step 2: Implement the Tick / ulong / CalendarDefinition converters**

Create `src/WorldEcon.Persistence/Conversions/ValueConverters.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Persistence.Conversions;

public sealed class TickConverter() : ValueConverter<Tick, long>(t => t.Value, v => new Tick(v));

/// <summary>SQLite has no unsigned 64-bit; bit-cast ulong&lt;-&gt;long round-trips all values.</summary>
public sealed class UInt64Converter() : ValueConverter<ulong, long>(v => unchecked((long)v), v => unchecked((ulong)v));

public sealed class CalendarDefinitionConverter() : ValueConverter<CalendarDefinition, string>(
    c => JsonSerializer.Serialize(c, Options),
    s => JsonSerializer.Deserialize<CalendarDefinition>(s, Options)!)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);
}
```

- [ ] **Step 3: Implement the entity configurations**

Create `src/WorldEcon.Persistence/Configurations/WorldConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class WorldConfiguration : IEntityTypeConfiguration<World>
{
    public void Configure(EntityTypeBuilder<World> b)
    {
        b.ToTable("worlds");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Seed).HasConversion<UInt64Converter>();
        b.Property(x => x.CurrentTick).HasConversion<TickConverter>();
        b.Property(x => x.Calendar).HasConversion<CalendarDefinitionConverter>();
        b.Property(x => x.RulesetVersion).IsRequired();
        b.Ignore(x => x.DomainEvents);
    }
}
```

Create `src/WorldEcon.Persistence/Configurations/ContinentConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Configurations;

public sealed class ContinentConfiguration : IEntityTypeConfiguration<Continent>
{
    public void Configure(EntityTypeBuilder<Continent> b)
    {
        b.ToTable("continents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.HasIndex(x => x.WorldId);
        b.Ignore(x => x.DomainEvents);
    }
}
```

Create `src/WorldEcon.Persistence/Configurations/CountryConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Configurations;

public sealed class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> b)
    {
        b.ToTable("countries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.ContinentId);
        b.Ignore(x => x.DomainEvents);
    }
}
```

Create `src/WorldEcon.Persistence/Configurations/RegionConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Configurations;

public sealed class RegionConfiguration : IEntityTypeConfiguration<Region>
{
    public void Configure(EntityTypeBuilder<Region> b)
    {
        b.ToTable("regions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.CountryId);
        b.Ignore(x => x.DomainEvents);
    }
}
```

Create `src/WorldEcon.Persistence/Configurations/SettlementConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Configurations;

public sealed class SettlementConfiguration : IEntityTypeConfiguration<Settlement>
{
    public void Configure(EntityTypeBuilder<Settlement> b)
    {
        b.ToTable("settlements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Type).HasConversion<string>();
        b.Property(x => x.Provenance).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.RegionId);
        b.Ignore(x => x.DomainEvents);
    }
}
```

Create `src/WorldEcon.Persistence/Configurations/RouteConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Configurations;

public sealed class RouteConfiguration : IEntityTypeConfiguration<Route>
{
    public void Configure(EntityTypeBuilder<Route> b)
    {
        b.ToTable("routes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Terrain).HasConversion<string>();
        b.Property(x => x.Category).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.FromSettlementId);
        b.HasIndex(x => x.ToSettlementId);
        b.Ignore(x => x.DomainEvents);
    }
}
```

- [ ] **Step 4: Implement the DbContext**

Create `src/WorldEcon.Persistence/WorldDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence;

public sealed class WorldDbContext(DbContextOptions<WorldDbContext> options) : DbContext(options)
{
    public DbSet<World> Worlds => Set<World>();
    public DbSet<Continent> Continents => Set<Continent>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<Route> Routes => Set<Route>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<WorldId>().HaveConversion<WorldIdConverter>();
        b.Properties<ContinentId>().HaveConversion<ContinentIdConverter>();
        b.Properties<CountryId>().HaveConversion<CountryIdConverter>();
        b.Properties<RegionId>().HaveConversion<RegionIdConverter>();
        b.Properties<SettlementId>().HaveConversion<SettlementIdConverter>();
        b.Properties<RouteId>().HaveConversion<RouteIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
        => b.ApplyConfigurationsFromAssembly(typeof(WorldDbContext).Assembly);
}
```

- [ ] **Step 5: Implement the design-time factory (needed by `dotnet ef`)**

Create `src/WorldEcon.Persistence/WorldDbContextFactory.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WorldEcon.Persistence;

/// <summary>Used only by the EF CLI at design time to build migrations.</summary>
public sealed class WorldDbContextFactory : IDesignTimeDbContextFactory<WorldDbContext>
{
    public WorldDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new WorldDbContext(options);
    }
}
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build`
Expected: build succeeds, 0 warnings.

- [ ] **Step 7: Commit**
```bash
git add src/WorldEcon.Persistence/Conversions src/WorldEcon.Persistence/Configurations src/WorldEcon.Persistence/WorldDbContext.cs src/WorldEcon.Persistence/WorldDbContextFactory.cs
git commit -m "feat: add WorldDbContext, value converters, and entity configurations"
```

---

## Task 6: Initial migration + persistence round-trip

**Files:**
- Create: `src/WorldEcon.Persistence/Migrations/*` (generated)
- Create: `tests/WorldEcon.Persistence.Tests.Unit/RoundTripTests.cs`

- [ ] **Step 1: Generate the initial migration**

Run:
```bash
dotnet ef migrations add InitialGeography --project src/WorldEcon.Persistence --startup-project src/WorldEcon.Persistence
```
Expected: a `Migrations/` folder with `*_InitialGeography.cs` + the model snapshot. Then `dotnet build` to confirm it compiles.

- [ ] **Step 2: Write the failing round-trip test**

Create `tests/WorldEcon.Persistence.Tests.Unit/RoundTripTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;

namespace WorldEcon.Persistence.Tests.Unit;

public class RoundTripTests
{
    private static WorldDbContext NewContextOnFile(string path)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new WorldDbContext(options);
    }

    [Test]
    public async Task World_AndGeography_PersistAndReload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 4242UL, CalendarDefinition.Default, "1.0.0").Value;
            var continent = Continent.Create(world.Id, "Mundus").Value;
            var country = Country.Create(world.Id, continent.Id, "Highmark").Value;
            var region = Region.Create(world.Id, country.Id, "The Reach").Value;
            var hammerfell = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 10, 20, 50_000).Value;
            var riverwood = Settlement.Create(world.Id, region.Id, "Riverwood", SettlementType.Village, 12, 25, 800).Value;
            var route = Route.Create(world.Id, hammerfell.Id, riverwood.Id, 120, Terrain.Plains, 3, RouteCategory.Land).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.Add(continent);
                ctx.Countries.Add(country);
                ctx.Regions.Add(region);
                ctx.Settlements.AddRange(hammerfell, riverwood);
                ctx.Routes.Add(route);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var w = await ctx.Worlds.SingleAsync();
                w.Name.Should().Be("Aerth");
                w.Seed.Should().Be(4242UL);
                w.CurrentTick.Should().Be(Tick.Zero);
                w.Calendar.Months.Should().HaveCount(12); // JSON round-trip survived

                (await ctx.Settlements.CountAsync()).Should().Be(2);
                var loadedRoute = await ctx.Routes.SingleAsync();
                loadedRoute.FromSettlementId.Should().Be(hammerfell.Id);
                loadedRoute.Category.Should().Be(RouteCategory.Land);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 3: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit`
Expected: PASS. Two known risk points — if either fails, **report the exact exception; do not silently change the model shape**:
- **EF constructor binding.** The entities have only private *parameterized* constructors (no parameterless ctor). EF Core binds these by matching ctor parameter names to property names (`id→Id`, `seed→Seed`, etc.), which is supported. If EF cannot bind (e.g. it rejects binding the value-converted `Calendar`/`CurrentTick`/`Seed` params), the minimal fix is to add a `private World() { }`-style parameterless ctor to each entity (EF then sets the `private set` properties directly); apply uniformly and note it.
- **CalendarDefinition JSON round-trip.** If deserialization of the `IReadOnlyList<MonthDef>`/`IReadOnlyList<SeasonDef>`/`IReadOnlyList<string>` members fails, confirm the converter uses a `JsonSerializerOptions` that deserializes records via their primary constructor (System.Text.Json supports records and deserializes `IReadOnlyList<T>` to `List<T>` by default). Adjust options only — keep `CalendarDefinition`'s shape.

- [ ] **Step 4: Commit**
```bash
git add src/WorldEcon.Persistence/Migrations tests/WorldEcon.Persistence.Tests.Unit/RoundTripTests.cs
git commit -m "feat: add initial migration and prove world+geography round-trip"
```

---

## Task 7: Repositories

**Files:**
- Create: `src/WorldEcon.Domain/Geography/IWorldRepository.cs`, `ISettlementRepository.cs`, `IRouteRepository.cs`
- Create: `src/WorldEcon.Persistence/Repositories/WorldRepository.cs`, `SettlementRepository.cs`, `RouteRepository.cs`
- Test: `tests/WorldEcon.Persistence.Tests.Unit/RepositoryTests.cs`

> Repository interfaces live in Domain (no EF). All list queries carry an explicit, stable `OrderBy(x => x.Id.Value)` — determinism rule (spec Build §4.3.3): query order must never be incidental.

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Persistence.Tests.Unit/RepositoryTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Repositories;

namespace WorldEcon.Persistence.Tests.Unit;

public class RepositoryTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task SettlementRepository_AddGetList_ByWorld_IsDeterministicallyOrdered()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            var continent = Continent.Create(world.Id, "C").Value;
            var country = Country.Create(world.Id, continent.Id, "Co").Value;
            var region = Region.Create(world.Id, country.Id, "R").Value;
            var s1 = Settlement.Create(world.Id, region.Id, "A", SettlementType.Town, 0, 0, 100).Value;
            var s2 = Settlement.Create(world.Id, region.Id, "B", SettlementType.Town, 1, 1, 200).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.Add(continent);
                ctx.Countries.Add(country);
                ctx.Regions.Add(region);
                ctx.Settlements.AddRange(s1, s2);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var repo = new SettlementRepository(ctx);

                var fetched = await repo.GetAsync(s1.Id);
                fetched!.Name.Should().Be("A");

                var all = await repo.ListByWorldAsync(world.Id);
                all.Should().HaveCount(2);
                // stable ordering by Id.Value across reloads
                var again = await repo.ListByWorldAsync(world.Id);
                again.Select(x => x.Id).Should().Equal(all.Select(x => x.Id));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit`
Expected: compile failure (repository types do not exist).

- [ ] **Step 3: Implement the interfaces (Domain)**

Create `src/WorldEcon.Domain/Geography/IWorldRepository.cs`:
```csharp
namespace WorldEcon.Domain.Geography;

public interface IWorldRepository
{
    Task<World?> GetAsync(WorldId id);
    Task AddAsync(World world);
}
```

Create `src/WorldEcon.Domain/Geography/ISettlementRepository.cs`:
```csharp
namespace WorldEcon.Domain.Geography;

public interface ISettlementRepository
{
    Task<Settlement?> GetAsync(SettlementId id);
    Task<IReadOnlyList<Settlement>> ListByWorldAsync(WorldId worldId);
    Task AddAsync(Settlement settlement);
}
```

Create `src/WorldEcon.Domain/Geography/IRouteRepository.cs`:
```csharp
namespace WorldEcon.Domain.Geography;

public interface IRouteRepository
{
    Task<IReadOnlyList<Route>> ListByWorldAsync(WorldId worldId);
    Task AddAsync(Route route);
}
```

- [ ] **Step 4: Implement the repositories (Persistence)**

Create `src/WorldEcon.Persistence/Repositories/WorldRepository.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class WorldRepository(WorldDbContext context) : IWorldRepository
{
    public Task<World?> GetAsync(WorldId id) => context.Worlds.FirstOrDefaultAsync(w => w.Id == id);
    public async Task AddAsync(World world) => await context.Worlds.AddAsync(world);
}
```

Create `src/WorldEcon.Persistence/Repositories/SettlementRepository.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class SettlementRepository(WorldDbContext context) : ISettlementRepository
{
    public Task<Settlement?> GetAsync(SettlementId id)
        => context.Settlements.FirstOrDefaultAsync(s => s.Id == id);

    public async Task<IReadOnlyList<Settlement>> ListByWorldAsync(WorldId worldId)
    {
        var list = await context.Settlements.Where(s => s.WorldId == worldId).ToListAsync();
        // Deterministic, stable order. Done in memory because ordering by a value-converted
        // typed-ID member does not reliably translate to SQL; the set is small and fully loaded,
        // so in-memory ordering is fully reproducible (spec Build §4.3.3).
        return list.OrderBy(s => s.Id.Value).ToList();
    }

    public async Task AddAsync(Settlement settlement) => await context.Settlements.AddAsync(settlement);
}
```

Create `src/WorldEcon.Persistence/Repositories/RouteRepository.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class RouteRepository(WorldDbContext context) : IRouteRepository
{
    public async Task<IReadOnlyList<Route>> ListByWorldAsync(WorldId worldId)
    {
        var list = await context.Routes.Where(r => r.WorldId == worldId).ToListAsync();
        // Deterministic, stable in-memory order (see SettlementRepository for rationale).
        return list.OrderBy(r => r.Id.Value).ToList();
    }

    public async Task AddAsync(Route route) => await context.Routes.AddAsync(route);
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit`
Expected: all tests pass.

> Ordering is done in memory (after `ToListAsync()`) by `Id.Value`, which is always deterministic and avoids the question of whether a value-converted typed-ID member translates to SQL. This is fine at world-authoring scale. If a later aggregate grows large enough that DB-side ordering matters, add an explicit monotonic sequence column then — do not push ordering into an untranslatable LINQ expression.

- [ ] **Step 6: Commit**
```bash
git add src/WorldEcon.Domain/Geography/I*Repository.cs src/WorldEcon.Persistence/Repositories tests/WorldEcon.Persistence.Tests.Unit/RepositoryTests.cs
git commit -m "feat: add geography repositories with deterministic ordering"
```

---

## Task 8: Snapshot, branch & compare

**Files:**
- Create: `src/WorldEcon.Persistence/Snapshots/ISnapshotService.cs`, `IBranchService.cs`, `ICompareService.cs`, `WorldDiff.cs`
- Create: `src/WorldEcon.Persistence/Snapshots/SqliteSnapshotService.cs`, `FileBranchService.cs`, `StructuralCompareService.cs`
- Test: `tests/WorldEcon.Persistence.Tests.Unit/SnapshotTests.cs`

> Snapshot = a consistent file copy via `VACUUM INTO` (spec Build §4.2). Branch = snapshot to a new path (the copy is an independent world DB). Compare = an in-C# structural diff over loaded settlements (added/removed/changed). The services take a SQLite file path, not a live context, because they operate at the file level.

- [ ] **Step 1: Write the failing test**

Create `tests/WorldEcon.Persistence.Tests.Unit/SnapshotTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Snapshots;

namespace WorldEcon.Persistence.Tests.Unit;

public class SnapshotTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private static async Task<(WorldId world, RegionId region)> SeedAsync(string path)
    {
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "C").Value;
        var country = Country.Create(world.Id, continent.Id, "Co").Value;
        var region = Region.Create(world.Id, country.Id, "R").Value;
        var s = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 0, 0, 50_000).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(s);
        await ctx.SaveChangesAsync();
        return (world.Id, region.Id);
    }

    [Test]
    public async Task Snapshot_ProducesIndependentCopy_AndCompareDetectsDivergence()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.db");
        var snapPath = Path.Combine(dir, "snap.db");
        try
        {
            var (worldId, regionId) = await SeedAsync(mainPath);

            // Snapshot the authored world.
            await new SqliteSnapshotService().CaptureAsync(mainPath, snapPath);
            File.Exists(snapPath).Should().BeTrue();

            // Diverge main: add a second settlement.
            await using (var ctx = NewContextOnFile(mainPath))
            {
                ctx.Settlements.Add(Settlement.Create(worldId, regionId, "Riverwood", SettlementType.Village, 1, 1, 800).Value);
                await ctx.SaveChangesAsync();
            }

            // Snapshot is unchanged; compare reports exactly one added settlement in main vs snap.
            var diff = await new StructuralCompareService().CompareAsync(snapPath, mainPath, worldId);
            diff.AddedSettlements.Should().ContainSingle(name => name == "Riverwood");
            diff.RemovedSettlements.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Branch_DivergesIndependently_FromParent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.db");
        var branchPath = Path.Combine(dir, "branch.db");
        try
        {
            var (worldId, regionId) = await SeedAsync(mainPath);

            await new FileBranchService(new SqliteSnapshotService()).BranchAsync(mainPath, branchPath);

            // Mutate the branch only.
            await using (var ctx = NewContextOnFile(branchPath))
            {
                ctx.Settlements.Add(Settlement.Create(worldId, regionId, "BranchTown", SettlementType.Town, 5, 5, 1000).Value);
                await ctx.SaveChangesAsync();
            }

            // Parent must still have exactly 1 settlement.
            await using (var ctx = NewContextOnFile(mainPath))
                (await ctx.Settlements.CountAsync()).Should().Be(1);

            await using (var ctx = NewContextOnFile(branchPath))
                (await ctx.Settlements.CountAsync()).Should().Be(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit`
Expected: compile failure (snapshot types do not exist).

- [ ] **Step 3: Implement the interfaces + diff record**

Create `src/WorldEcon.Persistence/Snapshots/ISnapshotService.cs`:
```csharp
namespace WorldEcon.Persistence.Snapshots;

public interface ISnapshotService
{
    /// <summary>Write a consistent, compacted copy of <paramref name="sourceDbPath"/> to <paramref name="destDbPath"/>.</summary>
    Task CaptureAsync(string sourceDbPath, string destDbPath);
}
```

Create `src/WorldEcon.Persistence/Snapshots/IBranchService.cs`:
```csharp
namespace WorldEcon.Persistence.Snapshots;

public interface IBranchService
{
    /// <summary>Fork <paramref name="sourceDbPath"/> into an independent world DB at <paramref name="branchDbPath"/>.</summary>
    Task BranchAsync(string sourceDbPath, string branchDbPath);
}
```

Create `src/WorldEcon.Persistence/Snapshots/WorldDiff.cs`:
```csharp
namespace WorldEcon.Persistence.Snapshots;

/// <summary>Structural difference between two world DBs for a given world (geography vertical).</summary>
public sealed record WorldDiff(
    IReadOnlyList<string> AddedSettlements,
    IReadOnlyList<string> RemovedSettlements,
    IReadOnlyList<string> ChangedSettlements);
```

Create `src/WorldEcon.Persistence/Snapshots/ICompareService.cs`:
```csharp
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Snapshots;

public interface ICompareService
{
    /// <summary>Diff settlements of <paramref name="worldId"/> between two world DBs (baseline → candidate).</summary>
    Task<WorldDiff> CompareAsync(string baselineDbPath, string candidateDbPath, WorldId worldId);
}
```

- [ ] **Step 4: Implement the snapshot service (`VACUUM INTO`)**

Create `src/WorldEcon.Persistence/Snapshots/SqliteSnapshotService.cs`:
```csharp
using Microsoft.Data.Sqlite;

namespace WorldEcon.Persistence.Snapshots;

/// <summary>Consistent file snapshot via SQLite `VACUUM INTO` (transactional + compacted).</summary>
public sealed class SqliteSnapshotService : ISnapshotService
{
    public async Task CaptureAsync(string sourceDbPath, string destDbPath)
    {
        if (File.Exists(destDbPath))
            File.Delete(destDbPath); // VACUUM INTO requires the target not to pre-exist

        await using var connection = new SqliteConnection($"Data Source={sourceDbPath}");
        await connection.OpenAsync();

        // Checkpoint any WAL so the copy is fully consistent, then VACUUM INTO.
        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "VACUUM main INTO $dest;";
        cmd.Parameters.AddWithValue("$dest", destDbPath);
        await cmd.ExecuteNonQueryAsync();
    }
}
```
> Note: `VACUUM INTO` accepts a bound parameter for the destination in current SQLite. If the installed SQLite rejects the parameter, fall back to inlining the path with single-quote escaping (`path.Replace("'", "''")`) — and report that you did.

- [ ] **Step 5: Implement the branch service**

Create `src/WorldEcon.Persistence/Snapshots/FileBranchService.cs`:
```csharp
namespace WorldEcon.Persistence.Snapshots;

/// <summary>A branch is a snapshot to a new path; the copy diverges independently (Fowler's Parallel Model).</summary>
public sealed class FileBranchService(ISnapshotService snapshots) : IBranchService
{
    public Task BranchAsync(string sourceDbPath, string branchDbPath)
        => snapshots.CaptureAsync(sourceDbPath, branchDbPath);
}
```

- [ ] **Step 6: Implement the structural compare service**

Create `src/WorldEcon.Persistence/Snapshots/StructuralCompareService.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Snapshots;

/// <summary>In-C# structural diff over settlements (portable; sqldiff/changeset generalization deferred).</summary>
public sealed class StructuralCompareService : ICompareService
{
    public async Task<WorldDiff> CompareAsync(string baselineDbPath, string candidateDbPath, WorldId worldId)
    {
        var baseline = await LoadSettlementsAsync(baselineDbPath, worldId);
        var candidate = await LoadSettlementsAsync(candidateDbPath, worldId);

        var added = candidate.Keys.Except(baseline.Keys)
            .Select(id => candidate[id].Name).OrderBy(n => n).ToList();
        var removed = baseline.Keys.Except(candidate.Keys)
            .Select(id => baseline[id].Name).OrderBy(n => n).ToList();
        var changed = candidate.Keys.Intersect(baseline.Keys)
            .Where(id => !SameContent(baseline[id], candidate[id]))
            .Select(id => candidate[id].Name).OrderBy(n => n).ToList();

        return new WorldDiff(added, removed, changed);
    }

    private static async Task<Dictionary<SettlementId, Settlement>> LoadSettlementsAsync(string path, WorldId worldId)
    {
        await using var ctx = new WorldDbContext(
            new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
        var list = await ctx.Settlements.Where(s => s.WorldId == worldId).ToListAsync();
        return list.ToDictionary(s => s.Id);
    }

    private static bool SameContent(Settlement a, Settlement b)
        => a.Name == b.Name && a.Type == b.Type && a.X == b.X && a.Y == b.Y
           && a.Population == b.Population && a.RegionId.Equals(b.RegionId);
}
```

> `StructuralCompareService` and the test both construct `WorldDbContext` over SQLite; add a `using Microsoft.Data.Sqlite;` only where needed. `Microsoft.Data.Sqlite` is transitively available via `Microsoft.EntityFrameworkCore.Sqlite`.

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet run --project tests/WorldEcon.Persistence.Tests.Unit`
Expected: all tests pass.

- [ ] **Step 8: Commit**
```bash
git add src/WorldEcon.Persistence/Snapshots tests/WorldEcon.Persistence.Tests.Unit/SnapshotTests.cs
git commit -m "feat: add snapshot (VACUUM INTO), branch, and structural compare"
```

---

## Final verification

- [ ] **Step 1: Full solution build + all tests**

Run:
```bash
dotnet build
dotnet run --project tests/WorldEcon.SharedKernel.Tests.Unit
dotnet run --project tests/WorldEcon.Simulation.Tests.Unit
dotnet run --project tests/WorldEcon.Domain.Tests.Unit
dotnet run --project tests/WorldEcon.Persistence.Tests.Unit
```
Expected: build succeeds with zero warnings; all four test projects green.

- [ ] **Step 2: Confirm clean tree**

Run: `git status`
Expected: nothing to commit, working tree clean (the `design-time.db` from the EF factory must be gitignored — `*.db` already is).

---

## What this unlocks
- A persisted, queryable relational world (the SQLite save file) — the substrate the price/margin query (Plan 2) reads from.
- Strongly-typed IDs, domain base classes, EF conventions, migration flow, and snapshot/branch/compare — the exact patterns every later aggregate (Economy, Demographics, Agents, Jurisdiction) reuses mechanically.
- Snapshot/branch/compare on a real save file — the determinism architecture (spec Build §4) proven end-to-end before the tick engine consumes it (Plan 3).

## Carry-forward notes for later plans
- **Deterministic IDs for simulation-created entities** (caravans, work orders): derive from the RNG, not `Guid.NewGuid()` (which is only acceptable for DM-authored seed entities).
- **Action log + RNG-state-in-snapshot** land with the tick engine (Plan 3) / party effects (Plan 4); snapshots will then also persist `IRngStreams.Capture()` and the log position.
- **`compare` generalization:** the current diff is settlement-only; extend per-aggregate, or swap to `sqldiff`/session-changeset for whole-DB diffs once the schema is broad.
- **Overflow policy:** revisit `Money` (unchecked) vs `FixedMath` (checked) consistency when cost-basis math lands (Plan 2).
- **Source-generated IDs:** optional later refactor to remove the hand-written ID boilerplate.
```