using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Cli.Tests.Unit;

[NotInParallel]
public class NodeRenameCommandTests
{
    private static WorldDbContext Ctx(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var context = TestContext.Current!;
        var outStart = context.Output.GetStandardOutput().Length;
        var errStart = context.Output.GetErrorOutput().Length;
        var exit = await CommandRunner.RunAsync(args);
        return (exit, context.Output.GetStandardOutput()[outStart..], context.Output.GetErrorOutput()[errStart..]);
    }

    private static async Task<(string Path, RouteNode Junction)> NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_cli_rename_{Guid.NewGuid():N}.db");
        var world = World.Create("w", 1, CalendarDefinition.Default, "1").Value;
        var j = RouteNode.Create(world.Id, RouteNodeKind.Junction, 42, null, null).Value;
        await using var ctx = Ctx(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.RouteNodes.Add(j);
        await ctx.SaveChangesAsync();
        return (path, j);
    }

    [Test]
    public async Task NodeRename_PrintsAndPersists()
    {
        var (path, j) = await NewDb();
        try
        {
            var (exit, output, _) = await RunAsync("node", "rename", path, j.Id.Value.ToString()[..8], "Old Ford");
            exit.Should().Be(0);
            output.Should().Contain("Renamed Junction @ tile 42 to \"Old Ford\"");
            await using var ctx = Ctx(path);
            (await ctx.RouteNodes.SingleAsync()).Name.Should().Be("Old Ford");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task NodeRename_Clear()
    {
        var (path, j) = await NewDb();
        try
        {
            await RunAsync("node", "rename", path, j.Id.Value.ToString(), "Old Ford");
            var (exit, output, _) = await RunAsync("node", "rename", path, j.Id.Value.ToString(), "--clear");
            exit.Should().Be(0);
            output.Should().Contain("Cleared the name of Junction @ tile 42");
            await using var ctx = Ctx(path);
            (await ctx.RouteNodes.SingleAsync()).Name.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task NodeRename_BadName_ExitsOne()
    {
        var (path, j) = await NewDb();
        try
        {
            var (exit, _, err) = await RunAsync("node", "rename", path, j.Id.Value.ToString(), new string('x', 65));
            exit.Should().Be(1);
            err.Should().Contain("Error:").And.Contain("at most 64");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task NodeRename_UnquotedWords_RejectedWithUsage_NothingSaved()
    {
        var (path, j) = await NewDb();
        try
        {
            var (exit, _, err) = await RunAsync("node", "rename", path, j.Id.Value.ToString(), "Old", "Mill");
            exit.Should().Be(1);
            err.Should().Contain("quote a name with spaces");
            await using var ctx = Ctx(path);
            (await ctx.RouteNodes.SingleAsync()).Name.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task NodeRename_MissingArgs_ExitsNonZero()
    {
        var (exit, _, err) = await RunAsync("node", "rename");
        exit.Should().NotBe(0);
        err.Should().Contain("node rename <dbPath> <nodeId> <name|--clear>");
    }
}
