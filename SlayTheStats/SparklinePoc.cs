using Godot;

namespace SlayTheStats;

// ─────────────────────────────────────────────────────────────────────
// Sparkline rendering.
//
// Texture-based sparkline: a 1px polyline connecting the series values,
// drawn flat in CurveColor (NeutralShade #b5b5b5) so the curve sits at
// the same weight as the surrounding context-row text. A translucent cream
// IQR fill and
// a steel-blue median rule sit behind the polyline to give a quick
// "where does this fight fall in the distribution" read without
// numerical annotation. When all sparklines in a table share an explicit
// XRange, pixel x maps to the same data x in every row, so distributions
// are directly comparable across rows.
// ─────────────────────────────────────────────────────────────────────
internal static class SparklinePoc
{
    // Curve color. Matches NeutralShade (#b5b5b5) used as the
    // significance-coloring neutral text color — the shade rendered when a
    // value's deviation is below threshold or sample size is too small. In
    // colored rows (per-character rows in all-chars and focused views) this
    // makes the curve sit at the same weight as the row's neutral elements
    // instead of competing with the colored numeric stats.
    private static readonly Color CurveColor = new Color(0.71f, 0.71f, 0.71f, 1f);

    // Dark outline drawn under the single dot used for a degenerate
    // (all-values-identical) distribution.
    private static readonly Color PercentileDotOutlineColor = new Color(0.08f, 0.07f, 0.05f, 0.80f);

    // Shaded IQR + median rule. Median rule uses a cool steel-blue so it stays
    // distinct from the warm-grey curve and the warm-cream IQR band — important
    // on bumpy curves where the rule's tip would otherwise blend into a peak's
    // color.
    private static readonly Color IqrFillColor        = new Color(0.95f, 0.92f, 0.82f, 0.12f);
    private static readonly Color MedianRuleColor     = new Color(0.30f, 0.60f, 0.96f, 1.0f);
    // Pixel gap between the median rule's top and the curve at median x.
    // Without it, the rule appears to merge into the curve in bimodal
    // distributions where the median sits in the valley between two peaks
    // (the rule terminates exactly on the curve, reading as one shape).
    private const float MedianRuleGapPx               = 2.0f;
    private const float MedianRuleWidth               = 1.5f;

    // "You are here" caret — marks an external value (e.g. the current fight)
    // on the curve's x-axis. Gold and a touch larger than the percentile carets
    // so it reads as a distinct annotation rather than another quartile marker.
    private static readonly Color CurrentValueCaretColor = new Color(0.96f, 0.81f, 0.32f, 0.95f);
    private const int CurrentValueCaretHalfWidthPx       = 4;
    private const int CurrentValueCaretHeightPx          = 6;
    private const int CurrentValueCaretGapPx             = 3; // gap between curve baseline and caret apex (2×-oversampled px)

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
                {
                    MaybeLogSparklineSize(tex, sparkW, sparkH);
                    label.AddImage(tex, sparkW, sparkH);
                }
            }
        }
    }

    // Tracks the last-logged (tex_w, tex_h, disp_w, disp_h) tuple so we
    // emit one line per distinct size-pair rather than per row. Debug-
    // only: flags when AddImage's display size drifts from the texture's
    // native size (which is what causes Godot to scale + blur/alias the
    // sparkline and what sets the column's min-content width floor).
#if DEV_BUILD
    private static (int, int, int, int)? _lastLoggedSparklineSize;
