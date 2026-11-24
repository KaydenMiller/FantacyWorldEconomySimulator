using FluentAssertions;
using Microsoft.Data.Sqlite;
using WorldEcon.Domain.Geography;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;
using WorldEcon.Seeding;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>Naming a junction or site from the app: validation in the editor, the save through the rename service, the session update.</summary>
[NotInParallel]
public class NodeNameEditorTests
{
    private static async Task<(MainViewModel Vm, string Db)> OpenDemoAsync()
    {
        var db = Path.Combine(Path.GetTempPath(), $"gui_rename_{Guid.NewGuid():N}.db");
        await DemoSeeder.CreateAsync(db, TestWorlds.Settings);
        SqliteConnection.ClearAllPools();
        var vm = new MainViewModel();
        (await vm.OpenAsync(db)).Should().BeTrue(vm.StatusText);
        return (vm, db);
    }

    private static void Cleanup(MainViewModel vm, string db)
    {
        vm.Session?.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(db);
    }

    private static GlobeNodeRef Site(MainViewModel vm) => new(vm.Session!.Data.Nodes.Single(n => n.Kind == RouteNodeKind.Site).Id);

    private sealed record GlobeNodeRef(Guid Id);

    [Test]
    public async Task SelectingANode_ShowsEditor_OtherSelectionHidesIt()
    {
        var (vm, db) = await OpenDemoAsync();
        try
        {
            vm.NodeEditor.Should().BeNull();
            vm.SelectObject(new MapObject(MapObjectKind.Node, Site(vm).Id));
            vm.NodeEditor.Should().NotBeNull();
            vm.NodeEditor!.Text.Should().Be("Greystone Quarry");
            vm.SelectCell(0);
            vm.NodeEditor.Should().BeNull();
        }
        finally { Cleanup(vm, db); }
    }

    [Test]
    public async Task CanSave_FalseForBlankOrUnchanged_CanClearWhenNamed()
    {
        var (vm, db) = await OpenDemoAsync();
        try
        {
            vm.SelectObject(new MapObject(MapObjectKind.Node, Site(vm).Id));
            var e = vm.NodeEditor!;
            e.CanSave.Should().BeFalse("unchanged");
            e.Text = "   ";
            e.CanSave.Should().BeFalse("blank");
            e.Text = " Greystone Quarry ";
            e.CanSave.Should().BeFalse("unchanged after trimming");
            e.Text = "Old Quarry";
            e.CanSave.Should().BeTrue();
            e.CanClear.Should().BeTrue();
        }
        finally { Cleanup(vm, db); }
    }

    [Test]
    public async Task Save_RenamesFile_UpdatesSession_RaisesDataChangedOnce()
    {
        var (vm, db) = await OpenDemoAsync();
        try
        {
            var id = Site(vm).Id;
            vm.SelectObject(new MapObject(MapObjectKind.Node, id));
            var changed = 0;
            vm.Session!.DataChanged += (_, _) => changed++;
            vm.NodeEditor!.Text = "Old Quarry";
            await vm.NodeEditor.SaveAsync();
            vm.NodeEditor.Error.Should().BeEmpty();
            vm.Session.Node(id)!.Name.Should().Be("Old Quarry");
            vm.Session.NodeMarkers.Single(m => m.Id == id).Label.Should().Be("Old Quarry");
            vm.DetailsTitle.Should().Be("Site: Old Quarry");
            changed.Should().Be(1);
            SqliteConnection.ClearAllPools();
            await using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Name FROM route_nodes WHERE Kind = 'Site'";
            (await cmd.ExecuteScalarAsync()).Should().Be("Old Quarry");
        }
        finally { Cleanup(vm, db); }
    }

    [Test]
    public async Task Save_ServiceError_ShownInError_NameUnchanged()
    {
        var (vm, db) = await OpenDemoAsync();
        try
        {
            var id = Site(vm).Id;
            vm.SelectObject(new MapObject(MapObjectKind.Node, id));
            vm.NodeEditor!.Text = new string('x', 65); // the text box limits input to 64; set past it to reach the service check
            await vm.NodeEditor.SaveAsync();
            vm.NodeEditor.Error.Should().Contain("at most 64");
            vm.Session!.Node(id)!.Name.Should().Be("Greystone Quarry");
        }
        finally { Cleanup(vm, db); }
    }

    [Test]
    public async Task Clear_ResetsToUnnamed_SiteLabelFallsBackToTile()
    {
        var (vm, db) = await OpenDemoAsync();
        try
        {
            var id = Site(vm).Id;
            vm.SelectObject(new MapObject(MapObjectKind.Node, id));
            await vm.NodeEditor!.ClearAsync();
            vm.Session!.Node(id)!.Name.Should().BeNull();
            vm.DetailsTitle.Should().StartWith("Site: Site @ tile ");
            vm.NodeEditor.CanClear.Should().BeFalse();
        }
        finally { Cleanup(vm, db); }
    }

    [Test]
    public void InMemoryWorld_EditorDisabled()
    {
        var (loaded, junction, _, _, _) = WorldSessionGraphTests.Graph();
        using var session = new WorldSession(loaded, path: null);
        var vm = TestWorlds.MainWithWorld(session: session);
        vm.SelectObject(new MapObject(MapObjectKind.Node, junction));
        vm.NodeEditor!.IsEnabled.Should().BeFalse();
        vm.NodeEditor.Text = "Old Ford";
        vm.NodeEditor.CanSave.Should().BeFalse();
        vm.NodeEditor.Error.Should().Contain("No database");
    }
}
