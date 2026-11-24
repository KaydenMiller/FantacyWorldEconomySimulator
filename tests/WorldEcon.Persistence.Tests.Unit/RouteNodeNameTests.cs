using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Persistence.Tests.Unit;

public class RouteNodeNameTests
{
    private static WorldDbContext Ctx(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static async Task WithDb(Func<string, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_rn_{Guid.NewGuid():N}.db");
        try
        {
            await using (var ctx = Ctx(path)) await ctx.Database.MigrateAsync();
            await body(path);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task NodeName_RoundTrips_64Chars()
    {
        await WithDb(async path =>
        {
            var world = World.Create("w", 1, CalendarDefinition.Default, "1").Value;
            var node = RouteNode.Create(world.Id, RouteNodeKind.Junction, 4, null, null).Value;
            node.Rename(new string('x', 64)).IsError.Should().BeFalse();
            await using (var ctx = Ctx(path))
            {
                ctx.Worlds.Add(world);
                ctx.RouteNodes.Add(node);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = Ctx(path))
                (await ctx.RouteNodes.SingleAsync()).Name.Should().Be(new string('x', 64));
        });
    }

    [Test]
    public async Task SettlementNode_LongName_Allowed()
    {
        await WithDb(async path =>
        {
            var world = World.Create("w", 1, CalendarDefinition.Default, "1").Value;
            var node = RouteNode.Create(world.Id, RouteNodeKind.Settlement, 4, SettlementId.New(), new string('s', 100)).Value;
            await using var ctx = Ctx(path);
            ctx.Worlds.Add(world);
            ctx.RouteNodes.Add(node);
            await ctx.SaveChangesAsync();
        });
    }

    [Test]
    public async Task NodeName_Over64_RejectedByDatabase()
    {
        await WithDb(async path =>
        {
            var world = World.Create("w", 1, CalendarDefinition.Default, "1").Value;
            var node = RouteNode.Create(world.Id, RouteNodeKind.Junction, 4, null, null).Value;
            await using (var ctx = Ctx(path))
            {
                ctx.Worlds.Add(world);
                ctx.RouteNodes.Add(node);
                await ctx.SaveChangesAsync();
            }

            await using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE route_nodes SET Name = $n";
            cmd.Parameters.AddWithValue("$n", new string('y', 65));
            var act = () => cmd.ExecuteNonQueryAsync();
            await act.Should().ThrowAsync<SqliteException>().Where(e => e.Message.Contains("CHECK"));
        });
    }
}
