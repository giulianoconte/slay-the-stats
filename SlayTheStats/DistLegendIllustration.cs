using Godot;

namespace SlayTheStats;

/// <summary>
/// Annotated reference illustration for the bestiary legend popup's
/// "Distribution" section. Renders the SparklinePoc curve at 200×88 native
/// (4× the bestiary table cell's pixel count, no display-time scaling) and
/// overlays a top bracket spanning the Mid-50% IQR fill plus a bottom arrow
/// pointing up at the median rule. The single "Mid 50%" bracket replaces
/// what used to be two separate "start"/"end" arrows so the IQR reads as a
/// contiguous range.
///
/// Uses a fixed reference dataset (LegendDistValues) so every player sees the
/// same shape regardless of their own data — picked because the shape is
/// instructive (right-skewed with a clear median rule and a wide Mid 50%).
/// </summary>
public partial class DistLegendIllustration : Control
{
    // 26 raw damage values from "All vs Queen" at A10 — all-character rollup,
    // single boss encounter. Hardcoded so the illustration is reproducible
    // across players (and across the same player's data drift over time).
    private static readonly double[] LegendDistValues =
    {
        22, 58, 30, 18, 33, 43, 32, 65, 101, 8, 12, 8, 24, 17, 70, 26,
        59, 28, 59, 46, 57, 36, 73, 30, 22, 22,
    };

    // Curve render size — drawn 1:1 at 200×88 (no display-time scaling).
    // 4× the bestiary table's 50×22 native pixel count, which gives the AA
    // brush in SparklinePoc room to spread coverage smoothly.
    private const int CurveW = 200;
    private const int CurveH = 88;

    // Margins around the curve.
    //  Top: room for the "Mid 50%" label + the bracket spanning the IQR.
    //  Bottom: stacked, top-down: "damage →" axis label, then the up-arrow
    //          to the median rule (passing left of the damage text), then
    //          "Median Dmg" caption.
    //  Left:   room for the "↑ fights" y-axis label.
    private const float MarginLeft = 56f;
    private const float MarginRight = 30f;
    private const float MarginTop = 38f;
    private const float MarginBottom = 64f;

    private static readonly Color LabelColor = new(0.95f, 0.93f, 0.88f, 1.0f);   // axis labels (cream)
    // Annotation color shared by the bracket, the median-arrow, and both
    // text callouts ("Mid 50%" / "Median Dmg"). Matches the gold accent used
    // for [color=#efc851] BBCode in the popup body so the callouts read as
    // part of the same family of "this is significant" highlights.
    private static readonly Color CalloutColor = ThemeStyle.GoldColor;
    private const int LabelFontSize = 14;

    private readonly ImageTexture _texture;
    private readonly float _p25Px;
    private readonly float _medianPx;
    private readonly float _p75Px;

    public DistLegendIllustration()
    {
        var sorted = (double[])LegendDistValues.Clone();
        System.Array.Sort(sorted);
        double p25 = Percentile(sorted, 0.25);
        double median = Percentile(sorted, 0.50);
        double p75 = Percentile(sorted, 0.75);
        double max = sorted[^1];

        var xRange = new SparklinePoc.XRange(0, max);
        _texture = SparklinePoc.BuildSparklineTexture(LegendDistValues, new Vector2I(CurveW, CurveH),
            SparklinePoc.MarkerStyle.ShadedIqrMedianRule, xRange);

        // Map landmark data values to pixel x within the curve.
        float Map(double v) => (float)((v - xRange.Lo) / (xRange.Hi - xRange.Lo) * (CurveW - 1));
        _p25Px = Map(p25);
        _medianPx = Map(median);
        _p75Px = Map(p75);

        CustomMinimumSize = new Vector2(MarginLeft + CurveW + MarginRight,
                                         MarginTop + CurveH + MarginBottom);
        SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        // 1:1 native render, but set Linear anyway so the host theme's choice
        // doesn't flip us into nearest-neighbor at integer scales.
        TextureFilter = TextureFilterEnum.Linear;
    }

