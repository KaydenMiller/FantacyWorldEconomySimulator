using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence.Conversions;
using WorldEcon.Persistence.Globe;
using WorldEcon.SharedKernel.Globe;

namespace WorldEcon.Persistence;

public sealed class WorldDbContext(DbContextOptions<WorldDbContext> options) : DbContext(options)
{
    public DbSet<World> Worlds => Set<World>();
    public DbSet<Continent> Continents => Set<Continent>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<PoliticalRegion> PoliticalRegions => Set<PoliticalRegion>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<RouteNode> RouteNodes => Set<RouteNode>();
    public DbSet<RouteTile> RouteTiles => Set<RouteTile>();
    public DbSet<Road> Roads => Set<Road>();
    public DbSet<RouteUpgrade> RouteUpgrades => Set<RouteUpgrade>();
    public DbSet<VehicleClass> VehicleClasses => Set<VehicleClass>();
    public DbSet<VoyagePath> VoyagePaths => Set<VoyagePath>();
    public DbSet<TravelKnowledge> TravelKnowledge => Set<TravelKnowledge>();
    public DbSet<PoliticalRegionContinent> PoliticalRegionContinents => Set<PoliticalRegionContinent>();
    public DbSet<TerritorialClaim> TerritorialClaims => Set<TerritorialClaim>();
    public DbSet<Good> Goods => Set<Good>();
    public DbSet<Stockpile> Stockpiles => Set<Stockpile>();
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<ProductionNode> ProductionNodes => Set<ProductionNode>();
    public DbSet<ResourceEndowment> ResourceEndowments => Set<ResourceEndowment>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<RepresentativeMerchant> Merchants => Set<RepresentativeMerchant>();
    public DbSet<RepresentativeConsumer> Consumers => Set<RepresentativeConsumer>();
    public DbSet<Caravan> Caravans => Set<Caravan>();
    public DbSet<LogEvent> LogEvents => Set<LogEvent>();
    public DbSet<LogEventScope> LogEventScopes => Set<LogEventScope>();
    public DbSet<MoneyLedgerSnapshot> MoneyLedgerSnapshots => Set<MoneyLedgerSnapshot>();
    public DbSet<MoneyLedgerLine> MoneyLedgerLines => Set<MoneyLedgerLine>();
    public DbSet<ShopPriceBelief> ShopPriceBeliefs => Set<ShopPriceBelief>();
    public DbSet<GeographicRegion> GeographicRegions => Set<GeographicRegion>();
    public DbSet<GeographicRegionCell> GeographicRegionCells => Set<GeographicRegionCell>();
    public DbSet<GeographicRegionContainment> GeographicRegionContainments => Set<GeographicRegionContainment>();

    // Generated globe tables; written in bulk by GlobeStore, exposed here for typed read queries.
    public DbSet<GlobeCellRow> GlobeCells => Set<GlobeCellRow>();
    public DbSet<GlobeCellNeighbourRow> GlobeCellNeighbours => Set<GlobeCellNeighbourRow>();
    public DbSet<PlateRow> Plates => Set<PlateRow>();
    public DbSet<GlobeCellCompositionRow> GlobeCellCompositions => Set<GlobeCellCompositionRow>();
    public DbSet<RiverEdgeRow> RiverEdges => Set<RiverEdgeRow>();
    public DbSet<WorldGenRunRow> WorldGenRuns => Set<WorldGenRunRow>();
    public DbSet<TileBlockRow> TileBlocks => Set<TileBlockRow>();
    public DbSet<LakeRow> Lakes => Set<LakeRow>();
    public DbSet<RegionLevelRow> RegionLevels => Set<RegionLevelRow>();
    public DbSet<MapImageryRow> MapImagery => Set<MapImageryRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<WorldId>().HaveConversion<WorldIdConverter>();
        b.Properties<ContinentId>().HaveConversion<ContinentIdConverter>();
        b.Properties<CountryId>().HaveConversion<CountryIdConverter>();
        b.Properties<PoliticalRegionId>().HaveConversion<PoliticalRegionIdConverter>();
        b.Properties<SettlementId>().HaveConversion<SettlementIdConverter>();
        b.Properties<RouteId>().HaveConversion<RouteIdConverter>();
        b.Properties<RouteNodeId>().HaveConversion<RouteNodeIdConverter>();
        b.Properties<RoadId>().HaveConversion<RoadIdConverter>();
        b.Properties<PoliticalRegionContinentId>().HaveConversion<PoliticalRegionContinentIdConverter>();
        b.Properties<TerritorialClaimId>().HaveConversion<TerritorialClaimIdConverter>();
        b.Properties<GoodId>().HaveConversion<GoodIdConverter>();
        b.Properties<StockpileId>().HaveConversion<StockpileIdConverter>();
        b.Properties<ShopId>().HaveConversion<ShopIdConverter>();
        b.Properties<ResourceEndowmentId>().HaveConversion<ResourceEndowmentIdConverter>();
        b.Properties<ProductionNodeId>().HaveConversion<ProductionNodeIdConverter>();
        b.Properties<RecipeId>().HaveConversion<RecipeIdConverter>();
        b.Properties<WorkOrderId>().HaveConversion<WorkOrderIdConverter>();
        b.Properties<MerchantId>().HaveConversion<MerchantIdConverter>();
        b.Properties<ConsumerId>().HaveConversion<ConsumerIdConverter>();
        b.Properties<CaravanId>().HaveConversion<CaravanIdConverter>();
        b.Properties<LogEventId>().HaveConversion<LogEventIdConverter>();
        b.Properties<LogEventScopeId>().HaveConversion<LogEventScopeIdConverter>();
        b.Properties<MoneyLedgerSnapshotId>().HaveConversion<MoneyLedgerSnapshotIdConverter>();
        b.Properties<MoneyLedgerLineId>().HaveConversion<MoneyLedgerLineIdConverter>();
        b.Properties<ShopPriceBeliefId>().HaveConversion<ShopPriceBeliefIdConverter>();
        b.Properties<GeographicRegionId>().HaveConversion<GeographicRegionIdConverter>();
        b.Properties<GeographicRegionContainmentId>().HaveConversion<GeographicRegionContainmentIdConverter>();
        b.Properties<GlobeCellId>().HaveConversion<GlobeCellIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
        => b.ApplyConfigurationsFromAssembly(typeof(WorldDbContext).Assembly);
}
