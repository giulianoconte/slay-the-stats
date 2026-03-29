using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace SlayTheStats;

/// <summary>
/// Shows a card stats tooltip when a card is hovered on a reward screen (or any non-combat screen).
/// Only fires for NGridCardHolder (reward/shop/etc.) — NHandCardHolder (in-combat hand) is skipped.
///
/// The panel lives as a child of the game's textHoverTipContainer so that scroll tracking is
/// handled automatically. However textHoverTipContainer is a VFlowContainer, so we must
/// override our local position after its layout pass rather than relying on auto-layout.
/// </summary>
[HarmonyPatch(typeof(NCardHolder), "CreateHoverTips")]
public static class CardHoverShowPatch
{
    private static bool         _warnedOnce;
    private static bool         _headerStyleApplied;
    private static bool         _tableStyleApplied;
    private static StyleBox?    _stolenPanelStyle;
    private static bool         _fontStealConnected;
    private static bool         _sortConnected;
    private static float?       _lastTipWidth;
    private static NCardHolder? _activeHolder;
    private static Control?     _panel;
    private static int          _showGen;   // incremented by ShowTooltip; checked by hide timers
    internal const string TooltipNodeName = "SlayTheStatsTooltip";

    private sealed class FontSettings
    {
        public Font? Bold;
        public Font? Normal;
        public int   Size       = 22;
        public Color Title      = new Color(0.988f, 0.784f, 0.184f, 1f); // golden yellow default
        public Color Text       = new Color(1f, 0.9647f, 0.8863f, 1f);
        public Color Shadow     = new Color(0f, 0f, 0f, 0.251f);
        public int   ShadowX    = 3;
        public int   ShadowY    = 2;
        public int   LineSep    = -2;
    }
    private static FontSettings _fonts = new()
    {
        Bold   = ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres"),
        Normal = ResourceLoader.Load<Font>("res://themes/kreon_regular_glyph_space_one.tres"),
    };

    internal static string? CurrentCharacter;

