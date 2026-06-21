namespace WorldEcon.Tui;

/// <summary>A single row in a <see cref="ResourceTable"/>. <see cref="Key"/> is the entity id as a
/// string (Guid string), used to fetch details or target a row action.</summary>
public sealed record ResourceRow(string Key, IReadOnlyList<string> Cells);

/// <summary>A UI-agnostic, fully-materialized table of one resource's rows.</summary>
public sealed record ResourceTable(IReadOnlyList<string> Columns, IReadOnlyList<ResourceRow> Rows);
