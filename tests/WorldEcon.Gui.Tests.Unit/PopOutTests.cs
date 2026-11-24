using ErrorOr;
using FluentAssertions;
using WorldEcon.Application.Queries;
using WorldEcon.Gui.ViewModels;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>Pop-out city windows: one per city, pinned, refreshed together, closed before their world is.</summary>
public class PopOutTests
{
    /// <summary>Records what the window host was asked to do, with the session's closed state at each close.</summary>
    internal sealed class FakeHost : IPanelWindowHost
    {
        public readonly Dictionary<object, (string Title, Action OnClosed)> Open = [];
        public readonly List<string> Log = [];
        public Func<bool>? ClosedStateProbe;

        public void Show(object panel, string title, Action onClosed)
        {
            Open[panel] = (title, onClosed);
            Log.Add($"show {title}");
        }

        public void Activate(object panel) => Log.Add($"activate {Open[panel].Title}");

        public void Close(object panel)
        {
            Log.Add($"close {Open[panel].Title} sessionClosed={ClosedStateProbe?.Invoke()}");
            var onClosed = Open[panel].OnClosed;
            Open.Remove(panel);
            onClosed();
        }

        public void SetTitle(object panel, string title)
        {
            Open[panel] = (title, Open[panel].OnClosed);
            Log.Add($"title {title}");
        }

        public void ActivateMain() => Log.Add("activate main");

        /// <summary>The user closes a window.</summary>
        public void UserCloses(object panel)
        {
            var onClosed = Open[panel].OnClosed;
            Open.Remove(panel);
            onClosed();
        }
    }

    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    /// <summary>A pop-out manager whose city panels use a stub query; counts queries per city.</summary>
    private static (PanelWindows Windows, FakeHost Host, Dictionary<Guid, int> Calls) Make()
    {
        var host = new FakeHost();
        var calls = new Dictionary<Guid, int>();
        var windows = new PanelWindows(host, (session, id) => new CityPanelViewModel(session, _ => host.ActivateMain(), _ => { }, null,
            (cid, _) =>
            {
                calls[cid] = calls.GetValueOrDefault(cid) + 1;
                return Task.FromResult<ErrorOr<SettlementDetails>>(CityPanelTests.Sample(cid, cid == A ? "Ashford" : "Brindle"));
            },
            () => Task.FromResult<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>>(new List<(Guid, string)> { (A, "Ashford"), (B, "Brindle") })));
        return (windows, host, calls);
    }

    [Test]
    public async Task OpenCity_Twice_ActivatesExisting_TwoCities_TwoWindows()
    {
        var (w, host, _) = Make();
        using var s = TestWorlds.Session();
        var first = await w.OpenCityAsync(s, A);
        (await w.OpenCityAsync(s, A)).Should().BeSameAs(first);
        w.OpenCount.Should().Be(1);
        host.Log.Should().Contain("activate Ashford — Testland");
        await w.OpenCityAsync(s, B);
        w.OpenCount.Should().Be(2);
        host.Open.Values.Select(v => v.Title).Should().BeEquivalentTo(["Ashford — Testland", "Brindle — Testland"]);
    }

    [Test]
    public async Task PoppedOut_IsPinned_SearchRepinsAndRetitles()
    {
        var (w, host, _) = Make();
        using var s = TestWorlds.Session();
        var panel = await w.OpenCityAsync(s, A);
        panel.SettlementId.Should().Be(A);
        panel.SearchText = "brin";
        await panel.ChooseAsync(panel.SearchResults[0]);
        panel.SettlementId.Should().Be(B);
        host.Open[panel].Title.Should().Be("Brindle — Testland");
        (await w.OpenCityAsync(s, B)).Should().BeSameAs(panel, "the window now shows Brindle");
    }

    [Test]
    public async Task DataChanged_RequeriesAllOpen()
    {
        var (w, _, calls) = Make();
        using var s = TestWorlds.Session();
        var a = await w.OpenCityAsync(s, A);
        var b = await w.OpenCityAsync(s, B);
        s.RaiseDataChanged();
        await a.Loaded;
        await b.Loaded;
        (calls[A], calls[B]).Should().Be((2, 2));
    }

    [Test]
    public async Task UserClosingAWindow_DetachesItsPanel()
    {
        var (w, host, calls) = Make();
        using var s = TestWorlds.Session();
        var a = await w.OpenCityAsync(s, A);
        host.UserCloses(a);
        w.OpenCount.Should().Be(0);
        s.RaiseDataChanged();
        calls[A].Should().Be(1, "a closed window no longer follows the world");
    }

