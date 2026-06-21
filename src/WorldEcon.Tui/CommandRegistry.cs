using WorldEcon.Tui.Actions;
using WorldEcon.Tui.Resources;

namespace WorldEcon.Tui;

/// <summary>
/// The wiring point between the shell and the UI-agnostic core: it owns the set of resources and
/// actions, resolves resource tokens (name or alias, case-insensitive), and answers which row
/// actions apply to a given resource. New data-entry forms are added as new <see cref="IGlobalAction"/>
/// or <see cref="IRowAction"/> implementations registered in <see cref="CreateDefault"/> — the shell
/// discovers them through this registry and never needs to change.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, IResource> _byToken;

    public CommandRegistry(
        IReadOnlyList<IResource> resources,
        IReadOnlyList<IGlobalAction> globalActions,
        IReadOnlyList<IRowAction> rowActions)
    {
        Resources = resources;
        GlobalActions = globalActions;
        RowActions = rowActions;

        _byToken = new Dictionary<string, IResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in resources)
        {
            _byToken[resource.Name] = resource;
            foreach (var alias in resource.Aliases)
                _byToken[alias] = resource;
        }
    }

    public IReadOnlyList<IResource> Resources { get; }
    public IReadOnlyList<IGlobalAction> GlobalActions { get; }
    public IReadOnlyList<IRowAction> RowActions { get; }

    /// <summary>Resolves a token (resource name or alias, case-insensitive). Returns null if unknown.</summary>
    public IResource? ResolveResource(string token)
        => token is not null && _byToken.TryGetValue(token, out var r) ? r : null;

    /// <summary>The row actions registered for the given canonical resource name.</summary>
    public IReadOnlyList<IRowAction> RowActionsFor(string resourceName)
        => RowActions
            .Where(a => string.Equals(a.ResourceName, resourceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Wires all default resources and actions.</summary>
    public static CommandRegistry CreateDefault()
    {
        IReadOnlyList<IResource> resources =
        [
            new CitiesResource(),
            new GoodsResource(),
            new ShopsResource(),
            new MerchantsResource(),
            new CaravansResource(),
            new StockpilesResource(),
            new RecipesResource(),
            new NodesResource(),
            new ActionsResource(),
        ];

        IReadOnlyList<IGlobalAction> globalActions =
        [
            new AdvanceAction(),
            new SnapshotAction(),
        ];

        IReadOnlyList<IRowAction> rowActions =
        [
            new BuyOutAction(),
            new DisableProductionAction(),
            new EnableProductionAction(),
        ];

        return new CommandRegistry(resources, globalActions, rowActions);
    }
}
