using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace SlayTheStats;

/// <summary>
/// Shared tooltip panel and styling infrastructure used by both the card and relic hover patches.
/// Owns the single PanelContainer singleton, font theft, style application, and position following.
/// </summary>
internal static class TooltipHelper
{
    internal sealed class FontSettings
    {
        public Font? Bold;
        public Font? Normal;
        public int   Size    = 22;
        public Color Title   = new Color(0.918f, 0.745f, 0.318f, 1f); // golden yellow — matches keyword panel titles
        public Color Text    = new Color(1f, 0.9647f, 0.8863f, 1f);
        public Color Shadow  = new Color(0f, 0f, 0f, 0.251f);
        public int   ShadowX = 3;
        public int   ShadowY = 2;
        public int   LineSep = -2;
    }

    internal static FontSettings Fonts = new()
    {
        Bold   = ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres"),
        Normal = ResourceLoader.Load<Font>("res://themes/kreon_regular_glyph_space_one.tres"),
    };

    private static bool           _headerStyleApplied;
    private static bool           _tableStyleApplied;
    private static StyleBox?      _stolenPanelStyle;
    private static Control?       _panel;
    private static NinePatchRect? _shadow;
    private static bool           _followerInjected;

    internal static bool HasBoldFont => Fonts.Bold != null;
    internal static Font? GetKreonFont() => Fonts.Normal;
    internal static Font? GetKreonBoldFont() => Fonts.Bold;

    // Empirically matched to game's native tooltip width. Game panels report 359px logical but
    // visually 348 aligns best — likely due to stone texture transparent edges or canvas scaling.
    internal const float TooltipWidth  = 348f;
    internal const float EncounterTooltipWidth = 640f;
    internal const float ShadowOffset  = 8f;

    internal static bool FontStealConnected;
    internal static bool HasActiveHover;
    internal static bool InspectActive;
    internal static int  ShowGen;
    private  static bool _sceneTheftDone;

    /// <summary>
    /// Snapshot of the card/relic holder that triggered the current ShowPanel call.
    /// Captured at ShowPanel time because ClearHoverTips can null out the patch-level
    /// _activeHolder before _Process runs (tooltip-less cards call Create+Clear in the
    /// same frame). Cleared by HideWithDelay's timer when the panel actually hides.
    /// </summary>
    internal static Control? LastShowHolder;

    internal const string TooltipNodeName = "SlayTheStatsTooltip";