    static void Postfix(NCardHolder __instance)
    {
        try
        {
            if (__instance is NHandCardHolder) return;

            EnsurePanelExists();

            var rawId = GetRawCardId(__instance);
            if (rawId == null) return;

            var upgradeLevel = GetUpgradeLevel(__instance);
            var suffix = upgradeLevel > 0 ? "+" : "";

            var lookupId = MainFile.Db.Cards.ContainsKey("CARD." + rawId + suffix) ? "CARD." + rawId + suffix
                         : MainFile.Db.Cards.ContainsKey("CARD." + rawId)          ? "CARD." + rawId
                         : MainFile.Db.Cards.ContainsKey(rawId + suffix)           ? rawId + suffix
                         : MainFile.Db.Cards.ContainsKey(rawId)                    ? rawId
                         : null;

            if (lookupId == null) return;

            var contextMap = MainFile.Db.Cards[lookupId];
            var character = CurrentCharacter ?? GetCharacterFromOwner(__instance);
            if (character != null && CurrentCharacter == null)
                CurrentCharacter = character;

            var actStats = StatsAggregator.AggregateByAct(contextMap, character, gameMode: "standard");
            if (actStats.Count == 0) return;

            ConnectFontTheft();
            ShowTooltip(__instance, BuildHeaderText(character), BuildStatsText(actStats));
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"SlayTheStats: tooltip unavailable — {e.Message}");
                _warnedOnce = true;
            }
        }
    }

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

    private static Control? GetPanel() => GetPanelPublic();

    internal static bool IsActiveHover() => _activeHolder != null;

    internal static void HideTooltip(NCardHolder? source = null)
    {
        var skip = source != null && source != _activeHolder;
        MainFile.Logger.Info($"[SlayTheStats] HideTooltip: source={source?.GetHashCode()} active={_activeHolder?.GetHashCode()} skip={skip}");
        if (skip) return;

        _activeHolder = null;

        // Use a short timer so quick card-to-card moves don't flash the panel off.
        // ShowTooltip increments _showGen; if the gen advances before the timer fires,
        // a new hover started and we cancel the hide.
        var genAtHide = _showGen;
        var st = (Engine.GetMainLoop() as SceneTree);
        var timer = st?.CreateTimer(0.05f);  // 50 ms — invisible at 60 fps, long enough to bridge frame gaps
        if (timer != null)
        {
            timer.Timeout += () =>
            {
                var cancel = _showGen != genAtHide || _activeHolder != null;
                MainFile.Logger.Info($"[SlayTheStats] HideTimer fired: genAtHide={genAtHide} showGen={_showGen} active={_activeHolder?.GetHashCode()} cancel={cancel}");
                if (cancel) return;
                var node = GetPanel();
                if (node != null) node.Visible = false;
            };
        }
    }

    private static string? GetRawCardId(NCardHolder holder)
    {
        var cardNode = AccessTools.Property(typeof(NCardHolder), "CardNode")?.GetValue(holder);
        if (cardNode == null) return null;
        var model = AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode);
        if (model == null) return null;
        var id = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
        if (id == null) return null;
        return AccessTools.Field(id.GetType(), "Entry")?.GetValue(id) as string
            ?? AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string
            ?? id.ToString();
    }

    private static int GetUpgradeLevel(NCardHolder holder)
    {
        try
        {
            var cardNode = AccessTools.Property(typeof(NCardHolder), "CardNode")?.GetValue(holder);
            var model = cardNode != null ? AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode) : null;
            if (model == null) return 0;
            return AccessTools.Property(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                ?? AccessTools.Field(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                ?? 0;
        }
        catch { return 0; }
    }

    private static string? GetCharacterFromOwner(NCardHolder holder)
    {
        try
        {
            var cardNode = AccessTools.Property(typeof(NCardHolder), "CardNode")?.GetValue(holder);
            var model = cardNode != null ? AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode) : null;
            var owner = model != null ? AccessTools.Property(model.GetType(), "Owner")?.GetValue(model) : null;
            return owner != null ? AccessTools.Property(owner.GetType(), "CharacterId")?.GetValue(owner) as string : null;
        }
        catch { return null; }
    }

    private static string BuildHeaderText(string? character) => "SlayTheStats";

    private static string BuildStatsText(Dictionary<int, CardStat> actStats)
    {
        var sb = new StringBuilder();
        sb.Append("Act  Picks    PR%   WR%\n");
        for (int act = 1; act <= 3; act++)
        {
            if (actStats.TryGetValue(act, out var stat))
            {
                var pr      = stat.RunsOffered > 0 ? $"{Math.Round(100.0 * stat.RunsPicked / stat.RunsOffered):F0}%" : "-";
                var wr      = stat.RunsPicked  > 0 ? $"{Math.Round(100.0 * stat.RunsWon    / stat.RunsPicked):F0}%"  : "-";
                var pickOff = $"{stat.RunsPicked}/{stat.RunsOffered}";
                sb.Append($"{act,3}  {pickOff,5}   {pr,4}  {wr,4}\n");
            }
            else
            {
                sb.Append($"{act,3}      -      -     -\n");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static void ApplyHeaderStyle(Label label)
    {
        label.AddThemeColorOverride("font_color", _fonts.Title);
        var font = _fonts.Bold ?? _fonts.Normal;
        if (font != null) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", _fonts.Size);
        label.AddThemeColorOverride("font_shadow_color", _fonts.Shadow);
        label.AddThemeConstantOverride("shadow_offset_x", _fonts.ShadowX);
        label.AddThemeConstantOverride("shadow_offset_y", _fonts.ShadowY);
        label.AddThemeConstantOverride("line_separation", _fonts.LineSep);
    }

    private static void ApplyTableStyle(Label label)
    {
        label.AddThemeColorOverride("font_color", _fonts.Text);
        var mono = ResourceLoader.Load<Font>("res://fonts/source_code_pro_medium.ttf");
        if (mono != null) label.AddThemeFontOverride("font", mono);
        label.AddThemeFontSizeOverride("font_size", _fonts.Size);
        label.AddThemeColorOverride("font_shadow_color", _fonts.Shadow);
        label.AddThemeConstantOverride("shadow_offset_x", _fonts.ShadowX);
        label.AddThemeConstantOverride("shadow_offset_y", _fonts.ShadowY);
        label.AddThemeConstantOverride("line_separation", _fonts.LineSep);
    }

    private static void ConnectFontTheft()
    {
        if (_fontStealConnected) return;
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        var tipSet = FindNodeByTypeName(root, "NHoverTipSet");
        var textContainer = tipSet?.GetNodeOrNull<Node>("textHoverTipContainer");
        if (textContainer == null) return;
        _fontStealConnected = true;

        textContainer.Connect(Node.SignalName.ChildEnteredTree, Callable.From<Node>(tipPanel =>
        {
            if (tipPanel.Name == TooltipNodeName) return;

            // Log the full child structure of this tip panel once, to find the title node.
            MainFile.Logger.Info($"[SlayTheStats] TipPanel: type={tipPanel.GetType().Name} children={tipPanel.GetChildCount()}");
            foreach (var child in tipPanel.GetChildren())
                MainFile.Logger.Info($"[SlayTheStats]   child: {child.GetType().Name} \"{child.Name}\" kids={child.GetChildCount()}");
            var vbox = tipPanel.GetNodeOrNull<Node>("TextContainer/VBoxContainer");
            if (vbox != null)
                foreach (var child in vbox.GetChildren())
                    MainFile.Logger.Info($"[SlayTheStats]   vbox child: {child.GetType().Name} \"{child.Name}\"");

            // Steal body text style from Description.
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

            // Try to steal title color from a sibling Label (the keyword name label).
            var titleColor = _fonts.Title;
            if (vbox != null)
            {
                foreach (var child in vbox.GetChildren())
                {
                    if (child.Name == "Description") continue;
                    if (child is Control ctrl)
                    {
                        var c = ctrl.GetThemeColor("font_color");
                        if (c != default)
                        {
                            titleColor = c;
                            MainFile.Logger.Info($"[SlayTheStats] FontTheft: title node=\"{child.Name}\" color={c}");
                        }
                    }
                }
            }

            MainFile.Logger.Info($"[SlayTheStats] FontTheft: size={size} lineSep={lineSep} titleColor={titleColor}");
            _fonts = new FontSettings
            {
                Normal = normal, Bold = bold,
                Size   = size > 0 ? size : _fonts.Size,
                Title  = titleColor,
                Text   = text != default ? text : _fonts.Text,
                Shadow = shadow, ShadowX = shadowX, ShadowY = shadowY, LineSep = lineSep,
            };
            _headerStyleApplied = false;
            _tableStyleApplied  = false;
        }));
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

    private static void ShowTooltip(NCardHolder anchor, string headerText, string tableText)
    {
        _showGen++;  // cancel any pending hide timers
        var panel = GetPanel();
        if (panel == null) return;

        var header = panel.GetNodeOrNull<Label>("VBoxContainer/HeaderLabel");
        var table  = panel.GetNodeOrNull<Label>("VBoxContainer/StatsLabel");
        if (header == null || table == null) return;

        if (!_headerStyleApplied) { ApplyHeaderStyle(header); _headerStyleApplied = true; }
        if (!_tableStyleApplied)  { ApplyTableStyle(table);   _tableStyleApplied  = true; }

        if (_stolenPanelStyle == null) _stolenPanelStyle = BuildPanelStyle();
        panel.AddThemeStyleboxOverride("panel", _stolenPanelStyle);

        header.Text   = headerText;
        table.Text    = tableText;
        _activeHolder = anchor;

        // Panel lives as a direct child of root — never subject to VFlowContainer layout.
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (panel.GetParent() != root)
        {
            panel.GetParent()?.RemoveChild(panel);
            root?.AddChild(panel);
        }

        // Inject the position-follower node once. It updates panel.Position every frame,
        // handling scroll and screen transitions without any extra patches.
        if (!_sortConnected)
        {
            _sortConnected = true;
            var follower = new SlayTheStatsPositionFollower { Name = "SlayTheStatsFollower" };
            root?.AddChild(follower);
            MainFile.Logger.Info("[SlayTheStats] PositionFollower injected");
        }

        panel.Visible = true;
        MainFile.Logger.Info($"[SlayTheStats] ShowTooltip: anchor={anchor.GetHashCode()}");
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

    private static Node? FindNodeByTypeName(Node? node, string typeName)
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

[HarmonyPatch(typeof(NCardHolder), "ClearHoverTips")]
public static class CardHoverHidePatch
{
    static void Postfix(NCardHolder __instance)
    {
        try { CardHoverShowPatch.HideTooltip(__instance); }
        catch { }
    }
}

/// <summary>
/// Safety net: hides the panel when a card is returned to the pool without going through
/// ClearHoverTips. Skips if there is an active hover so it doesn't clobber a concurrent Show.
/// </summary>
[HarmonyPatch(typeof(NCard), "OnFreedToPool")]
public static class CardFreedToPoolPatch
{
    static void Postfix()
    {
        try
        {
            var node = CardHoverShowPatch.GetPanelPublic();
            if (node == null || !node.Visible) return;
            if (CardHoverShowPatch.IsActiveHover()) return;  // an active hover owns the panel
            MainFile.Logger.Info("[SlayTheStats] FreedToPool: hiding panel (no active hover)");
            node.Visible = false;
        }
        catch { }
    }
}

[HarmonyPatch(typeof(NGame), "ReturnToMainMenuAfterRun")]
public static class ClearCurrentCharacterPatch
{
    static void Prefix()
    {
        CardHoverShowPatch.CurrentCharacter = null;
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
        var p = CardHoverShowPatch.GetPanelPublic();
        if (p == null || !p.Visible) return;

        var root          = (Engine.GetMainLoop() as SceneTree)?.Root;
        var tipSet        = CardHoverShowPatch.FindTipSet(root);
        var textContainer = tipSet?.GetNodeOrNull<Container>("textHoverTipContainer");
        if (textContainer == null) return;

        CardHoverShowPatch.UpdatePanelPosition(p, textContainer);
    }
}
