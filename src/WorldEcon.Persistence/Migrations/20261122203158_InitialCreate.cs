using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "caravans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GoodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitCostBasis = table.Column<long>(type: "INTEGER", nullable: false),
                    DepartTick = table.Column<long>(type: "INTEGER", nullable: false),
                    ArriveTick = table.Column<long>(type: "INTEGER", nullable: false),
                    Delivered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_caravans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "consumers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Seat = table.Column<Guid>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Budget = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consumers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "continents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_continents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "countries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContinentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_countries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "geographic_region_cells",
                columns: table => new
                {
                    GeographicRegionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CellId = table.Column<int>(type: "INTEGER", nullable: false),
                    CoverageBp = table.Column<short>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_geographic_region_cells", x => new { x.GeographicRegionId, x.CellId });
                    table.CheckConstraint("CK_geographic_region_cells_CoverageBp", "\"CoverageBp\" BETWEEN 1 AND 10000");
                });

            migrationBuilder.CreateTable(
                name: "geographic_region_containments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentRegionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChildRegionId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_geographic_region_containments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "geographic_regions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    NameAuthored = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_geographic_regions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "globe_cell_composition",
                columns: table => new
                {
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CellId = table.Column<int>(type: "INTEGER", nullable: false),
                    Slot = table.Column<byte>(type: "INTEGER", nullable: false),
                    Landform = table.Column<byte>(type: "INTEGER", nullable: false),
                    ShareBp = table.Column<short>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_globe_cell_composition", x => new { x.WorldId, x.CellId, x.Slot });
                });

            migrationBuilder.CreateTable(
                name: "globe_cell_neighbours",
                columns: table => new
                {
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CellId = table.Column<int>(type: "INTEGER", nullable: false),
                    Slot = table.Column<byte>(type: "INTEGER", nullable: false),
                    NeighbourCellId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_globe_cell_neighbours", x => new { x.WorldId, x.CellId, x.Slot });
                });

            migrationBuilder.CreateTable(
                name: "globe_cells",
                columns: table => new
                {
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CellId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlateId = table.Column<int>(type: "INTEGER", nullable: false),
                    ElevationM = table.Column<int>(type: "INTEGER", nullable: false),
                    BoundaryKind = table.Column<byte>(type: "INTEGER", nullable: false),
                    CurrentDir = table.Column<sbyte>(type: "INTEGER", nullable: false),
                    CurrentAnomalyDeciC = table.Column<short>(type: "INTEGER", nullable: false),
                    WindDir = table.Column<sbyte>(type: "INTEGER", nullable: false),
                    WindStrength = table.Column<byte>(type: "INTEGER", nullable: false),
                    TemperatureDeciC = table.Column<short>(type: "INTEGER", nullable: false),
                    PrecipitationMm = table.Column<int>(type: "INTEGER", nullable: false),
                    Biome = table.Column<byte>(type: "INTEGER", nullable: false),
                    DownstreamOf = table.Column<int>(type: "INTEGER", nullable: false),
                    FlowAccum = table.Column<long>(type: "INTEGER", nullable: false),
                    WaterBodyKind = table.Column<byte>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_globe_cells", x => new { x.WorldId, x.CellId });
                });

            migrationBuilder.CreateTable(
                name: "goods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    BaseValue = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseUnit = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<string>(type: "TEXT", nullable: false),
                    ShelfLifeTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Divisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConsumptionPerCapitaBp = table.Column<long>(type: "INTEGER", nullable: false),
                    Need = table.Column<string>(type: "TEXT", nullable: false),
                    PeakWillingnessMultipleBasisPoints = table.Column<long>(type: "INTEGER", nullable: false),
                    MassPerUnit = table.Column<long>(type: "INTEGER", nullable: false),
                    VolumePerUnit = table.Column<long>(type: "INTEGER", nullable: false),
                    Provenance = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lakes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    WaterLevelM = table.Column<int>(type: "INTEGER", nullable: false),
                    TileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AreaKm2 = table.Column<double>(type: "REAL", nullable: false),
                    OutletTile = table.Column<int>(type: "INTEGER", nullable: false),
                    LowestTile = table.Column<int>(type: "INTEGER", nullable: false),
                    Inflow = table.Column<long>(type: "INTEGER", nullable: false),
                    Evaporation = table.Column<long>(type: "INTEGER", nullable: false),
                    ExitTile = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lakes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "log_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredTick = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Magnitude = table.Column<string>(type: "TEXT", nullable: false),
                    OriginKind = table.Column<string>(type: "TEXT", nullable: false),
                    OriginId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsPlayerAction = table.Column<bool>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_log_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "merchants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Seat = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capital = table.Column<long>(type: "INTEGER", nullable: false),
                    WeightCapacity = table.Column<long>(type: "INTEGER", nullable: false),
                    VolumeCapacity = table.Column<long>(type: "INTEGER", nullable: false),
                    Reach = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merchants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "money_ledger_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Tick = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalSupply = table.Column<long>(type: "INTEGER", nullable: false),
                    NetDelta = table.Column<long>(type: "INTEGER", nullable: false),
                    Discrepancy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_money_ledger_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plates",
                columns: table => new
                {
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlateId = table.Column<int>(type: "INTEGER", nullable: false),
                    Continental = table.Column<bool>(type: "INTEGER", nullable: false),
                    RotationAxisX = table.Column<double>(type: "REAL", nullable: false),
                    RotationAxisY = table.Column<double>(type: "REAL", nullable: false),
                    RotationAxisZ = table.Column<double>(type: "REAL", nullable: false),
                    AngularSpeed = table.Column<double>(type: "REAL", nullable: false),
                    SeedCell = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plates", x => new { x.WorldId, x.PlateId });
                });

            migrationBuilder.CreateTable(
                name: "political_region_continents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PoliticalRegionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContinentId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_political_region_continents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "political_regions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CountryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_political_regions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "production_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Facility = table.Column<string>(type: "TEXT", nullable: false),
                    ThroughputCap = table.Column<long>(type: "INTEGER", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProducerShopId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "recipes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Facility = table.Column<string>(type: "TEXT", nullable: false),
                    Lines = table.Column<string>(type: "TEXT", nullable: false),
                    LaborCost = table.Column<long>(type: "INTEGER", nullable: false),
                    TicksToProduce = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "region_level",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_region_level", x => x.Id);
                    table.CheckConstraint("CK_region_level_Id", "\"Id\" = 0");
                });

            migrationBuilder.CreateTable(
                name: "resource_endowments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GoodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Abundance = table.Column<long>(type: "INTEGER", nullable: false),
                    ProducerShopId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_endowments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "river_edges",
                columns: table => new
                {
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LowCellId = table.Column<int>(type: "INTEGER", nullable: false),
                    HighCellId = table.Column<int>(type: "INTEGER", nullable: false),
                    FlowsLowToHigh = table.Column<bool>(type: "INTEGER", nullable: false),
                    Flow = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_river_edges", x => new { x.WorldId, x.LowCellId, x.HighCellId });
                });

            migrationBuilder.CreateTable(
                name: "routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromSettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToSettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Distance = table.Column<long>(type: "INTEGER", nullable: false),
                    Terrain = table.Column<string>(type: "TEXT", nullable: false),
                    Danger = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "settlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PoliticalRegionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    X = table.Column<int>(type: "INTEGER", nullable: false),
                    Y = table.Column<int>(type: "INTEGER", nullable: false),
                    Population = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Provenance = table.Column<string>(type: "TEXT", nullable: false),
                    CellId = table.Column<int>(type: "INTEGER", nullable: true),
                    TileId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shop_price_beliefs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShopId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GoodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Low = table.Column<long>(type: "INTEGER", nullable: false),
                    High = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shop_price_beliefs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MarkupBp = table.Column<int>(type: "INTEGER", nullable: false),
                    Till = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shops", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stockpiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKind = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GoodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    CostBasis = table.Column<long>(type: "INTEGER", nullable: false),
                    MarketPrice = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stockpiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "territorial_claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CountryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_territorial_claims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tile_blocks",
                columns: table => new
                {
                    CellId = table.Column<int>(type: "INTEGER", nullable: false),
                    TileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tile_blocks", x => x.CellId);
                });

            migrationBuilder.CreateTable(
                name: "work_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductionNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartTick = table.Column<long>(type: "INTEGER", nullable: false),
                    CompleteTick = table.Column<long>(type: "INTEGER", nullable: false),
                    CommittedInputCost = table.Column<long>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "worldgen_runs",
                columns: table => new
                {
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GeneratorVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    StagesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worldgen_runs", x => x.WorldId);
                });

            migrationBuilder.CreateTable(
                name: "worlds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    Calendar = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentTick = table.Column<long>(type: "INTEGER", nullable: false),
                    RulesetVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ElasticityExponent = table.Column<int>(type: "INTEGER", nullable: false),
                    MinPriceMultBp = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxPriceMultBp = table.Column<int>(type: "INTEGER", nullable: false),
                    BeliefNarrowFractionBasisPoints = table.Column<long>(type: "INTEGER", nullable: false),
                    BeliefWidenFractionBasisPoints = table.Column<long>(type: "INTEGER", nullable: false),
                    BeliefShiftFractionBasisPoints = table.Column<long>(type: "INTEGER", nullable: false),
                    VolumetricDivisor = table.Column<long>(type: "INTEGER", nullable: false),
                    TransportRate = table.Column<long>(type: "INTEGER", nullable: false),
                    DisplayUnitSystem = table.Column<string>(type: "TEXT", nullable: false),
                    GridFrequency = table.Column<int>(type: "INTEGER", nullable: false),
                    PlanetRadiusKm = table.Column<int>(type: "INTEGER", nullable: false),
                    WorldGenVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SeaLevelM = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worlds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "log_event_scopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LogEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeKind = table.Column<string>(type: "TEXT", nullable: false),
                    ScopeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_log_event_scopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_log_event_scopes_log_events_LogEventId",
                        column: x => x.LogEventId,
                        principalTable: "log_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "money_ledger_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_money_ledger_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_money_ledger_lines_money_ledger_snapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "money_ledger_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_caravans_ArriveTick",
                table: "caravans",
                column: "ArriveTick");

            migrationBuilder.CreateIndex(
                name: "IX_caravans_DestinationId",
                table: "caravans",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_caravans_OwnerId",
                table: "caravans",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_caravans_WorldId",
                table: "caravans",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_consumers_Seat",
                table: "consumers",
                column: "Seat");

            migrationBuilder.CreateIndex(
                name: "IX_consumers_WorldId",
                table: "consumers",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_continents_WorldId",
                table: "continents",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_countries_ContinentId",
                table: "countries",
                column: "ContinentId");

            migrationBuilder.CreateIndex(
                name: "IX_countries_WorldId",
                table: "countries",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_geographic_region_cells_CellId",
                table: "geographic_region_cells",
                column: "CellId");

            migrationBuilder.CreateIndex(
                name: "IX_geographic_region_containments_ChildRegionId",
                table: "geographic_region_containments",
                column: "ChildRegionId");

            migrationBuilder.CreateIndex(
                name: "IX_geographic_region_containments_ParentRegionId",
                table: "geographic_region_containments",
                column: "ParentRegionId");

            migrationBuilder.CreateIndex(
                name: "IX_geographic_region_containments_WorldId",
                table: "geographic_region_containments",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_geographic_regions_WorldId",
                table: "geographic_regions",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_goods_WorldId",
                table: "goods",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_log_event_scopes_LogEventId",
                table: "log_event_scopes",
                column: "LogEventId");

            migrationBuilder.CreateIndex(
                name: "IX_log_event_scopes_ScopeKind_ScopeId_Sequence",
                table: "log_event_scopes",
                columns: new[] { "ScopeKind", "ScopeId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_log_events_WorldId",
                table: "log_events",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_log_events_WorldId_Sequence",
                table: "log_events",
                columns: new[] { "WorldId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_merchants_Seat",
                table: "merchants",
                column: "Seat");

            migrationBuilder.CreateIndex(
                name: "IX_merchants_WorldId",
                table: "merchants",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_money_ledger_lines_SnapshotId",
                table: "money_ledger_lines",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_money_ledger_snapshots_WorldId",
                table: "money_ledger_snapshots",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_money_ledger_snapshots_WorldId_Sequence",
                table: "money_ledger_snapshots",
                columns: new[] { "WorldId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_political_region_continents_ContinentId",
                table: "political_region_continents",
                column: "ContinentId");

            migrationBuilder.CreateIndex(
                name: "IX_political_region_continents_PoliticalRegionId",
                table: "political_region_continents",
                column: "PoliticalRegionId");

            migrationBuilder.CreateIndex(
                name: "IX_political_region_continents_WorldId",
                table: "political_region_continents",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_political_regions_CountryId",
                table: "political_regions",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_political_regions_WorldId",
                table: "political_regions",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_production_nodes_ProducerShopId",
                table: "production_nodes",
                column: "ProducerShopId");

            migrationBuilder.CreateIndex(
                name: "IX_production_nodes_RecipeId",
                table: "production_nodes",
                column: "RecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_production_nodes_SettlementId",
                table: "production_nodes",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_production_nodes_WorldId",
                table: "production_nodes",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_recipes_WorldId",
                table: "recipes",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_resource_endowments_ProducerShopId",
                table: "resource_endowments",
                column: "ProducerShopId");

            migrationBuilder.CreateIndex(
                name: "IX_resource_endowments_SettlementId",
                table: "resource_endowments",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_resource_endowments_WorldId",
                table: "resource_endowments",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_routes_FromSettlementId",
                table: "routes",
                column: "FromSettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_routes_ToSettlementId",
                table: "routes",
                column: "ToSettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_routes_WorldId",
                table: "routes",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_settlements_PoliticalRegionId",
                table: "settlements",
                column: "PoliticalRegionId");

            migrationBuilder.CreateIndex(
                name: "IX_settlements_WorldId",
                table: "settlements",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_shop_price_beliefs_ShopId_GoodId",
                table: "shop_price_beliefs",
                columns: new[] { "ShopId", "GoodId" });

            migrationBuilder.CreateIndex(
                name: "IX_shop_price_beliefs_WorldId",
                table: "shop_price_beliefs",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_shops_SettlementId",
                table: "shops",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_shops_SettlementId_Kind",
                table: "shops",
                columns: new[] { "SettlementId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_shops_WorldId",
                table: "shops",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_stockpiles_OwnerKind_OwnerId_GoodId",
                table: "stockpiles",
                columns: new[] { "OwnerKind", "OwnerId", "GoodId" });

            migrationBuilder.CreateIndex(
                name: "IX_stockpiles_WorldId",
                table: "stockpiles",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_territorial_claims_CountryId",
                table: "territorial_claims",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_territorial_claims_TargetKind_TargetId",
                table: "territorial_claims",
                columns: new[] { "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_territorial_claims_WorldId",
                table: "territorial_claims",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_CompleteTick",
                table: "work_orders",
                column: "CompleteTick");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_ProductionNodeId",
                table: "work_orders",
                column: "ProductionNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_WorldId",
                table: "work_orders",
                column: "WorldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "caravans");

            migrationBuilder.DropTable(
                name: "consumers");

            migrationBuilder.DropTable(
                name: "continents");

            migrationBuilder.DropTable(
                name: "countries");

            migrationBuilder.DropTable(
                name: "geographic_region_cells");

            migrationBuilder.DropTable(
                name: "geographic_region_containments");

            migrationBuilder.DropTable(
                name: "geographic_regions");

            migrationBuilder.DropTable(
                name: "globe_cell_composition");

            migrationBuilder.DropTable(
                name: "globe_cell_neighbours");

            migrationBuilder.DropTable(
                name: "globe_cells");

            migrationBuilder.DropTable(
                name: "goods");

            migrationBuilder.DropTable(
                name: "lakes");

            migrationBuilder.DropTable(
                name: "log_event_scopes");

            migrationBuilder.DropTable(
                name: "merchants");

            migrationBuilder.DropTable(
                name: "money_ledger_lines");

            migrationBuilder.DropTable(
                name: "plates");

            migrationBuilder.DropTable(
                name: "political_region_continents");

            migrationBuilder.DropTable(
                name: "political_regions");

            migrationBuilder.DropTable(
                name: "production_nodes");

            migrationBuilder.DropTable(
                name: "recipes");

            migrationBuilder.DropTable(
                name: "region_level");

            migrationBuilder.DropTable(
                name: "resource_endowments");

            migrationBuilder.DropTable(
                name: "river_edges");

            migrationBuilder.DropTable(
                name: "routes");

            migrationBuilder.DropTable(
                name: "settlements");

            migrationBuilder.DropTable(
                name: "shop_price_beliefs");

            migrationBuilder.DropTable(
                name: "shops");

            migrationBuilder.DropTable(
                name: "stockpiles");

            migrationBuilder.DropTable(
                name: "territorial_claims");

            migrationBuilder.DropTable(
                name: "tile_blocks");

            migrationBuilder.DropTable(
                name: "work_orders");

            migrationBuilder.DropTable(
                name: "worldgen_runs");

            migrationBuilder.DropTable(
                name: "worlds");

            migrationBuilder.DropTable(
                name: "log_events");

            migrationBuilder.DropTable(
                name: "money_ledger_snapshots");
        }
    }
}
