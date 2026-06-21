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
    }

    protected override void OnModelCreating(ModelBuilder b)
        => b.ApplyConfigurationsFromAssembly(typeof(WorldDbContext).Assembly);
}
