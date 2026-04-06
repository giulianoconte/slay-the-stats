using BaseLib.Utils;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SlayTheStats;

// ─────────────────────────────────────────────────────────────────────
// Register our bestiary submenu type with the game's submenu stack
// (same pattern BaseLib uses for NModConfigSubmenu)
// ─────────────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(NMainMenuSubmenuStack), nameof(NMainMenuSubmenuStack.GetSubmenuType), typeof(Type))]
public static class InjectBestiarySubmenuPatch
{
    private static readonly SpireField<NMainMenuSubmenuStack, NBestiaryStatsSubmenu> SubmenuField = new(CreateSubmenu);

    private static NBestiaryStatsSubmenu CreateSubmenu(NMainMenuSubmenuStack stack)
    {
        var menu = new NBestiaryStatsSubmenu();
        menu.Visible = false;
        ((Node)stack).AddChild(menu);
        return menu;
    }

    public static bool Prefix(NMainMenuSubmenuStack __instance, Type type, ref NSubmenu __result)
    {
        if (type != typeof(NBestiaryStatsSubmenu)) return true;
        __result = SubmenuField.Get(__instance)!;
        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────
// Inject "Stats Bestiary" button into the compendium submenu
// ─────────────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(NCompendiumSubmenu), "_Ready")]
public static class BestiaryButtonPatch
{
    private static bool _warnedOnce;

    static void Postfix(NCompendiumSubmenu __instance)
    {
        try
        {
            var gameBestiaryButton = ((Node)__instance).GetNodeOrNull<Control>("%BestiaryButton");
            if (gameBestiaryButton == null) return;

            var buttonParent = ((Node)gameBestiaryButton).GetParent();
            if (buttonParent == null) return;

            // Small wrapper with label + button, visually separated from the game's grid
            var wrapper = new VBoxContainer();
            wrapper.Name = "SlayTheStatsBestiaryWrapper";
            wrapper.AddThemeConstantOverride("separation", 4);

            var label = new Label();
            label.Text = "SlayTheStats";
            label.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f, 0.7f));
            label.AddThemeFontSizeOverride("font_size", 14);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            wrapper.AddChild(label);

            var ourButton = new Button();
            ourButton.Name = "SlayTheStatsBestiaryButton";
            ourButton.Text = "Stats Bestiary";
            ourButton.CustomMinimumSize = new Vector2(0, 44);

            var normalStyle = new StyleBoxFlat();
            normalStyle.BgColor = new Color(0.15f, 0.16f, 0.22f, 0.90f);
            normalStyle.BorderColor = new Color(0.45f, 0.50f, 0.60f, 1f);
            normalStyle.SetBorderWidthAll(1);
            normalStyle.SetCornerRadiusAll(4);
            normalStyle.ContentMarginLeft = 12;
            normalStyle.ContentMarginRight = 12;
            normalStyle.ContentMarginTop = 6;
            normalStyle.ContentMarginBottom = 6;
            ourButton.AddThemeStyleboxOverride("normal", normalStyle);

            var hoverStyle = (StyleBoxFlat)normalStyle.Duplicate();
            hoverStyle.BgColor = new Color(0.20f, 0.22f, 0.30f, 0.95f);
            ourButton.AddThemeStyleboxOverride("hover", hoverStyle);

            ourButton.AddThemeColorOverride("font_color", new Color(0.95f, 0.93f, 0.88f, 1f));
            ourButton.AddThemeColorOverride("font_hover_color", new Color(0.918f, 0.745f, 0.318f, 1f));
            ourButton.AddThemeFontSizeOverride("font_size", 18);
            wrapper.AddChild(ourButton);

            int bestiaryIdx = gameBestiaryButton.GetIndex();
            buttonParent.AddChild(wrapper);
            buttonParent.MoveChild(wrapper, bestiaryIdx);

            ourButton.Pressed += () =>
            {
                // Get the submenu stack via reflection on _stack (protected field on NSubmenu)
                var stackField = AccessTools.Field(typeof(NSubmenu), "_stack");
                if (stackField?.GetValue(__instance) is NMainMenuSubmenuStack stack)
                {
                    var submenu = stack.PushSubmenuType<NBestiaryStatsSubmenu>();
                    submenu.Refresh();
                }
            };
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"[SlayTheStats] BestiaryButtonPatch failed: {e.Message}");
                _warnedOnce = true;
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────
// The bestiary submenu page — extends NSubmenu for native back button
// ─────────────────────────────────────────────────────────────────────

public partial class NBestiaryStatsSubmenu : NSubmenu
{
    private HBoxContainer? _biomeTabRow;
    private VBoxContainer? _encounterList;
    private RichTextLabel? _statsLabel;
    private Control? _renderArea;
    private string? _selectedBiome;
    private string? _hoveredEncounterId;
    private bool _built;

    private const float ContentWidth = 1012f;

    protected override Control InitialFocusedControl => _biomeTabRow ?? (Control)this;

    public NBestiaryStatsSubmenu()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;
    }

    public override void _Ready()
    {
        BuildUI();

        // Add the game's native back button
        var backButton = PreloadManager.Cache.GetScene(SceneHelper.GetScenePath("ui/back_button")).Instantiate<NBackButton>();
        backButton.Name = "BackButton";
        AddChild(backButton);

        ConnectSignals();
    }

    private void BuildUI()
    {
        if (_built) return;
        _built = true;

        // Outer margin: extra left padding so the back button (in the slightly-below-middle-left
        // position) doesn't overlap the encounter list
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 200);
        margin.AddThemeConstantOverride("margin_right", 40);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        AddChild(margin);

        var outerVbox = new VBoxContainer();
        outerVbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(outerVbox);

        // ── Title ──
        var title = new Label();
        title.Text = "Bestiary — Encounter Stats";
        title.AddThemeColorOverride("font_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        title.AddThemeFontSizeOverride("font_size", 26);
        outerVbox.AddChild(title);

        outerVbox.AddChild(new HSeparator());

        // ── Biome tabs ──
        _biomeTabRow = new HBoxContainer();
        _biomeTabRow.AddThemeConstantOverride("separation", 8);
        outerVbox.AddChild(_biomeTabRow);

        outerVbox.AddChild(new HSeparator());

        // ── Main content: left list | separator | right detail ──
        var contentSplit = new HBoxContainer();
        contentSplit.AddThemeConstantOverride("separation", 16);
        contentSplit.SizeFlagsVertical = SizeFlags.ExpandFill;
        outerVbox.AddChild(contentSplit);

        // Left: scrollable encounter name list (wider stretch ratio pushes the divider right)
        var leftScroll = new ScrollContainer();
        leftScroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        leftScroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        leftScroll.SizeFlagsStretchRatio = 1.4f;
        contentSplit.AddChild(leftScroll);

        _encounterList = new VBoxContainer();
        _encounterList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _encounterList.AddThemeConstantOverride("separation", 1);
        leftScroll.AddChild(_encounterList);

        contentSplit.AddChild(new VSeparator());

        // Right panel: stats (fixed height) + horizontal separator (fixed) + render area (fills rest)
        var rightPanel = new VBoxContainer();
        rightPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
        rightPanel.SizeFlagsStretchRatio = 1.0f;
        rightPanel.AddThemeConstantOverride("separation", 8);
        contentSplit.AddChild(rightPanel);

        // Stats label — wrapped in a fixed-height container so the divider below it doesn't move
        var statsBox = new MarginContainer();
        statsBox.CustomMinimumSize = new Vector2(0, 240);
        statsBox.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        rightPanel.AddChild(statsBox);

        _statsLabel = new RichTextLabel();
        _statsLabel.BbcodeEnabled = true;
        _statsLabel.FitContent = true;
        _statsLabel.ScrollActive = false;
        _statsLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _statsLabel.SizeFlagsVertical = SizeFlags.ExpandFill;
        _statsLabel.AddThemeColorOverride("default_color", new Color(0.95f, 0.93f, 0.88f, 1f));
        _statsLabel.AddThemeFontSizeOverride("normal_font_size", 18);
        _statsLabel.AddThemeFontSizeOverride("bold_font_size", 18);
        // Explicit line separation prevents bold rows from getting taller line height than regular rows
        _statsLabel.AddThemeConstantOverride("line_separation", 0);
        _statsLabel.Text = $"[color=#606060]Hover an encounter to see stats[/color]";

        TooltipHelper.TryLoadModFonts();
        var monoFont = TooltipHelper.GetMonoFont();
        if (monoFont != null)
        {
            _statsLabel.AddThemeFontOverride("normal_font", monoFont);
            var boldFont = TooltipHelper.GetMonoBoldFont();
            if (boldFont != null) _statsLabel.AddThemeFontOverride("bold_font", boldFont);
        }
        statsBox.AddChild(_statsLabel);

        rightPanel.AddChild(new HSeparator());

        // Render area — fills remaining vertical space
        _renderArea = new PanelContainer();
        _renderArea.SizeFlagsVertical = SizeFlags.ExpandFill;
        _renderArea.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var renderStyle = new StyleBoxFlat();
        renderStyle.BgColor = new Color(0.04f, 0.05f, 0.07f, 1f);
        renderStyle.SetBorderWidthAll(1);
        renderStyle.BorderColor = new Color(0.20f, 0.22f, 0.28f, 0.5f);
        renderStyle.SetCornerRadiusAll(4);
        ((PanelContainer)_renderArea).AddThemeStyleboxOverride("panel", renderStyle);
        rightPanel.AddChild(_renderArea);
    }

    // ───────────────────────── Public API ─────────────────────────

    public void Refresh()
    {
        if (_selectedBiome == null)
            _selectedBiome = GetDefaultBiome();
        RefreshEncounterList();
    }

    // ───────────────────────── Refresh Logic ─────────────────────────

    private void RefreshEncounterList()
    {
        if (_encounterList == null) return;

        while (_encounterList.GetChildCount() > 0)
        {
            var child = _encounterList.GetChild(0);
            _encounterList.RemoveChild(child);
            child.QueueFree();
        }

        if (_statsLabel != null)
            _statsLabel.Text = $"[color=#606060]Hover an encounter to see stats[/color]";
        _hoveredEncounterId = null;

        RebuildBiomeTabs();

        var encounters = GetEncountersForBiome(_selectedBiome);
        var categories = new[] { "weak", "normal", "elite", "boss", "event", "unknown" };

        foreach (var category in categories)
        {
            var catEncounters = encounters
                .Where(e => MainFile.Db.EncounterMeta.TryGetValue(e, out var meta) && meta.Category == category)
                .OrderBy(e => EncounterCategory.FormatName(e))
                .ToList();

            if (catEncounters.Count == 0) continue;

            var catRow = new HBoxContainer();
            catRow.CustomMinimumSize = new Vector2(0, 30);
            catRow.MouseFilter = MouseFilterEnum.Stop;
            var catLabel = new RichTextLabel();
            catLabel.BbcodeEnabled = true;
            catLabel.FitContent = true;
            catLabel.ScrollActive = false;
            catLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            catLabel.MouseFilter = MouseFilterEnum.Ignore;
            catLabel.AddThemeFontSizeOverride("normal_font_size", 19);
            catLabel.AddThemeFontSizeOverride("bold_font_size", 19);
            catLabel.Text = $"[b][color=#a0a0a0]{EncounterCategory.FormatCategory(category)}[/color][/b]";
            catRow.AddChild(catLabel);

            // Hover the category header to show pool-aggregate stats
            var capturedCategory = category;
            var capturedEncIds = catEncounters;
            catRow.MouseEntered += () => OnCategoryHover(capturedCategory, capturedEncIds);
            catRow.MouseExited += OnEncounterUnhover;

            _encounterList.AddChild(catRow);

            foreach (var encId in catEncounters)
                _encounterList.AddChild(BuildEncounterEntry(encId));
        }
    }

    private void RebuildBiomeTabs()
    {
        if (_biomeTabRow == null) return;

        while (_biomeTabRow.GetChildCount() > 0)
        {
            var child = _biomeTabRow.GetChild(0);
            _biomeTabRow.RemoveChild(child);
            child.QueueFree();
        }

        var biomes = GetBiomes();
        foreach (var biome in biomes)
        {
            bool selected = biome == _selectedBiome;

            var btn = new Button();
            btn.Text = FormatBiomeName(biome);
            btn.CustomMinimumSize = new Vector2(140, 36);

            var tabStyle = new StyleBoxFlat();
            tabStyle.BgColor = selected
                ? new Color(0.20f, 0.22f, 0.30f, 1f)
                : new Color(0.10f, 0.11f, 0.15f, 1f);
            tabStyle.BorderColor = selected
                ? new Color(0.918f, 0.745f, 0.318f, 0.8f)
                : new Color(0.30f, 0.33f, 0.40f, 0.8f);
            tabStyle.SetBorderWidthAll(1);
            tabStyle.SetCornerRadiusAll(4);
            tabStyle.ContentMarginLeft = 10;
            tabStyle.ContentMarginRight = 10;
            tabStyle.ContentMarginTop = 4;
            tabStyle.ContentMarginBottom = 4;
            btn.AddThemeStyleboxOverride("normal", tabStyle);

            var hoverTab = (StyleBoxFlat)tabStyle.Duplicate();
            hoverTab.BgColor = new Color(0.25f, 0.27f, 0.35f, 1f);
            btn.AddThemeStyleboxOverride("hover", hoverTab);

            btn.AddThemeColorOverride("font_color",
                selected ? new Color(0.918f, 0.745f, 0.318f, 1f) : new Color(0.80f, 0.78f, 0.72f, 1f));
            btn.AddThemeColorOverride("font_hover_color", new Color(0.918f, 0.745f, 0.318f, 1f));
            btn.AddThemeFontSizeOverride("font_size", 18);

            var capturedBiome = biome;
            btn.Pressed += () =>
            {
                _selectedBiome = capturedBiome;
                RefreshEncounterList();
            };
            _biomeTabRow.AddChild(btn);
        }
    }

    // ───────────────────────── Encounter Rows ─────────────────────────

    private Control BuildEncounterEntry(string encounterId)
    {
        var row = new HBoxContainer();
        row.CustomMinimumSize = new Vector2(0, 26);
        row.MouseFilter = MouseFilterEnum.Stop;

        var nameLabel = new RichTextLabel();
        nameLabel.BbcodeEnabled = true;
        nameLabel.FitContent = true;
        nameLabel.ScrollActive = false;
        nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameLabel.MouseFilter = MouseFilterEnum.Ignore;
        nameLabel.AddThemeColorOverride("default_color", new Color(0.90f, 0.88f, 0.82f, 1f));
        nameLabel.AddThemeFontSizeOverride("normal_font_size", 17);

        var encounterName = EncounterCategory.FormatName(encounterId);
        string monsterInfo = "";
        if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
        {
            var monsterCounts = meta.MonsterIds
                .GroupBy(m => m)
                .Select(g => g.Count() > 1 ? $"{FormatMonsterName(g.Key)} x{g.Count()}" : FormatMonsterName(g.Key));
            monsterInfo = $"  [color=#505050]({string.Join(", ", monsterCounts)})[/color]";
        }
        nameLabel.Text = $"   {encounterName}{monsterInfo}";
        row.AddChild(nameLabel);

        row.MouseEntered += () => OnEncounterHover(encounterId);
        row.MouseExited += OnEncounterUnhover;

        return row;
    }

    // ───────────────────────── Hover Logic ─────────────────────────

    private void OnEncounterHover(string encounterId)
    {
        _hoveredEncounterId = encounterId;
        UpdateStatsPanel(encounterId);
        RenderMonsterPreview(encounterId);
    }

    private void OnCategoryHover(string category, List<string> encounterIds)
    {
        if (_statsLabel == null) return;

        var filter = BuildFilter();

        // Aggregate per-character across all encounters in this category
        var combined = new Dictionary<string, EncounterEvent>();
        foreach (var encId in encounterIds)
        {
            if (!MainFile.Db.Encounters.TryGetValue(encId, out var contextMap)) continue;

            var perChar = StatsAggregator.AggregateEncountersByCharacter(contextMap, filter);
            foreach (var (charId, stat) in perChar)
            {
                if (!combined.TryGetValue(charId, out var agg))
                {
                    agg = new EncounterEvent();
                    combined[charId] = agg;
                }
                agg.Fought           += stat.Fought;
                agg.Died             += stat.Died;
                agg.WonRun           += stat.WonRun;
                agg.TurnsTakenSum    += stat.TurnsTakenSum;
                agg.DamageTakenSum   += stat.DamageTakenSum;
                agg.DamageTakenSqSum += stat.DamageTakenSqSum;
                agg.HpEnteringSum    += stat.HpEnteringSum;
                agg.MaxHpSum         += stat.MaxHpSum;
                agg.PotionsUsedSum   += stat.PotionsUsedSum;
                agg.DmgPctSum        += stat.DmgPctSum;
                agg.DmgPctSqSum      += stat.DmgPctSqSum;
            }
        }

        var categoryLabel = EncounterCategory.FormatCategory(category);
        double deathRateBaseline = StatsAggregator.GetEncounterDeathRateBaseline(MainFile.Db, filter, category);
        double dmgPctBaseline = StatsAggregator.GetEncounterDmgPctBaseline(MainFile.Db, filter, category);

        var statsText = EncounterTooltipHelper.BuildEncounterStatsText(
            combined, deathRateBaseline, dmgPctBaseline,
            filter.AscensionMin, filter.AscensionMax, categoryLabel);

        var biomeName = _selectedBiome != null ? FormatBiomeName(_selectedBiome) : "";
        _statsLabel.Text = $"[b]All {categoryLabel} Encounters — {biomeName}[/b]\n{statsText}";

        // Clear the monster preview area when hovering a category (no specific monsters to show)
        ClearMonsterPreview();
    }

    private void OnEncounterUnhover()
    {
        _hoveredEncounterId = null;
        if (_statsLabel != null)
            _statsLabel.Text = $"[color=#606060]Hover an encounter to see stats[/color]";
        ClearMonsterPreview();
    }

    private void UpdateStatsPanel(string encounterId)
    {
        if (_statsLabel == null) return;

        if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap))
        {
            _statsLabel.Text = $"[b]{EncounterCategory.FormatName(encounterId)}[/b]\n[color=#606060]No data[/color]";
            return;
        }