#endif

    // Debug-only size telemetry; compiled out of release binaries (#41). The call
    // site stays as a no-op so the hot path reads the same in both configs.
    private static void MaybeLogSparklineSize(ImageTexture tex, int displayW, int displayH)
    {
#if DEV_BUILD
        if (!SlayTheStatsConfig.DebugMode) return;
        int texW = tex.GetWidth();
        int texH = tex.GetHeight();
        var key = (texW, texH, displayW, displayH);
        if (_lastLoggedSparklineSize == key) return;
        _lastLoggedSparklineSize = key;
        var tag = (texW == displayW && texH == displayH) ? "ok" : "MISMATCH (will scale)";
        MainFile.DebugLog($"Sparkline size: texture={texW}x{texH} display={displayW}x{displayH} [{tag}]");
#endif
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
    /// <summary>Optional explicit x-domain. When supplied, the curve is plotted
    /// against this range instead of the data's natural <c>min/max ± bandwidth</c>.
    /// All sparklines in the same table should share a single range so pixel x
    /// maps to the same data x across rows — letting the eye compare e.g. dmg%
    /// positions at a glance. Pass <c>null</c> for legacy per-row auto-fit.</summary>
    internal readonly record struct XRange(double Lo, double Hi);

    internal static ImageTexture BuildSparklineTexture(
        IReadOnlyList<double> values,
        Vector2I size,
        XRange? explicitRange = null,
        double? markerValue = null)
    {
        return ImageTexture.CreateFromImage(BuildSparklineImage(values, size, explicitRange, markerValue));
    }

    // Percentile cutoffs for the shared-range computation. Asymmetric: pLow=0
    // because dmg% has a natural floor (0 — no damage taken) so the actual
    // min is never far from zero, no point clipping it. pHigh=0.95 keeps
    // single-fight outliers at the high end (e.g. one disastrous near-death
    // at 200% dmg) from stretching the global frame and squashing every
    // other row's curve. Values outside the cutoffs still get plotted — the
    // curve at those positions just sits beyond the rendered texture's edges
    // and is clipped. Tighten pHigh (e.g. 0.90) if outliers still dominate;
    // widen (1.0) to disable upper clipping.
    private const double SharedRangePLow  = 0.0;
    private const double SharedRangePHigh = 0.95;

    /// <summary>Computes a global x-range across multiple value series for use
    /// as <see cref="XRange"/>. Pools all values, sorts, and picks the
    /// <c>p_lo</c>/<c>p_hi</c> percentile cutoffs (default 1%/99%) so a single
    /// extreme outlier doesn't blow out the frame for every other row. Returns
    /// <c>null</c> when every series is empty. Caller is responsible for any
    /// bandwidth padding needed to keep curve tails from clipping at edges.</summary>
    internal static XRange? ComputeSharedXRange(
        IEnumerable<IReadOnlyList<double>?> series,
        double pLow  = SharedRangePLow,
        double pHigh = SharedRangePHigh)
    {
        var pooled = new List<double>();
        foreach (var s in series)
        {
            if (s == null) continue;
            for (int i = 0; i < s.Count; i++) pooled.Add(s[i]);
        }
        if (pooled.Count == 0) return null;
        var arr = pooled.ToArray();
        System.Array.Sort(arr);
        double lo = Percentile(arr, pLow);
        double hi = Percentile(arr, pHigh);
        // Degenerate (all values equal, or pLow == pHigh): fall back to raw
        // min/max so the renderer still has a valid non-empty domain.
        if (hi <= lo) { lo = arr[0]; hi = arr[arr.Length - 1]; }
        return new XRange(lo, hi);
    }

    private static Image BuildSparklineImage(
        IReadOnlyList<double> values,
        Vector2I size,
        XRange? explicitRange = null,
        double? markerValue = null)
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

            const float LineRadius = 1.9f;

            if (range <= 0)
            {
                // Degenerate: all values identical. Place a single dot.
                // With a shared range, position it at the value's x in
                // the shared frame (so its column color matches its dmg%);
                // without one, center the panel since there's no
                // meaningful x reference.
                double cx;
                if (explicitRange.HasValue && explicitRange.Value.Hi > explicitRange.Value.Lo)
                {
                    var er = explicitRange.Value;
                    cx = (values[0] - er.Lo) / (er.Hi - er.Lo) * (w - 1);
                    cx = System.Math.Clamp(cx, 0, w - 1);
                }
                else
                {
                    cx = (w - 1) * 0.5;
                }
                double cy = (h - 1) * 0.5;
                DrawAaDisc(image, cx, cy, LineRadius + 1f, PercentileDotOutlineColor);
                DrawAaDisc(image, cx, cy, LineRadius,      CurveColor);
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

                // Per-row: pad the x range by one bandwidth on each side so
                // the curve's Gaussian tails don't get clipped. Shared-range:
                // honor the caller's explicit domain verbatim — they're
                // responsible for any padding so all rows share one frame.
                double xLo, xHi;
                if (explicitRange.HasValue)
                {
                    xLo = explicitRange.Value.Lo;
                    xHi = explicitRange.Value.Hi;
                    if (xHi <= xLo) { xLo = min - bandwidth; xHi = max + bandwidth; }
                }
                else
                {
                    xLo = min - bandwidth;
                    xHi = max + bandwidth;
                }
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
                // Reserve a strip at the bottom for the current-value caret so it
                // sits clear below the curve and IQR shade instead of overlapping
                // them. Zero when there's no caret, so other callers (the bestiary)
                // render exactly as before.
                int caretZone = markerValue.HasValue
                    ? CurrentValueCaretHeightPx + CurrentValueCaretGapPx : 0;
                double BaselineY = h - 1 - caretZone;
                double yScale = maxDensity > 0 ? 0.85 * BaselineY / maxDensity : 0;
                double YOfDensity(double d) => BaselineY - d * yScale;

                // Pre-curve markers: IQR fill + median rule, both clipped to the
                // curve Y so neither draws above the density line. Drawn before
                // the polyline so the curve paints on top and caps the rule.
                int iqrLeft  = System.Math.Clamp((int)System.Math.Round(PxAtDataX(q1)), 0, w - 1);
                int iqrRight = System.Math.Clamp((int)System.Math.Round(PxAtDataX(q3)), 0, w - 1);
                for (int px = iqrLeft; px <= iqrRight; px++)
                {
                    int yTop = System.Math.Clamp((int)System.Math.Round(YOfDensity(densities[px])), 0, (int)BaselineY);
                    int yBot = (int)BaselineY;
                    for (int py = yTop; py <= yBot; py++)
                        BlendOver(image, px, py, IqrFillColor);
                }

                // Median rule stops MedianRuleGapPx below the curve so a
                // visible gap separates the two shapes — important when
                // the median lands in a valley between bimodal peaks,
                // where the rule would otherwise read as part of the
                // curve. Skip drawing entirely when the gap would push
                // the rule's top past the baseline (curve already near
                // zero density at median).
                double mxPre    = PxAtDataX(q2);
                double myTopPre = YOfDensity(Density(q2)) + MedianRuleGapPx;
                if (myTopPre < BaselineY)
                    DrawAaVerticalLine(image, mxPre, myTopPre, BaselineY, MedianRuleWidth, MedianRuleColor);

                // Polyline through per-pixel density samples. Drawn flat in
                // CurveColor; cross-row comparability comes from the shared
                // XRange (same pixel x = same dmg% across all rows of the
                // table) rather than from per-pixel coloration.
                for (int px = 0; px < w - 1; px++)
                {
                    double y1 = YOfDensity(densities[px]);
                    double y2 = YOfDensity(densities[px + 1]);
                    DrawAaSegment(image, px, y1, px + 1, y2, LineRadius);
                }

                // "You are here" caret: mark an external value (the current
                // fight) on the curve's x-axis. Drawn last so it sits over the
                // IQR/median markers. The data value is clamped into the rendered
                // range (an out-of-range fight pins at the edge), and the *pixel*
                // center is then inset by the caret's half-width so the whole
                // triangle always stays on-image. Without the pixel inset, a
                // new-extreme value lands on pixel 0 / w-1 and DrawBaselineCaret
                // clips half the triangle — which is why a tight distribution
                // (turns) showed a smaller-looking caret than a wide one (damage)
                // in the same tooltip. The inset keeps the caret a consistent
                // size across metrics. #19
                if (markerValue.HasValue)
                {
                    double cv = System.Math.Clamp(markerValue.Value, xLo, xHi);
                    double caretPx = System.Math.Clamp(PxAtDataX(cv),
                        CurrentValueCaretHalfWidthPx, w - 1 - CurrentValueCaretHalfWidthPx);
                    DrawBaselineCaret(image, caretPx,
                        CurrentValueCaretHalfWidthPx, CurrentValueCaretHeightPx, CurrentValueCaretColor);
                }
            }
        }

