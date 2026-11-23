using System.Diagnostics;
using System.Numerics;
using FluentAssertions;
using WorldEcon.Rendering.Gpu;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>
/// The backend-neutral GPU data (design 2026-11-23-gpu-map-rendering): the C# mirror of the vertex shader against
/// <see cref="MapViewState"/>, the palette LUT against <see cref="LayerPalette"/>, the scene, atlas, river and tile block
/// buffers, and the shared shader sources (compiled as GLSL ES 3.00 and 3.30 when glslangValidator is installed).
/// </summary>
public class GpuTests
{
    private static readonly WorldGenSettings Settings = new() { Grid = new GridSettings { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 } };
    private static readonly Lazy<GeneratedWorld> Gen = new(() => WorldGenerator.Generate(42, Settings));

    private static MapWorld World() => MapWorld.FromGenerated(Gen.Value, Settings);

    private static readonly MapDrawOptions Options = new() { Layer = GlobeLayer.Biome, Borders = true };

    [Test]
    [Arguments(MapLevel.Globe)]
    [Arguments(MapLevel.Region)]
    [Arguments(MapLevel.Detail)]
    public void Shader_projection_matches_the_view_state(MapLevel level)
    {
        var w = World();
        var centre = w.Globe.Geometry.Centre(10);
        var v = w.View(centre, 1, 1280, 800);
        v = level switch
        {
            MapLevel.Globe => v.WithScale(v.MinScale),
            MapLevel.Region => v.WithScale(w.ScaleForRegionPx(60)),
            _ => v.WithScale(w.ScaleForTilePx(40)),
        };
        v.Level.Should().Be(level);
        var f = GpuFrame.For(v, Options, level, 1);
        var checkedPoints = 0;
        for (var c = 0; c < w.Globe.Grid.CellCount; c++)
        {
            var p = w.Globe.Geometry.Centre(c);
            var pf = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            if (!v.ProjectFast(pf, out var x, out var y) || x < -100 || x > 1380 || y < -100 || y > 900) continue;
            GpuProjection.Project(f, pf, out var s).Should().BeTrue();
            s.X.Should().BeApproximately(x, 0.05f, $"cell {c}");
            s.Y.Should().BeApproximately(y, 0.05f, $"cell {c}");
            checkedPoints++;
        }

        checkedPoints.Should().BeGreaterThan(3);
    }

    [Test]
    public void Shader_inset_moves_a_cell_edge_in_by_the_gap()
    {
        var w = World();
        var v = w.View(w.Globe.Geometry.Centre(0), 1, 1280, 800);
        v = v.WithScale(v.MinScale * 4);
        var f = GpuFrame.For(v, Options, MapLevel.Globe, 1);
        f.EdgeInsetPx.Should().Be(MapRenderer.GlobeGapPx / 2);
        var c = w.Globe.Geometry.Centre(0);
        var corner = w.Globe.Geometry.Corner(w.Globe.Grid.CornerTriangles(0)[0]);
        Vector3 F(Vector3d d) => new((float)d.X, (float)d.Y, (float)d.Z);
        GpuProjection.Project(f, F(c), out var pc);
        GpuProjection.Project(f, F(corner), out var pk);
        var inset = GpuProjection.CellVertex(f, F(corner), F(c))!.Value;
        // The corner moves toward the centre by gap / 2 / 0.866 (the edge, 0.866 × radius away, moves in by gap / 2).
        ((pk - pc).Length() - (inset - pc).Length()).Should().BeApproximately(f.EdgeInsetPx / 0.866f, 0.01f);
        GpuFrame.For(v, Options, MapLevel.Region, 1).EdgeInsetPx.Should().Be(2f, "1.5 px group step + 0.5 px cell gap");
        GpuFrame.For(v, Options with { Borders = false }, MapLevel.Region, 1).EdgeInsetPx.Should().Be(0f);
    }

    [Test]
    public void A_hidden_cell_centre_drops_the_cell()
    {
        var w = World();
        var centre = w.Globe.Geometry.Centre(0);
        var v = w.View(centre, 1, 1280, 800);
        v = v.WithScale(v.MinScale);
        var f = GpuFrame.For(v, Options, MapLevel.Globe, 1);
        var back = new Vector3((float)-centre.X, (float)-centre.Y, (float)-centre.Z);
        GpuProjection.CellVertex(f, back, back).Should().BeNull();
    }

