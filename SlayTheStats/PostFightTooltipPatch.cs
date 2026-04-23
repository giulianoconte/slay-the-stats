using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using System.Reflection;
using System.Text;

namespace SlayTheStats;

// ─────────────────────────────────────────────────────────────────────
// Post-fight comparison tooltip.
//
// After combat ends and the reward screen appears, shows a brief
// tooltip comparing this fight's damage, turns, and potions against
// the player's historical medians/averages for the same encounter.
// Dismissed by clicking the panel or automatically when the player
// leaves the reward screen (TreeExiting on NRewardsScreen).
//
// Data is derived entirely from the RunState's MapPointHistory — the
// same source the game uses for its own floor stats on the map. This
// works identically whether the fight just happened or the player
// resumed a save that landed on the reward screen.
// ─────────────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
public static class PostFightTooltipPatch
{
    private const float TopMarginPx = 20f;
    private const float RightMarginPx = 20f;

    private static PanelContainer? _panel;
    private static RichTextLabel? _nameLabel;
    private static RichTextLabel? _tableLabel;
    private static Label? _brandLabel;
    private static NinePatchRect? _shadow;
    private static bool _warnedOnce;
    internal static bool _dismissed;
    // Tracks how many NOverlayStack-driven overlays (map, deck, pause, etc.)
    // are currently on top of the reward screen. HideOverlays/ShowOverlays fire
    // per push/pop, so nested overlays (map → deck → back to map) would
    // otherwise cause the tooltip to reappear over the map when the deck pops.
    // Only restore visibility when the counter returns to 0.
    internal static int _overlayDepth;

