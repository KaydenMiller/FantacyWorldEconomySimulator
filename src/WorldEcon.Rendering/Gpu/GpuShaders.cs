using System.Reflection;

namespace WorldEcon.Rendering.Gpu;

/// <summary>GLSL dialect a backend compiles: the shader bodies are shared, only the header differs.</summary>
public enum GlslDialect
{
    /// <summary>OpenGL ES 3.0 and WebGL2: <c>#version 300 es</c> with high precision defaults.</summary>
    Es300,

    /// <summary>Desktop OpenGL 3.3 core: <c>#version 330 core</c>.</summary>
    Core330,
}

/// <summary>
/// The shared shader sources (embedded <c>Gpu/Shaders/*.glsl</c>). Bodies use only features common to GLSL ES 3.00 and
/// GLSL 3.30 (layout locations on vertex inputs, flat integer varyings, integer textures, <c>texelFetch</c>), so a
/// WebGL2 front end loads the same files and prepends the ES header.
/// </summary>
public static class GpuShaders
{
    public const string GlobeVertex = "globe.vert.glsl";
    public const string GlobeFragment = "globe.frag.glsl";
    public const string BackgroundVertex = "background.vert.glsl";
    public const string BackgroundFragment = "background.frag.glsl";
    public const string RiverVertex = "river.vert.glsl";
    public const string RiverFragment = "river.frag.glsl";
    public const string GlyphVertex = "glyph.vert.glsl";
    public const string GlyphFragment = "glyph.frag.glsl";
    public const string TileVertex = "tile.vert.glsl";
    public const string TileFragment = "tile.frag.glsl";

    /// <summary>Every shader file name (for exporting to a web front end).</summary>
    public static readonly IReadOnlyList<string> All = [GlobeVertex, GlobeFragment, BackgroundVertex, BackgroundFragment, RiverVertex, RiverFragment, GlyphVertex, GlyphFragment, TileVertex, TileFragment];

    /// <summary>The header a backend prepends for <paramref name="dialect"/>.</summary>
    public static string Header(GlslDialect dialect) => dialect switch
    {
        GlslDialect.Es300 => "#version 300 es\nprecision highp float;\nprecision highp int;\n",
        _ => "#version 330 core\n",
    };

    /// <summary>Shared GLSL included by other shaders (<c>#include "palette.glsl"</c>), not a stage of its own.</summary>
    public const string PaletteInclude = "palette.glsl";

    /// <summary>
    /// The shared body of <paramref name="name"/> (one of the constants) with every <c>#include "file"</c> line replaced by
    /// that file's body (GLSL has no includes; a web front end loads the resolved text the same way).
    /// </summary>
    public static string Body(string name)
    {
        var lines = Raw(name).Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("#include \"", StringComparison.Ordinal) && t.EndsWith('"'))
                sb.Append(Body(t[10..^1])).Append('\n');
            else
                sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static string Raw(string name)
    {
        var asm = typeof(GpuShaders).Assembly;
        var resource = asm.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("." + name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"No embedded shader '{name}'.", nameof(name));
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Header + body, ready to compile.</summary>
    public static string Source(string name, GlslDialect dialect) => Header(dialect) + Body(name);
}
