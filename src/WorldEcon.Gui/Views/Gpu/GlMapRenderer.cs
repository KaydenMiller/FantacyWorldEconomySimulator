using Avalonia.OpenGL;
using WorldEcon.Rendering;
using WorldEcon.Rendering.Gpu;

namespace WorldEcon.Gui.Views.Gpu;

/// <summary>
/// Everything uploaded once per world (built on a worker thread by <see cref="Prepare"/>; about 0.5 s and 200 MB of
/// arrays at 40,962 cells, released after the upload).
/// </summary>
internal sealed record GpuMapData(GpuScene Scene, GpuRivers Rivers, GpuPalette Palette, float[] CellGeometry, int CellGeometryRows, MapRenderer Renderer)
{
    public static GpuMapData Prepare(MapRenderer renderer)
    {
        var scene = GpuScene.Build(renderer.World);
        var rivers = GpuRivers.Build(renderer);
        var palette = GpuPalette.Build(renderer);
        var geometry = GpuRivers.CellGeometry(renderer.World, out var rows);
        return new GpuMapData(scene, rivers, palette, geometry, rows, renderer);
    }
}

/// <summary>
/// The desktop OpenGL backend of the GPU map (design 2026-11-23-gpu-map-rendering §5): uploads a <see cref="GpuMapData"/>
/// once and draws every level from a <see cref="GpuFrame"/> (uniforms only): background, globe cells from the texel
/// atlas, tile blocks where they are loaded (<see cref="GlTileBlocks"/>), mountain symbols, rivers. Uses only calls that
/// OpenGL ES 3.0 / WebGL2 also have. Owns GL objects; call <see cref="Dispose"/> on the GL thread.
/// </summary>
internal sealed unsafe class GlMapRenderer : IDisposable
{
    private readonly GlApi _api;
    private readonly GlInterface _gl;
    private readonly int _globeProgram;
    private readonly int _backgroundProgram;
    private readonly int _riverProgram;
    private readonly int _glyphProgram;
    private readonly int _tileProgram;
    private readonly int _emptyVao;
    private readonly Dictionary<(int Program, string Name), int> _uniforms = [];
    private readonly List<int> _buffers = [];
    private readonly List<int> _textures = [];
    private readonly List<int> _visible = [];
    private readonly List<(GlTileBlocks.Mesh Mesh, int Cell)> _tiled = [];
    private int _globeVao;
    private int _globeIndexCount;
    private int _riverVao;
    private int _globeGlyphVao;
    private int _globeGlyphCount;
    private int _regionGlyphVao;
    private int _regionGlyphCount;
    private IReadOnlyList<(int First, int Count)> _riverLevels = [];
    private int _texClass;
    private int _texValue;
    private int _texPalette;
    private int _texCellColour;
    private int _texCellFlags;
    private int _texCellGeom;
    private int _cellRows;
    private int _cellCount;
    private bool _hasAtlas;
    private int _elevMin;
    private int _elevMax;
    private GlobeLayer? _cellColourLayer;
    private byte[] _flags = [];
    private byte[] _flagsUploaded = [];
    private MapRenderer? _renderer;
    private GlTileBlocks? _tiles;

    public GlMapRenderer(GlInterface gl, GlslDialect dialect)
    {
        _gl = gl;
        _api = new GlApi(gl);
        Dialect = dialect;
        _globeProgram = Link(GpuShaders.GlobeVertex, GpuShaders.GlobeFragment);
        _backgroundProgram = Link(GpuShaders.BackgroundVertex, GpuShaders.BackgroundFragment);
        _riverProgram = Link(GpuShaders.RiverVertex, GpuShaders.RiverFragment);
        _glyphProgram = Link(GpuShaders.GlyphVertex, GpuShaders.GlyphFragment);
        _tileProgram = Link(GpuShaders.TileVertex, GpuShaders.TileFragment);
        _emptyVao = gl.GenVertexArray();
    }

    public GlslDialect Dialect { get; }

    /// <summary>River pieces uploaded (all course levels).</summary>
    public int RiverPieces { get; private set; }

    /// <summary>Tile block meshes drawn in the last frame.</summary>
    public int LastTileBlocks { get; private set; }

    /// <summary>Tile block meshes being built (not yet drawable).</summary>
    public int PendingTileBlocks => _tiles?.Pending ?? 0;

    /// <summary>Raised (on a worker thread) when a tile block mesh finished building.</summary>
    public event Action? TileBlockBuilt;