    public override void _Draw()
    {
        var curveRect = new Rect2(MarginLeft, MarginTop, CurveW, CurveH);
        DrawTextureRect(_texture, curveRect, tile: false);

        var font = TooltipHelper.GetKreonFont() ?? ThemeDB.FallbackFont;
        var ascent = font.GetAscent(LabelFontSize);
        var descent = font.GetDescent(LabelFontSize);

        float p25X = MarginLeft + _p25Px;
        float medX = MarginLeft + _medianPx;
        float p75X = MarginLeft + _p75Px;
        float curveTopY = MarginTop;
        float curveBotY = MarginTop + CurveH;

        // ── TOP: "Mid 50%" label + bracket spanning the IQR ──
        var midLabel = "Mid 50%";
        var midLabelSize = font.GetStringSize(midLabel, HorizontalAlignment.Left, -1, LabelFontSize);
        float midLabelTopY = 2f;
        float midLabelBaselineY = midLabelTopY + ascent;
        DrawString(font, new Vector2((p25X + p75X) / 2f - midLabelSize.X / 2f, midLabelBaselineY),
            midLabel, HorizontalAlignment.Left, -1, LabelFontSize, CalloutColor);

        float bracketBaseY = curveTopY - 2f;
        DrawTopBracket(p25X, p75X, bracketBaseY, CalloutColor);

        // ── BOTTOM (stacked, top-down): "damage →" axis just below curve,
        // then a long up-arrow to the median rule passing alongside the
        // axis text, then "Median Dmg" caption below the arrow. The damage
        // axis label sits to the right side of the curve so it doesn't
        // crowd the median arrow (which is at medX, near the left of centre
        // for this dataset). ──
        var xLabel = "damage →";
        var xLabelSize = font.GetStringSize(xLabel, HorizontalAlignment.Left, -1, LabelFontSize);
        float xLabelTopY = curveBotY + 4f;
        float xLabelBaselineY = xLabelTopY + ascent;
        // Sit on the right side of the axis (clear of the median arrow
        // which is left-of-centre for this dataset), but pulled in from
        // the right edge so it doesn't read as flush-right axis label.
        float xLabelX = MarginLeft + CurveW - xLabelSize.X - 24f;
        DrawString(font, new Vector2(xLabelX, xLabelBaselineY),
            xLabel, HorizontalAlignment.Left, -1, LabelFontSize, LabelColor);

        // Median arrow: tip at curve bottom (pointing UP into the median
        // rule), base just above the "Median Dmg" caption.
        float arrowTipY = curveBotY + 2f;
        float medLabelTopY = xLabelBaselineY + descent + 6f;
        float arrowBaseY = medLabelTopY - 2f;
        DrawArrowUp(new Vector2(medX, arrowBaseY), new Vector2(medX, arrowTipY));

        var medLabel = "Median Dmg";
        var medLabelSize = font.GetStringSize(medLabel, HorizontalAlignment.Left, -1, LabelFontSize);
        float medLabelBaselineY = medLabelTopY + ascent;
        DrawString(font, new Vector2(medX - medLabelSize.X / 2f, medLabelBaselineY),
            medLabel, HorizontalAlignment.Left, -1, LabelFontSize, CalloutColor);

        // ── LEFT: "↑ fights" y-axis label, vertically centred with the curve ──
        var yLabel = "↑ fights";
        var yLabelSize = font.GetStringSize(yLabel, HorizontalAlignment.Left, -1, LabelFontSize);
        DrawString(font,
            new Vector2(MarginLeft - yLabelSize.X - 6f, MarginTop + CurveH / 2f + ascent / 2f - descent / 2f),
            yLabel, HorizontalAlignment.Left, -1, LabelFontSize, LabelColor);
    }

    /// <summary>
    /// Draws a top bracket spanning [leftX, rightX] with rounded outer
    /// corners and arms pointing down to baseY (just above the curve). No
    /// central feature — earlier prototype had a TeX-overbrace style apex
    /// peak made of two diagonals, but the diagonals showed visible
    /// aliasing at the apex tip. The rounded-corner version reads as a
    /// unifying span marker without that artifact.
    /// </summary>
    private void DrawTopBracket(float leftX, float rightX, float baseY, Color color)
    {
        const float ArmDepth = 8f;
        const float CornerRadius = 3f;
        const float LineWidth = 1.2f;

        float topY = baseY - ArmDepth;
        float legStartY = topY + CornerRadius;

        // Outer-left rounded corner — quarter-arc sweeping the upper-left
        // quadrant (west to north relative to the corner's centre).
        // Antialiased — without `antialiased: true` on every line below the
        // arcs would render at a visibly different effective thickness vs
        // the AA arcs (the AA half-coverage edge pixels make the arcs read
        // ~1 px wider). All segments share AA so the join is seamless.
        DrawArc(new Vector2(leftX + CornerRadius, topY + CornerRadius), CornerRadius,
            Mathf.Pi, Mathf.Pi * 1.5f, 16, color, LineWidth, true);
        // Outer-right rounded corner — upper-right quadrant.
        DrawArc(new Vector2(rightX - CornerRadius, topY + CornerRadius), CornerRadius,
            Mathf.Pi * 1.5f, Mathf.Pi * 2f, 16, color, LineWidth, true);

        // Top horizontal connecting the two corners (between arc tops).
        DrawLine(new Vector2(leftX + CornerRadius, topY), new Vector2(rightX - CornerRadius, topY), color, LineWidth, true);

        // Vertical legs descending from arc bottoms to baseY.
        DrawLine(new Vector2(leftX, legStartY), new Vector2(leftX, baseY), color, LineWidth, true);
        DrawLine(new Vector2(rightX, legStartY), new Vector2(rightX, baseY), color, LineWidth, true);
    }

    private void DrawArrowUp(Vector2 from, Vector2 to)
    {
        // Body: kept thin so the head's triangle reads as the focal point
        // rather than the body matching its width.
        const float BodyWidth = 0.9f;
        // Head: 6 px wide × 6 px tall — small but with a definite triangle
        // shape, ~3× the body width.
        const float HeadHalfWidth = 3f;
        const float HeadHeight = 6f;

        DrawLine(from, to, CalloutColor, BodyWidth, true);
        var head = new[]
        {
            to,
            to + new Vector2(-HeadHalfWidth, HeadHeight),
            to + new Vector2(HeadHalfWidth, HeadHeight),
        };
        DrawColoredPolygon(head, CalloutColor);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        double idx = (sorted.Length - 1) * p;
        int lo = (int)System.Math.Floor(idx);
        int hi = (int)System.Math.Ceiling(idx);
        double frac = idx - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }
}