    [Test]
    public async Task ReplacingTheWorld_ClosesAllPopOuts_BeforeTheSessionIsDisposed()
    {
        var host = new FakeHost();
        var vm = TestWorlds.MainWithWorld();
        vm.Windows = new PanelWindows(host);
        var old = vm.Session!;
        host.ClosedStateProbe = () => old.IsClosed;
        vm.OpenSettlementInWindow(old.Data.Settlements[0].Id);
        host.Open.Should().ContainSingle();
        vm.UseSession(TestWorlds.Session());
        host.Open.Should().BeEmpty();
        host.Log.Should().Contain(l => l.StartsWith("close ", StringComparison.Ordinal) && l.EndsWith("sessionClosed=False", StringComparison.Ordinal));
        vm.Session!.Dispose();
    }

    [Test]
    public async Task LateResultAfterClose_Dropped()
    {
        var gate = new TaskCompletionSource<ErrorOr<SettlementDetails>>();
        var host = new FakeHost();
        var windows = new PanelWindows(host, (session, _) => new CityPanelViewModel(session, _ => { }, _ => { }, null, (_, _) => gate.Task,
            () => Task.FromResult<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>>(new List<(Guid, string)>())));
        using var s = TestWorlds.Session();
        var opening = windows.OpenCityAsync(s, A);
        var panel = host.Open.Keys.OfType<CityPanelViewModel>().Single();
        windows.CloseAll();
        gate.SetResult(CityPanelTests.Sample(A));
        await opening;
        panel.Details.Should().BeNull("the window was closed before the result arrived");
    }

    [Test]
    public void RightClick_OnSettlementMarker_OpensItsWindow_SettlementDetailsOfferNewWindow()
    {
        var host = new FakeHost();
        var loaded = TestWorlds.Loaded;
        var town = loaded.Settlements[0];
        var vm = TestWorlds.MainWithWorld();
        vm.Windows = new PanelWindows(host);
        vm.Map!.CentreOn(loaded.TileGrid.Centre(town.TileId!.Value));
        vm.Map.ZoomToLevel(WorldEcon.Rendering.MapLevel.Detail);
        var view = vm.Map.View;
        using (var bmp = new SkiaSharp.SKBitmap((int)view.Width, (int)view.Height))
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
            vm.Map.Renderer.DrawOverlay(canvas, view, vm.Map.DrawOptions);
        var p = loaded.TileGrid.Centre(town.TileId.Value);
        view.ProjectFast(new System.Numerics.Vector3((float)p.X, (float)p.Y, (float)p.Z), out var x, out var y).Should().BeTrue();

        vm.ContextClick(x, y).Should().BeTrue();
        host.Open.Keys.OfType<CityPanelViewModel>().Single().SettlementId.Should().Be(town.Id);
        vm.ContextClick(1, 1).Should().BeFalse("nothing to open there");

        vm.SelectObject(new WorldEcon.Rendering.MapObject(WorldEcon.Rendering.MapObjectKind.Settlement, town.Id));
        vm.DetailRows.Should().Contain(r => r.Label == "New window" && r.Open != null);
    }

    [Test]
    public void NoHost_CanPopOutFalse()
    {
        var vm = TestWorlds.MainWithWorld();
        vm.Windows = new PanelWindows(null);
        vm.Windows.Supported.Should().BeFalse();
        vm.City!.CanPopOut.Should().BeFalse();
        vm.OpenSettlementInWindow(vm.Session!.Data.Settlements[0].Id);
        vm.Windows.OpenCount.Should().Be(0);
        vm.Windows = new PanelWindows(new FakeHost());
        vm.City.CanPopOut.Should().BeTrue();
    }

    [Test]
    public async Task PopOutRequested_FromDockedPanel_OpensTheShownCity()
    {
        var host = new FakeHost();
        var vm = TestWorlds.MainWithWorld();
        vm.Windows = new PanelWindows(host);
        var id = vm.Session!.Data.Settlements[0].Id;
        vm.OpenSettlement(id);
        await vm.City!.Loaded;
        vm.City.RequestPopOut();
        vm.Windows.OpenCount.Should().Be(1);
        host.Open.Keys.OfType<CityPanelViewModel>().Single().SettlementId.Should().Be(id);
        vm.City.SettlementId.Should().Be(id, "the docked panel is unchanged");
    }
}
