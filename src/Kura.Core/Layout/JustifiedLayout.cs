namespace Kura.Core.Layout;

/// <summary>One packed row: items [Start, Start+Count) rendered at <see cref="Height"/> with the given widths.</summary>
public sealed record JustifiedRow(int Start, int Count, double Height, IReadOnlyList<double> Widths, bool IsComplete);

/// <summary>
/// Justified ("Flickr-style") row packing from known aspect ratios (SPEC 4.3 G2). Deterministic from any
/// row start, so appending a page only re-packs from the last incomplete row.
/// </summary>
public static class JustifiedLayout
{
    public static List<JustifiedRow> Pack(IReadOnlyList<double> aspects, double width, double targetHeight, double spacing,
        int startIndex = 0, double maxHeightFactor = 1.6)
    {
        var rows = new List<JustifiedRow>();
        if (width <= 0 || targetHeight <= 0) return rows;
        var maxHeight = targetHeight * maxHeightFactor;
        var i = startIndex;
        while (i < aspects.Count)
        {
            double sum = 0;
            var n = 0;
            var placed = false;
            while (i + n < aspects.Count)
            {
                sum += Safe(aspects[i + n]);
                n++;
                var rowWidthAtTarget = sum * targetHeight + spacing * (n - 1);
                if (rowWidthAtTarget < width) continue;

                // Row is full. Choose between including or dropping the last item, whichever lands nearer the target height.
                var hWith = (width - spacing * (n - 1)) / sum;
                if (n > 1)
                {
                    var sumWithout = sum - Safe(aspects[i + n - 1]);
                    var hWithout = (width - spacing * (n - 2)) / sumWithout;
                    if (Math.Abs(hWithout - targetHeight) < Math.Abs(hWith - targetHeight) && hWithout <= maxHeight)
                    {
                        n--;
                        sum = sumWithout;
                        hWith = hWithout;
                    }
                }
                rows.Add(Make(aspects, i, n, Math.Min(hWith, maxHeight), width, spacing, complete: true));
                i += n;
                placed = true;
                break;
            }
            if (!placed)
            {
                // Trailing partial row: keep the target height, left-aligned.
                rows.Add(Make(aspects, i, n, targetHeight, null, spacing, complete: false));
                i += n;
            }
        }
        return rows;
    }

    private static JustifiedRow Make(IReadOnlyList<double> aspects, int start, int count, double height, double? fillWidth,
        double spacing, bool complete)
    {
        var widths = new double[count];
        double total = 0;
        for (var k = 0; k < count; k++)
        {
            widths[k] = Math.Max(1, Math.Floor(Safe(aspects[start + k]) * height));
            total += widths[k];
        }
        if (fillWidth is { } fw && count > 0)
        {
            // Give rounding slack to the last cell so the row's right edge is flush.
            var slack = fw - spacing * (count - 1) - total;
            widths[^1] = Math.Max(1, widths[^1] + slack);
        }
        return new JustifiedRow(start, count, Math.Round(height), widths, complete);
    }

    private static double Safe(double a) => double.IsFinite(a) && a > 0 ? Math.Clamp(a, 0.2, 5) : 1;
}
