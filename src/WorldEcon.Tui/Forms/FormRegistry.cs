namespace WorldEcon.Tui.Forms;

/// <summary>The ordered set of create-forms the shell's <c>n</c> (new) command offers. Grouped
/// geography → goods → economy → production so the chooser reads top-down through the model.</summary>
public static class FormRegistry
{
    public static IReadOnlyList<IEntityForm> All { get; } =
    [
        new ContinentForm(),
        new CountryForm(),
        new RegionForm(),
        new SettlementForm(),
        new RouteForm(),
        new GoodForm(),
        new ShopForm(),
        new StockpileForm(),
        new MerchantForm(),
        new ConsumerForm(),
        new EndowmentForm(),
        new RecipeForm(),
        new ProductionNodeForm(),
    ];
}
