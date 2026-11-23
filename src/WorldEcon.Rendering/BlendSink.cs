using SkiaSharp;

namespace WorldEcon.Rendering;

/// <summary>
/// Triangles between three tile centres, drawn without hex edges (design 2026-11-22-continuous-imagery, sub-tile smoothing).
/// Each vertex carries its tile's colour and a class key (the land/water or land-type kind). Per pixel, the barycentric
/// weights decide: the key holding the most weight wins (ties: the first vertex, then the second), so a boundary between
/// kinds is a straight line inside each triangle instead of a hex staircase. For a categorical layer the pixel takes the
/// winning key's colour; for a continuous layer (elevation, temperature, precipitation) it blends the colours of the
/// vertices of the winning key by their weights. Shade and highlight modulate as in <see cref="TextureSink"/>.
/// </summary>
internal sealed class BlendSink
{
    private Triangle[] _tris = new Triangle[256];

    private struct Triangle
    {
        public SKPoint A, B, C;
        public uint Ca, Cb, Cc, Ka, Kb, Kc;
        public bool Continuous, Tint;
        public float Shade;
    }

    public int Count { get; private set; }

    public void Reset() => Count = 0;

    public void Tri(SKPoint a, SKPoint b, SKPoint c, uint ca, uint ka, uint cb, uint kb, uint cc, uint kc, bool continuous, float shade, bool tint)
    {
        if (Count == _tris.Length) Array.Resize(ref _tris, _tris.Length * 2);
        _tris[Count++] = new Triangle { A = a, B = b, C = c, Ca = ca, Cb = cb, Cc = cc, Ka = ka, Kb = kb, Kc = kc, Continuous = continuous, Shade = shade, Tint = tint };
    }

    public unsafe void Fill(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var basePtr = (byte*)bitmap.GetPixels();
        var rowBytes = bitmap.RowBytes;
        for (var i = 0; i < Count; i++) Fill(basePtr, rowBytes, width, height, ref _tris[i]);
    }

    /// <summary>The colour at barycentric weights (wa, wb, wc) by the rule in the class summary.</summary>
    internal static uint Pick(uint ca, uint ka, float wa, uint cb, uint kb, float wb, uint cc, uint kc, float wc, bool continuous)
    {
        var sa = wa + (kb == ka ? wb : 0) + (kc == ka ? wc : 0);
        var sb = wb + (ka == kb ? wa : 0) + (kc == kb ? wc : 0);
        var sc = wc + (ka == kc ? wa : 0) + (kb == kc ? wb : 0);
        var win = sa >= sb && sa >= sc ? ka : sb >= sc ? kb : kc;
        if (!continuous) return win == ka ? ca : win == kb ? cb : cc;

        float r = 0, g = 0, b = 0, w = 0;
        void Add(uint col, uint key, float weight)
        {
            if (key != win || weight <= 0) return;
            r += ((col >> 16) & 0xFF) * weight;
            g += ((col >> 8) & 0xFF) * weight;
            b += (col & 0xFF) * weight;
            w += weight;
        }

        Add(ca, ka, wa);
        Add(cb, kb, wb);
        Add(cc, kc, wc);
        if (w <= 0) return win == ka ? ca : win == kb ? cb : cc;
        return 0xFF000000u | ((uint)Math.Clamp((int)MathF.Round(r / w), 0, 255) << 16) | ((uint)Math.Clamp((int)MathF.Round(g / w), 0, 255) << 8)
            | (uint)Math.Clamp((int)MathF.Round(b / w), 0, 255);
    }

    private static unsafe void Fill(byte* basePtr, int rowBytes, int width, int height, ref Triangle t)
    {
        SKPoint a = t.A, b = t.B, c = t.C;
        var det = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        if (MathF.Abs(det) < 1e-9f) return;
        // Barycentric weights of B and C are affine in (x, y): wb = ((x − ax)(cy − ay) − (cx − ax)(y − ay)) / det, etc.
        var dbdx = (c.Y - a.Y) / det;
        var dbdy = -(c.X - a.X) / det;
        var dcdx = -(b.Y - a.Y) / det;
        var dcdy = (b.X - a.X) / det;
        float ax = a.X, ay = a.Y;

        SKPoint p = a, q = b, r = c;
        if (q.Y < p.Y) (p, q) = (q, p);
        if (r.Y < q.Y) (q, r) = (r, q);
        if (q.Y < p.Y) (p, q) = (q, p);
        var y0 = Math.Max(0, (int)MathF.Ceiling(p.Y - 0.5f));
        var y1 = Math.Min(height, (int)MathF.Ceiling(r.Y - 0.5f));
        if (y0 >= y1 || r.Y - p.Y <= 0) return;
        var maxX = MathF.Max(p.X, MathF.Max(q.X, r.X));
        var minX = MathF.Min(p.X, MathF.Min(q.X, r.X));
        if (maxX < 0 || minX > width) return;

        var modulate = t.Shade < 0.999f || t.Tint;
        // Same colours and keys at all three vertices: one flat colour, no per-pixel weights needed.
        var flat = t.Ka == t.Kb && t.Kb == t.Kc && (!t.Continuous || (t.Ca == t.Cb && t.Cb == t.Cc));
        var flatColour = flat ? Finish(t.Ca, ref t, modulate) : 0u;
        for (var y = y0; y < y1; y++)
        {
            var yc = y + 0.5f;
            var xLong = EdgeX(p, r, yc);
            var xShort = yc < q.Y ? EdgeX(p, q, yc) : EdgeX(q, r, yc);
            var xl = MathF.Min(xLong, xShort);
            var xr = MathF.Max(xLong, xShort);
            var x0 = Math.Max(0, (int)MathF.Ceiling(xl - 0.5f));
            var x1 = Math.Min(width, (int)MathF.Ceiling(xr - 0.5f));
            if (x1 <= x0) continue;
            var row = (uint*)(basePtr + ((long)y * rowBytes));
            if (flat)
            {
                new Span<uint>(row + x0, x1 - x0).Fill(flatColour);
                continue;
            }

            var wb = (dbdx * (x0 + 0.5f - ax)) + (dbdy * (yc - ay));
            var wc = (dcdx * (x0 + 0.5f - ax)) + (dcdy * (yc - ay));
            for (var x = x0; x < x1; x++, wb += dbdx, wc += dcdx)
            {
                var col = Pick(t.Ca, t.Ka, 1 - wb - wc, t.Cb, t.Kb, wb, t.Cc, t.Kc, wc, t.Continuous);
                row[x] = Finish(col, ref t, modulate);
            }
        }
    }

    private static uint Finish(uint col, ref Triangle t, bool modulate)
    {
        if (!modulate) return col | 0xFF000000u;
        var c = new SKColor(col | 0xFF000000u);
        if (t.Shade < 0.999f) c = new SKColor((byte)(c.Red * t.Shade), (byte)(c.Green * t.Shade), (byte)(c.Blue * t.Shade));
        if (t.Tint) c = MapRenderer.HighlightTint(c);
        return (uint)c;
    }

    private static float EdgeX(SKPoint p, SKPoint q, float y)
    {
        var dy = q.Y - p.Y;
        if (dy <= 0) return p.X;
        return p.X + ((q.X - p.X) * ((y - p.Y) / dy));
    }
}
