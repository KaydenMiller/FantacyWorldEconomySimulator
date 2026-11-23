namespace WorldEcon.SharedKernel.Globe;

/// <summary>Kind of a natural geographic area (a many-to-many grouping of globe cells with a coverage share).</summary>
public enum GeographicRegionKind : byte
{
    Ocean,
    Sea,
    Lake,
    DryLakeBed,
    MountainRange,
    Hills,
    Basin,
    Plain,
    Forest,
    Desert,
    Grassland,
    Tundra,
    Swamp,
    IceSheet,
}