    internal static void EnsurePanelExists()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel)) return;

        var panel = new PanelContainer();
        panel.Name    = TooltipNodeName;
        panel.Visible = false;

        var vbox = new VBoxContainer();
        vbox.Name = "VBoxContainer";
        vbox.AddThemeConstantOverride("separation", 2);
        vbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        // Force the vbox to fill the panel's content area horizontally so the header
        // Control's right-anchored brand lands at the right edge.
        vbox.CustomMinimumSize = new Vector2(TooltipWidth - 22f, 0);

        // Header is a plain Control (not an HBox) so we can anchor the title label
        // to the top-left and the brand label to the top-right independently. This
        // mirrors EncounterStatsTooltipPatch's header so the brand sits flush with
        // the top edge consistently across card/relic and encounter tooltips.
        const int HeaderHeightPx  = 28;
        const int BrandRightPadPx = 12;
        var headerRow = new Control();
        headerRow.Name = "HeaderRow";
        headerRow.CustomMinimumSize = new Vector2(0, HeaderHeightPx);
        headerRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.MouseFilter = Control.MouseFilterEnum.Ignore;
        vbox.AddChild(headerRow);

        var titleLabel = new Label();
        titleLabel.Name        = "TitleLabel";
        titleLabel.Text        = "Stats";
        titleLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        titleLabel.AnchorLeft   = 0f;
        titleLabel.AnchorTop    = 0f;
        titleLabel.AnchorRight  = 0f;
        titleLabel.AnchorBottom = 0f;
        titleLabel.OffsetLeft   = 0;
        titleLabel.OffsetTop    = 0;
        titleLabel.VerticalAlignment = VerticalAlignment.Top;
        headerRow.AddChild(titleLabel);

        var brandLabel = new Label();
        brandLabel.Name                = "SlayTheStatsBrand";
        brandLabel.Text                = "SlayTheStats";
        brandLabel.MouseFilter         = Control.MouseFilterEnum.Ignore;
        // Anchor to top-right so the brand's right edge always sits BrandRightPadPx
        // inside the header's right edge. VerticalAlignment.Top + OffsetTop=0 nudge
        // the brand up so its top sits flush with the title — visual symmetry with
        // the encounter tooltip's header.
        brandLabel.AnchorLeft   = 1f;
        brandLabel.AnchorRight  = 1f;
        brandLabel.AnchorTop    = 0f;
        brandLabel.AnchorBottom = 0f;
        brandLabel.GrowHorizontal = Control.GrowDirection.Begin;
        brandLabel.OffsetRight  = -BrandRightPadPx;
        brandLabel.OffsetTop    = 0;
        brandLabel.VerticalAlignment = VerticalAlignment.Top;
        headerRow.AddChild(brandLabel);

        var table = new RichTextLabel();
        table.Name                = "StatsLabel";
        table.BbcodeEnabled       = true;
        table.FitContent          = true;
        table.AutowrapMode        = TextServer.AutowrapMode.WordSmart;
        table.ScrollActive        = false;
        table.SelectionEnabled    = false;
        table.ShortcutKeysEnabled = false;
        vbox.AddChild(table);

        panel.AddChild(vbox);
        // Steel blue-gray tint distinguishes our panel from the game's warm-stone (positive) and
        // brick-red (negative) tooltips — "data" reads cool and analytical.
        // SelfModulate only affects the panel's own draw (background stylebox), not child labels.
        panel.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);
        // High ZIndex ensures the panel renders above card nodes regardless of scene tree order.
        panel.ZIndex = 100;
        _panel = panel;
        _headerStyleApplied = false;
        _tableStyleApplied  = false;

        // Shadow: same texture as the tooltip, dark-modulated, offset ShadowOffset px down-right.
        // NinePatchRect gives the same rounded-corner shape as the tooltip itself.
        var shadow = new NinePatchRect();
        shadow.Name              = "SlayTheStatsTooltipShadow";
        shadow.Texture           = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png");
        shadow.PatchMarginLeft   = 55;
        shadow.PatchMarginRight  = 91;
        shadow.PatchMarginTop    = 43;
        shadow.PatchMarginBottom = 32;
        shadow.Modulate          = new Color(0f, 0f, 0f, 0.25098f);
        shadow.ZIndex            = 99;
        shadow.Visible           = false;
        _shadow = shadow;
    }

    internal static Control? GetPanelPublic() =>
        _panel != null && GodotObject.IsInstanceValid(_panel) ? _panel : null;

    internal static NinePatchRect? GetShadowPublic() =>
        _shadow != null && GodotObject.IsInstanceValid(_shadow) ? _shadow : null;

    /// <summary>
    /// Increments ShowGen (cancels any pending hide timers), sets panel text, attaches to root,
    /// injects the position follower on first call, and makes the panel visible.
    /// </summary>
    internal static float ActiveWidth = TooltipWidth;

    internal static void ShowPanel(string tableText, Control? holder = null, float? widthOverride = null)
    {
        ShowGen++;
        HasActiveHover = true;
        ActiveWidth = widthOverride ?? TooltipWidth;
        if (holder != null) LastShowHolder = holder;

        var panel = GetPanelPublic();
        if (panel == null) return;

        var titleLabel = panel.GetNodeOrNull<Label>("VBoxContainer/HeaderRow/TitleLabel");
        var brandLabel = panel.GetNodeOrNull<Label>("VBoxContainer/HeaderRow/SlayTheStatsBrand");
        var table      = panel.GetNodeOrNull<RichTextLabel>("VBoxContainer/StatsLabel");
        if (titleLabel == null || brandLabel == null || table == null) return;

        bool firstStyle = !_headerStyleApplied;
        if (!_headerStyleApplied) { ApplyHeaderLabelStyles(titleLabel, brandLabel); _headerStyleApplied = true; }
        if (!_tableStyleApplied)  { ApplyTableStyle(table); _tableStyleApplied  = true; }

        if (_stolenPanelStyle == null) _stolenPanelStyle = BuildPanelStyle();
        panel.AddThemeStyleboxOverride("panel", _stolenPanelStyle);

        // Pin the panel's min width BEFORE assigning BBCode text. The position
        // follower also sets this every _Process, but that runs after ShowPanel
        // — which means the RichTextLabel would otherwise compute its FitContent
        // height against a degenerate ~1px width and balloon to thousands of px.
        panel.CustomMinimumSize = new Vector2(ActiveWidth, 0);
        // Also pin the table's min width: setting the panel's min propagates up,
        // not down, so child layout won't give the table a real width until end
        // of frame. Giving the table its own explicit min width lets BBCode
        // FitContent measure the correct wrapped height immediately.
        // Read content margins off the base StyleBox — StyleBoxTexture (stolen /
        // BuildPanelStyle default) and StyleBoxFlat both expose them. The prior
        // `is StyleBoxFlat` check missed the texture case and fell through to a
        // hardcoded 16, leaving the vbox ~6px too wide: the PanelContainer grew
        // the panel to fit, and the right-anchored brand ended up further from
        // the visible frame border than its encounter-tooltip twin.
        float contentPad = _stolenPanelStyle != null
            ? _stolenPanelStyle.ContentMarginLeft + _stolenPanelStyle.ContentMarginRight
            : 22f;
        table.CustomMinimumSize = new Vector2(ActiveWidth - contentPad, 0);
        // Pin the vbox's min width so the header Control's right-anchored brand
        // label lands at the correct right edge on the very first show. Without
        // this, vbox takes its min from the narrower content and the anchored
        // brand sits mid-panel until a later layout pass corrects it.
        var vbox = panel.GetNodeOrNull<VBoxContainer>("VBoxContainer");
        if (vbox != null) vbox.CustomMinimumSize = new Vector2(ActiveWidth - contentPad, 0);

        // Header labels are static ("Stats" / "SlayTheStats") and set at panel
        // construction; only the table text varies per show.
        table.Text = tableText;
        // The PanelContainer is placed freely in the root (not inside a layout), so it won't
        // auto-resize when child content changes height. ResetSize() on labels and the panel
        // forces the full chain to recalculate from the new content minimum size.
        // Explicit per-label ResetSize calls ensure the Label children in the header HBox
        // don't carry stale (default-font) min sizes into the panel's combined min size.
        titleLabel.ResetSize();
        brandLabel.ResetSize();
        table.ResetSize();
        panel.ResetSize();

        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (panel.GetParent() != root)
        {
            panel.GetParent()?.RemoveChild(panel);
            root?.AddChild(panel);
        }

        if (_shadow != null && GodotObject.IsInstanceValid(_shadow) && _shadow.GetParent() != root)
        {
            _shadow.GetParent()?.RemoveChild(_shadow);
            root?.AddChild(_shadow);
        }

        if (!_followerInjected)
        {
            _followerInjected = true;
            var follower = new SlayTheStatsPositionFollower { Name = "SlayTheStatsFollower" };
            root?.AddChild(follower);
        }

        // On first style application, Label min-size recomputation from the newly-applied
        // fonts lags by a frame. Showing the panel immediately would render one frame of
        // oversized tooltip before the deferred reset_size snaps it to the correct height.
        // Defer both the reset AND the visibility flip so the panel only appears with correct
        // metrics. Subsequent shows use cached metrics and can flip visibility synchronously.
        if (firstStyle)
        {
            // First show: BBCode content height is still 0 this frame (table.Text was
            // just assigned and FitContent hasn't laid out yet). Defer the visibility
            // flip so the panel only appears once the table has measured its content.
            panel.CallDeferred("reset_size");
            var p = panel;
            var s = _shadow;
            Callable.From(() =>
            {
                p.Visible = true;
                if (s != null && GodotObject.IsInstanceValid(s)) s.Visible = true;
            }).CallDeferred();
        }
        else
        {
            if (_shadow != null && GodotObject.IsInstanceValid(_shadow)) _shadow.Visible = true;
            panel.Visible = true;
        }
    }

    /// <summary>
    /// Starts a 50 ms debounce timer that hides the panel if no new show fires.
    /// Captures ShowGen so any ShowPanel call cancels the pending hide.
    /// </summary>
    internal static void HideWithDelay()
    {
        var genAtHide = ShowGen;
        var timer = (Engine.GetMainLoop() as SceneTree)?.CreateTimer(0.05f);
        if (timer == null) return;

        timer.Timeout += () =>
        {
            var hiding = ShowGen == genAtHide && !HasActiveHover && !InspectActive;
            MainFile.Logger.Info($"[SlayTheStats] HideTimer gen={ShowGen} expected={genAtHide} activeHover={HasActiveHover} hiding={hiding}");
            if (!hiding) return;
            LastShowHolder = null;
            var node = GetPanelPublic();
            if (node != null) node.Visible = false;
            if (_shadow != null && GodotObject.IsInstanceValid(_shadow)) _shadow.Visible = false;
        };
    }

    internal static void InvalidateStyles()
    {
        _headerStyleApplied = false;
        _tableStyleApplied  = false;
    }

    /// <summary>
    /// Loads hover_tip.tscn once to steal accurate font and margin values directly from the scene,
    /// then invalidates cached styles so they are reapplied on the next show.
    /// </summary>
    internal static void TrySceneTheftOnce()
    {
        if (_sceneTheftDone) return;
        _sceneTheftDone = true;
        try
        {
            var scene = ResourceLoader.Load<PackedScene>("res://scenes/ui/hover_tip.tscn");
            if (scene == null) return;

            var panel = scene.Instantiate<Control>();

            var desc = panel.GetNodeOrNull<Control>("TextContainer/VBoxContainer/Description");
            if (desc != null)
            {
                var normal  = desc.GetThemeFont("normal_font");
                var bold    = desc.GetThemeFont("bold_font");
                var size    = desc.GetThemeFontSize("normal_font_size");
                var text    = desc.GetThemeColor("default_color");
                var shadow  = desc.GetThemeColor("font_shadow_color");
                var shadowX = desc.GetThemeConstant("shadow_offset_x");
                var shadowY = desc.GetThemeConstant("shadow_offset_y");
                var lineSep = desc.GetThemeConstant("line_separation");
                if (normal != null || bold != null)
                {
                    Fonts = new FontSettings
                    {
                        Normal = normal, Bold = bold,
                        Size   = size > 0 ? size : Fonts.Size,
                        Title  = Fonts.Title,
                        Text   = text != default ? text : Fonts.Text,
                        Shadow = shadow, ShadowX = shadowX, ShadowY = shadowY, LineSep = lineSep,
                    };
                    InvalidateStyles();
                }
            }

            var bg            = panel.GetNodeOrNull<NinePatchRect>("Bg");
            var textContainer = panel.GetNodeOrNull<MarginContainer>("TextContainer");


            var tex           = bg?.Texture as Texture2D ?? ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png");
            if (tex != null)
            {
                var sb = new StyleBoxTexture();
                sb.Texture             = tex;
                sb.TextureMarginLeft   = bg?.PatchMarginLeft   ?? 55f;
                sb.TextureMarginRight  = bg?.PatchMarginRight  ?? 91f;
                sb.TextureMarginTop    = bg?.PatchMarginTop    ?? 43f;
                sb.TextureMarginBottom = bg?.PatchMarginBottom ?? 32f;
                sb.ContentMarginLeft   = textContainer?.GetThemeConstant("margin_left")   ?? 22f;
                sb.ContentMarginRight  = 0f;
                sb.ContentMarginTop    = textContainer?.GetThemeConstant("margin_top")    ?? 16f;
                sb.ContentMarginBottom = 12f;
                _stolenPanelStyle = sb;
            }

            panel.QueueFree();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] TrySceneTheft failed: {e.Message}");
        }
    }

    internal static void ConnectFontTheft()
    {
        if (FontStealConnected) return;
        var tipSet = FindTipSet(null);
        var textContainer = tipSet?.GetNodeOrNull<Node>("textHoverTipContainer");
        if (textContainer == null) return;
        FontStealConnected = true;

        textContainer.Connect(Node.SignalName.ChildEnteredTree, Callable.From<Node>(tipPanel =>
        {
            if (tipPanel.Name == TooltipNodeName) return;

            var vbox = tipPanel.GetNodeOrNull<Node>("TextContainer/VBoxContainer");
            var desc = tipPanel.GetNodeOrNull<Control>("TextContainer/VBoxContainer/Description");
            if (desc == null) return;
            var normal  = desc.GetThemeFont("normal_font");
            var bold    = desc.GetThemeFont("bold_font");
            var size    = desc.GetThemeFontSize("normal_font_size");
            var text    = desc.GetThemeColor("default_color");
            var shadow  = desc.GetThemeColor("font_shadow_color");
            var shadowX = desc.GetThemeConstant("shadow_offset_x");
            var shadowY = desc.GetThemeConstant("shadow_offset_y");
            var lineSep = desc.GetThemeConstant("line_separation");
            if (normal == null && bold == null) return;

            var titleColor = Fonts.Title;
            if (vbox != null)
            {
                foreach (var child in vbox.GetChildren())
                {
                    if (child.Name == "Description") continue;
                    if (child is Control ctrl)
                    {
                        var c = ctrl.GetThemeColor("font_color");
                        if (c != default)
                            titleColor = c;
                    }
                }
            }

            Fonts = new FontSettings
            {
                Normal = normal, Bold = bold,
                Size   = size > 0 ? size : Fonts.Size,
                Title  = titleColor,
                Text   = text != default ? text : Fonts.Text,
                Shadow = shadow, ShadowX = shadowX, ShadowY = shadowY, LineSep = lineSep,
            };
            InvalidateStyles();
        }));
    }

    /// <summary>
    /// Themes the restructured header's two Labels. Title takes the game-stolen
    /// bold title font + size + shadow; brand takes Kreon regular at BrandSize
    /// in FooterGrey with the same shadow. Mirrors EncounterStatsTooltipPatch's
    /// header styling so card/relic and encounter tooltips read consistently.
    /// </summary>
    private static void ApplyHeaderLabelStyles(Label titleLabel, Label brandLabel)
    {
        // Title — bold, gold, game-stolen title size.
        titleLabel.AddThemeColorOverride("font_color", Fonts.Title);
        if (Fonts.Bold != null) titleLabel.AddThemeFontOverride("font", Fonts.Bold);
        titleLabel.AddThemeFontSizeOverride("font_size", Fonts.Size);
        titleLabel.AddThemeColorOverride("font_shadow_color", Fonts.Shadow);
        titleLabel.AddThemeConstantOverride("shadow_offset_x", Fonts.ShadowX);
        titleLabel.AddThemeConstantOverride("shadow_offset_y", Fonts.ShadowY);

        // Brand — Kreon regular, BrandSize, FooterGrey. Font_color on the Label
        // directly (no BBCode in a plain Label).
        var brandColor = new Color(0.408f, 0.408f, 0.408f, 1f); // #686868 (ThemeStyle.FooterGrey)
        brandLabel.AddThemeColorOverride("font_color", brandColor);
        if (Fonts.Normal != null) brandLabel.AddThemeFontOverride("font", Fonts.Normal);
        brandLabel.AddThemeFontSizeOverride("font_size", ThemeStyle.BrandSize);
        brandLabel.AddThemeColorOverride("font_shadow_color", Fonts.Shadow);
        brandLabel.AddThemeConstantOverride("shadow_offset_x", Fonts.ShadowX);
        brandLabel.AddThemeConstantOverride("shadow_offset_y", Fonts.ShadowY);
    }

    private static void ApplyTableStyle(RichTextLabel label)
    {
        label.AddThemeColorOverride("default_color", Fonts.Text);
        // Use Kreon proportional font — column alignment is handled by [table=N]
        // BBCode layout, not character width.
        if (Fonts.Normal != null) label.AddThemeFontOverride("normal_font", Fonts.Normal);
        if (Fonts.Bold != null)
        {
            label.AddThemeFontOverride("bold_font", Fonts.Bold);
            label.AddThemeFontSizeOverride("bold_font_size", 20);
        }
        label.AddThemeFontSizeOverride("normal_font_size", 20);
        label.AddThemeColorOverride("font_shadow_color", Fonts.Shadow);
        label.AddThemeConstantOverride("shadow_offset_x", Fonts.ShadowX);
        label.AddThemeConstantOverride("shadow_offset_y", Fonts.ShadowY);
        label.AddThemeConstantOverride("line_separation", Fonts.LineSep);
    }

    /// <summary>
    /// Wraps a filter-context string in the standard tooltip footer style
    /// (Kreon regular, 16px, FooterGrey). Used by card/relic/encounter
    /// tooltips to render the active-filter line below the stats table.
    /// Returns empty if the input is empty.
    /// </summary>
    internal static string FormatFooter(string filterCtx)
    {
        if (string.IsNullOrEmpty(filterCtx)) return "";
        return $"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color={ThemeStyle.FooterGrey}]{filterCtx}[/color][/font_size][/font]";
    }

    /// <summary>
    /// Wraps baseline-body text (e.g. "Baseline Pick% 35%, Win% 48%") in the
    /// same footer style as FormatFooter. Separate entry point so callers can
    /// build the baseline line independently of the filter line (they often
    /// render on separate lines below the table).
    /// </summary>
    internal static string FormatBaselineLine(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        return $"\n[font=res://themes/kreon_regular_glyph_space_one.tres][font_size=16][color={ThemeStyle.FooterGrey}]{body}[/color][/font_size][/font]";
    }

    /// <summary>Wraps text in a BBCode color tag based on sample size. Bolds at n≥16 when bold font is loaded.</summary>
    internal static string ColN(string text, int n) => n switch
    {
        < 8  => $"[color={NeutralShade}]{text}[/color]",
        < 16 => $"[color=#E4E4E4]{text}[/color]",
        _    => HasBoldFont ? $"[b]{text}[/b]" : text,
    };

    // Three shades per direction: light, medium, heavy. Chosen by significance score.
    // NeutralShade is used when significance is below threshold — dimmer than even the faintest shade.
    internal const string NeutralShade = "#b5b5b5";

    // Color-blind palette: teal (good) / orange (bad) — distinguishable without red/green.
    private static readonly string[] _cbBadShades  = { "#A87850", "#D86828", "#F06020" };
    private static readonly string[] _cbGoodShades = { "#508C8C", "#30B8B0", "#20E0D0" };

    // Normal palette: orange→red (bad) / lime→green (good).
    // Level 0 uses orange/lime so faint signals read as "slightly off" rather than immediately red/green.
    // Level-2 shades are intentionally below full saturation to avoid jarring vivid colors.
    private static readonly string[] _normalBadShades  = { "#C07828", "#C04020", "#CC2828" };
    private static readonly string[] _normalGoodShades = { "#88B840", "#40C040", "#28CC28" };

    private static string[] BadShades  => SlayTheStatsConfig.ColorBlindMode ? _cbBadShades  : _normalBadShades;
    private static string[] GoodShades => SlayTheStatsConfig.ColorBlindMode ? _cbGoodShades : _normalGoodShades;

    // k_win calibrated for Win%, where n = RunsPresent.
    // k_pick = k_win * KPickFactor: Pick% uses RunsOffered as n, which is ~4× larger than
    // RunsPresent for a ~25% pick-rate card. Since n ∝ 1/k in tanh(k × n × deviation),
    // scaling k down by the same factor keeps the significance thresholds equivalent.
    private const double KWin       = 0.0025;
    private const double KPickFactor = 0.25;

    /// <summary>
    /// Combined significance score in [0,1]: tanh(k × N × |pct − baseline|).
    /// </summary>
    private static double Significance(double pct, double baseline, int n, double k)
        => Math.Tanh(k * n * Math.Abs(pct - baseline));

    /// <summary>Maps a significance score to a shade index 0–2, or -1 for no colour.</summary>
    private static int SigLevel(double sig) => sig switch { < 0.25 => -1, < 0.40 => 0, < 0.65 => 1, _ => 2 };

    /// <summary>
    /// Colours text by win-rate significance relative to a baseline (default 50%).
    /// Pass the character's overall WR as the baseline so a card at 60% WR on a 65%-WR
    /// character correctly shows orange rather than teal.
    /// Direction (orange vs teal) comes from whether pct is above or below baseline;
    /// shade intensity comes from tanh(k × N × deviation) so both high deviation and
    /// high N push toward vivid colour.
    /// </summary>
    internal static string ColWR(string text, double pct, int n, double baseline = 50.0,
        bool skipSmallSampleFilter = false)
    {
        // Bestiary / encounter surfaces pass skipSmallSampleFilter=true because their
        // cells are compared against each other in a sorted list — a 3-run encounter
        // ranked high by significance must still color so the list stays monotonic.
        // Card/relic surfaces keep the default filter: stats there stand alone, and
        // low-n coloration would overstate confidence.
        if (!skipSmallSampleFilter && n <= 3) return $"[color={NeutralShade}]{text}[/color]";
        if (double.IsNaN(baseline)) return $"[color={NeutralShade}]{text}[/color]";
        var level = SigLevel(Significance(pct, baseline, n, KWin));
        if (level < 0) return $"[color={NeutralShade}]{text}[/color]";
        var inner = level == 2 && HasBoldFont ? $"[b]{text}[/b]" : text;
        return $"[color={(pct >= baseline ? GoodShades : BadShades)[level]}]{inner}[/color]";
    }

    /// <summary>
    /// Colours text by shop buy-rate significance relative to a baseline.
    /// Uses KWin as k because RunsShopSeen is comparable in scale to RunsPresent.
    /// </summary>
    internal static string ColBuys(string text, double pct, int n, double baseline = 20.0)
    {
        if (n <= 3 || double.IsNaN(baseline)) return $"[color={NeutralShade}]{text}[/color]";
        var level = SigLevel(Significance(pct, baseline, n, KWin));
        if (level < 0) return $"[color={NeutralShade}]{text}[/color]";
        var inner = level == 2 && HasBoldFont ? $"[b]{text}[/b]" : text;
        return $"[color={(pct >= baseline ? GoodShades : BadShades)[level]}]{inner}[/color]";
    }

    /// <summary>
    /// Colours text by pick-rate significance relative to a baseline.
    /// Baseline should be (1 - skipRate) / 3. Uses KWin * KPickFactor as k because
    /// RunsOffered (the n for Pick%) is ~4× larger than RunsPresent (n for Win%),
    /// so we scale k down proportionally to keep evidence thresholds equivalent.
    /// </summary>
    internal static string ColPR(string text, double pct, int n, double baseline = 100.0 / 3.0)
    {
        if (n <= 3 || double.IsNaN(baseline)) return $"[color={NeutralShade}]{text}[/color]";
        var level = SigLevel(Significance(pct, baseline, n, KWin * KPickFactor));
        if (level < 0) return $"[color={NeutralShade}]{text}[/color]";
        var inner = level == 2 && HasBoldFont ? $"[b]{text}[/b]" : text;
        return $"[color={(pct >= baseline ? GoodShades : BadShades)[level]}]{inner}[/color]";
    }

    internal static StyleBox BuildPanelStyle()
    {
        var tex = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png");
        if (tex != null)
        {
            var sb = new StyleBoxTexture();
            sb.Texture             = tex;
            sb.TextureMarginLeft   = 55f;
            sb.TextureMarginRight  = 91f;
            sb.TextureMarginTop    = 43f;
            sb.TextureMarginBottom = 32f;
            sb.ContentMarginLeft   = 22f;
            sb.ContentMarginRight  = 0f;
            sb.ContentMarginTop    = 16f;
            sb.ContentMarginBottom = 12f;
            return sb;
        }
        var flat = new StyleBoxFlat();
        flat.BgColor                = new Color(0.11f, 0.07f, 0.06f, 0.94f);
        flat.ContentMarginLeft      = 8f;
        flat.ContentMarginRight     = 8f;
        flat.ContentMarginTop       = 6f;
        flat.ContentMarginBottom    = 6f;
        flat.CornerRadiusTopLeft    = flat.CornerRadiusTopRight      = 3;
        flat.CornerRadiusBottomLeft = flat.CornerRadiusBottomRight   = 3;
        return flat;
    }

    internal static Node? FindTipSet(Node? _)
    {
        // Access HoverTipsContainer directly and return the last (most recently added) child.
        // A QueueFreed stale NHoverTipSet may still be in the container during the same frame
        // as a transition — children are appended by AddChild, so the newest is always last.
        var container = NGame.Instance?.HoverTipsContainer;
        if (container == null) return null;
        var count = container.GetChildCount();
        for (int i = count - 1; i >= 0; i--)
        {
            var child = container.GetChild(i);
            if (child?.GetType().Name == "NHoverTipSet" && GodotObject.IsInstanceValid(child))
                return child;
        }
        return null;
    }

    internal static void UpdatePanelPosition(Control p, Container textContainer)
    {
        p.CustomMinimumSize = new Vector2(ActiveWidth, 0);

        int sep = textContainer.GetThemeConstant("separation");
        float x = textContainer.GlobalPosition.X;
        // When a card is near the right edge, the game repositions its own tooltips leftward
        // automatically. Cards with no native tooltips don't trigger that logic, so
        // textHoverTipContainer may sit at a position that would push our panel off-screen.
        // Clamp to the viewport so our panel never overflows the right (or left) edge.
        var viewportSize = p.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        if (x + ActiveWidth > viewportSize.X)
            x = viewportSize.X - ActiveWidth;
        if (x < 0) x = 0;

        p.Position = new Vector2(x, textContainer.GlobalPosition.Y + textContainer.Size.Y + sep);
    }

    internal static Node? FindNodeByTypeName(Node? node, string typeName)
    {
        if (node == null) return null;
        if (node.GetType().Name == typeName) return node;
        foreach (var child in node.GetChildren())
        {
            var found = FindNodeByTypeName(child, typeName);
            if (found != null) return found;
        }
        return null;
    }
}