    [Test]
    public void Palette_colours_equal_the_cpu_palette()
    {
        var w = World();
        using var r = new MapRenderer(w);
        var p = GpuPalette.Build(r);
        var img = w.Imagery!;
        var sea = w.Globe.SeaLevelM;
        static int MaxChannelDiff(uint a, uint b) =>
            new[] { 16, 8, 0 }.Max(s => Math.Abs((int)((a >> s) & 0xFF) - (int)((b >> s) & 0xFF)));
        for (var k = 0; k < img.Biome.Length; k += 7)
        {
            var water = (TileWaterKind)img.Water[k];
            uint Gpu(GpuLayerMode mode) => p.Colour(mode, img.Biome[k], img.Landform[k], img.Water[k], img.FlowLevel[k], img.ElevationM[k], img.TemperatureDeciC[k], img.PrecipitationMm[k], 0);
            Gpu(GpuLayerMode.Biome).Should().Be(LayerPalette.BiomeColor((Biome)img.Biome[k]) | 0xFF000000u);
            Gpu(GpuLayerMode.Landform).Should().Be(LayerPalette.LandformColor((Landform)img.Landform[k]) | 0xFF000000u);
            Gpu(GpuLayerMode.Temperature).Should().Be(LayerPalette.TemperatureColor(img.TemperatureDeciC[k]) | 0xFF000000u);
            MaxChannelDiff(Gpu(GpuLayerMode.Elevation), LayerPalette.ElevationColor(img.ElevationM[k], sea, water == TileWaterKind.Ocean)).Should().BeLessThanOrEqualTo(2, "2048 elevation steps");
            MaxChannelDiff(Gpu(GpuLayerMode.Precipitation), LayerPalette.PrecipitationColor(img.PrecipitationMm[k])).Should().BeLessThanOrEqualTo(1, "2 mm steps");
            var kind = water switch
            {
                TileWaterKind.River => WaterBodyKind.River,
                TileWaterKind.Lake => WaterBodyKind.Lake,
                TileWaterKind.DryLakeBed => WaterBodyKind.DryLakeBed,
                _ => WaterBodyKind.None,
            };
            Gpu(GpuLayerMode.Hydrology).Should().Be(LayerPalette.HydrologyColor(kind, (Biome)img.Biome[k], r.RiverT(CellImagery.LevelToFlow(img.FlowLevel[k]))) | 0xFF000000u);
        }

        GpuPalette.ModeOf(GlobeLayer.Plates).Should().Be(GpuLayerMode.CellColour);
        GpuPalette.ModeOf(GlobeLayer.Settlements).Should().Be(GpuLayerMode.Biome);
    }

    [Test]
    public void Scene_mesh_is_a_fan_per_cell_and_the_atlas_holds_every_texel()
    {
        var w = World();
        var scene = GpuScene.Build(w);
        var grid = w.Globe.Grid;
        var expectedVertices = Enumerable.Range(0, grid.CellCount).Sum(c => grid.CornerTriangles(c).Length + 1);
        scene.VertexCount.Should().Be(expectedVertices);
        scene.Vertices.Length.Should().Be(expectedVertices * GpuScene.FloatsPerVertex);
        scene.Indices.Should().OnlyContain(i => i < (uint)expectedVertices);
        scene.Indices.Length.Should().Be(Enumerable.Range(0, grid.CellCount).Sum(c => 3 * grid.CornerTriangles(c).Length));

        // Each triangle's vertices belong to one cell; the fan's first vertex is the cell centre at the chart centre (texel 8, 8).
        for (var t = 0; t < scene.Indices.Length; t += 3)
        {
            var cell = scene.VertexCells[scene.Indices[t]];
            scene.VertexCells[scene.Indices[t + 1]].Should().Be(cell);
            scene.VertexCells[scene.Indices[t + 2]].Should().Be(cell);
        }

        var v0 = (int)scene.Indices[0] * GpuScene.FloatsPerVertex;
        scene.Vertices[v0 + 6].Should().BeApproximately(8f, 1e-3f);
        scene.Vertices[v0 + 7].Should().BeApproximately(8f, 1e-3f);

        var img = w.Imagery!;
        scene.AtlasLayers.Should().Be((grid.CellCount + GpuScene.CellsPerLayer - 1) / GpuScene.CellsPerLayer);
        foreach (var c in new[] { 0, 1, grid.CellCount / 2, grid.CellCount - 1 })
            for (var j = 0; j < CellImagery.Size; j += 5)
                for (var i = 0; i < CellImagery.Size; i += 3)
                {
                    var (x, y, layer) = GpuScene.AtlasTexel(c, i, j);
                    var o = ((layer * (long)GpuScene.AtlasSize * GpuScene.AtlasSize) + ((long)y * GpuScene.AtlasSize) + x) * 4;
                    var k = CellImagery.Index(c, i, j);
                    scene.AtlasClass![o].Should().Be(img.Biome[k]);
                    scene.AtlasClass[o + 2].Should().Be(img.Water[k]);
                    scene.AtlasValue![o].Should().Be(img.ElevationM[k]);
                    scene.AtlasValue[o + 1].Should().Be(img.TemperatureDeciC[k]);
                }
    }