    static void Postfix(NRewardsScreen __instance)
    {
        if (SlayTheStatsConfig.DisableTooltipsEntirely) return;
        if (!CardHoverShowPatch.IsInRun) return;

        try
        {
            var fightData = ExtractFightData(__instance);
            if (fightData == null)
            {
                if (SlayTheStatsConfig.DebugMode)
                    MainFile.Logger.Info("[SlayTheStats] PostFight: no fight data extracted from reward screen");
                return;
            }

            if (SlayTheStatsConfig.DebugMode)
                MainFile.Logger.Info($"[SlayTheStats] PostFight: {fightData.EncounterId} dmg={fightData.DamageTaken} turns={fightData.TurnsTaken} pots={fightData.PotionsUsed}");

            var statsText = BuildComparisonText(fightData);
            if (statsText == null)
            {
                if (SlayTheStatsConfig.DebugMode)
                    MainFile.Logger.Info("[SlayTheStats] PostFight: no historical data to compare against");
                return;
            }

            Show(__instance, fightData.EncounterName, statsText);
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"[SlayTheStats] Post-fight tooltip failed: {e.Message}");
                _warnedOnce = true;
            }
        }
    }

    private sealed class FightData
    {
        public required string EncounterId;
        public required string EncounterName;
        public int DamageTaken;
        public int TurnsTaken;
        public int PotionsUsed;
        public int MaxHp;
    }

    private static FightData? ExtractFightData(NRewardsScreen instance)
    {
        var runState = AccessTools.Field(typeof(NRewardsScreen), "_runState")?.GetValue(instance);
        if (runState == null) return null;

        var currentEntry = runState.GetType()
            .GetProperty("CurrentMapPointHistoryEntry", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(runState);
        if (currentEntry == null) return null;

        // Find the combat room in this floor's rooms
        var rooms = currentEntry.GetType()
            .GetProperty("Rooms", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(currentEntry) as System.Collections.IList;
        if (rooms == null || rooms.Count == 0) return null;

        string? encounterId = null;
        int turnsTaken = 0;

        foreach (var room in rooms)
        {
            if (room == null) continue;
            var modelId = room.GetType()
                .GetProperty("ModelId", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(room);
            if (modelId == null) continue;

            var modelIdStr = modelId.ToString();
            if (modelIdStr == null || !modelIdStr.StartsWith("ENCOUNTER.")) continue;

            encounterId = modelIdStr.ToUpper();
            turnsTaken = (int)(room.GetType()
                .GetProperty("TurnsTaken", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(room) ?? 0);
            break;
        }

        if (encounterId == null) return null;
        if (!MainFile.Db.Encounters.ContainsKey(encounterId)) return null;

        // Read player stats for this floor
        var playerStats = currentEntry.GetType()
            .GetProperty("PlayerStats", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(currentEntry) as System.Collections.IList;
        if (playerStats == null || playerStats.Count == 0) return null;

        var ps = playerStats[0];
        if (ps == null) return null;

        int damageTaken = (int)(ps.GetType()
            .GetProperty("DamageTaken", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(ps) ?? 0);
        int maxHp = (int)(ps.GetType()
            .GetProperty("MaxHp", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(ps) ?? 1);

        var potionUsedList = ps.GetType()
            .GetProperty("PotionUsed", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(ps) as System.Collections.IList;
        int potionsUsed = potionUsedList?.Count ?? 0;

        return new FightData
        {
            EncounterId = encounterId,
            EncounterName = EncounterCategory.FormatName(encounterId),
            DamageTaken = damageTaken,
            TurnsTaken = turnsTaken,
            PotionsUsed = potionsUsed,
            MaxHp = maxHp,
        };
    }

    private static string? BuildComparisonText(FightData fight)
    {
        if (!MainFile.Db.Encounters.TryGetValue(fight.EncounterId, out var contextMap))
            return null;

        var character = CardHoverShowPatch.RunCharacter;
        var filter = BuildFilter(character);
        var actStats = StatsAggregator.AggregateEncountersByAct(contextMap, filter);

        var combined = new EncounterEvent();
        foreach (var stat in actStats.Values)
            EncounterTooltipHelper.Accumulate(combined, stat);

        if (combined.Fought == 0) return null;

        double? medianDmg = combined.DamageMedian();
        double avgDmg = (double)combined.DamageTakenSum / combined.Fought;
        double avgTurns = (double)combined.TurnsTakenSum / combined.Fought;
        double avgPots = (double)combined.PotionsUsedSum / combined.Fought;

        int n = combined.Fought;
        double dmgBaseline = medianDmg ?? avgDmg;
        double? dmgPct  = EncounterEvent.PercentileRank(combined.DamageValues, fight.DamageTaken);
        double? turnPct = EncounterEvent.PercentileRank(combined.TurnsValues, fight.TurnsTaken);
        double? potPct  = EncounterEvent.PercentileRank(combined.PotionsValues, fight.PotionsUsed);

        var sb = new StringBuilder();
        sb.Append("[table=4]");

        // Header
        sb.Append(LabelCell(""));
        sb.Append(HeaderCell(""));
        sb.Append(HeaderCell(L.T("postfight.col.vs_hist")));
        sb.Append(HeaderCell(L.T("postfight.col.pctile")));

        // Damage: raw | delta vs median | percentile
        sb.Append(LabelCell(L.T("postfight.row.damage")));
        sb.Append(ValueCell($"{fight.DamageTaken}"));
        sb.Append(DeltaCell(fight.DamageTaken - dmgBaseline, highIsBad: true, n));
        sb.Append(PercentileCell(dmgPct));

        // Turns: raw | delta vs avg | percentile
        sb.Append(LabelCell(L.T("tooltip.col.turns")));
        sb.Append(ValueCell($"{fight.TurnsTaken}"));
        sb.Append(DeltaCell(fight.TurnsTaken - avgTurns, highIsBad: true, n));
        sb.Append(PercentileCell(turnPct));

        // Potions: raw | delta vs avg | percentile
        sb.Append(LabelCell(L.T("postfight.row.pots")));
        sb.Append(ValueCell($"{fight.PotionsUsed}"));
        sb.Append(DeltaCell(fight.PotionsUsed - avgPots, highIsBad: true, n));
        sb.Append(PercentileCell(potPct));

        // Runs: total fights fed into this comparison. No delta / pctile —
        // this is context, not a per-fight metric. Dulled + blank trailing
        // cells so the row reads as reference rather than a comparison line.
        sb.Append(DimLabelCell(L.T("tooltip.col.runs")));
        sb.Append(DimValueCell($"{combined.Fought}"));
        sb.Append(EmptyCell());
        sb.Append(EmptyCell());

        sb.Append("[/table]");

        // Footer
        var charLabel = character != null
            ? CardHoverShowPatch.GetCharacterDisplay(character)
            : L.T("filter.all_characters");
        var filterCtx = CardHoverShowPatch.BuildFilterContext(charLabel, filter);
        sb.Append(TooltipHelper.FormatFooter(filterCtx));

        return sb.ToString();
    }

    // padding=L,T,R,B. Vertical at 0 so row height is just the font line-height;
    // anything above 0 compounds across 5 rows into visible empty bands.
    private const string CellPad = "padding=6,0,2,0 expand=1";

    private static string HeaderCell(string text) =>
        $"[cell {CellPad}][right][color={ThemeStyle.HeaderGrey}]{text}[/color][/right][/cell]";

    private static string LabelCell(string text) =>
        $"[cell {CellPad}][color={ThemeStyle.Cream}]{text}[/color][/cell]";

    private static string ValueCell(string text) =>
        $"[cell {CellPad}][right][color={TooltipHelper.NeutralShade}]{text}[/color][/right][/cell]";

    // Dulled cells for context rows (e.g. Runs) that shouldn't draw the eye
    // the same way as per-metric comparison rows. Matches the bestiary's
    // dimmer-grey convention for baseline / reference rows.
    private static string DimLabelCell(string text) =>
        $"[cell {CellPad}][color={ThemeStyle.FooterGrey}]{text}[/color][/cell]";

    private static string DimValueCell(string text) =>
        $"[cell {CellPad}][right][color={ThemeStyle.FooterGrey}]{text}[/color][/right][/cell]";

    private static string EmptyCell() =>
        $"[cell {CellPad}][/cell]";

    private static string PercentileCell(double? pctile)
    {
        if (pctile == null)
            return $"[cell {CellPad}][right][color={TooltipHelper.NeutralShade}]—[/color][/right][/cell]";
        return $"[cell {CellPad}][right][color={TooltipHelper.NeutralShade}]p{pctile:F0}[/color][/right][/cell]";
    }

    private static string DeltaCell(double delta, bool highIsBad, int sampleSize)
    {
        string sign = delta >= 0 ? "+" : "";
        string formatted = $"{sign}{delta:F1}";

        string color;
        if (sampleSize <= 3 || Math.Abs(delta) < 0.05)
        {
            color = TooltipHelper.NeutralShade;
        }
        else if (highIsBad)
        {
            color = delta > 0 ? "#e8a060" : "#60c8a0";
        }
        else
        {
            color = delta > 0 ? "#60c8a0" : "#e8a060";
        }

        return $"[cell {CellPad}][right][color={color}]{formatted}[/color][/right][/cell]";
    }

    private static void Show(NRewardsScreen rewardsScreen, string encounterName, string statsText)
    {
        Dismiss();

        TooltipHelper.TrySceneTheftOnce();

        if (_panel == null || !GodotObject.IsInstanceValid(_panel))
            BuildPanel();

        if (_panel == null) return;

        // Parent the panel to the game's HoverTipsContainer — the same parent
        // that NHoverTipSet children (card previews) go into on hover. With
        // a shared parent, any hover-tip added after us becomes a later
        // sibling, which Godot renders on top by default tree order. Prior
        // attempts to fight this with ZIndex alone didn't work: the hover-tip
        // container apparently sits in a rendering context where our Root-
        // attached ZIndex=50 panel still occluded card hover previews.
        //
        // Falls back to Root if HoverTipsContainer is unavailable (e.g. pre-
        // _Ready), which keeps the existing single-panel show-path intact.
        var container = NGame.Instance?.HoverTipsContainer ?? rewardsScreen.GetTree()?.Root;
        if (container == null) return;

        // Add shadow first so it renders behind the panel (shadow has a +6,+6
        // offset — it's a drop shadow and must be underneath). Without this
        // ordering the shadow draws over the panel because they share a
        // ZIndex and sibling order decides the tie.
        if (_shadow != null && _shadow.GetParent() != container)
        {
            _shadow.GetParent()?.RemoveChild(_shadow);
            container.AddChild(_shadow);
        }
        if (_panel.GetParent() != container)
        {
            _panel.GetParent()?.RemoveChild(_panel);
            container.AddChild(_panel);
        }

        _nameLabel!.Text = $"[b]{encounterName}[/b]";
        _tableLabel!.Text = statsText;
        _nameLabel.ResetSize();
        _tableLabel.ResetSize();
        _panel.ResetSize();

        // Start transparent; PositionTopRight tweens alpha + x into place so the
        // panel fades and slides in from the right (~0.2s).
        _panel.Visible = true;
        _panel.Modulate = new Color(1f, 1f, 1f, 0f);
        if (_shadow != null)
        {
            _shadow.Visible = true;
            _shadow.Modulate = new Color(0f, 0f, 0f, 0f);
        }

        _dismissed = false;
        // Reset overlay depth to a clean baseline each time the tooltip is shown.
        // Any stale counter from a previous fight shouldn't influence show/hide here.
        _overlayDepth = 0;

        // Auto-hide when the rewards screen leaves the tree (player moves to next floor)
        rewardsScreen.TreeExiting += Dismiss;

        // Position in top-right after a deferred frame so the panel has measured its size
        Callable.From(() => PositionTopRight(rewardsScreen)).CallDeferred();
    }

    private static void PositionTopRight(NRewardsScreen rewardsScreen)
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;

        var viewportSize = _panel.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        var panelSize = _panel.Size;

        float targetX = viewportSize.X - panelSize.X - RightMarginPx;
        float targetY = (viewportSize.Y - panelSize.Y) * 0.5f;

        const float SlideOffsetPx = 40f;
        const float AnimDuration  = 0.4f;
        const float ShadowAlpha   = 0.25098f;

        // Snap to the slid-out, transparent starting pose, then tween in.
        _panel.GlobalPosition = new Vector2(targetX + SlideOffsetPx, targetY);

        if (_shadow != null && GodotObject.IsInstanceValid(_shadow))
        {
            _shadow.GlobalPosition = new Vector2(targetX + SlideOffsetPx + 6, targetY + 6);
            _shadow.Size = panelSize;
        }

        var tween = _panel.CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(_panel, "global_position:x", targetX, AnimDuration)
             .SetTrans(Tween.TransitionType.Cubic)
             .SetEase(Tween.EaseType.Out);
        tween.TweenProperty(_panel, "modulate:a", 1.0f, AnimDuration);

        if (_shadow != null && GodotObject.IsInstanceValid(_shadow))
        {
            tween.TweenProperty(_shadow, "global_position:x", targetX + 6, AnimDuration)
                 .SetTrans(Tween.TransitionType.Cubic)
                 .SetEase(Tween.EaseType.Out);
            tween.TweenProperty(_shadow, "modulate:a", ShadowAlpha, AnimDuration);
        }
    }

    private static void Dismiss()
    {
        _dismissed = true;
        SetVisible(false);
    }

    internal static void SetVisible(bool visible)
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
            _panel.Visible = visible;
        if (_shadow != null && GodotObject.IsInstanceValid(_shadow))
            _shadow.Visible = visible;
    }

    private static void BuildPanel()
    {
        var kreonBold = TooltipHelper.GetKreonBoldFont();
        var kreonRegular = TooltipHelper.GetKreonFont();

        _panel = new PanelContainer();
        _panel.Name = "PostFightTooltip";
        _panel.Visible = false;
        _panel.MouseFilter = Control.MouseFilterEnum.Stop;
        _panel.GuiInput += (InputEvent @event) =>
        {
            if (@event is InputEventMouseButton { Pressed: true })
                Dismiss();
        };
        _panel.CustomMinimumSize = new Vector2(TooltipHelper.TooltipWidth, 0);
        _panel.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);
        // Parent is NGame.HoverTipsContainer (see Show), so rendering order
        // within that container is governed by tree order — anything added
        // after us (e.g. a card hover's NHoverTipSet) naturally draws on top.
        // ZIndex stays at the default 0: HoverTipsContainer sits above the
        // NOverlayStack backstop (so card hover tips aren't dimmed on reward
        // screens), and a negative ZIndex here would push us behind that
        // backstop and make the panel read as dimmed.
        _panel.ZIndex = 0;

        var vbox = new VBoxContainer();
        vbox.Name = "PostFightVBox";
        vbox.AddThemeConstantOverride("separation", 2);
        vbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        vbox.CustomMinimumSize = new Vector2(TooltipHelper.TooltipWidth - 22f, 0);
        _panel.AddChild(vbox);

        // Header row. 22 is just enough for the 20pt Kreon name + its shadow;
        // any taller and the gap to the table's first row reads as empty space.
        const int HeaderHeightPx = 22;
        const int BrandRightPadPx = 24;
        var headerRow = new Control();
        headerRow.Name = "HeaderRow";
        headerRow.CustomMinimumSize = new Vector2(0, HeaderHeightPx);
        headerRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.MouseFilter = Control.MouseFilterEnum.Ignore;
        vbox.AddChild(headerRow);

        _nameLabel = new RichTextLabel();
        _nameLabel.Name = "PostFightNameLabel";
        _nameLabel.BbcodeEnabled = true;
        _nameLabel.FitContent = true;
        _nameLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        _nameLabel.ClipContents = false;
        _nameLabel.ScrollActive = false;
        _nameLabel.SelectionEnabled = false;
        _nameLabel.ShortcutKeysEnabled = false;
        _nameLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _nameLabel.AnchorLeft = 0f;
        _nameLabel.AnchorTop = 0f;
        _nameLabel.AnchorRight = 0f;
        _nameLabel.AnchorBottom = 1f;
        if (kreonBold != null) _nameLabel.AddThemeFontOverride("bold_font", kreonBold);
        if (kreonRegular != null) _nameLabel.AddThemeFontOverride("normal_font", kreonRegular);
        _nameLabel.AddThemeFontSizeOverride("normal_font_size", 20);
        _nameLabel.AddThemeFontSizeOverride("bold_font_size", 20);
        _nameLabel.AddThemeColorOverride("default_color", ThemeStyle.GoldColor);
        TooltipHelper.ApplyTooltipShadow(_nameLabel);
        headerRow.AddChild(_nameLabel);

        _brandLabel = new Label();
        _brandLabel.Name = "SlayTheStatsBrand";
        _brandLabel.Text = "SlayTheStats";
        _brandLabel.AddThemeColorOverride("font_color", ThemeStyle.FooterGreyColor);
        _brandLabel.AddThemeFontSizeOverride("font_size", ThemeStyle.BrandSize);
        _brandLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        if (kreonRegular != null) _brandLabel.AddThemeFontOverride("font", kreonRegular);
        TooltipHelper.ApplyTooltipShadow(_brandLabel);
        _brandLabel.AnchorLeft = 1f;
        _brandLabel.AnchorRight = 1f;
        _brandLabel.AnchorTop = 0f;
        _brandLabel.AnchorBottom = 0f;
        _brandLabel.GrowHorizontal = Control.GrowDirection.Begin;
        _brandLabel.OffsetRight = -BrandRightPadPx;
        _brandLabel.VerticalAlignment = VerticalAlignment.Top;
        headerRow.AddChild(_brandLabel);

        _tableLabel = new RichTextLabel();
        _tableLabel.Name = "PostFightStatsLabel";
        _tableLabel.BbcodeEnabled = true;
        _tableLabel.FitContent = true;
        _tableLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _tableLabel.CustomMinimumSize = new Vector2(258, 0);
        _tableLabel.ScrollActive = false;
        _tableLabel.SelectionEnabled = false;
        _tableLabel.ShortcutKeysEnabled = false;
        _tableLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _tableLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var tableFont = TooltipHelper.GetKreonFont();
        if (tableFont != null)
        {
            _tableLabel.AddThemeFontOverride("normal_font", tableFont);
            var tableBold = TooltipHelper.GetKreonBoldFont();
            if (tableBold != null) _tableLabel.AddThemeFontOverride("bold_font", tableBold);
        }
        _tableLabel.AddThemeFontSizeOverride("normal_font_size", 18);
        _tableLabel.AddThemeFontSizeOverride("bold_font_size", 18);
        _tableLabel.AddThemeColorOverride("default_color", ThemeStyle.CreamColor);
        _tableLabel.AddThemeConstantOverride("line_separation", 0);
        TooltipHelper.ApplyTooltipShadow(_tableLabel);
        vbox.AddChild(_tableLabel);

        var style = TooltipHelper.BuildPanelStyle();
        if (style != null)
            _panel.AddThemeStyleboxOverride("panel", style);

        _shadow = new NinePatchRect();
        _shadow.Name = "PostFightShadow";
        _shadow.Texture = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png");
        _shadow.PatchMarginLeft = 55;
        _shadow.PatchMarginRight = 91;
        _shadow.PatchMarginTop = 43;
        _shadow.PatchMarginBottom = 32;
        _shadow.Modulate = new Color(0f, 0f, 0f, 0.25098f);
        _shadow.ZIndex = 0;
        _shadow.Visible = false;
        _shadow.MouseFilter = Control.MouseFilterEnum.Ignore;
    }

    private static AggregationFilter BuildFilter(string? character)
    {
        var filter = SlayTheStatsConfig.BuildSafeFilterFromDefaults();
        filter.Character = character;
        return filter;
    }
}

[HarmonyPatch(typeof(NOverlayStack), nameof(NOverlayStack.HideOverlays))]
public static class PostFightOverlayHidePatch
{
    static void Postfix()
    {
        PostFightTooltipPatch._overlayDepth++;
        if (!PostFightTooltipPatch._dismissed)
            PostFightTooltipPatch.SetVisible(false);
        if (SlayTheStatsConfig.DebugMode)
            MainFile.Logger.Info($"[SlayTheStats] PostFight overlay Hide: depth={PostFightTooltipPatch._overlayDepth}");
    }
}

[HarmonyPatch(typeof(NOverlayStack), nameof(NOverlayStack.ShowOverlays))]
public static class PostFightOverlayShowPatch
{
    static void Postfix()
    {
        if (PostFightTooltipPatch._overlayDepth > 0)
            PostFightTooltipPatch._overlayDepth--;
        // Only reshow when we're back to bedrock (no overlays on top of the reward
        // screen). Without the depth check, closing a nested overlay (deck stacked
        // over map) would un-hide the tooltip while the outer overlay (map) is still
        // covering the reward screen.
        if (!PostFightTooltipPatch._dismissed && PostFightTooltipPatch._overlayDepth == 0)
            PostFightTooltipPatch.SetVisible(true);
        if (SlayTheStatsConfig.DebugMode)
            MainFile.Logger.Info($"[SlayTheStats] PostFight overlay Show: depth={PostFightTooltipPatch._overlayDepth}");
    }
}
