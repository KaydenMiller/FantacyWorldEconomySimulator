using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
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
    public DbSet<Good> Goods => Set<Good>();
    public DbSet<Stockpile> Stockpiles => Set<Stockpile>();
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<ProductionNode> ProductionNodes => Set<ProductionNode>();
    public DbSet<ResourceEndowment> ResourceEndowments => Set<ResourceEndowment>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<RepresentativeMerchant> Merchants => Set<RepresentativeMerchant>();
    public DbSet<Caravan> Caravans => Set<Caravan>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<WorldId>().HaveConversion<WorldIdConverter>();
        b.Properties<ContinentId>().HaveConversion<ContinentIdConverter>();
        b.Properties<CountryId>().HaveConversion<CountryIdConverter>();
        b.Properties<RegionId>().HaveConversion<RegionIdConverter>();
        b.Properties<SettlementId>().HaveConversion<SettlementIdConverter>();
        b.Properties<RouteId>().HaveConversion<RouteIdConverter>();
        b.Properties<GoodId>().HaveConversion<GoodIdConverter>();
        b.Properties<StockpileId>().HaveConversion<StockpileIdConverter>();
        b.Properties<ShopId>().HaveConversion<ShopIdConverter>();
        b.Properties<ResourceEndowmentId>().HaveConversion<ResourceEndowmentIdConverter>();
        b.Properties<ProductionNodeId>().HaveConversion<ProductionNodeIdConverter>();
        b.Properties<RecipeId>().HaveConversion<RecipeIdConverter>();
        b.Properties<WorkOrderId>().HaveConversion<WorkOrderIdConverter>();
        b.Properties<MerchantId>().HaveConversion<MerchantIdConverter>();
        b.Properties<CaravanId>().HaveConversion<CaravanIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
        => b.ApplyConfigurationsFromAssembly(typeof(WorldDbContext).Assembly);
}
