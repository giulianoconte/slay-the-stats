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
/// Displays runs offered, runs picked, pick rate, and win rate per act,
/// filtered to the current character and standard (solo) mode only.
/// </summary>
[HarmonyPatch(typeof(NCardHolder), "CreateHoverTips")]
public static class CardHoverShowPatch
{
    private static bool _warnedOnce;
    private static bool _headerStyleApplied;
    private static bool _tableStyleApplied;
    private static StyleBox? _stolenPanelStyle;
    private static bool _styleTheftScheduled;
    private static bool _signalConnected;
    private static bool _fontStealConnected;
    private static bool _panelCreated;
    private static float? _lastTipX; // X of last seen keyword tip panel column; used as fallback for non-keyword cards
    internal const string TooltipNodeName  = "SlayTheStatsTooltip";
    private const  string CanvasLayerName  = "SlayTheStatsLayer";

    // Font settings read from the game's keyword tooltip at runtime.
    // Defaults match values extracted via logging; updated dynamically on first keyword hover.
    private sealed class FontSettings
    {
        public Font? Bold;
        public Font? Normal;
        public int   Size       = 22;
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

    /// <summary>
    /// Cached from the run-start patch so we have the character during reward screen hover,
    /// where card.Owner is null (card not yet assigned to the player).
    /// </summary>
    internal static string? CurrentCharacter;