#if DEV_BUILD
        if (SlayTheStatsConfig.DebugMode)
            DrawDebugBorder(image);
#endif

        return image;
    }

    // Cyan, distinct from the red cell border painted by BestiaryPatch's
    // ApplyDebugTableOverlay. Together the two outlines let you see:
    //   • red rect  = Godot table cell (column width × row height)
    //   • cyan rect = sparkline texture bounds, scaled to display size
    // Any gap between them is cell padding + display-size margin. If the
    // cyan line renders non-uniformly thick, Godot is scaling the texture
    // (i.e. AddImage(w,h) doesn't match BuildSparklineImage's Vector2I size).
#if DEV_BUILD
    private static readonly Color DebugBorderColor = new Color(0.20f, 0.90f, 0.95f, 0.90f);

    private static void DrawDebugBorder(Image img)
    {
        int w = img.GetWidth();
        int h = img.GetHeight();
        for (int px = 0; px < w; px++)
        {
            img.SetPixel(px, 0,     DebugBorderColor);
            img.SetPixel(px, h - 1, DebugBorderColor);
        }
        for (int py = 0; py < h; py++)
        {
            img.SetPixel(0,     py, DebugBorderColor);
            img.SetPixel(w - 1, py, DebugBorderColor);
        }
    }
#endif


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

    /// <summary>
    /// Anti-aliased capsule-shaped segment. For each pixel in the
    /// segment's bbox, computes distance to the capped line segment
    /// and uses (radius − distance) as the coverage alpha. Uniform
    /// visual width across slopes (unlike a vertical-brush) and
    /// sub-pixel smooth diagonals without supersampling.
    /// </summary>
    private static void DrawAaSegment(
        Image img,
        double x1, double y1, double x2, double y2,
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

                var src = CurveColor;
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
    /// same distance-based coverage as <see cref="DrawAaSegment"/>.
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

}