/// <summary>
/// Injected into the root scene tree once. Updates the stats panel position every frame
/// so it follows textHoverTipContainer regardless of scroll or screen transitions.
/// </summary>
public partial class SlayTheStatsPositionFollower : Node
{
    private int _lastDiagGen = -1;

    public override void _Process(double delta)
    {
        var p = TooltipHelper.GetPanelPublic();
        if (p == null || !p.Visible) return;

        // If there is no active hover, the panel is either in the 50ms hide-delay window or
        // was briefly touched and released. Either way, don't reposition — stay put and let
        // the timer hide the panel. This prevents a blink when the mouse quickly passes over
        // an element: Show fires (making panel visible) then Hide fires immediately, and
        // without this guard _Process would reposition the panel at the new element before
        // the hide timer catches up.
        // Exception: inspect mode — the inspect screen keeps the panel alive via InspectActive
        // but HasActiveHover goes false when the original card's ClearHoverTips fires, so we
        // must continue running to reposition when a native NHoverTipSet appears (e.g. on
        // hovering the upgrade button or any card keyword).
        if (!TooltipHelper.HasActiveHover && !TooltipHelper.InspectActive) return;

        var root          = (Engine.GetMainLoop() as SceneTree)?.Root;
        var tipSet        = TooltipHelper.FindTipSet(root);
        var textContainer = tipSet?.GetNodeOrNull<Container>("textHoverTipContainer");

        // Log once per ShowGen so the log isn't flooded every frame.
        var logOnce = TooltipHelper.ShowGen != _lastDiagGen;
        if (logOnce) _lastDiagGen = TooltipHelper.ShowGen;

        if (textContainer == null)
        {
            if (logOnce)
                MainFile.Logger.Info($"[SlayTheStats] _Process gen={TooltipHelper.ShowGen}: textContainer null (tipSet={(tipSet == null ? "null" : "found")}) activeHover={TooltipHelper.HasActiveHover}");

            // NHoverTipSet absent during an active hover — position relative to the active
            // holder directly (e.g. QueueFree hasn't flushed yet, or no native tip set).
            var holder = CardHoverShowPatch.ActiveHolderControl ?? RelicHoverHelper.ActiveHolderControl ?? TooltipHelper.LastShowHolder;
            if (holder == null || !GodotObject.IsInstanceValid(holder)) return;

            p.CustomMinimumSize = new Vector2(TooltipHelper.ActiveWidth, 0);
            var viewportSize = p.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
            // NCardHolder and NCard report Size=(0,0) — GlobalPosition is the visual center.
            // Use half the card's visual width (~140px at default scale) to find the right edge.
            float holderW = holder.Size.X;
            float holderRight;
            float holderLeft;
            if (holderW > 1f)
            {
                holderRight = holder.GlobalPosition.X + holderW;
                holderLeft  = holder.GlobalPosition.X;
            }
            else
            {
                // Zero-size node: GlobalPosition is center. Estimate card half-width.
                float halfCard = 140f * (holder.Scale.X > 0 ? holder.Scale.X : 1f);
                holderRight = holder.GlobalPosition.X + halfCard;
                holderLeft  = holder.GlobalPosition.X - halfCard;
            }

            bool flipToLeft = holderRight + TooltipHelper.ActiveWidth > viewportSize.X;
            float x         = flipToLeft ? holderLeft - TooltipHelper.ActiveWidth : holderRight;
            x = Math.Clamp(x, 0f, viewportSize.X - TooltipHelper.ActiveWidth);
            p.Position = new Vector2(x, holder.GlobalPosition.Y);

            var shadowFallback = TooltipHelper.GetShadowPublic();
            if (shadowFallback != null)
            {
                shadowFallback.Position = p.Position + new Vector2(TooltipHelper.ShadowOffset, TooltipHelper.ShadowOffset);
                shadowFallback.Size     = p.Size;
            }
            return;
        }

        if (logOnce)
            MainFile.Logger.Info($"[SlayTheStats] _Process gen={TooltipHelper.ShowGen}: textContainer at {textContainer.GlobalPosition} children={textContainer.GetChildCount()}");

        if (textContainer.GetChildCount() > 0)
        {
            // Game has repositioned textHoverTipContainer beside the card — follow it.
            TooltipHelper.UpdatePanelPosition(p, textContainer);
        }
        else
        {
            // No native tooltips. Use textContainer.GlobalPosition as the anchor — the game's
            // SetFollowOwner reliably places textContainer at the card's right edge each frame,
            // making tcX/tcY the most accurate position reference available.
            p.CustomMinimumSize = new Vector2(TooltipHelper.ActiveWidth, 0);
            int   sep          = textContainer.GetThemeConstant("separation");
            var   viewportSize = p.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
            float tcX          = textContainer.GlobalPosition.X;
            float tcY          = textContainer.GlobalPosition.Y;

            var ancientBtn = AncientEventOptionFocusPatch.ActiveAncientOptionControl;
            var cardHolder = CardHoverShowPatch.ActiveHolderControl
                          ?? MerchantCardCreateHoverTipPatch.ActiveMerchantCard
                          ?? TooltipHelper.LastShowHolder;

            float x;
            if (ancientBtn != null && GodotObject.IsInstanceValid(ancientBtn))
            {
                // Ancient event options use HoverTipAlignment.Left — panel goes to the LEFT.
                // Use the snapshotted anchor position so the button's hover-scale tween
                // doesn't cause the tooltip to drift.
                var anchorPos = AncientEventOptionFocusPatch.AnchorGlobal;
                x = anchorPos.X - TooltipHelper.ActiveWidth;
                x = Math.Clamp(x, 0f, viewportSize.X - TooltipHelper.ActiveWidth);
                p.Position = new Vector2(x, anchorPos.Y);
            }
            else if (cardHolder != null && GodotObject.IsInstanceValid(cardHolder))
            {
                // NCardHolder has Size=(0,0); GlobalPosition is the card's visual centre.
                // The game places textHoverTipContainer at the card's right edge normally,
                // but flips it to the left edge when the card is near the right viewport edge.
                // Detect which side the game chose by comparing tcX to the holder centre.
                bool gameFlippedLeft = tcX < cardHolder.GlobalPosition.X;
                if (gameFlippedLeft)
                {
                    // tcX is the card's left edge; place our panel to the left of it.
                    x = tcX - TooltipHelper.ActiveWidth;
                }
                else
                {
                    // tcX is the card's right edge; place our panel there.
                    x = tcX;
                }
                x = Math.Clamp(x, 0f, viewportSize.X - TooltipHelper.ActiveWidth);
                p.Position = new Vector2(x, tcY + sep);
                if (logOnce)
                    MainFile.Logger.Info($"[SlayTheStats] _Process no-tip card: holder.GlobalPos={cardHolder.GlobalPosition} tcX={tcX} gameFlippedLeft={gameFlippedLeft} x={x}");
            }
            else
            {
                bool flipToLeft = tcX + TooltipHelper.ActiveWidth > viewportSize.X;
                x = flipToLeft ? tcX - TooltipHelper.ActiveWidth : tcX;
                x = Math.Max(0, x);
                p.Position = new Vector2(x, tcY + sep);
            }
        }

        // Overflow correction: if our panel bottom goes below the viewport, shift the
        // game's NHoverTipSet (parent of textHoverTipContainer + cardHoverTipContainer)
        // upward by the overshoot so the whole tooltip column stays on screen.
        // p.Size.Y may be 0 on the very first frame before layout runs — skip in that case.
        if (tipSet is Control tipSetCtrl && p.Size.Y > 0)
        {
            var vpSize    = p.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
            float overflow = (p.Position.Y + p.Size.Y) - vpSize.Y;
            if (overflow > 0)
            {
                if (logOnce)
                    MainFile.Logger.Info($"[SlayTheStats] _Process gen={TooltipHelper.ShowGen}: overflow={overflow:F0} shifting NHoverTipSet up");
                tipSetCtrl.GlobalPosition = new Vector2(tipSetCtrl.GlobalPosition.X, tipSetCtrl.GlobalPosition.Y - overflow);
                p.Position = new Vector2(p.Position.X, p.Position.Y - overflow);
            }
        }

        var shadow = TooltipHelper.GetShadowPublic();
        if (shadow != null)
        {
            shadow.Position = p.Position + new Vector2(TooltipHelper.ShadowOffset, TooltipHelper.ShadowOffset);
            shadow.Size     = p.Size;
        }

        // For relics whose card tip is positioned BELOW the text container (SetAlignmentForRelic),
        // push the card container further down to make room for our panel.
        // Relics using SetAlignment(Left|Right) place the card BESIDE the text — don't move those.
        var cardContainer = tipSet?.GetNodeOrNull<Control>("cardHoverTipContainer");
        if (cardContainer != null && cardContainer.GetChildCount() > 0 && p.Size.Y > 0
            && RelicHoverHelper.ShouldPushCardContainer)
        {
            int sep = textContainer.GetThemeConstant("separation");
            cardContainer.GlobalPosition = new Vector2(
                cardContainer.GlobalPosition.X,
                p.GlobalPosition.Y + p.Size.Y + sep + 12f);
        }
    }
}
