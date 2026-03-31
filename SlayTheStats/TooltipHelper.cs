using Godot;

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

    private static Font? _monoRegular;
    private static Font? _monoBold;
    private static bool  _modFontsLoaded;

    internal static bool HasBoldFont => _monoBold != null;

    // Empirically matched to game's native tooltip width. Game panels report 359px logical but
    // visually 348 aligns best — likely due to stone texture transparent edges or canvas scaling.
    internal const float TooltipWidth  = 348f;
    internal const float ShadowOffset  = 8f;

    internal static bool FontStealConnected;
    internal static bool HasActiveHover;
    internal static bool InspectActive;
    internal static int  ShowGen;
    private  static bool _sceneTheftDone;

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

        var header = new RichTextLabel();
        header.Name               = "HeaderLabel";
        header.BbcodeEnabled      = true;
        header.FitContent         = true;
        header.AutowrapMode       = TextServer.AutowrapMode.Off;
        header.ScrollActive       = false;
        header.SelectionEnabled   = false;
        header.ShortcutKeysEnabled = false;
        vbox.AddChild(header);

        var table = new RichTextLabel();
        table.Name                = "StatsLabel";
        table.BbcodeEnabled       = true;
        table.FitContent          = true;
        table.AutowrapMode        = TextServer.AutowrapMode.Off;
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
    internal static void ShowPanel(string tableText)
    {
        ShowGen++;
        HasActiveHover = true;

        var panel = GetPanelPublic();
        if (panel == null) return;

        var header = panel.GetNodeOrNull<RichTextLabel>("VBoxContainer/HeaderLabel");
        var table  = panel.GetNodeOrNull<RichTextLabel>("VBoxContainer/StatsLabel");
        if (header == null || table == null) return;

        if (!_headerStyleApplied) { ApplyHeaderStyle(header); _headerStyleApplied = true; }
        if (!_tableStyleApplied)  { ApplyTableStyle(table);   _tableStyleApplied  = true; }

        if (_stolenPanelStyle == null) _stolenPanelStyle = BuildPanelStyle();
        panel.AddThemeStyleboxOverride("panel", _stolenPanelStyle);

        // When there is no data, fold the message into the header and hide the table label so the
        // panel collapses to a single line (VBoxContainer excludes invisible children from layout).
        header.Text = $"[b]Stats[/b]                    [color=#606060]SlayTheStats[/color]";
        table.Text  = tableText;
        // The PanelContainer is placed freely in the root (not inside a layout), so it won't
        // auto-resize when child content changes height. ResetSize() on both labels and the panel
        // forces the full chain to recalculate from the new content minimum size.
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

        if (_shadow != null && GodotObject.IsInstanceValid(_shadow)) _shadow.Visible = true;
        panel.Visible = true;
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
            if (ShowGen != genAtHide || HasActiveHover || InspectActive) return;
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
    /// Loads FiraMono regular and bold from the mod's own fonts/ directory.
    /// Uses the DLL location so the path works regardless of where GDWeave is installed.
    /// </summary>
    internal static void TryLoadModFonts()
    {
        if (_modFontsLoaded) return;
        _modFontsLoaded = true;
        try
        {
            var modDir      = System.IO.Path.GetDirectoryName(
                                  System.Reflection.Assembly.GetExecutingAssembly().Location)!;
            var regularPath = System.IO.Path.Combine(modDir, "fonts", "FiraMono-Regular.ttf");
            var boldPath    = System.IO.Path.Combine(modDir, "fonts", "FiraMono-Medium.ttf");

            if (System.IO.File.Exists(regularPath))
            {
                var ff = new FontFile();
                ff.LoadDynamicFont(regularPath);
                _monoRegular = ff;
            }
            if (System.IO.File.Exists(boldPath))
            {
                var ff = new FontFile();
                ff.LoadDynamicFont(boldPath);
                _monoBold = ff;
            }

            if (_monoRegular == null)
                MainFile.Logger.Warn($"[SlayTheStats] FiraMono-Regular.ttf not found at {regularPath}");
            if (_monoBold == null)
                MainFile.Logger.Warn($"[SlayTheStats] FiraMono-Medium.ttf not found at {boldPath}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] TryLoadModFonts failed: {e.Message}");
        }
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
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        var tipSet = FindNodeByTypeName(root, "NHoverTipSet");
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

    private static void ApplyHeaderStyle(RichTextLabel label)
    {
        // RichTextLabel uses different override keys than Label.
        // default_color is the base colour; [b] tags use bold_font; [color=] overrides in BBCode.
        label.AddThemeColorOverride("default_color", Fonts.Title);
        if (Fonts.Bold   != null) label.AddThemeFontOverride("bold_font",   Fonts.Bold);
        if (Fonts.Normal != null) label.AddThemeFontOverride("normal_font", Fonts.Normal);
        label.AddThemeFontSizeOverride("bold_font_size",   Fonts.Size);
        label.AddThemeFontSizeOverride("normal_font_size", Fonts.Size);
        label.AddThemeColorOverride("font_shadow_color", Fonts.Shadow);
        label.AddThemeConstantOverride("shadow_offset_x", Fonts.ShadowX);
        label.AddThemeConstantOverride("shadow_offset_y", Fonts.ShadowY);
        label.AddThemeConstantOverride("line_separation",  Fonts.LineSep);
    }

    private static void ApplyTableStyle(RichTextLabel label)
    {
        label.AddThemeColorOverride("default_color", Fonts.Text);
        // Prefer bundled FiraMono; fall back to the game's SourceCodePro (no bold variant).
        var normal = _monoRegular ?? ResourceLoader.Load<Font>("res://fonts/source_code_pro_medium.ttf");
        if (normal != null) label.AddThemeFontOverride("normal_font", normal);
        if (_monoBold != null)
        {
            label.AddThemeFontOverride("bold_font", _monoBold);
            label.AddThemeFontSizeOverride("bold_font_size", 20);
        }
        label.AddThemeFontSizeOverride("normal_font_size", 20); // smaller than header to fit 4-col table within panel width
        label.AddThemeColorOverride("font_shadow_color", Fonts.Shadow);
        label.AddThemeConstantOverride("shadow_offset_x", Fonts.ShadowX);
        label.AddThemeConstantOverride("shadow_offset_y", Fonts.ShadowY);
        label.AddThemeConstantOverride("line_separation", Fonts.LineSep);
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
    internal const string NeutralShade = "#909090";

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
    internal static string ColWR(string text, double pct, int n, double baseline = 50.0)
    {
        if (n <= 3) return $"[color={NeutralShade}]{text}[/color]";
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
        if (n <= 3) return $"[color={NeutralShade}]{text}[/color]";
        var level = SigLevel(Significance(pct, baseline, n, KWin * KPickFactor));
        if (level < 0) return $"[color={NeutralShade}]{text}[/color]";
        var inner = level == 2 && HasBoldFont ? $"[b]{text}[/b]" : text;
        return $"[color={(pct >= baseline ? GoodShades : BadShades)[level]}]{inner}[/color]";
    }

    private static StyleBox BuildPanelStyle()
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

    internal static Node? FindTipSet(Node? root) => FindNodeByTypeName(root, "NHoverTipSet");

    internal static void UpdatePanelPosition(Control p, Container textContainer)
    {
        p.CustomMinimumSize = new Vector2(TooltipWidth, 0);

        int sep = textContainer.GetThemeConstant("separation");
        float x = textContainer.GlobalPosition.X;
        // When a card is near the right edge, the game repositions its own tooltips leftward
        // automatically. Cards with no native tooltips don't trigger that logic, so
        // textHoverTipContainer may sit at a position that would push our panel off-screen.
        // Clamp to the viewport so our panel never overflows the right (or left) edge.
        var viewportSize = p.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        if (x + TooltipWidth > viewportSize.X)
            x = viewportSize.X - TooltipWidth;
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
    public override void _Process(double delta)
    {
        var p = TooltipHelper.GetPanelPublic();
        if (p == null || !p.Visible) return;

        var root          = (Engine.GetMainLoop() as SceneTree)?.Root;
        var tipSet        = TooltipHelper.FindTipSet(root);
        var textContainer = tipSet?.GetNodeOrNull<Container>("textHoverTipContainer");
        if (textContainer == null) return;

        if (textContainer.GetChildCount() > 0)
        {
            // Game has repositioned textHoverTipContainer beside the card — follow it.
            TooltipHelper.UpdatePanelPosition(p, textContainer);
        }
        else
        {
            // No native tooltips: the game still positions textContainer at the card's right
            // edge (normal) or left edge (flipped, when card is near the right of the screen).
            // textContainer.X is the correct placement anchor — use it directly.
            // The only thing we need to decide is which side: use the card holder's actual
            // right edge for an exact overflow check instead of the old midpoint heuristic,
            // which misfired for cards in the middle-right area of the screen.
            p.CustomMinimumSize = new Vector2(TooltipHelper.TooltipWidth, 0);
            int sep = textContainer.GetThemeConstant("separation");
            var viewportSize = p.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
            float tcX = textContainer.GlobalPosition.X;

            bool flipToLeft;
            var holder = CardHoverShowPatch.ActiveHolderControl;
            if (holder != null && GodotObject.IsInstanceValid(holder))
            {
                // Game flips when placing panel to the right of the card would overflow.
                float cardRight = holder.GlobalPosition.X + holder.Size.X;
                flipToLeft = cardRight + TooltipHelper.TooltipWidth > viewportSize.X;
            }
            else
            {
                // Fallback: if textContainer (= card right edge when not flipped) + panel
                // overflows, the game must have already flipped textContainer to the left edge.
                flipToLeft = tcX + TooltipHelper.TooltipWidth > viewportSize.X;
            }

            // textContainer.X is at the card's right edge (no flip) or left edge (flip).
            float x = flipToLeft ? tcX - TooltipHelper.TooltipWidth : tcX;
            x = Math.Max(0, x);
            p.Position = new Vector2(x, textContainer.GlobalPosition.Y + sep);
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
