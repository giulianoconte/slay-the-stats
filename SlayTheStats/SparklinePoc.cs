using Godot;

namespace SlayTheStats;

// ─────────────────────────────────────────────────────────────────────
// Sparkline rendering proof-of-concept.
//
// Texture-based sparkline: a 1px polyline connecting the series values,
// colored point-by-point on a green→yellow→red gradient (high = red) so
// the peaks draw the eye. A translucent IQR band and a median line sit
// behind the polyline to give a quick "where does this fight fall in
// the distribution" read without numerical annotation.
//
// Colorblind notes: the green↔red pair isn't ideal for red-green
// deficiencies, but stepping through yellow at the midpoint gives the
// ramp a strong luminance gradient (mid-luma yellow, darker at both
// ends) that survives most monochrome viewers. Swap hues here if/when
// we pick a more colorblind-safe palette.
//
// Unicode block-character rendering is kept alongside purely for
// comparison inside the same debug panel; the texture path is the
// chosen direction going forward.
//
// Remove this file + the BuildDebugPanel call from BestiaryPatch once
// the texture styling is locked in and migrated onto real surfaces.
// ─────────────────────────────────────────────────────────────────────
internal static class SparklinePoc
{
    // Fallback synthetic series used only when the user's DB has no
    // Regent-vs-Byrdonis fights yet. Real data is preferred — the shape
    // of an actual distribution is what we're trying to eyeball.
    private static readonly double[] FallbackSampleValues = { 12, 18, 5, 22, 8, 15, 20, 3, 18, 14 };

    private const string EncounterIdByrdonisElite = "ENCOUNTER.BYRDONIS_ELITE";
    private const string CharacterIdRegent        = "CHARACTER.REGENT";

    /// <summary>
    /// Pulls damage-taken values for Byrdonis-elite fights played as
    /// Regent from the live stats DB. Returns null when the DB lacks
    /// data for that matchup, so the caller can fall through to a
    /// synthetic series.
    /// </summary>
    internal static double[]? GetByrdonisVsRegentDamageValues()
    {
        try
        {
            var db = MainFile.Db;
            if (db == null) return null;
            if (!db.Encounters.TryGetValue(EncounterIdByrdonisElite, out var contextMap))
                return null;

            var filter = new AggregationFilter { Character = CharacterIdRegent };
            var perChar = StatsAggregator.AggregateEncountersByCharacter(contextMap, filter);
            if (!perChar.TryGetValue(CharacterIdRegent, out var stat)) return null;
            if (stat.DamageValues == null || stat.DamageValues.Count == 0) return null;

            var arr = new double[stat.DamageValues.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = stat.DamageValues[i];
            return arr;
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] SparklinePoc.GetByrdonisVsRegentDamageValues: {e.Message}");
            return null;
        }
    }

    // Gradient: neutral warm gray below the midpoint, fading into red
    // as the value rises above the midpoint. Low + Mid stops share the
    // gray value so the bottom half of the curve reads as a quiet
    // neutral line, with red reserved for the peak region. Swap the
    // Gray here if the panel chrome ever changes.
    private static readonly Color GradLow  = new Color(0.70f, 0.68f, 0.62f, 1f); // warm gray
    private static readonly Color GradMid  = new Color(0.70f, 0.68f, 0.62f, 1f); // warm gray (same — flat low half)
    private static readonly Color GradHigh = new Color(0.86f, 0.28f, 0.20f, 1f); // red

    // Percentile marker styling. Warm neutral fill with a dark
    // outline — same palette across all three marker variants so
    // only the glyph shape changes between them.
    private static readonly Color PercentileDotColor        = new Color(0.95f, 0.92f, 0.82f, 0.95f);
    private static readonly Color PercentileDotOutlineColor = new Color(0.08f, 0.07f, 0.05f, 0.80f);
    private const float PercentileDotRadius                 = 2.0f;
    private const float PercentileMedianExtraRadius         = 0.6f;

    // Shaded IQR + median rule variant.
    private static readonly Color IqrFillColor        = new Color(0.95f, 0.92f, 0.82f, 0.12f);
    private static readonly Color MedianRuleColor     = new Color(0.95f, 0.92f, 0.82f, 0.75f);
    private const float MedianRuleWidth               = 1.2f;

