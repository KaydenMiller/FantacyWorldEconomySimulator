using FluentAssertions;
using WorldEcon.Tui.Shell;

namespace WorldEcon.Tui.Tests.Unit;

/// <summary>
/// Headless smoke test for the Terminal.Gui shell. We do NOT enter the interactive run loop (there is
/// no TTY in CI), and Terminal.Gui v2.4 has no fake/headless driver. Instead we construct the shell's
/// views directly — Terminal.Gui v2 allows building a <c>Window</c>/<c>TableView</c> tree without
/// <c>Application.Init</c> — and assert the initial resource table is populated and that the
/// non-Terminal.Gui key glue maps characters to the right registry actions.
///
/// Full interactive UI behaviour (rendering, focus, modal dialogs) is verified manually by running
/// <c>dotnet run --project src/WorldEcon.Tui -- &lt;dbPath&gt;</c>.
/// </summary>
public class ShellSmokeTests
{
    [Test]
    public async Task Shell_Constructs_And_PopulatesInitialCitiesTable()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx, path);
            var registry = CommandRegistry.CreateDefault();
            var ui = new FakeUserInteraction();

            // Build the shell views without an interactive run loop.
            using var shell = new TuiShell(tui, registry, ui);

            // Defaults to the cities resource and loads its rows up front.
            shell.CurrentResource.Name.Should().Be("cities");
            shell.CurrentTable.Columns.Should().Contain("Name");
            shell.CurrentTable.Rows.Should().NotBeEmpty();
            shell.SelectedKey.Should().NotBeNull();

            // The non-Terminal.Gui key glue resolves known keys (details, help, global + row actions).
            shell.HandleActionKey('d').Should().BeTrue();   // details
            shell.HandleActionKey('?').Should().BeTrue();   // help
            shell.HandleActionKey('a').Should().BeTrue();   // AdvanceAction (global)
            shell.HandleActionKey('b').Should().BeTrue();   // BuyOutAction (cities row action)
            shell.HandleActionKey('z').Should().BeFalse();  // unknown key

            // Switching resources reloads the table.
            var goods = registry.ResolveResource("goods");
            goods.Should().NotBeNull();
            shell.SwitchResource(goods!);
            shell.CurrentResource.Name.Should().Be("goods");
            shell.CurrentTable.Rows.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
