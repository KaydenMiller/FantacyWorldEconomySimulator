using System.Runtime.InteropServices;
using System.Text.Json;

namespace WorldEcon.Rendering.Gpu;

/// <summary>
/// Writes the backend-neutral GPU data of a world to a folder for a web front end (design 2026-11-23-gpu-map-rendering
/// §9, phase 5): <c>manifest.json</c> (constants, counts, file names, river level ranges), one little-endian binary file
/// per buffer or texture (loaded as typed arrays and uploaded to WebGL2 as is), and the resolved shader bodies under
/// <c>shaders/</c> (the page prepends the ES header from <see cref="GpuShaders.Header"/>).
/// </summary>
public static class GpuSceneExport
{
    public static void Write(MapRenderer renderer, string directory)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var w = renderer.World;
        var scene = GpuScene.Build(w);
        var rivers = GpuRivers.Build(renderer);
        var palette = GpuPalette.Build(renderer);
        var geometry = GpuRivers.CellGeometry(w, out var geometryRows);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "shaders"));

        void Bin<T>(string name, T[] data) where T : unmanaged =>
            File.WriteAllBytes(Path.Combine(directory, name), MemoryMarshal.AsBytes(data.AsSpan()).ToArray());

        Bin("globe.vertices.f32", scene.Vertices);
        Bin("globe.cells.u32", scene.VertexCells);
        Bin("globe.indices.u32", scene.Indices);
        if (scene.AtlasClass is { } ac) Bin("atlas.class.u8", ac);
        if (scene.AtlasValue is { } av) Bin("atlas.value.i16", av);
        Bin("palette.u8", palette.Lut);
        Bin("cellgeom.f32", geometry);
        Bin("rivers.vertices.f32", rivers.Vertices);
        Bin("rivers.cells.u32", rivers.Cells);
        Bin("rivers.indices.u32", rivers.Indices);
        Bin("glyphs.vertices.f32", scene.GlobeGlyphs.Vertices);
        Bin("glyphs.groups.u32", scene.GlobeGlyphs.Groups);
        var cellColours = GpuPalette.CellColours(renderer, GlobeLayer.Plates, scene.CellTextureRows);
        Bin("cellcolour.plates.u8", cellColours);
        foreach (var name in GpuShaders.All) File.WriteAllText(Path.Combine(directory, "shaders", name), GpuShaders.Body(name));

        var manifest = new
        {
            format = "worldecon-gpu-scene/1",
            esHeader = GpuShaders.Header(GlslDialect.Es300),
            cellCount = scene.CellCount,
            vertexCount = scene.VertexCount,
            indexCount = scene.Indices.Length,
            floatsPerVertex = GpuScene.FloatsPerVertex,
            atlasSize = GpuScene.AtlasSize,
            atlasLayers = scene.AtlasLayers,
            cellTextureWidth = GpuScene.CellTextureWidth,
            cellTextureRows = scene.CellTextureRows,
            cellGeometryRows = geometryRows,
            palette = new { width = GpuPalette.Width, rows = GpuPalette.Rows, elevationMin = palette.ElevationMin, elevationMax = palette.ElevationMax },
            rivers = new
            {
                floatsPerVertex = GpuRivers.FloatsPerVertex,
                pieceCount = rivers.PieceCount,
                levels = rivers.Levels.Select((l, i) => new { first = l.First, count = l.Count, toleranceRad = rivers.TolerancesRad[i], minLengthPx = rivers.MinLengthPx[i] }),
                minFrac = GpuRivers.MinFrac,
                addFrac = GpuRivers.AddFrac,
                maxPx = GpuRivers.MaxPx,
                courseTolerancePx = 1.5,
            },
            glyphs = new { floatsPerVertex = GpuGlyphs.FloatsPerVertex, vertexCount = scene.GlobeGlyphs.VertexCount, minPx = MapRenderer.MountainSymbolMinPx, colour = LayerPalette.MountainSymbolColor },
            view = new
            {
                regionCellAngle = w.RegionCellAngle,
                tileAngle = w.TileAngle,
                globeGapPx = MapRenderer.GlobeGapPx,
                thresholds = new MapLevelThresholds(),
                light = new[] { GpuFrame.Light.X, GpuFrame.Light.Y, GpuFrame.Light.Z },
                discColour = Rgb(MapRenderer.GapColor),
                outerColour = Rgb(MapRenderer.BackgroundColor),
            },
            layers = Enum.GetValues<GlobeLayer>().Select(l => new { name = l.ToString(), mode = (int)GpuPalette.ModeOf(l) }),
            shaders = GpuShaders.All,
        };
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static float[] Rgb(SkiaSharp.SKColor c) => [c.Red / 255f, c.Green / 255f, c.Blue / 255f];
}