    // Stem variant.
    private static readonly Color StemColor           = new Color(0.95f, 0.92f, 0.82f, 0.55f);
    private const float StemWidth                     = 1.0f;
    private const float MedianStemWidth               = 1.6f;

    // Caret variant — small filled triangle at the baseline pointing up.
    private static readonly Color CaretColor          = new Color(0.95f, 0.92f, 0.82f, 0.90f);
    private const int CaretHalfWidthPx                = 3;
    private const int CaretHeightPx                   = 4;
    private const int MedianCaretHalfWidthPx          = 4;
    private const int MedianCaretHeightPx             = 5;

    /// <summary>Which percentile marker treatment to draw. POC-only enum
    /// so we can stamp out three variants side-by-side in the debug
    /// panel and eyeball the least-cluttered option.</summary>
    internal enum MarkerStyle
    {
        Dots,                   // original — filled dots on the curve
        ShadedIqrMedianRule,    // translucent IQR fill + full-height median rule
        BaselineCarets,         // small ▲ triangles below the baseline at p25/p50/p75
        Stems,                  // vertical stems from baseline up to the curve at p25/p50/p75
    }

    /// <summary>Placeholder token written into BBCode cells where a
    /// sparkline texture should be inserted. Characters are from the
    /// Unicode Private Use Area so they won't collide with real text.
    /// Consumers call <see cref="PopulateLabelWithSparklines"/> to
    /// split on the marker and interleave <c>AddImage</c> calls between
    /// the <c>AppendText</c> segments.</summary>
    internal const string SparklineMarker = "\uE000\uE001";

