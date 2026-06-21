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
