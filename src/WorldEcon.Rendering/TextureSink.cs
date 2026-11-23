using System.Numerics;
using SkiaSharp;

namespace WorldEcon.Rendering;

/// <summary>
/// Textured triangles for the continuous map imagery, reused between frames. Each triangle carries texture coordinates in
/// texels (affine across the screen triangle), a square texture stored as <c>size × size</c> ARGB values from an offset in a
/// shared array (row-major, row = j), a shade factor and a highlight flag. <see cref="Fill"/> scan-converts them into an
/// opaque BGRA bitmap with the same pixel-centre rule as the flat-colour filler, sampling the nearest texel (coordinates
/// outside the texture clamp to its edge).
/// </summary>
internal sealed class TextureSink
{
    private Triangle[] _tris = new Triangle[256];
    private readonly List<uint[]> _textures = [];
    private readonly Dictionary<uint[], int> _textureIndex = new(ReferenceEqualityComparer.Instance);

    private struct Triangle
    {
        public SKPoint A, B, C;
        public Vector2 Ta, Tb, Tc;
        public int Texture, Offset, Size;
        public float Shade;
        public bool Tint;
    }

    public int Count { get; private set; }

    public void Reset()
    {
        Count = 0;
        _textures.Clear();
        _textureIndex.Clear();
    }

    public void Tri(SKPoint a, SKPoint b, SKPoint c, Vector2 ta, Vector2 tb, Vector2 tc, uint[] texels, int offset, int size, float shade, bool tint)
    {
        if (!_textureIndex.TryGetValue(texels, out var tex))
        {
            tex = _textures.Count;
            _textures.Add(texels);
            _textureIndex[texels] = tex;
        }

        if (Count == _tris.Length) Array.Resize(ref _tris, _tris.Length * 2);
        _tris[Count++] = new Triangle { A = a, B = b, C = c, Ta = ta, Tb = tb, Tc = tc, Texture = tex, Offset = offset, Size = size, Shade = shade, Tint = tint };
    }

    public unsafe void Fill(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var basePtr = (byte*)bitmap.GetPixels();
        var rowBytes = bitmap.RowBytes;
        for (var i = 0; i < Count; i++) Fill(basePtr, rowBytes, width, height, ref _tris[i]);
    }

    private unsafe void Fill(byte* basePtr, int rowBytes, int width, int height, ref Triangle t)
    {
        SKPoint a = t.A, b = t.B, c = t.C;
        // Affine texture mapping: u(x, y) = ua + dudx (x − xa) + dudy (y − ya), from the unsorted vertices.
        var det = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        if (MathF.Abs(det) < 1e-9f) return;
        var dudx = (((t.Tb.X - t.Ta.X) * (c.Y - a.Y)) - ((t.Tc.X - t.Ta.X) * (b.Y - a.Y))) / det;
        var dudy = (((t.Tc.X - t.Ta.X) * (b.X - a.X)) - ((t.Tb.X - t.Ta.X) * (c.X - a.X))) / det;
        var dvdx = (((t.Tb.Y - t.Ta.Y) * (c.Y - a.Y)) - ((t.Tc.Y - t.Ta.Y) * (b.Y - a.Y))) / det;
        var dvdy = (((t.Tc.Y - t.Ta.Y) * (b.X - a.X)) - ((t.Tb.Y - t.Ta.Y) * (c.X - a.X))) / det;
        float ax = a.X, ay = a.Y, ua = t.Ta.X, va = t.Ta.Y;

        // Sort by y: a.Y <= b.Y <= c.Y.
        if (b.Y < a.Y) (a, b) = (b, a);
        if (c.Y < b.Y) (b, c) = (c, b);
        if (b.Y < a.Y) (a, b) = (b, a);
        var y0 = Math.Max(0, (int)MathF.Ceiling(a.Y - 0.5f));
        var y1 = Math.Min(height, (int)MathF.Ceiling(c.Y - 0.5f));
        if (y0 >= y1 || c.Y - a.Y <= 0) return;
        var maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        var minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        if (maxX < 0 || minX > width) return;

        var tex = _textures[t.Texture];
        int size = t.Size, offset = t.Offset, last = size - 1;
        var shade = t.Shade;
        var modulate = shade < 0.999f || t.Tint;
        for (var y = y0; y < y1; y++)
        {
            var yc = y + 0.5f;
            var xLong = EdgeX(a, c, yc);
            var xShort = yc < b.Y ? EdgeX(a, b, yc) : EdgeX(b, c, yc);
            var xl = MathF.Min(xLong, xShort);
            var xr = MathF.Max(xLong, xShort);
            var x0 = Math.Max(0, (int)MathF.Ceiling(xl - 0.5f));
            var x1 = Math.Min(width, (int)MathF.Ceiling(xr - 0.5f));
            if (x1 <= x0) continue;
            var row = (uint*)(basePtr + ((long)y * rowBytes));
            var u = ua + (dudx * (x0 + 0.5f - ax)) + (dudy * (yc - ay));
            var v = va + (dvdx * (x0 + 0.5f - ax)) + (dvdy * (yc - ay));
            for (var x = x0; x < x1; x++, u += dudx, v += dvdx)
            {
                var i = Math.Clamp((int)MathF.Floor(u), 0, last);
                var j = Math.Clamp((int)MathF.Floor(v), 0, last);
                var col = tex[offset + (j * size) + i];
                row[x] = modulate ? Modulate(col, shade, t.Tint) : col | 0xFF000000u;
            }
        }
    }

    private static uint Modulate(uint argb, float shade, bool tint)
    {
        var col = new SKColor(argb | 0xFF000000u);
        if (shade < 0.999f) col = new SKColor((byte)(col.Red * shade), (byte)(col.Green * shade), (byte)(col.Blue * shade));
        if (tint) col = MapRenderer.HighlightTint(col);
        return (uint)col;
    }

    /// <summary>x of the edge p–q (p.Y &lt; q.Y) at height y, as in the flat-colour filler.</summary>
    private static float EdgeX(SKPoint p, SKPoint q, float y)
    {
        var dy = q.Y - p.Y;
        if (dy <= 0) return p.X;
        return p.X + ((q.X - p.X) * ((y - p.Y) / dy));
    }
}