        var filter = BuildFilter();
        var charStats = StatsAggregator.AggregateEncountersByCharacter(contextMap, filter);

        string? category = null;
        if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
            category = meta.Category;

        var categoryLabel = category != null ? EncounterCategory.FormatCategory(category) : "";

        double deathRateBaseline = StatsAggregator.GetEncounterDeathRateBaseline(MainFile.Db, filter, category);
        double dmgPctBaseline = StatsAggregator.GetEncounterDmgPctBaseline(MainFile.Db, filter, category);

        string statsText;
        if (charStats.Count == 0)
            statsText = EncounterTooltipHelper.NoDataText(null, filter.AscensionMin, filter.AscensionMax);
        else
            statsText = EncounterTooltipHelper.BuildEncounterStatsText(
                charStats, deathRateBaseline, dmgPctBaseline,
                filter.AscensionMin, filter.AscensionMax, categoryLabel);

        _statsLabel.Text = $"[b]{EncounterCategory.FormatName(encounterId)}[/b]\n{statsText}";
    }

    // ───────────────────────── Monster Preview ─────────────────────────

    private void RenderMonsterPreview(string encounterId)
    {
        ClearMonsterPreview();
        if (_renderArea == null) return;
        if (!MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta)) return;

        // TODO: implement on-hover Spine rendering via SubViewport
        var label = new Label();
        label.Name = "MonsterPreviewLabel";
        label.Text = string.Join("   ", meta.MonsterIds.Select(FormatMonsterName));
        label.AddThemeColorOverride("font_color", new Color(0.45f, 0.45f, 0.45f, 0.6f));
        label.AddThemeFontSizeOverride("font_size", 16);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SizeFlagsVertical = SizeFlags.ExpandFill;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ((Node)_renderArea).AddChild(label);
    }

    private void ClearMonsterPreview()
    {
        if (_renderArea == null) return;
        var preview = ((Node)_renderArea).GetNodeOrNull("MonsterPreviewLabel");
        if (preview != null)
        {
            ((Node)_renderArea).RemoveChild(preview);
            preview.QueueFree();
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static AggregationFilter BuildFilter()
    {
        var filter = new AggregationFilter();
        if (SlayTheStatsConfig.AscensionMin > 0)
            filter.AscensionMin = SlayTheStatsConfig.AscensionMin;
        if (SlayTheStatsConfig.AscensionMax < 20)
            filter.AscensionMax = SlayTheStatsConfig.AscensionMax;
        if (!string.IsNullOrEmpty(SlayTheStatsConfig.VersionMin))
            filter.VersionMin = SlayTheStatsConfig.VersionMin;
        if (!string.IsNullOrEmpty(SlayTheStatsConfig.VersionMax))
            filter.VersionMax = SlayTheStatsConfig.VersionMax;
        if (!string.IsNullOrEmpty(SlayTheStatsConfig.FilterProfile))
            filter.Profile = SlayTheStatsConfig.FilterProfile;
        return filter;
    }

    private static List<string> GetBiomes()
    {
        var biomeAct = new Dictionary<string, int>();
        foreach (var meta in MainFile.Db.EncounterMeta.Values)
        {
            if (!string.IsNullOrEmpty(meta.Biome) && !biomeAct.ContainsKey(meta.Biome))
                biomeAct[meta.Biome] = meta.Act;
        }
        return biomeAct.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
    }

    private static string? GetDefaultBiome()
    {
        var biomes = GetBiomes();
        return biomes.Count > 0 ? biomes[0] : null;
    }

    private static List<string> GetEncountersForBiome(string? biome)
    {
        if (biome == null) return MainFile.Db.EncounterMeta.Keys.ToList();
        return MainFile.Db.EncounterMeta
            .Where(kvp => kvp.Value.Biome == biome)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    private static string FormatBiomeName(string biome)
    {
        var dotIdx = biome.IndexOf('.');
        var name = dotIdx >= 0 ? biome[(dotIdx + 1)..] : biome;
        if (name.Length == 0) return biome;
        return char.ToUpper(name[0]) + name[1..].ToLower().Replace('_', ' ');
    }

    private static string FormatMonsterName(string monsterId)
    {
        var dotIdx = monsterId.IndexOf('.');
        var name = dotIdx >= 0 ? monsterId[(dotIdx + 1)..] : monsterId;
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpper(words[i][0]) + words[i][1..].ToLower();
        }
        return string.Join(' ', words);
    }
}
