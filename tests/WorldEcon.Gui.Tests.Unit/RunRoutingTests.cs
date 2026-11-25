using ErrorOr;
using FluentAssertions;
using WorldEcon.Application.Globe;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Geography;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>While a simulation run is open, economy reads go through it and writes to the file are disabled.</summary>
public class RunRoutingTests
{
    private static (WorldSession Session, MainViewModel Vm) FileWorld()
    {
        // A session "with a file" whose path reads are stubs (the path is never opened here).
        var session = new WorldSession(TestWorlds.Loaded, path: "stub.db");
        var vm = TestWorlds.MainWithWorld(session: session);
        vm.CaravanQuery = _ => Task.FromResult<ErrorOr<CaravanPositions>>(new CaravanPositions(1_000, []));
        return (session, vm);
    }

    [Test]
    public async Task ActiveRun_RoutesCaravanAndCityReads()
    {
        var (session, vm) = FileWorld();
        await vm.CaravansLoaded;
        var run = new FakeRun(99_000);

        session.ActiveRun = run;
        await vm.RefreshCaravansAsync();
        session.CurrentTick.Should().Be(99_999);
        var city = Guid.NewGuid();
        await vm.City!.ShowAsync(city);
        vm.City.Details!.Header.ReadTick.Should().Be(99_999);

        session.ActiveRun = null;
        await vm.RefreshCaravansAsync();
        session.CurrentTick.Should().Be(1_000);
    }

    [Test]
    public async Task ActiveRun_DisablesFileActionsRefreshAndRename()
    {
        var (session, vm) = FileWorld();
        await vm.CaravansLoaded;
        var editor = new NodeNameEditor(session, new GlobeNode(Guid.NewGuid(), RouteNodeKind.Junction, 1, "Old", [])) { Text = "New" };
        editor.CanSave.Should().BeTrue();
        vm.CanUseFileActions.Should().BeTrue();
        vm.City!.CanRefresh.Should().BeTrue();

        session.ActiveRun = new FakeRun(0);
        vm.IsTimeRunning.Should().BeTrue();
        vm.CanUseFileActions.Should().BeFalse();
        vm.City.CanRefresh.Should().BeFalse();
        editor.CanSave.Should().BeFalse();
        editor.CanClear.Should().BeFalse();

        session.ActiveRun = null;
        vm.CanUseFileActions.Should().BeTrue();
        vm.City.CanRefresh.Should().BeTrue();
        editor.CanSave.Should().BeTrue();
    }

    [Test]
    public async Task ReadStartedBeforeRun_ResultDroppedWhenNewerReadStarted()
    {
        var (session, vm) = FileWorld();
        await vm.CaravansLoaded;
        var gate = new TaskCompletionSource<ErrorOr<CaravanPositions>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.CaravanQuery = _ => { called.TrySetResult(); return gate.Task; };
        var pathRead = vm.RefreshCaravansAsync();
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10));

        session.ActiveRun = new FakeRun(99_000);
        await vm.RefreshCaravansAsync();
        gate.SetResult(new CaravanPositions(1_000, []));
        await pathRead.WaitAsync(TimeSpan.FromSeconds(10));

        session.CurrentTick.Should().Be(99_999, "the path read started before the run finished last and is stale");
        vm.Caravans!.Tick.Should().Be(99_999);
    }

    [Test]
    public async Task SelectedCaravanDelivered_ShowsNote()
    {
        var (session, vm) = FileWorld();
        await vm.CaravansLoaded;
        var caravan = TestCaravan(session);
        vm.CaravanQuery = _ => Task.FromResult<ErrorOr<CaravanPositions>>(new CaravanPositions(1_000, [caravan]));
        await vm.RefreshCaravansAsync();
        vm.SelectObject(new MapObject(MapObjectKind.Caravan, caravan.Id));
        vm.DetailsTitle.Should().StartWith("Caravan:");

        vm.CaravanQuery = _ => Task.FromResult<ErrorOr<CaravanPositions>>(new CaravanPositions(2_000, []));
        await vm.RefreshCaravansAsync();

        vm.Map!.SelectedObject.Should().Be(new MapObject(MapObjectKind.Caravan, caravan.Id));
        vm.DetailRows.Select(r => r.Value).Should().Contain("Delivered — no longer on the map");
    }

    private static CaravanOnMap TestCaravan(WorldSession session)
    {
        var route = session.Data.Routes.FirstOrDefault();
        IReadOnlyList<CaravanLegInfo> legs = route is null
            ? []
            : [new CaravanLegInfo(0, route.Id, TravelDirection.AtoB, 0, 5_000, route.LengthMetres, 0)];
        var s = session.Data.Settlements[0];
        return new CaravanOnMap(Guid.NewGuid(), Guid.NewGuid(), s.Name, s.Id, s.Name, s.Id, s.Name, Guid.NewGuid(), "Grain", 10, 5, "Wagon",
            0, 5_000, 10_000, legs);
    }
}
