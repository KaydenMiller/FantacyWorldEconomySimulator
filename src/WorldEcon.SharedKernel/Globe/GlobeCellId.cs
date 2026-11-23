namespace WorldEcon.SharedKernel.Globe;

/// <summary>
/// Identifies one cell of the generated hex globe. The value is a dense index in
/// <c>0 … 10m² + 1</c> (m = grid frequency), usable directly as an array index.
/// Ids 0–11 are the 12 pentagon cells.
/// </summary>
public readonly record struct GlobeCellId(int Value);
