using SkiaSharp;

namespace WorldEcon.Rendering;

/// <summary>
/// Greedy label declutter for map markers (globe settlements, region settlements). Labels are placed in input order;
/// each tries four slots around its anchor (right, left, above, below, each <c>gapPx</c> from the anchor) and takes the
/// first whose rectangle does not intersect a label placed earlier. A label with no free slot is skipped (null); the
/// caller still draws its marker dot.
/// </summary>
public static class LabelLayout
{
    /// <summary>Label rectangles in screen pixels, one per input (null = skipped).</summary>
    public static SKRect?[] Place(IReadOnlyList<(SKPoint Anchor, SKSize Size)> labels, float gapPx)
    {
        var result = new SKRect?[labels.Count];
        var placed = new List<SKRect>(labels.Count);
        for (var i = 0; i < labels.Count; i++)
        {
            var (a, size) = labels[i];
            float w = size.Width, h = size.Height;
            ReadOnlySpan<SKRect> slots =
            [
                SKRect.Create(a.X + gapPx, a.Y - (h / 2), w, h),
                SKRect.Create(a.X - gapPx - w, a.Y - (h / 2), w, h),
                SKRect.Create(a.X - (w / 2), a.Y - gapPx - h, w, h),
                SKRect.Create(a.X - (w / 2), a.Y + gapPx, w, h),
            ];
            foreach (var slot in slots)
            {
                if (placed.Any(p => p.IntersectsWith(slot))) continue;
                placed.Add(slot);
                result[i] = slot;
                break;
            }
        }

        return result;
    }

    /// <summary>Draws a label with a one-pixel dark halo inside <paramref name="rect"/> (as placed by <see cref="Place"/>).</summary>
    internal static void Draw(SKCanvas canvas, string text, SKRect rect, SKFont font, SKPaint fill, SKPaint halo)
    {
        // Baseline: the rectangle is font-size tall; the text's ascent sits at its top.
        var baseline = rect.Top + (rect.Height * 0.8f);
        canvas.DrawText(text, rect.Left + 1, baseline + 1, font, halo);
        canvas.DrawText(text, rect.Left, baseline, font, fill);
    }
}
