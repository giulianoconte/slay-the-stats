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
        public Color Title   = new Color(0.988f, 0.784f, 0.184f, 1f); // golden yellow default
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

    private static bool      _headerStyleApplied;
    private static bool      _tableStyleApplied;
    private static StyleBox? _stolenPanelStyle;
    private static float?    _lastTipWidth;
    private static Control?  _panel;
    private static bool      _followerInjected;

    internal static bool FontStealConnected;
    internal static bool HasActiveHover;
    internal static int  ShowGen;

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

        var header = new Label();
        header.Name         = "HeaderLabel";
        header.AutowrapMode = TextServer.AutowrapMode.Off;
        vbox.AddChild(header);

        var table = new Label();
        table.Name         = "StatsLabel";
        table.AutowrapMode = TextServer.AutowrapMode.Off;
        vbox.AddChild(table);

        panel.AddChild(vbox);
        _panel = panel;
        _headerStyleApplied = false;
        _tableStyleApplied  = false;
    }

    internal static Control? GetPanelPublic() =>
        _panel != null && GodotObject.IsInstanceValid(_panel) ? _panel : null;

    /// <summary>
    /// Increments ShowGen (cancels any pending hide timers), sets panel text, attaches to root,
    /// injects the position follower on first call, and makes the panel visible.
    /// </summary>
    internal static void ShowPanel(string headerText, string tableText)
    {
        ShowGen++;
        HasActiveHover = true;

        var panel = GetPanelPublic();
        if (panel == null) return;

        var header = panel.GetNodeOrNull<Label>("VBoxContainer/HeaderLabel");
        var table  = panel.GetNodeOrNull<Label>("VBoxContainer/StatsLabel");
        if (header == null || table == null) return;

        if (!_headerStyleApplied) { ApplyHeaderStyle(header); _headerStyleApplied = true; }
        if (!_tableStyleApplied)  { ApplyTableStyle(table);   _tableStyleApplied  = true; }

        if (_stolenPanelStyle == null) _stolenPanelStyle = BuildPanelStyle();
        panel.AddThemeStyleboxOverride("panel", _stolenPanelStyle);

        header.Text = headerText;
        table.Text  = tableText;

        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (panel.GetParent() != root)
        {
            panel.GetParent()?.RemoveChild(panel);
            root?.AddChild(panel);
        }

        if (!_followerInjected)
        {
            _followerInjected = true;
            var follower = new SlayTheStatsPositionFollower { Name = "SlayTheStatsFollower" };
            root?.AddChild(follower);
        }

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
            if (ShowGen != genAtHide || HasActiveHover) return;
            var node = GetPanelPublic();
            if (node != null) node.Visible = false;
        };
    }

    internal static void InvalidateStyles()
    {
        _headerStyleApplied = false;
        _tableStyleApplied  = false;
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

    private static void ApplyHeaderStyle(Label label)
    {
        label.AddThemeColorOverride("font_color", Fonts.Title);
        var font = Fonts.Bold ?? Fonts.Normal;
        if (font != null) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", Fonts.Size);
        label.AddThemeColorOverride("font_shadow_color", Fonts.Shadow);
        label.AddThemeConstantOverride("shadow_offset_x", Fonts.ShadowX);
        label.AddThemeConstantOverride("shadow_offset_y", Fonts.ShadowY);
        label.AddThemeConstantOverride("line_separation", Fonts.LineSep);
    }

    private static void ApplyTableStyle(Label label)
    {
        label.AddThemeColorOverride("font_color", Fonts.Text);
        var mono = ResourceLoader.Load<Font>("res://fonts/source_code_pro_medium.ttf");
        if (mono != null) label.AddThemeFontOverride("font", mono);
        label.AddThemeFontSizeOverride("font_size", Fonts.Size);
        label.AddThemeColorOverride("font_shadow_color", Fonts.Shadow);
        label.AddThemeConstantOverride("shadow_offset_x", Fonts.ShadowX);
        label.AddThemeConstantOverride("shadow_offset_y", Fonts.ShadowY);
        label.AddThemeConstantOverride("line_separation", Fonts.LineSep);
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
            sb.ContentMarginLeft   = 18f;
            sb.ContentMarginRight  = 18f;
            sb.ContentMarginTop    = 14f;
            sb.ContentMarginBottom = 14f;
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
        if (_lastTipWidth == null)
        {
            var sib = textContainer.GetChildren().OfType<Control>()
                .FirstOrDefault(c => c.Visible && c.Size.X > 0);
            if (sib != null) _lastTipWidth = sib.Size.X;
        }
        if (_lastTipWidth.HasValue) p.CustomMinimumSize = new Vector2(_lastTipWidth.Value, 0);

        int sep = textContainer.GetThemeConstant("separation");
        p.Position = new Vector2(
            textContainer.GlobalPosition.X,
            textContainer.GlobalPosition.Y + textContainer.Size.Y + sep);
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

        TooltipHelper.UpdatePanelPosition(p, textContainer);

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
                p.GlobalPosition.Y + p.Size.Y + sep);
        }
    }
}