    /// <summary>
    /// Populates <paramref name="label"/> by splitting <paramref name="bbcodeWithMarkers"/>
    /// on <see cref="SparklineMarker"/>, calling <c>AppendText</c> on each
    /// segment and <c>AddImage(<paramref name="sparklines"/>[i], w, h)</c>
    /// between consecutive segments. Nulls in the sparkline list mean
    /// "no sparkline for this slot" — the surrounding cell renders
    /// empty. Sparkline count must match marker count.
    /// </summary>
    internal static void PopulateLabelWithSparklines(
        RichTextLabel label,
        string bbcodeWithMarkers,
        IReadOnlyList<ImageTexture?> sparklines,
        int sparkW,
        int sparkH)
    {
        label.Clear();
        var segments = bbcodeWithMarkers.Split(SparklineMarker, System.StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length > 0) label.AppendText(segments[i]);
            // Between segments: spark slot i.
            if (i < segments.Length - 1)
            {
                var tex = i < sparklines.Count ? sparklines[i] : null;
                if (tex != null)
                    label.AddImage(tex, sparkW, sparkH);
            }
        }
    }

    // Eight discrete bar heights from Unicode's block elements range
    // (U+2581..U+2588). Index 0 is the shortest bar, index 7 is full.
    private static readonly char[] BlockChars = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

    /// <summary>
    /// Renders <paramref name="values"/> as a sequence of Unicode block
    /// characters. Kept for comparison in the debug panel.
    /// </summary>
    internal static string BuildUnicodeSparkline(IReadOnlyList<double> values)
    {
        if (values == null || values.Count == 0) return "";

        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in values) { if (v < min) min = v; if (v > max) max = v; }
        double range = max - min;

        var sb = new System.Text.StringBuilder(values.Count);
        foreach (var v in values)
        {
            int idx = range <= 0
                ? BlockChars.Length / 2
                : (int)System.Math.Clamp(
                    System.Math.Round((v - min) / range * (BlockChars.Length - 1)),
                    0, BlockChars.Length - 1);
            sb.Append(BlockChars[idx]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Renders <paramref name="values"/> as a Gaussian-KDE density
    /// curve TextureRect. X axis = value (low→high); Y axis = relative
    /// frequency at that value. Gradient runs along X — low values
    /// green, high values red — so the worst-outcome tail naturally
    /// reddens. Percentile dots (p25, p50, p75) land on the curve at
    /// their respective value x-positions, always left-to-right. Line
    /// AA via per-pixel distance-to-segment coverage.
    /// </summary>
    /// <summary>
    /// Image/Texture-only entry point. Use this when you want to
    /// <c>RichTextLabel.AddImage(tex, w, h)</c> the sparkline into a
    /// BBCode cell — no Control wrapping required.
    /// </summary>
    internal static ImageTexture BuildSparklineTexture(
        IReadOnlyList<double> values,
        Vector2I size,
        MarkerStyle markerStyle = MarkerStyle.ShadedIqrMedianRule)
    {
        return ImageTexture.CreateFromImage(BuildSparklineImage(values, size, markerStyle));
    }

    internal static TextureRect BuildTextureSparkline(
        IReadOnlyList<double> values,
        Vector2I size,
        MarkerStyle markerStyle = MarkerStyle.ShadedIqrMedianRule)
    {
        int w = System.Math.Max(size.X, 1);
        int h = System.Math.Max(size.Y, 1);
        var texture = ImageTexture.CreateFromImage(BuildSparklineImage(values, size, markerStyle));
        return new TextureRect
        {
            Texture = texture,
            StretchMode = TextureRect.StretchModeEnum.Keep,
            CustomMinimumSize = new Vector2(w, h),
        };
    }

    private static Image BuildSparklineImage(
        IReadOnlyList<double> values,
        Vector2I size,
        MarkerStyle markerStyle)
    {
        int w = System.Math.Max(size.X, 1);
        int h = System.Math.Max(size.Y, 1);

        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        image.Fill(new Color(0f, 0f, 0f, 0f));

        if (values != null && values.Count > 0)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var v in values) { if (v < min) min = v; if (v > max) max = v; }
            double range = max - min;

            int n = values.Count;
            var sorted = new double[n];
            for (int i = 0; i < n; i++) sorted[i] = values[i];
            System.Array.Sort(sorted);

            double q1 = Percentile(sorted, 0.25);
            double q2 = Percentile(sorted, 0.50);
            double q3 = Percentile(sorted, 0.75);

            const float LineRadius = 1.4f;

            if (range <= 0)
            {
                // Degenerate: all values identical. Single dot at the
                // panel center — KDE bandwidth would be zero otherwise.
                double cx = (w - 1) * 0.5;
                double cy = (h - 1) * 0.5;
                DrawAaDisc(image, cx, cy, LineRadius + 1f, PercentileDotOutlineColor);
                DrawAaDisc(image, cx, cy, LineRadius,      ColorAt(values[0], min, range));
            }
            else
            {
                // Silverman's rule-of-thumb bandwidth, using the more
                // outlier-robust min(σ, IQR/1.34) spread estimator.
                // Floored at 3% of the data range so degenerate-IQR
                // cases still smooth instead of spiking.
                double mean = 0;
                for (int i = 0; i < n; i++) mean += values[i];
                mean /= n;
                double sumSq = 0;
                for (int i = 0; i < n; i++) { double d = values[i] - mean; sumSq += d * d; }
                double sigma = System.Math.Sqrt(sumSq / n);
                double iqr = q3 - q1;
                double spread = iqr > 0 ? System.Math.Min(sigma, iqr / 1.34) : sigma;
                if (spread <= 0) spread = range;
                double bandwidth = 0.9 * spread * System.Math.Pow(n, -0.2);
                if (bandwidth < range * 0.03) bandwidth = range * 0.03;

                // Pad the x range by one bandwidth on each side so the
                // curve's Gaussian tails don't get clipped — panel
                // shows the full shape rather than cutting at min/max.
                double xLo    = min - bandwidth;
                double xHi    = max + bandwidth;
                double xRange = xHi - xLo;

                double DataXAtPx(double px) => xLo + (px / (w - 1.0)) * xRange;
                double PxAtDataX(double vx) => (vx - xLo) / xRange * (w - 1);

                double Density(double xv)
                {
                    double s = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double u = (xv - values[i]) / bandwidth;
                        s += System.Math.Exp(-0.5 * u * u);
                    }
                    return s; // normalization cancels below (renormalized by max)
                }

                var densities = new double[w];
                double maxDensity = 0;
                for (int px = 0; px < w; px++)
                {
                    densities[px] = Density(DataXAtPx(px));
                    if (densities[px] > maxDensity) maxDensity = densities[px];
                }

                // Fit the tallest peak to ~85% of panel height so the
                // curve breathes against the top edge and the dots on
                // the peak have clearance.
                double yScale = maxDensity > 0 ? 0.85 * (h - 1) / maxDensity : 0;
                double BaselineY = h - 1;
                double YOfDensity(double d) => BaselineY - d * yScale;

                // Pre-curve markers for the shaded-IQR variant: IQR
                // fill + median rule, both clipped to the curve Y so
                // neither draws above the density line.
                if (markerStyle == MarkerStyle.ShadedIqrMedianRule)
                {
                    int iqrLeft  = System.Math.Clamp((int)System.Math.Round(PxAtDataX(q1)), 0, w - 1);
                    int iqrRight = System.Math.Clamp((int)System.Math.Round(PxAtDataX(q3)), 0, w - 1);
                    for (int px = iqrLeft; px <= iqrRight; px++)
                    {
                        int yTop = System.Math.Clamp((int)System.Math.Round(YOfDensity(densities[px])), 0, h - 1);
                        int yBot = h - 1;
                        for (int py = yTop; py <= yBot; py++)
                            BlendOver(image, px, py, IqrFillColor);
                    }

                    // Median rule stops exactly at the curve height at
                    // median x — mirrors the IQR fill's top-silhouette
                    // behavior. Drawing pre-curve means the curve's
                    // anti-aliased line paints over any rule pixels
                    // that happened to land on the same rows as the
                    // line.
                    double mxPre    = PxAtDataX(q2);
                    double myTopPre = YOfDensity(Density(q2));
                    DrawAaVerticalLine(image, mxPre, myTopPre, h - 1, MedianRuleWidth, MedianRuleColor);
                }

                // Polyline through per-pixel density samples. Value
                // carried to ColorAt is the DENSITY height itself
                // (normalized by maxDensity), so the gradient runs
                // along the y axis — low density (valleys/tails) are
                // green, high density (the peak) is red.
                for (int px = 0; px < w - 1; px++)
                {
                    double y1 = YOfDensity(densities[px]);
                    double y2 = YOfDensity(densities[px + 1]);
                    DrawAaSegment(image, px, y1, px + 1, y2,
                        densities[px], densities[px + 1],
                        0, maxDensity, LineRadius);
                }

                // Post-curve percentile marker rendering. One branch
                // per style — all three get the same q1/q2/q3 inputs
                // so the shape differences are the only variable.
                double PxOfValue(double vx) => PxAtDataX(vx);
                double PyOnCurve(double vx) => YOfDensity(Density(vx));

                switch (markerStyle)
                {
                    case MarkerStyle.Dots:
                    {
                        void DotAtValue(double vx, bool isMedian)
                        {
                            double px = PxOfValue(vx);
                            double py = PyOnCurve(vx);
                            float rFill    = PercentileDotRadius + (isMedian ? PercentileMedianExtraRadius : 0f);
                            float rOutline = rFill + 1.0f;
                            DrawAaDisc(image, px, py, rOutline, PercentileDotOutlineColor);
                            DrawAaDisc(image, px, py, rFill,    PercentileDotColor);
                        }
                        DotAtValue(q1, isMedian: false);
                        DotAtValue(q3, isMedian: false);
                        DotAtValue(q2, isMedian: true);
                        break;
                    }

                    case MarkerStyle.ShadedIqrMedianRule:
                        // Both the IQR fill and the median rule draw
                        // pre-curve so the line paints on top and
                        // naturally caps the rule at its top.
                        break;

                    case MarkerStyle.Stems:
                    {
                        void StemAt(double vx, bool isMedian)
                        {
                            double px = PxOfValue(vx);
                            double yTop = PyOnCurve(vx);
                            float width = isMedian ? MedianStemWidth : StemWidth;
                            DrawAaVerticalLine(image, px, yTop, h - 1, width, StemColor);
                        }
                        StemAt(q1, isMedian: false);
                        StemAt(q3, isMedian: false);
                        StemAt(q2, isMedian: true);
                        break;
                    }

                    case MarkerStyle.BaselineCarets:
                    {
                        void CaretAt(double vx, bool isMedian)
                        {
                            double px = PxOfValue(vx);
                            int halfW = isMedian ? MedianCaretHalfWidthPx : CaretHalfWidthPx;
                            int heightPx = isMedian ? MedianCaretHeightPx : CaretHeightPx;
                            DrawBaselineCaret(image, px, halfW, heightPx, CaretColor);
                        }
                        CaretAt(q1, isMedian: false);
                        CaretAt(q3, isMedian: false);
                        CaretAt(q2, isMedian: true);
                        break;
                    }
                }
            }
        }

        return image;
    }

    /// <summary>Ascending-sorted copy of <paramref name="values"/> as int[] — used by the debug panel header for human-readable value lists.</summary>
    private static int[] Sorted(IReadOnlyList<double> values)
    {
        var arr = new int[values.Count];
        for (int i = 0; i < values.Count; i++) arr[i] = (int)System.Math.Round(values[i]);
        System.Array.Sort(arr);
        return arr;
    }

    /// <summary>Type-7 (linear-interpolated) percentile on a pre-sorted array.</summary>
    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        double idx  = (sorted.Length - 1) * p;
        int    lo   = (int)System.Math.Floor(idx);
        int    hi   = (int)System.Math.Ceiling(idx);
        double frac = idx - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }

    /// <summary>Maps a value's position in [min..min+range] to the green→yellow→red gradient.</summary>
    private static Color ColorAt(double v, double min, double range)
    {
        double norm = range <= 0 ? 0.5 : System.Math.Clamp((v - min) / range, 0, 1);
        return norm < 0.5
            ? LerpColor(GradLow,  GradMid,  norm * 2.0)
            : LerpColor(GradMid,  GradHigh, (norm - 0.5) * 2.0);
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        float ft = (float)System.Math.Clamp(t, 0, 1);
        return new Color(
            a.R + (b.R - a.R) * ft,
            a.G + (b.G - a.G) * ft,
            a.B + (b.B - a.B) * ft,
            a.A + (b.A - a.A) * ft);
    }

    /// <summary>
    /// Anti-aliased capsule-shaped segment. For each pixel in the
    /// segment's bbox, computes distance to the capped line segment
    /// and uses (radius − distance) as the coverage alpha. Uniform
    /// visual width across slopes (unlike a vertical-brush) and
    /// sub-pixel smooth diagonals without supersampling. Color per
    /// pixel interpolates between the two endpoint values along the
    /// segment's parameter.
    /// </summary>
    private static void DrawAaSegment(
        Image img,
        double x1, double y1, double x2, double y2,
        double v1, double v2,
        double min, double range,
        float radius)
    {
        int w = img.GetWidth();
        int h = img.GetHeight();

        double minX = System.Math.Min(x1, x2) - radius;
        double minY = System.Math.Min(y1, y2) - radius;
        double maxX = System.Math.Max(x1, x2) + radius;
        double maxY = System.Math.Max(y1, y2) + radius;
        int bx1 = System.Math.Max(0,     (int)System.Math.Floor(minX));
        int by1 = System.Math.Max(0,     (int)System.Math.Floor(minY));
        int bx2 = System.Math.Min(w - 1, (int)System.Math.Ceiling(maxX));
        int by2 = System.Math.Min(h - 1, (int)System.Math.Ceiling(maxY));

        double dx = x2 - x1;
        double dy = y2 - y1;
        double lenSq = dx * dx + dy * dy;

        for (int py = by1; py <= by2; py++)
        {
            for (int px = bx1; px <= bx2; px++)
            {
                // Pixel center → param t along the segment, clamped to
                // the endpoints so cap geometry is circular.
                double pcx = px + 0.5, pcy = py + 0.5;
                double t = lenSq > 0
                    ? System.Math.Clamp(((pcx - x1) * dx + (pcy - y1) * dy) / lenSq, 0, 1)
                    : 0;
                double cx = x1 + t * dx;
                double cy = y1 + t * dy;
                double ddx = pcx - cx, ddy = pcy - cy;
                double d = System.Math.Sqrt(ddx * ddx + ddy * ddy);

                double coverage = radius - d;
                if (coverage <= 0) continue;
                if (coverage > 1) coverage = 1;

                double v = v1 + (v2 - v1) * t;
                var src = ColorAt(v, min, range);
                src.A *= (float)coverage;

                BlendOver(img, px, py, src);
            }
        }
    }

    /// <summary>
    /// Anti-aliased filled disc centered at (<paramref name="cx"/>,
    /// <paramref name="cy"/>). Alpha falls off linearly across the last
    /// pixel-width at the edge, same coverage rule as
    /// <see cref="DrawAaSegment"/>.
    /// </summary>
    private static void DrawAaDisc(Image img, double cx, double cy, float radius, Color color)
    {
        int w = img.GetWidth();
        int h = img.GetHeight();
        int bx1 = System.Math.Max(0,     (int)System.Math.Floor(cx - radius));
        int by1 = System.Math.Max(0,     (int)System.Math.Floor(cy - radius));
        int bx2 = System.Math.Min(w - 1, (int)System.Math.Ceiling(cx + radius));
        int by2 = System.Math.Min(h - 1, (int)System.Math.Ceiling(cy + radius));

        for (int py = by1; py <= by2; py++)
        {
            for (int px = bx1; px <= bx2; px++)
            {
                double ddx = (px + 0.5) - cx;
                double ddy = (py + 0.5) - cy;
                double d = System.Math.Sqrt(ddx * ddx + ddy * ddy);

                double coverage = radius - d;
                if (coverage <= 0) continue;
                if (coverage > 1) coverage = 1;

                var src = color;
                src.A *= (float)coverage;
                BlendOver(img, px, py, src);
            }
        }
    }

    /// <summary>
    /// Anti-aliased vertical line segment of a solid color. Uses the
    /// same distance-based coverage as <see cref="DrawAaSegment"/> but
    /// with a fixed color (no gradient lookup).
    /// </summary>
    private static void DrawAaVerticalLine(
        Image img, double xCenter, double y1, double y2, float radius, Color color)
    {
        int w = img.GetWidth();
        int h = img.GetHeight();
        double minY = System.Math.Min(y1, y2) - radius;
        double maxY = System.Math.Max(y1, y2) + radius;
        int bx1 = System.Math.Max(0,     (int)System.Math.Floor(xCenter - radius));
        int bx2 = System.Math.Min(w - 1, (int)System.Math.Ceiling(xCenter + radius));
        int by1 = System.Math.Max(0,     (int)System.Math.Floor(minY));
        int by2 = System.Math.Min(h - 1, (int)System.Math.Ceiling(maxY));

        for (int py = by1; py <= by2; py++)
        {
            double pcy = py + 0.5;
            // Clamp to the y range so the rule has hemispherical caps.
            double ty  = System.Math.Clamp(pcy, System.Math.Min(y1, y2), System.Math.Max(y1, y2));
            for (int px = bx1; px <= bx2; px++)
            {
                double pcx = px + 0.5;
                double ddx = pcx - xCenter;
                double ddy = pcy - ty;
                double d = System.Math.Sqrt(ddx * ddx + ddy * ddy);
                double coverage = radius - d;
                if (coverage <= 0) continue;
                if (coverage > 1) coverage = 1;
                var src = color;
                src.A *= (float)coverage;
                BlendOver(img, px, py, src);
            }
        }
    }

    /// <summary>
    /// Small filled triangle pointing up, base centered at
    /// <paramref name="xCenter"/> on the bottom row of the image.
    /// Scanline fill — no AA on the sloped edges, which at 4–5px tall
    /// reads as clean-enough pixel-art glyphs.
    /// </summary>
    private static void DrawBaselineCaret(
        Image img, double xCenter, int halfWidthPx, int heightPx, Color color)
    {
        int w = img.GetWidth();
        int h = img.GetHeight();
        int cxi = (int)System.Math.Round(xCenter);
        int baseY = h - 1;
        int apexY = baseY - heightPx + 1;

        for (int y = apexY; y <= baseY; y++)
        {
            if (y < 0 || y >= h) continue;
            double t = heightPx > 1 ? (double)(y - apexY) / (heightPx - 1) : 1.0;
            int halfW = (int)System.Math.Round(t * halfWidthPx);
            for (int dx = -halfW; dx <= halfW; dx++)
            {
                int px = cxi + dx;
                if (px < 0 || px >= w) continue;
                BlendOver(img, px, y, color);
            }
        }
    }

    /// <summary>
    /// Standard "source over destination" Porter-Duff compositing.
    /// Read the existing pixel, blend the incoming source on top,
    /// write the result. Keeps straight-alpha semantics; the Image
    /// stores RGBA8 so no pre-multiplication concerns.
    /// </summary>
    private static void BlendOver(Image img, int x, int y, Color src)
    {
        var dst = img.GetPixel(x, y);
        float outA = src.A + dst.A * (1f - src.A);
        if (outA <= 0f)
        {
            img.SetPixel(x, y, new Color(0, 0, 0, 0));
            return;
        }
        float outR = (src.R * src.A + dst.R * dst.A * (1f - src.A)) / outA;
        float outG = (src.G * src.A + dst.G * dst.A * (1f - src.A)) / outA;
        float outB = (src.B * src.A + dst.B * dst.A * (1f - src.A)) / outA;
        img.SetPixel(x, y, new Color(outR, outG, outB, outA));
    }

    /// <summary>
    /// Builds a titled debug panel comparing the two sparkline renderers
    /// on the same series. Prefers live Regent-vs-Byrdonis damage values
    /// from the DB; falls back to a synthetic series when that matchup
    /// has no data yet.
    /// </summary>
    internal static Control BuildDebugPanel()
    {
        var real   = GetByrdonisVsRegentDamageValues();
        var values = real ?? FallbackSampleValues;
        var source = real != null
            ? $"Regent vs Byrdonis (n={values.Length})"
            : $"fallback synthetic (n={values.Length})";

        var root = new VBoxContainer
        {
            Name = "SparklinePocDebug",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", 2);

        var header = new Label
        {
            Text = $"Sparkline POC  [{source}]  {System.String.Join(",", Sorted(values))}",
        };
        header.AddThemeColorOverride("font_color", new Color(0.56f, 0.53f, 0.46f, 1f));
        header.AddThemeFontSizeOverride("font_size", 12);
        header.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        header.CustomMinimumSize = new Vector2(300, 0);
        root.AddChild(header);

        // Unicode block-character row (kept for comparison).
        var unicodeRow = new HBoxContainer();
        unicodeRow.AddThemeConstantOverride("separation", 8);
        var unicodeLabel = new Label { Text = "Unicode:" };
        unicodeLabel.AddThemeColorOverride("font_color", new Color(0.80f, 0.78f, 0.72f, 1f));
        unicodeLabel.AddThemeFontSizeOverride("font_size", 14);
        unicodeLabel.CustomMinimumSize = new Vector2(70, 0);
        unicodeRow.AddChild(unicodeLabel);

        var unicodeSpark = new Label { Text = BuildUnicodeSparkline(values) };
        unicodeSpark.AddThemeFontSizeOverride("font_size", 20);
        unicodeSpark.AddThemeColorOverride("font_color", new Color(0.58f, 0.80f, 0.62f, 1f));
        unicodeRow.AddChild(unicodeSpark);
        root.AddChild(unicodeRow);

        // One row per MarkerStyle variant so we can eyeball them
        // stacked. All three share the same values + panel size so
        // only the marker treatment varies.
        var styleVariants = new (string name, MarkerStyle style)[]
        {
            ("IQR+Rule:", MarkerStyle.ShadedIqrMedianRule),
            ("Carets:",   MarkerStyle.BaselineCarets),
            ("Stems:",    MarkerStyle.Stems),
        };

        foreach (var (name, style) in styleVariants)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var lbl = new Label { Text = name };
            lbl.AddThemeColorOverride("font_color", new Color(0.80f, 0.78f, 0.72f, 1f));
            lbl.AddThemeFontSizeOverride("font_size", 14);
            lbl.CustomMinimumSize = new Vector2(70, 0);
            row.AddChild(lbl);
            row.AddChild(BuildTextureSparkline(values, new Vector2I(60, 28), style));
            root.AddChild(row);
        }

        return root;
    }
}
