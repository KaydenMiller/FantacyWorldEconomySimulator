using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Routes;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Application.Tests.Unit;

public class RouteNodeRenameServiceTests
{
    private static WorldDbContext Ctx(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    /// <summary>A database with one world, a junction, a site and a settlement node; returns their ids.</summary>
    private static async Task<(string Path, RouteNode Junction, RouteNode Site, RouteNode Town)> NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_rename_{Guid.NewGuid():N}.db");
        var world = World.Create("w", 1, CalendarDefinition.Default, "1").Value;
        var j = RouteNode.Create(world.Id, RouteNodeKind.Junction, 10, null, null).Value;
        var s = RouteNode.Create(world.Id, RouteNodeKind.Site, 11, null, "Quarry").Value;
        var t = RouteNode.Create(world.Id, RouteNodeKind.Settlement, 12, SettlementId.New(), "Town").Value;
        await using var ctx = Ctx(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.RouteNodes.AddRange(j, s, t);
        await ctx.SaveChangesAsync();
        return (path, j, s, t);
    }

    [Test]
    public async Task Rename_ByFullId()
    {
        var (path, j, _, _) = await NewDb();
        try
        {
            var r = await RouteNodeRenameService.RenameAsync(path, j.Id.Value.ToString(), "Old Ford");
            r.IsError.Should().BeFalse();
            r.Value.Should().Be(new RenamedNode(j.Id.Value, RouteNodeKind.Junction, 10, "Old Ford"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Rename_ByUniquePrefix_EitherForm()
    {
        var (path, j, _, _) = await NewDb();
        try
        {
            var n = j.Id.Value.ToString("N");
            (await RouteNodeRenameService.RenameAsync(path, n[..12].ToUpperInvariant(), "A")).IsError.Should().BeFalse();
            (await RouteNodeRenameService.RenameAsync(path, j.Id.Value.ToString("D")[..13], "B")).Value.Name.Should().Be("B");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Rename_AmbiguousPrefix_Error()
    {
        var (path, _, _, _) = await NewDb();
        try
        {
            (await RouteNodeRenameService.RenameAsync(path, "", "X")).FirstError.Code.Should().Be("routenode.ambiguous");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Rename_Unknown_NotFound()
    {
        var (path, _, _, _) = await NewDb();
        try
        {
            (await RouteNodeRenameService.RenameAsync(path, "zzzz", "X")).FirstError.Code.Should().Be("routenode.notFound");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Rename_SettlementNode_Error()
    {
        var (path, _, _, t) = await NewDb();
        try
        {
            (await RouteNodeRenameService.RenameAsync(path, t.Id.Value.ToString(), "X")).FirstError.Code.Should().Be("routenode.name.settlement");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Rename_Persists_AndClears()
    {
        var (path, _, s, _) = await NewDb();
        try
        {
            await RouteNodeRenameService.RenameAsync(path, s.Id.Value.ToString(), "  Greystone Quarry ");
            await using (var ctx = Ctx(path))
                (await ctx.RouteNodes.SingleAsync(n => n.Id == s.Id)).Name.Should().Be("Greystone Quarry");
            (await RouteNodeRenameService.RenameAsync(path, s.Id.Value.ToString(), null)).Value.Name.Should().BeNull();
            await using (var ctx = Ctx(path))
                (await ctx.RouteNodes.SingleAsync(n => n.Id == s.Id)).Name.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Rename_InvalidName_NotSaved()
    {
        var (path, _, s, _) = await NewDb();
        try
        {
            (await RouteNodeRenameService.RenameAsync(path, s.Id.Value.ToString(), new string('x', 65))).FirstError.Code
                .Should().Be("routenode.name.tooLong");
            await using var ctx = Ctx(path);
            (await ctx.RouteNodes.SingleAsync(n => n.Id == s.Id)).Name.Should().Be("Quarry");
        }
        finally { File.Delete(path); }
    }
}