    private int Link(string vertex, string fragment)
    {
        int Compile(int type, string name)
        {
            var shader = _gl.CreateShader(type);
            var error = _gl.CompileShaderAndGetError(shader, GpuShaders.Source(name, Dialect));
            if (error is not null) throw new InvalidOperationException($"Shader {name} ({Dialect}) did not compile: {error}");
            return shader;
        }

        var program = _gl.CreateProgram();
        var vs = Compile(GlApi.VertexShader, vertex);
        var fs = Compile(GlApi.FragmentShader, fragment);
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        var link = _gl.LinkProgramAndGetError(program);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        if (link is not null) throw new InvalidOperationException($"Program {vertex} + {fragment} did not link: {link}");
        return program;
    }

    private int U(int program, string name)
    {
        if (!_uniforms.TryGetValue((program, name), out var loc)) _uniforms[(program, name)] = loc = _gl.GetUniformLocationString(program, name);
        return loc;
    }

    private int Buffer<T>(int target, T[] data) where T : unmanaged
    {
        var b = _gl.GenBuffer();
        _buffers.Add(b);
        _gl.BindBuffer(target, b);
        fixed (T* p = data) _gl.BufferData(target, (nint)((long)data.Length * sizeof(T)), (nint)p, GlApi.StaticDraw);
        return b;
    }

    private int Texture(int target)
    {
        var t = _gl.GenTexture();
        _textures.Add(t);
        _gl.BindTexture(target, t);
        _gl.TexParameteri(target, GlApi.TextureMinFilter, GlApi.Nearest);
        _gl.TexParameteri(target, GlApi.TextureMagFilter, GlApi.Nearest);
        _gl.TexParameteri(target, GlApi.TextureWrapS, GlApi.ClampToEdge);
        _gl.TexParameteri(target, GlApi.TextureWrapT, GlApi.ClampToEdge);
        return t;
    }

    /// <summary>
    /// Binds the attributes of a <see cref="GpuRivers"/> buffer set to the bound vertex array (floats 0–4 and 6, the end
    /// cells 5) and uploads its index buffer; returns the buffers created.
    /// </summary>
    public static int[] SetUpRiverAttributes(GlInterface gl, GlApi api, GpuRivers rivers, Func<int, float[], int> floats, Func<int, uint[], int> uints)
    {
        var vb = floats(GlApi.ArrayBuffer, rivers.Vertices);
        const int rs = GpuRivers.FloatsPerVertex * 4;
        gl.VertexAttribPointer(0, 3, GlApi.Float, 0, rs, 0);
        gl.VertexAttribPointer(1, 3, GlApi.Float, 0, rs, 12);
        gl.VertexAttribPointer(2, 2, GlApi.Float, 0, rs, 24);
        gl.VertexAttribPointer(3, 1, GlApi.Float, 0, rs, 32);
        gl.VertexAttribPointer(4, 1, GlApi.Float, 0, rs, 36);
        gl.VertexAttribPointer(6, 2, GlApi.Float, 0, rs, 40);
        foreach (var i in (ReadOnlySpan<int>)[0, 1, 2, 3, 4, 6]) gl.EnableVertexAttribArray(i);
        var cb = uints(GlApi.ArrayBuffer, rivers.Cells);
        api.VertexAttribIPointer(5, 2, GlApi.UnsignedInt, 8, 0);
        gl.EnableVertexAttribArray(5);
        var ib = uints(GlApi.ElementArrayBuffer, rivers.Indices);
        return [vb, cb, ib];
    }

    /// <summary>Uploads the scene (GL thread). The arrays in <paramref name="data"/> are not kept.</summary>
    public void Upload(GpuMapData data)
    {
        var scene = data.Scene;
        _renderer = data.Renderer;
        _cellCount = scene.CellCount;
        _cellRows = scene.CellTextureRows;
        _api.PixelStorei(GlApi.UnpackAlignment, 1);

        // Globe cell mesh: interleaved floats (location 0–2) and the cell id (location 3, integer).
        _globeVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_globeVao);
        Buffer(GlApi.ArrayBuffer, scene.Vertices);
        const int stride = GpuScene.FloatsPerVertex * 4;
        _gl.VertexAttribPointer(0, 3, GlApi.Float, 0, stride, 0);
        _gl.VertexAttribPointer(1, 3, GlApi.Float, 0, stride, 12);
        _gl.VertexAttribPointer(2, 2, GlApi.Float, 0, stride, 24);
        for (var i = 0; i < 3; i++) _gl.EnableVertexAttribArray(i);
        Buffer(GlApi.ArrayBuffer, scene.VertexCells);
        _api.VertexAttribIPointer(3, 1, GlApi.UnsignedInt, 4, 0);
        _gl.EnableVertexAttribArray(3);
        Buffer(GlApi.ElementArrayBuffer, scene.Indices);
        _globeIndexCount = scene.Indices.Length;