    [Test]
    public void Rivers_cover_every_piece_of_every_course_level_sorted_by_width()
    {
        var w = World();
        using var r = new MapRenderer(w);
        var rivers = GpuRivers.Build(r);
        var levels = r.RiverCourseLevels();
        rivers.PieceCount.Should().Be(levels.Sum(l => l.Points.Sum(p => Math.Max(0, p.Length - 1))));
        rivers.Levels.Count.Should().Be(levels.Count);
        var next = 0;
        foreach (var (first, count) in rivers.Levels)
        {
            first.Should().Be(next);
            next = first + count;
            var widths = Enumerable.Range(first / 6, count / 6).Select(pc => rivers.Vertices[(pc * 4 * GpuRivers.FloatsPerVertex) + 8]).ToList();
            widths.Should().BeInAscendingOrder();
        }

        rivers.Indices.Should().OnlyContain(i => i < (uint)(rivers.PieceCount * 4));
    }

    [Test]
    public async Task A_tile_block_mesh_is_complete_once_its_neighbours_are_loaded()
    {
        var w = World();
        using var r = new MapRenderer(w);
        var l = w.Globe;
        var cell = Enumerable.Range(0, l.Grid.CellCount).First(c => !l.IsOcean(c));
        await r.Cache!.EnsureLoadedAsync(cell);
        var e = r.Cache.TryGet(cell, out _)!;

        var alone = GpuTileBlock.Build(r, e, c => c == cell ? e : null);
        alone.VertexCount.Should().BeGreaterThan(0);
        (alone.VertexCount % 3).Should().Be(0);
        alone.Vertices.Length.Should().Be(alone.VertexCount * GpuTileBlock.Stride);
        alone.Complete.Should().BeFalse("triangles across the block border reach unloaded neighbours");

        foreach (var nb in l.Grid.Neighbours(cell).ToArray()) await r.Cache.EnsureLoadedAsync(nb);
        var full = GpuTileBlock.Build(r, e, c => r.Cache.TryGet(c, out _));
        full.Complete.Should().BeTrue();
        full.VertexCount.Should().Be(alone.VertexCount, "the same triangles, with the neighbours' data");

        // Vertex 0's class A is its own tile's (the first tile of the block emits first).
        var first = e.Block;
        full.Vertices[12].Should().Be(first.Biome[0]);
        full.Vertices[14].Should().Be(first.Water[0]);
    }

    [Test]
    public void Shader_sources_resolve_includes_and_compile_as_es300_and_core330()
    {
        foreach (var name in GpuShaders.All)
        {
            var body = GpuShaders.Body(name);
            body.Split('\n').Should().NotContain(l => l.TrimStart().StartsWith("#include", StringComparison.Ordinal), $"{name}: every include directive is resolved");
            body.Should().Contain("void main()", name);
        }

        GpuShaders.Body(GpuShaders.GlobeFragment).Should().Contain("vec3 texelColour(", "palette.glsl is included");
        var validator = FindValidator();
        if (validator is null) return; // glslangValidator not installed: the resolution checks above still ran

        var dir = Directory.CreateTempSubdirectory("gpu-shaders-");
        try
        {
            foreach (var name in GpuShaders.All)
                foreach (var dialect in Enum.GetValues<GlslDialect>())
                {
                    var stage = name.Contains(".vert.") ? "vert" : "frag";
                    var file = Path.Combine(dir.FullName, $"{dialect}.{name}.{stage}");
                    File.WriteAllText(file, GpuShaders.Source(name, dialect));
                    using var proc = Process.Start(new ProcessStartInfo(validator, file) { RedirectStandardOutput = true, RedirectStandardError = true })!;
                    var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    proc.ExitCode.Should().Be(0, $"{name} as {dialect}: {output}");
                }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string? FindValidator() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, "glslangValidator"))
            .FirstOrDefault(File.Exists);
}
