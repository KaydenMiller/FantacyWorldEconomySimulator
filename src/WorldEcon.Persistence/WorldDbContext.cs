using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
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
    public DbSet<RegionContinent> RegionContinents => Set<RegionContinent>();
    public DbSet<RegionContainment> RegionContainments => Set<RegionContainment>();
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

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<WorldId>().HaveConversion<WorldIdConverter>();
        b.Properties<ContinentId>().HaveConversion<ContinentIdConverter>();
        b.Properties<CountryId>().HaveConversion<CountryIdConverter>();
        b.Properties<RegionId>().HaveConversion<RegionIdConverter>();
        b.Properties<SettlementId>().HaveConversion<SettlementIdConverter>();
        b.Properties<RouteId>().HaveConversion<RouteIdConverter>();
        b.Properties<RegionContinentId>().HaveConversion<RegionContinentIdConverter>();
        b.Properties<RegionContainmentId>().HaveConversion<RegionContainmentIdConverter>();
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
    }

    protected override void OnModelCreating(ModelBuilder b)
        => b.ApplyConfigurationsFromAssembly(typeof(WorldDbContext).Assembly);
}
