namespace WorldEcon.Tui.Navigation;

/// <summary>What a drill row represents — drives Enter-drill behaviour and per-kind actions.</summary>
public enum NavKind
{
    Continent, Country, Region, City, CityCategory,
    Merchant, Shop, Factory, Caravan, Good, Recipe, Claim, Action, Leaf,
}

/// <summary>A row in a drill view. <see cref="Key"/> is the entity Guid string, or a composite token
/// for <see cref="NavKind.CityCategory"/> (e.g. "{settlementGuid}|shops").</summary>
public sealed record NavRow(string Key, NavKind Kind, IReadOnlyList<string> Cells);

/// <summary>A drill level: a titled, columnar list. The shell renders it and pushes/pops a stack of these.</summary>
public sealed record NavView(string Title, IReadOnlyList<string> Columns, IReadOnlyList<NavRow> Rows);