    static void Postfix(NCardHolder __instance)
    {
        try
        {
            // Only show on non-combat card screens (reward, shop, etc.).
            // NHandCardHolder is the in-combat player hand — skip it.
            if (__instance is NHandCardHolder) return;

            EnsurePanelExists();

            var rawId = GetRawCardId(__instance);
            if (rawId == null) return;

            var upgradeLevel = GetUpgradeLevel(__instance);
            var suffix = upgradeLevel > 0 ? "+" : "";

            // Run files store ids with "CARD." prefix; game model returns them without.
            // Try prefixed form first, then raw.
            var lookupId = MainFile.Db.Cards.ContainsKey("CARD." + rawId + suffix) ? "CARD." + rawId + suffix
                         : MainFile.Db.Cards.ContainsKey("CARD." + rawId)          ? "CARD." + rawId
                         : MainFile.Db.Cards.ContainsKey(rawId + suffix)           ? rawId + suffix
                         : MainFile.Db.Cards.ContainsKey(rawId)                    ? rawId
                         : null;

            if (lookupId == null) return;

            var contextMap = MainFile.Db.Cards[lookupId];
            var character = CurrentCharacter ?? GetCharacterFromOwner(__instance);
            if (character != null && CurrentCharacter == null)
                CurrentCharacter = character; // cache for reward screens where Owner is null

            var actStats = StatsAggregator.AggregateByAct(contextMap, character, gameMode: "standard");
            if (actStats.Count == 0) return;

            ConnectFontTheft();
            LogTipContainerState();
            var headerText = BuildHeaderText(character);
            var tableText  = BuildStatsText(actStats);
            ShowTooltip(__instance, headerText, tableText);
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"SlayTheStats: tooltip unavailable — game update may have changed the card hover API: {e.Message}");
                _warnedOnce = true;
            }
        }
    }

    /// <summary>
    /// Creates the persistent tooltip panel inside a CanvasLayer if it doesn't exist yet.
    /// The CanvasLayer keeps the panel in screen space (unaffected by camera/scroll),
    /// and its coordinate system matches the game's keyword tooltip panels.
    /// </summary>
    internal static void EnsurePanelExists()
    {
        if (_panelCreated) return;
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root == null) return;
        _panelCreated = true;

        var canvasLayer = new CanvasLayer();
        canvasLayer.Name  = CanvasLayerName;
        canvasLayer.Layer = 128;

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
        canvasLayer.AddChild(panel); // added before canvasLayer enters tree — safe

        root.CallDeferred(Node.MethodName.AddChild, canvasLayer);
    }

    private static Control? GetPanel()
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        return root?.GetNodeOrNull<Control>($"{CanvasLayerName}/{TooltipNodeName}");
    }

    internal static void HideTooltip()
    {
        var node = GetPanel();
        if (node != null) node.Visible = false;
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

    private static string BuildHeaderText(string? character)
    {
        var characterLabel = character != null
            ? char.ToUpper(character.Split('.').Last()[0]) + character.Split('.').Last().Substring(1).ToLower()
            : "All Characters";
        return $"SlayTheStats ({characterLabel}, Solo)";
    }

    private static string BuildStatsText(Dictionary<int, CardStat> actStats)
    {
        var sb = new StringBuilder();
        // No pipes; columns: Act(3) 2sp Picks(5) 3sp PR%(4) 2sp WR%(4)
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
        label.AddThemeColorOverride("font_color", _fonts.Text);
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
        // Keep monospace font for table alignment; use game size/shadow from _fonts.
        var mono = ResourceLoader.Load<Font>("res://fonts/source_code_pro_medium.ttf");
        if (mono != null) label.AddThemeFontOverride("font", mono);
        label.AddThemeFontSizeOverride("font_size", _fonts.Size);
        label.AddThemeColorOverride("font_shadow_color", _fonts.Shadow);
        label.AddThemeConstantOverride("shadow_offset_x", _fonts.ShadowX);
        label.AddThemeConstantOverride("shadow_offset_y", _fonts.ShadowY);
        label.AddThemeConstantOverride("line_separation", _fonts.LineSep);
    }

    /// Connects to textHoverTipContainer.child_entered_tree once and steals font
    /// settings from the first keyword panel that appears. Re-applies styles to our
    /// labels so they stay in sync if the game changes its fonts.
    private static void ConnectFontTheft()
    {
        if (_fontStealConnected) return;
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        var tipSet = FindNodeByTypeName(root, "NHoverTipSet");
        var textContainer = tipSet?.GetNodeOrNull<Node>("textHoverTipContainer");
        if (textContainer == null) return;
        _fontStealConnected = true;
        textContainer.Connect(Node.SignalName.ChildEnteredTree, Callable.From<Node>(panel =>
        {
            var desc = panel.GetNodeOrNull<Control>("TextContainer/VBoxContainer/Description");
            if (desc == null) return;
            var normal    = desc.GetThemeFont("normal_font");
            var bold      = desc.GetThemeFont("bold_font");
            var size      = desc.GetThemeFontSize("normal_font_size");
            var text      = desc.GetThemeColor("default_color");
            var shadow    = desc.GetThemeColor("font_shadow_color");
            var shadowX   = desc.GetThemeConstant("shadow_offset_x");
            var shadowY   = desc.GetThemeConstant("shadow_offset_y");
            var lineSep   = desc.GetThemeConstant("line_separation");
            if (normal == null && bold == null) return; // game returned defaults, nothing useful
            MainFile.Logger.Info($"[SlayTheStats] FontTheft: normal={normal?.ResourcePath} bold={bold?.ResourcePath} size={size} lineSep={lineSep}");
            _fonts = new FontSettings
            {
                Normal  = normal,
                Bold    = bold,
                Size    = size > 0 ? size : _fonts.Size,
                Text    = text != default ? text : _fonts.Text,
                Shadow  = shadow,
                ShadowX = shadowX,
                ShadowY = shadowY,
                LineSep = lineSep,
            };
            // Force re-apply on next tooltip show.
            _headerStyleApplied = false;
            _tableStyleApplied  = false;
        }));
        MainFile.Logger.Info("[SlayTheStats] ConnectFontTheft: connected");
    }

    /// Builds the panel style by loading the game's hover_tip texture directly.
    /// Falls back to a styled flat box if the texture isn't available.
    private static StyleBox BuildPanelStyle()
    {
        var tex = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png");
        MainFile.Logger.Info($"[SlayTheStats] BuildPanelStyle: hover_tip.png loaded={tex != null}");
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
        return MakeFallbackStyle();
    }

    private static StyleBoxFlat MakeFallbackStyle()
    {
        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.11f, 0.07f, 0.06f, 0.94f);
        style.BorderWidthBottom = 1;
        style.BorderWidthTop = 1;
        style.BorderWidthLeft = 1;
        style.BorderWidthRight = 1;
        style.BorderColor = new Color(0f, 0f, 0f, 0.55f);
        style.ContentMarginLeft = 8f;
        style.ContentMarginRight = 8f;
        style.ContentMarginTop = 6f;
        style.ContentMarginBottom = 6f;
        style.CornerRadiusTopLeft = 3;
        style.CornerRadiusTopRight = 3;
        style.CornerRadiusBottomLeft = 3;
        style.CornerRadiusBottomRight = 3;
        return style;
    }

    /// <summary>
    /// Steals the StyleBoxFlat used by the game's individual keyword tooltip panels
    /// (the stony gray panels for Block, Vulnerable etc).
    /// Those panels are children of NHoverTipSet/textHoverTipContainer, populated after
    /// CreateHoverTips returns — so this succeeds on the deferred retry, not the first call.
    /// Falls back to NHoverTipSet.panel (the outer container style) if no children are present yet.
    /// </summary>
    private static StyleBox? TryStealGamePanelStyle()
    {
        try
        {
            var root = (Engine.GetMainLoop() as SceneTree)?.Root;
            if (root == null) { MainFile.Logger.Info("[SlayTheStats] TrySteal: no SceneTree root"); return null; }

            var tipSet = FindNodeByTypeName(root, "NHoverTipSet");
            if (tipSet == null) { MainFile.Logger.Info("[SlayTheStats] TrySteal: NHoverTipSet not found"); return null; }

            // Prefer the individual keyword tip panels inside textHoverTipContainer.
            var textContainer = tipSet.GetNodeOrNull<Control>("textHoverTipContainer");
            MainFile.Logger.Info($"[SlayTheStats] TrySteal: textHoverTipContainer children={textContainer?.GetChildCount() ?? -1}");

            if (textContainer != null && textContainer.GetChildCount() > 0)
            {
                var tipPanel = textContainer.GetChild(0);
                MainFile.Logger.Info($"[SlayTheStats] TrySteal: tip panel type={tipPanel.GetType().Name} children={tipPanel.GetChildCount()}");

                // The panel uses NinePatchRect for rendering, not StyleBox.
                // Steal the "Bg" NinePatchRect texture and wrap it in a StyleBoxTexture.
                var bgRect = tipPanel.GetNodeOrNull<NinePatchRect>("Bg");
                MainFile.Logger.Info($"[SlayTheStats] TrySteal: Bg NinePatchRect found={bgRect != null} texture={bgRect?.Texture?.ResourcePath ?? "null"}");
                if (bgRect?.Texture != null)
                {
                    // Log font info from TextContainer label for text style diagnostics.
                    var tipTextContainer = tipPanel.GetNodeOrNull<Node>("TextContainer");
                    if (tipTextContainer != null)
                        foreach (var desc in tipTextContainer.GetChildren())
                            if (desc is Label lbl)
                            {
                                var font = lbl.GetThemeFont("font");
                                var size = lbl.GetThemeFontSize("font_size");
                                var color = lbl.GetThemeColor("font_color");
                                MainFile.Logger.Info($"[SlayTheStats] TipLabel: font={font?.ResourcePath ?? "null"} size={size} color={color}");
                            }

                    var stolen = new StyleBoxTexture();
                    stolen.Texture = bgRect.Texture;
                    stolen.TextureMarginLeft   = bgRect.PatchMarginLeft;
                    stolen.TextureMarginRight  = bgRect.PatchMarginRight;
                    stolen.TextureMarginTop    = bgRect.PatchMarginTop;
                    stolen.TextureMarginBottom = bgRect.PatchMarginBottom;
                    stolen.ContentMarginLeft   = 10f;
                    stolen.ContentMarginRight  = 10f;
                    stolen.ContentMarginTop    = 6f;
                    stolen.ContentMarginBottom = 6f;
                    MainFile.Logger.Info($"[SlayTheStats] TrySteal: built StyleBoxTexture patch={bgRect.PatchMarginLeft}/{bgRect.PatchMarginRight}/{bgRect.PatchMarginTop}/{bgRect.PatchMarginBottom}");
                    return stolen;
                }
            }

            MainFile.Logger.Info("[SlayTheStats] TrySteal: textHoverTipContainer empty, skipping outer fallback");
            return null;
        }
        catch (Exception e) { MainFile.Logger.Info($"[SlayTheStats] TrySteal threw: {e.Message}"); }
        return null;
    }

    private static Node? FindNodeByTypeName(Node node, string typeName)
    {
        if (node.GetType().Name == typeName) return node;
        foreach (var child in node.GetChildren())
        {
            var found = FindNodeByTypeName(child, typeName);
            if (found != null) return found;
        }
        return null;
    }

    private static bool _fontLogged;

    private static void LogTipContainerState()
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root == null) return;
        var tipSet = FindNodeByTypeName(root, "NHoverTipSet");
        if (tipSet == null) { MainFile.Logger.Info("[SlayTheStats] LogTipState: NHoverTipSet not found"); return; }
        var textContainer = tipSet.GetNodeOrNull<Node>("textHoverTipContainer");
        var count = textContainer?.GetChildCount() ?? -1;
        MainFile.Logger.Info($"[SlayTheStats] LogTipState: textHoverTipContainer children={count}");
        if (textContainer == null) return;
        foreach (var c in textContainer.GetChildren())
        {
            MainFile.Logger.Info($"[SlayTheStats] LogTipState:   child: {c.GetType().Name} \"{c.Name}\" vis={(c is CanvasItem ci ? ci.Visible : (bool?)null)}");
            if (_fontLogged) continue;
            // One-time: inspect label font/color inside first keyword tip panel.
            var tipText = c.GetNodeOrNull<Node>("TextContainer");
            MainFile.Logger.Info($"[SlayTheStats] TipText found={tipText != null} children={tipText?.GetChildCount() ?? -1}");
            if (tipText == null) continue;
            foreach (var desc in tipText.GetChildren())
            {
                MainFile.Logger.Info($"[SlayTheStats] TipText child: {desc.GetType().Name} \"{desc.Name}\"");
                var candidates = desc is Label ? new[] { desc } : desc.GetChildren().ToArray();
                foreach (var node in candidates)
                {
                    MainFile.Logger.Info($"[SlayTheStats] TipInner: {node.GetType().Name} \"{node.Name}\"");
                    if (node is Control ctrl2)
                    {
                        // Fonts
                        foreach (var key in new[] { "font", "normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font" })
                        {
                            var f = ctrl2.GetThemeFont(key);
                            if (f != null) MainFile.Logger.Info($"[SlayTheStats]   font[{key}]={f.ResourcePath}");
                        }
                        // Font sizes
                        foreach (var key in new[] { "font_size", "normal_font_size", "bold_font_size", "italics_font_size", "mono_font_size" })
                            MainFile.Logger.Info($"[SlayTheStats]   font_size[{key}]={ctrl2.GetThemeFontSize(key)}");
                        // Colors
                        foreach (var key in new[] { "font_color", "default_color", "font_shadow_color", "font_outline_color", "font_selected_color" })
                            MainFile.Logger.Info($"[SlayTheStats]   color[{key}]={ctrl2.GetThemeColor(key)}");
                        // Spacing / kerning constants
                        foreach (var key in new[] { "shadow_offset_x", "shadow_offset_y", "outline_size",
                                                    "spacing_glyph", "spacing_space", "spacing_top", "spacing_bottom",
                                                    "line_separation", "h_separation" })
                            MainFile.Logger.Info($"[SlayTheStats]   const[{key}]={ctrl2.GetThemeConstant(key)}");

                        // Raw text content (BBCode may override size)
                        var textProp = AccessTools.Property(ctrl2.GetType(), "Text")
                                    ?? AccessTools.Property(ctrl2.GetType(), "BbcodeText");
                        if (textProp != null)
                        {
                            var txt = textProp.GetValue(ctrl2) as string ?? "";
                            MainFile.Logger.Info($"[SlayTheStats]   text(200)={txt[..Math.Min(200, txt.Length)]}");
                        }
                    }
                }
                // Margins from the enclosing MarginContainer (the HoverTip panel itself)
                if (desc is MarginContainer mc)
                    foreach (var key in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                        MainFile.Logger.Info($"[SlayTheStats] HoverTip margin[{key}]={mc.GetThemeConstant(key)}");
                _fontLogged = true;
            }
        }
    }

    private static void TryStealFromNode(Node node)
    {
        // node is a MarginContainer from textHoverTipContainer; look for "Bg" NinePatchRect inside.
        var bgRect = node.GetNodeOrNull<NinePatchRect>("Bg");
        MainFile.Logger.Info($"[SlayTheStats] TryStealFromNode: Bg found={bgRect != null}");
        if (bgRect?.Texture == null) return;

        var stolen = new StyleBoxTexture();
        stolen.Texture = bgRect.Texture;
        stolen.TextureMarginLeft   = bgRect.PatchMarginLeft;
        stolen.TextureMarginRight  = bgRect.PatchMarginRight;
        stolen.TextureMarginTop    = bgRect.PatchMarginTop;
        stolen.TextureMarginBottom = bgRect.PatchMarginBottom;
        stolen.ContentMarginLeft   = bgRect.PatchMarginLeft   + 4f;
        stolen.ContentMarginRight  = bgRect.PatchMarginRight  + 4f;
        stolen.ContentMarginTop    = bgRect.PatchMarginTop    + 2f;
        stolen.ContentMarginBottom = bgRect.PatchMarginBottom + 2f;

        _stolenPanelStyle = stolen;
        MainFile.Logger.Info($"[SlayTheStats] TryStealFromNode: built StyleBoxTexture");
        var r = (Engine.GetMainLoop() as SceneTree)?.Root;
        var p = r?.GetNodeOrNull<Control>(TooltipNodeName);
        if (p != null && GodotObject.IsInstanceValid(p))
            p.AddThemeStyleboxOverride("panel", stolen);
    }

    /// Recursively finds the first PanelContainer with a non-null "panel" StyleBox.
    private static StyleBox? FindPanelContainerStylebox(Node node)
    {
        if (node is PanelContainer pc)
        {
            var sb = pc.GetThemeStylebox("panel");
            if (sb != null) { MainFile.Logger.Info($"[SlayTheStats] FindPanelContainer: found on {pc.GetType().Name} \"{pc.Name}\" = {sb.GetType().Name}"); return sb; }
        }
        foreach (var child in node.GetChildren())
        {
            var found = FindPanelContainerStylebox(child);
            if (found != null) return found;
        }
        return null;
    }

    /// Recursively searches a node tree for any non-null StyleBox on a Control node.
    private static StyleBox? FindDeepStylebox(Node node)
    {
        if (node is Control ctrl)
        {
            foreach (var key in new[] { "panel", "background", "bg", "normal" })
            {
                var sb = ctrl.GetThemeStylebox(key);
                if (sb != null) return sb;
            }
        }
        foreach (var child in node.GetChildren())
        {
            var found = FindDeepStylebox(child);
            if (found != null) return found;
        }
        return null;
    }


    private static void ShowTooltip(NCardHolder anchor, string headerText, string tableText)
    {
        var root  = (Engine.GetMainLoop() as SceneTree)?.Root;
        var panel = GetPanel();
        if (panel == null) return;

        var header = panel.GetNodeOrNull<Label>("VBoxContainer/HeaderLabel");
        var table  = panel.GetNodeOrNull<Label>("VBoxContainer/StatsLabel");
        if (header == null || table == null) return;

        if (!_headerStyleApplied) { ApplyHeaderStyle(header); _headerStyleApplied = true; }
        if (!_tableStyleApplied)  { ApplyTableStyle(table);   _tableStyleApplied  = true; }

        if (_stolenPanelStyle == null)
            _stolenPanelStyle = BuildPanelStyle();
        panel.AddThemeStyleboxOverride("panel", _stolenPanelStyle);

        header.Text = headerText;
        table.Text  = tableText;
        panel.Visible = false;

        var tipSet        = FindNodeByTypeName(root, "NHoverTipSet");
        var textContainer = tipSet?.GetNodeOrNull<Container>("textHoverTipContainer");

        // Local helpers — capture panel and textContainer.
        void placeFromPanels(List<Control> vis)
        {
            if (!GodotObject.IsInstanceValid(panel)) return;
            float bottomEdge = float.MinValue, leftEdge = 0;
            foreach (var c in vis)
            {
                var b = c.GlobalPosition.Y + c.Size.Y;
                MainFile.Logger.Info($"[SlayTheStats] Place child: pos={c.GlobalPosition} size={c.Size} bottom={b}");
                if (b > bottomEdge) { bottomEdge = b; leftEdge = c.GlobalPosition.X; }
            }
            _lastTipX = leftEdge;
            MainFile.Logger.Info($"[SlayTheStats] Place: below panels ({leftEdge}, {bottomEdge + 6})");
            panel.GlobalPosition = new Vector2(leftEdge, bottomEdge + 6);
            panel.Visible = true;
        }

        void placeFromFallback()
        {
            if (!GodotObject.IsInstanceValid(panel)) return;
            float x = _lastTipX ?? (textContainer != null && GodotObject.IsInstanceValid(textContainer)
                ? textContainer.GlobalPosition.X : 0);
            float y = textContainer != null && GodotObject.IsInstanceValid(textContainer)
                ? textContainer.GlobalPosition.Y : 0;
            MainFile.Logger.Info($"[SlayTheStats] Place: fallback ({x}, {y})");
            panel.GlobalPosition = new Vector2(x, y);
            panel.Visible = true;
        }

        List<Control> VisiblePanels() =>
            textContainer?.GetChildren().OfType<Control>().Where(c => c.Visible).ToList()
            ?? new List<Control>();

        bool LayoutDone(List<Control> vis) =>
            vis.Count <= 1 || vis.Select(c => c.GlobalPosition.Y).Distinct().Count() > 1;

        if (textContainer == null)
        {
            // No NHoverTipSet found — defer one frame and use cached/fallback position.
            (Engine.GetMainLoop() as SceneTree)?.Connect(SceneTree.SignalName.ProcessFrame,
                Callable.From(placeFromFallback), (uint)GodotObject.ConnectFlags.OneShot);
            return;
        }

        var vis0 = VisiblePanels();
        if (vis0.Count > 0 && LayoutDone(vis0))
        {
            // Second hover: panels already exist and are fully laid out.
            // Wait one ProcessFrame so our panel doesn't flicker at the old position.
            (Engine.GetMainLoop() as SceneTree)?.Connect(SceneTree.SignalName.ProcessFrame,
                Callable.From(() => placeFromPanels(VisiblePanels())),
                (uint)GodotObject.ConnectFlags.OneShot);
        }
        else if (vis0.Count > 0)
        {
            // Panels exist but layout hasn't run — use sort_children to know when it's done.
            textContainer.Connect("sort_children", Callable.From(() => placeFromPanels(VisiblePanels())),
                (uint)GodotObject.ConnectFlags.OneShot);
        }
        else
        {
            // No panels yet (first hover or no-keyword card).
            // Poll for up to 3 frames; once panels appear use sort_children, otherwise fall back.
            var waits = new[] { 0 };
            Action poll = null!;
            poll = () =>
            {
                if (!GodotObject.IsInstanceValid(panel)) return;
                var vis = VisiblePanels();
                if (vis.Count > 0)
                    textContainer.Connect("sort_children", Callable.From(() => placeFromPanels(VisiblePanels())),
                        (uint)GodotObject.ConnectFlags.OneShot);
                else if (waits[0]++ < 3)
                    (Engine.GetMainLoop() as SceneTree)?.Connect(SceneTree.SignalName.ProcessFrame,
                        Callable.From(poll), (uint)GodotObject.ConnectFlags.OneShot);
                else
                    placeFromFallback();
            };
            (Engine.GetMainLoop() as SceneTree)?.Connect(SceneTree.SignalName.ProcessFrame,
                Callable.From(poll), (uint)GodotObject.ConnectFlags.OneShot);
        }
    }

    /// Connects to textHoverTipContainer.child_entered_tree so we steal the style
    /// the first time any keyword tooltip panel (Block, Vulnerable, etc.) is added —
    /// regardless of timing or which card triggered it.
    private static void TryConnectChildSignal()
    {
        if (_signalConnected || _stolenPanelStyle != null) return;

        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root == null) { MainFile.Logger.Info("[SlayTheStats] TryConnectChildSignal: no root"); return; }

        var tipSet = FindNodeByTypeName(root, "NHoverTipSet");
        if (tipSet == null) { MainFile.Logger.Info("[SlayTheStats] TryConnectChildSignal: NHoverTipSet not found"); return; }

        var textContainer = tipSet.GetNodeOrNull<Node>("textHoverTipContainer");
        if (textContainer == null) { MainFile.Logger.Info("[SlayTheStats] TryConnectChildSignal: textHoverTipContainer not found"); return; }

        // Dump current NHoverTipSet child structure for diagnostics.
        MainFile.Logger.Info($"[SlayTheStats] NHoverTipSet direct children: {tipSet.GetChildCount()}");
        foreach (var c in tipSet.GetChildren())
            MainFile.Logger.Info($"[SlayTheStats]   child: {c.GetType().Name} \"{c.Name}\" kids={c.GetChildCount()} vis={(c is CanvasItem ci ? ci.Visible : (bool?)null)}");

        // Log existing children of textHoverTipContainer.
        MainFile.Logger.Info($"[SlayTheStats] textHoverTipContainer children now: {textContainer.GetChildCount()}");
        foreach (var c in textContainer.GetChildren())
            MainFile.Logger.Info($"[SlayTheStats]   tipChild: {c.GetType().Name} \"{c.Name}\" vis={(c is CanvasItem ci2 ? ci2.Visible : (bool?)null)}");

        _signalConnected = true;

        // Connect to textHoverTipContainer directly.
        textContainer.Connect(Node.SignalName.ChildEnteredTree, Callable.From<Node>(child =>
        {
            MainFile.Logger.Info($"[SlayTheStats] textContainer.child_entered_tree: type={child.GetType().Name} \"{child.Name}\"");
            TryStealFromNode(child);
        }));

        // Also connect to NHoverTipSet itself in case panels are added at that level.
        tipSet.Connect(Node.SignalName.ChildEnteredTree, Callable.From<Node>(child =>
        {
            MainFile.Logger.Info($"[SlayTheStats] tipSet.child_entered_tree: type={child.GetType().Name} \"{child.Name}\"");
            TryStealFromNode(child);
        }));

        MainFile.Logger.Info("[SlayTheStats] TryConnectChildSignal: connected to both textContainer and tipSet");
    }

    /// Schedules a one-frame deferred attempt to steal the style from textHoverTipContainer
    /// children. Only sets _stolenPanelStyle if real keyword panel children are found —
    /// never from the NHoverTipSet outer-container fallback. Diagnostic-only otherwise.
    private static void ScheduleStyleTheft(Control panel)
    {
        if (_styleTheftScheduled || _stolenPanelStyle != null) return;
        _styleTheftScheduled = true;
        (Engine.GetMainLoop() as SceneTree)?.Connect(
            SceneTree.SignalName.ProcessFrame,
            Callable.From(() =>
            {
                _styleTheftScheduled = false;
                if (_stolenPanelStyle != null) return;
                MainFile.Logger.Info("[SlayTheStats] Deferred style theft firing");
                var style = TryStealGamePanelStyle();
                if (style == null) { MainFile.Logger.Info("[SlayTheStats] Deferred theft: no style from children"); return; }
                _stolenPanelStyle = style;
                MainFile.Logger.Info($"[SlayTheStats] Deferred theft: stole {style.GetType().Name}");
                var root = (Engine.GetMainLoop() as SceneTree)?.Root;
                var p = root?.GetNodeOrNull<Control>(TooltipNodeName);
                if (p != null && GodotObject.IsInstanceValid(p))
                    p.AddThemeStyleboxOverride("panel", style);
            }),
            (uint)GodotObject.ConnectFlags.OneShot);
    }
}

[HarmonyPatch(typeof(NCardHolder), "ClearHoverTips")]
public static class CardHoverHidePatch
{
    static void Postfix(NCardHolder __instance)
    {
        try { CardHoverShowPatch.HideTooltip(); }
        catch { }
    }
}

/// <summary>
/// Safety net: hides the tooltip if the card node is returned to the pool
/// (e.g. when a card is picked and the reward screen closes) without going through ClearHoverTips.
/// </summary>
[HarmonyPatch(typeof(NCard), "OnFreedToPool")]
public static class CardFreedToPoolPatch
{
    static void Postfix()
    {
        try { CardHoverShowPatch.HideTooltip(); }
        catch { }
    }
}

/// <summary>
/// Caches the current character when a run starts so the tooltip can filter by it,
/// since reward screen cards don't have an Owner set yet.
/// Cleared when returning to the main menu.
/// </summary>
[HarmonyPatch(typeof(NGame), "ReturnToMainMenuAfterRun")]
public static class ClearCurrentCharacterPatch
{
    static void Prefix()
    {
        CardHoverShowPatch.CurrentCharacter = null;
    }
}
