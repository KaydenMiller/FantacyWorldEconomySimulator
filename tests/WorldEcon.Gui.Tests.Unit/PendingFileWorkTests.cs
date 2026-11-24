using ErrorOr;
using FluentAssertions;
using WorldEcon.Application.Queries;
using WorldEcon.Gui.ViewModels;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>Reads and writes of the world's file are tracked so a replace of the file can wait for all of them; caravan refreshes apply in order.</summary>
public class PendingFileWorkTests
{
    [Test]
    public async Task Track_ListsRunningWork_UntilItFinishes()
    {
        using var s = TestWorlds.Session();
        var gate = new TaskCompletionSource();
        var tracked = s.Track(gate.Task);
        s.PendingFileWork.Should().ContainSingle();
        gate.SetResult();
        await tracked;
        s.PendingFileWork.Should().BeEmpty();
    }

    [Test]
    public async Task CityPanelLoad_IsTracked()
    {
        using var s = TestWorlds.Session();
        var gate = new TaskCompletionSource<ErrorOr<SettlementDetails>>();
        var panel = new CityPanelViewModel(s, _ => { }, _ => { }, null, (_, _) => gate.Task,
            () => Task.FromResult<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>>(new List<(Guid, string)>()));
        var loading = panel.ShowAsync(Guid.NewGuid());
        s.PendingFileWork.Should().NotBeEmpty();
        gate.SetResult(CityPanelTests.Sample(Guid.NewGuid()));
        await loading;
        s.PendingFileWork.Should().BeEmpty();
    }

    [Test]
    public async Task CaravanRefreshes_ApplyInOrder_OlderReadFinishingLastIsDropped()
    {
        // A session "with a file" whose caravan reads are stubs (the path is never opened here).
        var session = new WorldSession(TestWorlds.Loaded, path: "stub.db");
        var vm = TestWorlds.MainWithWorld(session: session);
        await vm.CaravansLoaded;
        var first = new TaskCompletionSource<ErrorOr<CaravanPositions>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<ErrorOr<CaravanPositions>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The query runs on a worker thread: start the second refresh only once the first has called its stub.
        var firstCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.CaravanQuery = _ => { firstCalled.TrySetResult(); return first.Task; };
        var a = vm.RefreshCaravansAsync();
        await firstCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.CaravanQuery = _ => second.Task;
        var b = vm.RefreshCaravansAsync();
        session.PendingFileWork.Should().HaveCount(2);
        second.SetResult(new CaravanPositions(2_000, []));
        await b.WaitAsync(TimeSpan.FromSeconds(10));
        first.SetResult(new CaravanPositions(1_000, []));
        await a.WaitAsync(TimeSpan.FromSeconds(10));
        session.CurrentTick.Should().Be(2_000, "the older read finished last and is dropped");
        session.PendingFileWork.Should().BeEmpty();
    }
}