        // Major rivers (course levels).
        var rivers = data.Rivers;
        _riverVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_riverVao);
        if (rivers.PieceCount > 0) SetUpRiverAttributes(_gl, _api, rivers, (t, d) => Buffer(t, d), (t, d) => Buffer(t, d));
        _riverLevels = rivers.Levels;
        RiverPieces = rivers.PieceCount;

        // Mountain symbols: cell centre and corner (locations 0, 1), group (2, integer); 3 vertices per symbol.
        (_globeGlyphVao, _globeGlyphCount) = GlyphVao(scene.GlobeGlyphs);
        (_regionGlyphVao, _regionGlyphCount) = GlyphVao(scene.RegionGlyphs);
        _gl.BindVertexArray(0);

        // Texel atlas (integer texture arrays), palette, per-cell textures.
        _hasAtlas = scene.AtlasLayers > 0;
        _texClass = Texture(GlApi.Texture2DArray);
        _texValue = Texture(GlApi.Texture2DArray);
        if (_hasAtlas)
        {
            _gl.BindTexture(GlApi.Texture2DArray, _texClass);
            fixed (byte* p = scene.AtlasClass) _api.TexImage3D(GlApi.Texture2DArray, 0, GlApi.Rgba8Ui, GpuScene.AtlasSize, GpuScene.AtlasSize, scene.AtlasLayers, GlApi.RgbaInteger, GlApi.UnsignedByte, p);
            _gl.BindTexture(GlApi.Texture2DArray, _texValue);
            fixed (short* p = scene.AtlasValue) _api.TexImage3D(GlApi.Texture2DArray, 0, GlApi.Rgba16I, GpuScene.AtlasSize, GpuScene.AtlasSize, scene.AtlasLayers, GlApi.RgbaInteger, GlApi.Short, p);
        }
        else
        {
            // Samplers must be complete even when unused.
            uint zero = 0;
            _gl.BindTexture(GlApi.Texture2DArray, _texClass);
            _api.TexImage3D(GlApi.Texture2DArray, 0, GlApi.Rgba8Ui, 1, 1, 1, GlApi.RgbaInteger, GlApi.UnsignedByte, &zero);
            ulong zero8 = 0;
            _gl.BindTexture(GlApi.Texture2DArray, _texValue);
            _api.TexImage3D(GlApi.Texture2DArray, 0, GlApi.Rgba16I, 1, 1, 1, GlApi.RgbaInteger, GlApi.Short, &zero8);
        }

        _texPalette = Texture(GlApi.Texture2D);
        fixed (byte* p = data.Palette.Lut) _gl.TexImage2D(GlApi.Texture2D, 0, GlApi.Rgba8, GpuPalette.Width, GpuPalette.Rows, 0, GlApi.Rgba, GlApi.UnsignedByte, (nint)p);
        _elevMin = data.Palette.ElevationMin;
        _elevMax = data.Palette.ElevationMax;
        _texCellColour = Texture(GlApi.Texture2D);
        _gl.TexImage2D(GlApi.Texture2D, 0, GlApi.Rgba8, GpuScene.CellTextureWidth, _cellRows, 0, GlApi.Rgba, GlApi.UnsignedByte, 0);
        _texCellFlags = Texture(GlApi.Texture2D);
        _flags = new byte[GpuScene.CellTextureWidth * _cellRows];
        _flagsUploaded = new byte[_flags.Length];
        fixed (byte* p = _flags) _gl.TexImage2D(GlApi.Texture2D, 0, GlApi.R8Ui, GpuScene.CellTextureWidth, _cellRows, 0, GlApi.RedInteger, GlApi.UnsignedByte, (nint)p);
        _texCellGeom = Texture(GlApi.Texture2D);
        fixed (float* p = data.CellGeometry) _gl.TexImage2D(GlApi.Texture2D, 0, GlApi.Rgba32F, GpuScene.CellTextureWidth, data.CellGeometryRows, 0, GlApi.Rgba, GlApi.Float, (nint)p);
        _api.Check("uploading the GPU map scene");

        if (_renderer.Cache is not null && _renderer.World.Tiles is not null)
        {
            _tiles = new GlTileBlocks(_gl, _api, _renderer);
            _tiles.Built += () => TileBlockBuilt?.Invoke();
        }
    }

    private (int Vao, int Count) GlyphVao(GpuGlyphs glyphs)
    {
        var vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);
        if (glyphs.VertexCount > 0)
        {
            Buffer(GlApi.ArrayBuffer, glyphs.Vertices);
            const int gs = GpuGlyphs.FloatsPerVertex * 4;
            _gl.VertexAttribPointer(0, 3, GlApi.Float, 0, gs, 0);
            _gl.VertexAttribPointer(1, 3, GlApi.Float, 0, gs, 12);
            _gl.EnableVertexAttribArray(0);
            _gl.EnableVertexAttribArray(1);
            Buffer(GlApi.ArrayBuffer, glyphs.Groups);
            _api.VertexAttribIPointer(2, 1, GlApi.UnsignedInt, 4, 0);
            _gl.EnableVertexAttribArray(2);
        }

        return (vao, glyphs.VertexCount);
    }

    private void UpdateCellTextures(MapDrawOptions options)
    {
        if (_cellColourLayer != options.Layer && GpuPalette.ModeOf(options.Layer) == GpuLayerMode.CellColour)
        {
            var bytes = GpuPalette.CellColours(_renderer!, options.Layer, _cellRows);
            _gl.BindTexture(GlApi.Texture2D, _texCellColour);
            fixed (byte* p = bytes) _api.TexSubImage2D(GlApi.Texture2D, 0, 0, 0, GpuScene.CellTextureWidth, _cellRows, GlApi.Rgba, GlApi.UnsignedByte, p);
            _cellColourLayer = options.Layer;
        }

        // Flags: bit 0 highlighted, bit 1 drawn from its tile block this frame. Uploaded only when they change.
        Array.Clear(_flags);
        if (options.HighlightedGlobeCells is { Count: > 0 } h)
            foreach (var c in h)
                if (c >= 0 && c < _cellCount) _flags[c] |= 1;
        foreach (var (_, c) in _tiled) _flags[c] |= 2;
        if (!_flags.AsSpan().SequenceEqual(_flagsUploaded))
        {
            _gl.BindTexture(GlApi.Texture2D, _texCellFlags);
            fixed (byte* p = _flags) _api.TexSubImage2D(GlApi.Texture2D, 0, 0, 0, GpuScene.CellTextureWidth, _cellRows, GlApi.RedInteger, GlApi.UnsignedByte, p);
            _flags.CopyTo(_flagsUploaded, 0);
        }
    }

    /// <summary>
    /// The visible globe cells drawn from their tile block this frame (as the CPU renderer: every visible cell at Detail
    /// View, cells at least <see cref="MapRenderer.BlockTextureMinPx"/> wide at Region View); requests the blocks (and
    /// their neighbours, for the triangles across block borders) from the tile cache, as the CPU renderer does.
    /// </summary>
    private void CollectTileBlocks(MapViewState view, MapLevel level)
    {
        _tiled.Clear();
        if (_tiles is null || level == MapLevel.Globe) return;
        var r = _renderer!;
        var cache = r.Cache!;
        cache.BeginFrame();
        _tiles.BeginFrame();
        r.VisibleGlobeCells(view, _visible);
        foreach (var g in _visible)
        {
            if (level != MapLevel.Detail && r.ProjectedCellDiameter(view, g) < MapRenderer.BlockTextureMinPx) continue;
            Request(cache, g);
            foreach (var nb in r.World.Globe.Grid.Neighbours(g)) Request(cache, nb);
            if (_tiles.Get(g) is { } mesh) _tiled.Add((mesh, g));
        }

        _tiles.Trim();
    }

    private static void Request(TileBlockCache cache, int cell)
    {
        var state = cache.StateOf(cell);
        if (state is TileBlockState.NotLoaded or TileBlockState.Loading or TileBlockState.Failed) cache.Request(cell);
    }

    private void ViewUniforms(int program, in GpuFrame f)
    {
        _api.Uniform3f(U(program, "uRight"), f.Right.X, f.Right.Y, f.Right.Z);
        _api.Uniform3f(U(program, "uUp"), f.Up.X, f.Up.Y, f.Up.Z);
        _api.Uniform3f(U(program, "uCentre"), f.Centre.X, f.Centre.Y, f.Centre.Z);
        _gl.Uniform1f(U(program, "uScale"), f.ScalePxPerRad);
        _gl.Uniform1f(U(program, "uFlat"), f.Flatness);
        _gl.Uniform1f(U(program, "uMaxAngle"), f.MaxAngle);
        _api.Uniform2f(U(program, "uViewport"), f.Viewport.X, f.Viewport.Y);
    }

    private void PaletteUniforms(int program)
    {
        _gl.Uniform1i(U(program, "uElevMin"), _elevMin);
        _gl.Uniform1i(U(program, "uElevMax"), _elevMax);
    }

    private void BindTexture(int unit, int target, int texture, int program, string name)
    {
        _gl.ActiveTexture(GlApi.Texture0 + unit);
        _gl.BindTexture(target, texture);
        _gl.Uniform1i(U(program, name), unit);
    }

    /// <summary>
    /// Draws one frame into framebuffer <paramref name="fb"/> of <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/>.
    /// CPU work: the visible-cell pass at Region and Detail View (tile block requests), the uniforms, and one draw call per
    /// pass plus one or two per tile block.
    /// </summary>
    public void Render(int fb, int pixelWidth, int pixelHeight, in GpuFrame f, MapDrawOptions options, MapViewState view, MapLevel level)
    {
        _gl.BindFramebuffer(GlApi.Framebuffer, fb);
        _gl.Viewport(0, 0, pixelWidth, pixelHeight);
        _gl.Disable(GlApi.DepthTest);
        _gl.Disable(GlApi.CullFace);
        _gl.Disable(GlApi.ScissorTest);
        _gl.Disable(GlApi.Blend);
        CollectTileBlocks(view, level);
        LastTileBlocks = _tiled.Count;
        UpdateCellTextures(options);
        var r = _renderer!;

        // Background: outer colour beyond the visible cap, the gap colour inside it.
        _gl.UseProgram(_backgroundProgram);
        _api.Uniform2f(U(_backgroundProgram, "uViewport"), f.Viewport.X, f.Viewport.Y);
        _gl.Uniform1f(U(_backgroundProgram, "uPixelRatio"), f.PixelRatio);
        _gl.Uniform1f(U(_backgroundProgram, "uMaxRadius"), f.MaxRadius);
        _api.Uniform3f(U(_backgroundProgram, "uDiscColour"), f.DiscColour.X, f.DiscColour.Y, f.DiscColour.Z);
        _api.Uniform3f(U(_backgroundProgram, "uOuterColour"), f.OuterColour.X, f.OuterColour.Y, f.OuterColour.Z);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(GlApi.Triangles, 0, 3);

        // Globe cells from the texel atlas (cells drawn from their tile block are skipped by the vertex shader).
        var p = _globeProgram;
        _gl.UseProgram(p);
        ViewUniforms(p, f);
        PaletteUniforms(p);
        _gl.Uniform1f(U(p, "uEdgeInset"), f.EdgeInsetPx);
        _gl.Uniform1i(U(p, "uShaded"), f.Shaded ? 1 : 0);
        _api.Uniform3f(U(p, "uLight"), GpuFrame.Light.X, GpuFrame.Light.Y, GpuFrame.Light.Z);
        _gl.Uniform1i(U(p, "uMode"), (int)f.Mode);
        _gl.Uniform1i(U(p, "uHasAtlas"), _hasAtlas ? 1 : 0);
        BindTexture(0, GlApi.Texture2DArray, _texClass, p, "uClass");
        BindTexture(1, GlApi.Texture2DArray, _texValue, p, "uValue");
        BindTexture(2, GlApi.Texture2D, _texPalette, p, "uPalette");
        BindTexture(3, GlApi.Texture2D, _texCellColour, p, "uCellColour");
        BindTexture(4, GlApi.Texture2D, _texCellFlags, p, "uCellFlags");
        _gl.BindVertexArray(_globeVao);
        _gl.DrawElements(GlApi.Triangles, _globeIndexCount, GlApi.UnsignedInt, 0);

        // Tile blocks: one draw call each (group centre, shade, tint and cell colour per block).
        if (_tiled.Count > 0)
        {
            var t = _tileProgram;
            _gl.UseProgram(t);
            ViewUniforms(t, f);
            PaletteUniforms(t);
            _gl.Uniform1i(U(t, "uMode"), (int)f.Mode);
            _gl.Uniform1i(U(t, "uGroup"), options.Borders ? 1 : 0);
            BindTexture(0, GlApi.Texture2D, _texPalette, t, "uPalette");
            var geo = r.World.Globe.Geometry;
            var grid = r.World.Globe.Grid;
            var cellColours = f.Mode == GpuLayerMode.CellColour ? r.GlobeColoursFor(options.Layer) : null;
            var highlighted = options.HighlightedGlobeCells;
            foreach (var (mesh, g) in _tiled)
            {
                var centre = geo.Centre(g);
                var corner = geo.Corner(grid.CornerTriangles(g)[0]);
                _api.Uniform3f(U(t, "uGroupCentre"), (float)centre.X, (float)centre.Y, (float)centre.Z);
                _api.Uniform3f(U(t, "uGroupCorner"), (float)corner.X, (float)corner.Y, (float)corner.Z);
                _gl.Uniform1f(U(t, "uShade"), GpuProjection.Shade(f, new System.Numerics.Vector3((float)centre.X, (float)centre.Y, (float)centre.Z)));
                _gl.Uniform1i(U(t, "uTint"), highlighted?.Contains(g) == true ? 1 : 0);
                var cc = cellColours?[g] ?? 0;
                _api.Uniform3f(U(t, "uCellColour"), ((cc >> 16) & 0xFF) / 255f, ((cc >> 8) & 0xFF) / 255f, (cc & 0xFF) / 255f);
                _gl.BindVertexArray(mesh.Vao);
                _gl.DrawArrays(GlApi.Triangles, 0, mesh.VertexCount);
            }
        }

        // Mountain symbols (before rivers, as the CPU renderer's overlay flush): globe cells at Globe View; region cells
        // at Region View while tiles are under TilePolygonMinPx (the CPU renderer draws them with textured cells).
        var regionGlyphs = level == MapLevel.Region && r.World.TileAngle * view.ScalePxPerRad < MapRenderer.TilePolygonMinPx;
        var (glyphVao, glyphCount) = level == MapLevel.Globe ? (_globeGlyphVao, _globeGlyphCount) : regionGlyphs ? (_regionGlyphVao, _regionGlyphCount) : (0, 0);
        if (options.MountainSymbols && glyphCount > 0)
        {
            var g = _glyphProgram;
            _gl.UseProgram(g);
            ViewUniforms(g, f);
            _gl.Uniform1f(U(g, "uEdgeInset"), level == MapLevel.Globe ? f.EdgeInsetPx : 0f);
            _gl.Uniform1i(U(g, "uGroup"), level != MapLevel.Globe && options.Borders ? 1 : 0);
            _gl.Uniform1f(U(g, "uMinPx"), MapRenderer.MountainSymbolMinPx);
            var mc = LayerPalette.MountainSymbolColor;
            _api.Uniform3f(U(g, "uColour"), ((mc >> 16) & 0xFF) / 255f, ((mc >> 8) & 0xFF) / 255f, (mc & 0xFF) / 255f);
            BindTexture(0, GlApi.Texture2D, _texCellGeom, g, "uCellGeom");
            _gl.BindVertexArray(glyphVao);
            _gl.DrawArrays(GlApi.Triangles, 0, glyphCount);
        }

        if (options.Rivers) DrawRivers(f, options, view, level);
        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
        _gl.ActiveTexture(GlApi.Texture0);
    }

    /// <summary>
    /// Rivers as the CPU renderer: Globe and Region View draw the major rivers from the course levels (Region View first
    /// the mid-size block rivers under them); Detail View draws every block's rivers on the tile width scale.
    /// </summary>
    private void DrawRivers(in GpuFrame f, MapDrawOptions options, MapViewState view, MapLevel level)
    {
        var r = _renderer!;
        var w = r.World;
        var rp = _riverProgram;
        _gl.UseProgram(rp);
        ViewUniforms(rp, f);
        _gl.Uniform1f(U(rp, "uPixelRatio"), f.PixelRatio);
        _gl.Uniform1i(U(rp, "uGroup"), level != MapLevel.Globe && options.Borders ? 1 : 0);
        BindTexture(0, GlApi.Texture2D, _texPalette, rp, "uPalette");
        BindTexture(1, GlApi.Texture2D, _texCellGeom, rp, "uCellGeom");
        _gl.Enable(GlApi.Blend);
        _api.BlendFunc(GlApi.One, GlApi.OneMinusSrcAlpha);

        // Block rivers: all at Detail View; at Region View the mid-size ones (below the major-river flow).
        if (level != MapLevel.Globe && _tiled.Count > 0)
        {
            var tilePx = w.TileAngle * view.ScalePxPerRad;
            double minFlow = level == MapLevel.Detail ? 0 : r.RegionRiverMinFlow(tilePx);
            double maxFlow = level == MapLevel.Detail ? double.MaxValue : w.MajorRiverFlow;
            if (minFlow < maxFlow)
            {
                RiverWidths(rp, (float)tilePx, 0.08f, 0.25f, 14f, 0);
                _gl.Uniform1f(U(rp, "uMinLogFlow"), minFlow <= 0 ? -1e30f : (float)Math.Log10(minFlow));
                _gl.Uniform1f(U(rp, "uMaxLogFlow"), maxFlow == double.MaxValue ? 1e30f : (float)Math.Log10(maxFlow));
                foreach (var (mesh, _) in _tiled)
                {
                    if (mesh.RiverIndexCount == 0) continue;
                    _gl.BindVertexArray(mesh.RiverVao);
                    _gl.DrawElements(GlApi.Triangles, mesh.RiverIndexCount, GlApi.UnsignedInt, 0);
                }
            }
        }

        // Major rivers (one course level) at Globe and Region View.
        if (level != MapLevel.Detail && _riverLevels.Count > 0 && RiverPieces > 0)
        {
            var li = Math.Clamp(r.RiverCourseLevelFor(view), 0, _riverLevels.Count - 1);
            var (first, count) = _riverLevels[li];
            if (count > 0)
            {
                RiverWidths(rp, (float)(w.RegionCellAngle * view.ScalePxPerRad), GpuRivers.MinFrac, GpuRivers.AddFrac, GpuRivers.MaxPx, (float)r.RiverMinLengthPx(li));
                _gl.Uniform1f(U(rp, "uMinLogFlow"), -1e30f);
                _gl.Uniform1f(U(rp, "uMaxLogFlow"), 1e30f);
                _gl.BindVertexArray(_riverVao);
                _gl.DrawElements(GlApi.Triangles, count, GlApi.UnsignedInt, first * sizeof(uint));
            }
        }

        _gl.Disable(GlApi.Blend);
    }

    private void RiverWidths(int program, float unitPx, float minFrac, float addFrac, float maxPx, float minLengthPx)
    {
        _gl.Uniform1f(U(program, "uUnitPx"), unitPx);
        _gl.Uniform1f(U(program, "uMinFrac"), minFrac);
        _gl.Uniform1f(U(program, "uAddFrac"), addFrac);
        _gl.Uniform1f(U(program, "uMaxPx"), maxPx);
        _gl.Uniform1f(U(program, "uMinLengthPx"), minLengthPx);
    }

    /// <summary>Reads framebuffer <paramref name="fb"/> back as top-down RGBA rows (diagnostic screenshots only).</summary>
    public byte[] ReadPixels(int fb, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        _gl.BindFramebuffer(GlApi.Framebuffer, fb);
        _api.PixelStorei(GlApi.PackAlignment, 1);
        fixed (byte* p = rgba) _api.ReadPixels(0, 0, width, height, GlApi.Rgba, GlApi.UnsignedByte, p);
        var flipped = new byte[rgba.Length];
        var row = width * 4;
        for (var y = 0; y < height; y++) System.Buffer.BlockCopy(rgba, (height - 1 - y) * row, flipped, y * row, row);
        return flipped;
    }

    public void Dispose()
    {
        _tiles?.Dispose();
        foreach (var t in _textures) _gl.DeleteTexture(t);
        foreach (var b in _buffers) _gl.DeleteBuffer(b);
        foreach (var vao in (ReadOnlySpan<int>)[_globeVao, _riverVao, _globeGlyphVao, _regionGlyphVao])
            if (vao != 0) _gl.DeleteVertexArray(vao);
        _gl.DeleteVertexArray(_emptyVao);
        foreach (var program in (ReadOnlySpan<int>)[_globeProgram, _backgroundProgram, _riverProgram, _glyphProgram, _tileProgram])
            _gl.DeleteProgram(program);
    }
}
