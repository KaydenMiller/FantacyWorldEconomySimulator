using FluentAssertions;
using WorldEcon.Tui.Navigation;
using WorldEcon.Tui.Shell;

namespace WorldEcon.Tui.Tests.Unit;

/// <summary>
/// Headless smoke test for the Terminal.Gui drill shell. We do NOT enter the interactive run loop
/// (no TTY in CI, and Terminal.Gui 2.4 exposes no fake driver). Instead we construct the shell with no
/// <see cref="Terminal.Gui.App.IApplication"/> (so it runs inline) and drive its public, Terminal.Gui-free
/// glue: the initial root view, key mapping, and drill/back stack. Full interactive UI (rendering,
/// focus, modal/prompt dialogs, autocomplete) is verified manually under tmux.
/// </summary>
public class ShellSmokeTests
{
    [Test]
    public async Task Shell_StartsAtCitiesRoot_AndDrillsAndGoesBack()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx, path);
            using var shell = new TuiShell(tui, new Navigator(), new FakeUserInteraction());

            shell.Depth.Should().Be(1);
            shell.CurrentView.Title.Should().Be("Cities");
            shell.CurrentView.Rows.Should().NotBeEmpty();
            shell.CurrentView.Rows.Should().OnlyContain(r => r.Kind == NavKind.City);

            // Drill first city -> category chooser; Back -> root.
            shell.DrillSelected();
            shell.Depth.Should().Be(2);
            shell.CurrentView.Rows.Should().OnlyContain(r => r.Kind == NavKind.CityCategory);

            shell.Back();
            shell.Depth.Should().Be(1);
            shell.CurrentView.Title.Should().Be("Cities");

            shell.SetRoot("goods");
            shell.CurrentView.Title.Should().Be("Goods");
            shell.Depth.Should().Be(1);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task HandleActionKey_MapsKnownKeys()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx, path);
            using var shell = new TuiShell(tui, new Navigator(), new FakeUserInteraction());

            shell.HandleActionKey('?').Should().BeTrue();   // help
            shell.HandleActionKey('a').Should().BeTrue();   // advance (global)
            shell.HandleActionKey('z').Should().BeFalse();  // unknown
        }
        finally { File.Delete(path); }
    }
}
