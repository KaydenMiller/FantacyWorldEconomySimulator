using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Rendering;

/// <summary>Which map layer shows a generation stage's result (stage viewer, <c>globe gen --png auto</c>).</summary>
public static class StageLayers
{
    /// <summary>Tectonics → Elevation; Plates → Plates; Elevation and SeaLevel → Elevation; Currents → Currents; Winds → Winds; Climate → Temperature; a finished world → Biome.</summary>
    public static GlobeLayer DefaultFor(WorldGenStage stage) => stage switch
    {
        WorldGenStage.Plates => GlobeLayer.Plates,
        WorldGenStage.Tectonics => GlobeLayer.Elevation,
        WorldGenStage.Elevation or WorldGenStage.SeaLevel => GlobeLayer.Elevation,
        WorldGenStage.Currents => GlobeLayer.Currents,
        WorldGenStage.Winds => GlobeLayer.Winds,
        WorldGenStage.Climate => GlobeLayer.Temperature,
        _ => GlobeLayer.Biome,
    };
}
