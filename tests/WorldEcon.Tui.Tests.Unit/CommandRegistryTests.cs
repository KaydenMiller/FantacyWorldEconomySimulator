using FluentAssertions;
using WorldEcon.Tui.Resources;

namespace WorldEcon.Tui.Tests.Unit;

public class CommandRegistryTests
{
    [Test]
    public async Task CommandRegistry_ResolvesAliases()
    {
        var registry = CommandRegistry.CreateDefault();

        registry.ResolveResource("city").Should().BeOfType<CitiesResource>();
        registry.ResolveResource("settlements").Should().BeOfType<CitiesResource>();
        registry.ResolveResource("CITIES").Should().BeOfType<CitiesResource>();
        registry.ResolveResource("nope").Should().BeNull();

        // Cities exposes the three settlement-scoped row actions.
        registry.RowActionsFor("cities").Select(a => a.Key)
            .Should().BeEquivalentTo(['b', 'x', 'e']);

        await Task.CompletedTask;
    }
}
