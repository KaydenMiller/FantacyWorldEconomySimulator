namespace WorldEcon.WorldGen.Stages;

/// <summary>Plate boundary classification stored per cell in <see cref="WorldLayers.BoundaryKind"/>.</summary>
public enum BoundaryKind : byte
{
    None,
    Convergent,
    Divergent,
    Transform,
}
