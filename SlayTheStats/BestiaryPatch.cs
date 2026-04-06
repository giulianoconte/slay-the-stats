using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SlayTheStats;

/// <summary>
/// Adds a custom "Stats Bestiary" button to the compendium submenu (separate from the game's
/// disabled bestiary button) and wires it to open a bestiary panel showing encounter stats by biome.
/// </summary>
[HarmonyPatch(typeof(NCompendiumSubmenu), "_Ready")]
public static class BestiaryCompendiumPatch
{
    private static PanelContainer? _bestiaryPanel;
    private static HBoxContainer? _biomeTabRow;
    private static VBoxContainer? _encounterList;
    private static string? _selectedBiome;
    private static bool _warnedOnce;

    static void Postfix(NCompendiumSubmenu __instance)
    {
        try
        {
            // Find the game's bestiary button as a reference point for inserting our button above it
            var gameBestiaryButton = ((Node)__instance).GetNodeOrNull<Control>("%BestiaryButton");
            if (gameBestiaryButton == null) return;

            var parent = ((Node)gameBestiaryButton).GetParent();
            if (parent == null) return;

            // Create our own button and insert it before the game's bestiary button
            var ourButton = new Button();
            ourButton.Name = "SlayTheStatsBestiaryButton";
            ourButton.Text = "Stats Bestiary";
            ourButton.CustomMinimumSize = new Vector2(0, 50);

            // Style to match the compendium's dark theme
            var normalStyle = new StyleBoxFlat();
            normalStyle.BgColor = new Color(0.15f, 0.16f, 0.22f, 0.85f);
            normalStyle.BorderColor = new Color(0.45f, 0.50f, 0.60f, 1f);
            normalStyle.SetBorderWidthAll(1);
            normalStyle.SetCornerRadiusAll(4);
            normalStyle.ContentMarginLeft = 12;
            normalStyle.ContentMarginRight = 12;
            normalStyle.ContentMarginTop = 8;
            normalStyle.ContentMarginBottom = 8;
            ourButton.AddThemeStyleboxOverride("normal", normalStyle);

            var hoverStyle = (StyleBoxFlat)normalStyle.Duplicate();
            hoverStyle.BgColor = new Color(0.20f, 0.22f, 0.30f, 0.90f);
            ourButton.AddThemeStyleboxOverride("hover", hoverStyle);

            ourButton.AddThemeColorOverride("font_color", new Color(0.95f, 0.93f, 0.88f, 1f));
            ourButton.AddThemeColorOverride("font_hover_color", new Color(0.918f, 0.745f, 0.318f, 1f));
            ourButton.AddThemeFontSizeOverride("font_size", 20);

            // Insert before the game's bestiary button
            int bestiaryIdx = gameBestiaryButton.GetIndex();
            parent.AddChild(ourButton);
            parent.MoveChild(ourButton, bestiaryIdx);

            ourButton.Pressed += () => OnBestiaryClicked(__instance);
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"[SlayTheStats] BestiaryPatch failed: {e.Message}");
                _warnedOnce = true;
            }
        }
    }

    private static void OnBestiaryClicked(NCompendiumSubmenu compendium)
    {
        if (_bestiaryPanel != null && GodotObject.IsInstanceValid(_bestiaryPanel))
        {
            _bestiaryPanel.Visible = !_bestiaryPanel.Visible;
            if (_bestiaryPanel.Visible) RefreshEncounterList();
            return;
        }

        _bestiaryPanel = BuildBestiaryPanel();
        ((Node)compendium).AddChild(_bestiaryPanel);
        _selectedBiome = GetDefaultBiome();
        RefreshEncounterList();
    }

    private static PanelContainer BuildBestiaryPanel()
    {
        var panel = new PanelContainer();
        panel.Name = "SlayTheStatsBestiary";
        panel.ZIndex = 90;

        // Center panel at ~80% viewport
        panel.AnchorLeft = 0.1f;
        panel.AnchorTop = 0.05f;
        panel.AnchorRight = 0.9f;
        panel.AnchorBottom = 0.95f;
        panel.OffsetLeft = 0;
        panel.OffsetTop = 0;
        panel.OffsetRight = 0;
        panel.OffsetBottom = 0;

        // Panel background — fully opaque
        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.08f, 0.09f, 0.12f, 1f);
        style.BorderColor = new Color(0.45f, 0.50f, 0.60f, 1f);
        style.BorderWidthLeft = 1;
        style.BorderWidthRight = 1;
        style.BorderWidthTop = 1;
        style.BorderWidthBottom = 1;
        style.CornerRadiusTopLeft = 6;
        style.CornerRadiusTopRight = 6;
        style.CornerRadiusBottomLeft = 6;
        style.CornerRadiusBottomRight = 6;
        panel.AddThemeStyleboxOverride("panel", style);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        // Header row
        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(headerRow);

        var title = new Label();
        title.Text = "Bestiary — Encounter Stats";
        title.AddThemeColorOverride("font_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        title.AddThemeFontSizeOverride("font_size", 24);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(title);

        var closeBtn = new Button();
        closeBtn.Text = "X";
        closeBtn.CustomMinimumSize = new Vector2(32, 32);
        closeBtn.Pressed += () => { if (GodotObject.IsInstanceValid(_bestiaryPanel)) _bestiaryPanel!.Visible = false; };
        headerRow.AddChild(closeBtn);

        // Separator
        vbox.AddChild(new HSeparator());

        // Biome tabs — stored as field for direct access (no node path lookup)
        _biomeTabRow = new HBoxContainer();
        _biomeTabRow.Name = "BiomeTabs";
        _biomeTabRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(_biomeTabRow);

        // Scrollable encounter list
        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        vbox.AddChild(scroll);

        _encounterList = new VBoxContainer();
        _encounterList.Name = "EncounterList";
        _encounterList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _encounterList.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_encounterList);

        return panel;
    }

    private static void RefreshEncounterList()
    {
        if (_encounterList == null || !GodotObject.IsInstanceValid(_encounterList)) return;
        if (_bestiaryPanel == null || !GodotObject.IsInstanceValid(_bestiaryPanel)) return;

        // Clear existing entries immediately
        while (_encounterList.GetChildCount() > 0)
        {
            var child = _encounterList.GetChild(0);
            _encounterList.RemoveChild(child);
            child.QueueFree();
        }

        // Rebuild biome tabs
        var tabRow = _biomeTabRow;
        MainFile.Logger.Info($"[SlayTheStats] RefreshEncounterList: tabRow={tabRow != null} valid={tabRow != null && GodotObject.IsInstanceValid(tabRow)} selectedBiome={_selectedBiome}");
        if (tabRow != null && GodotObject.IsInstanceValid(tabRow))
        {
            while (tabRow.GetChildCount() > 0)
            {
                var child = tabRow.GetChild(0);
                tabRow.RemoveChild(child);
                child.QueueFree();
            }

            var biomes = GetBiomes();
            MainFile.Logger.Info($"[SlayTheStats] Biomes found: {biomes.Count} — [{string.Join(", ", biomes)}]");
            foreach (var biome in biomes)
            {
                var btn = new Button();
                btn.Text = FormatBiomeName(biome);
                btn.CustomMinimumSize = new Vector2(140, 36);

                // Style the tab button
                var tabStyle = new StyleBoxFlat();
                tabStyle.BgColor = (biome == _selectedBiome)
                    ? new Color(0.20f, 0.22f, 0.30f, 0.95f)
                    : new Color(0.12f, 0.13f, 0.18f, 0.85f);
                tabStyle.BorderColor = (biome == _selectedBiome)
                    ? new Color(0.918f, 0.745f, 0.318f, 0.8f)
                    : new Color(0.35f, 0.38f, 0.45f, 0.8f);
                tabStyle.SetBorderWidthAll(1);
                tabStyle.SetCornerRadiusAll(4);
                tabStyle.ContentMarginLeft = 8;
                tabStyle.ContentMarginRight = 8;
                tabStyle.ContentMarginTop = 4;
                tabStyle.ContentMarginBottom = 4;
                btn.AddThemeStyleboxOverride("normal", tabStyle);

                var hoverTab = (StyleBoxFlat)tabStyle.Duplicate();
                hoverTab.BgColor = new Color(0.25f, 0.27f, 0.35f, 0.95f);
                btn.AddThemeStyleboxOverride("hover", hoverTab);

                btn.AddThemeColorOverride("font_color",
                    biome == _selectedBiome
                        ? new Color(0.918f, 0.745f, 0.318f, 1f)
                        : new Color(0.85f, 0.83f, 0.78f, 1f));
                btn.AddThemeColorOverride("font_hover_color", new Color(0.918f, 0.745f, 0.318f, 1f));
                btn.AddThemeFontSizeOverride("font_size", 18);

                var capturedBiome = biome;
                btn.Pressed += () =>
                {
                    _selectedBiome = capturedBiome;
                    RefreshEncounterList();
                };
                tabRow.AddChild(btn);
                MainFile.Logger.Info($"[SlayTheStats] Added biome tab: {FormatBiomeName(biome)} selected={biome == _selectedBiome}");
            }
            MainFile.Logger.Info($"[SlayTheStats] Tab row child count after rebuild: {tabRow.GetChildCount()}");
        }

        // Get encounters for selected biome
        var encounters = GetEncountersForBiome(_selectedBiome);
        MainFile.Logger.Info($"[SlayTheStats] Encounters for biome {_selectedBiome}: {encounters.Count}");

        // Group by category
        var categories = new[] { "weak", "normal", "elite", "boss", "event", "unknown" };
        foreach (var category in categories)
        {
            var catEncounters = encounters
                .Where(e => MainFile.Db.EncounterMeta.TryGetValue(e, out var meta) && meta.Category == category)
                .OrderBy(e => EncounterCategory.FormatName(e))
                .ToList();

            if (catEncounters.Count == 0) continue;

            // Category header
            var catLabel = new Label();
            catLabel.Text = $"  {EncounterCategory.FormatCategory(category)} Encounters";
            catLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f, 1f));
            catLabel.AddThemeFontSizeOverride("font_size", 18);
            _encounterList.AddChild(catLabel);

            foreach (var encId in catEncounters)
            {
                var entry = BuildEncounterEntry(encId);
                _encounterList.AddChild(entry);
            }
        }
    }

    private static Control BuildEncounterEntry(string encounterId)
    {
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 12);
        hbox.CustomMinimumSize = new Vector2(0, 28);

        var nameLabel = new RichTextLabel();
        nameLabel.BbcodeEnabled = true;
        nameLabel.FitContent = true;
        nameLabel.ScrollActive = false;
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLabel.CustomMinimumSize = new Vector2(200, 24);

        var encounterName = EncounterCategory.FormatName(encounterId);

        // Show monster composition if available
        string monsterInfo = "";
        if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
        {
            var monsterCounts = meta.MonsterIds
                .GroupBy(m => m)
                .Select(g => g.Count() > 1 ? $"{FormatMonsterName(g.Key)} x{g.Count()}" : FormatMonsterName(g.Key));
            monsterInfo = $"  [color=#808080]({string.Join(", ", monsterCounts)})[/color]";
        }

        nameLabel.Text = $"    {encounterName}{monsterInfo}";
        nameLabel.AddThemeColorOverride("default_color", new Color(0.95f, 0.93f, 0.88f, 1f));
        nameLabel.AddThemeFontSizeOverride("normal_font_size", 18);
        nameLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        hbox.AddChild(nameLabel);

        // Quick stats summary (inline)
        if (MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap))
        {
            var filter = BuildFilter();
            var actStats = StatsAggregator.AggregateEncountersByAct(contextMap, filter);
            int totalFought = actStats.Values.Sum(e => e.Fought);
            int totalDied = actStats.Values.Sum(e => e.Died);
            double totalDmgPct = actStats.Values.Sum(e => e.DmgPctSum);
            int totalN = actStats.Values.Sum(e => e.Fought);
            double avgDmgPct = totalN > 0 ? totalDmgPct / totalN * 100.0 : 0;

            var statsLabel = new Label();
            statsLabel.Text = $"Fought: {totalFought}  Died: {totalDied}  Dmg%: {Math.Round(avgDmgPct)}%";
            statsLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f, 1f));
            statsLabel.AddThemeFontSizeOverride("font_size", 16);
            statsLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            hbox.AddChild(statsLabel);
        }

        // Hover shows full tooltip positioned at top-right of the bestiary panel
        hbox.MouseEntered += () => ShowEncounterTooltip(encounterId);
        hbox.MouseExited += () =>
        {
            TooltipHelper.HasActiveHover = false;
            TooltipHelper.FixedPosition = null;
            TooltipHelper.HideWithDelay();
        };

        // Make it focusable for mouse hover detection
        hbox.MouseFilter = Control.MouseFilterEnum.Stop;

        return hbox;
    }

    private static void ShowEncounterTooltip(string encounterId)
    {
        if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap)) return;

        var filter = BuildFilter();
        var actStats = StatsAggregator.AggregateEncountersByAct(contextMap, filter);

        string? category = null;
        if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
            category = meta.Category;

        var categoryLabel = category != null ? EncounterCategory.FormatCategory(category) : "";
        var characterLabel = CardHoverShowPatch.RunCharacter != null
            ? FormatCharacterName(CardHoverShowPatch.RunCharacter)
            : "All chars";

        double deathRateBaseline = StatsAggregator.GetEncounterDeathRateBaseline(MainFile.Db, filter, category);
        double dmgPctBaseline = StatsAggregator.GetEncounterDmgPctBaseline(MainFile.Db, filter, category);

        string statsText;
        if (actStats.Count == 0)
        {
            statsText = EncounterTooltipHelper.NoDataText(characterLabel, filter.AscensionMin, filter.AscensionMax);
        }
        else
        {
            statsText = EncounterTooltipHelper.BuildEncounterStatsText(
                actStats, deathRateBaseline, dmgPctBaseline,
                characterLabel, filter.AscensionMin, filter.AscensionMax, categoryLabel);
        }

        var encounterName = EncounterCategory.FormatName(encounterId);
        TooltipHelper.TrySceneTheftOnce();
        TooltipHelper.EnsurePanelExists();

        // Position tooltip at top-center of the viewport, above the bestiary content
        var viewport = _bestiaryPanel?.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        float tooltipX = (viewport.X - TooltipHelper.EncounterTooltipWidth) / 2f;
        TooltipHelper.FixedPosition = new Vector2(tooltipX, 20f);

        TooltipHelper.ShowPanel($"[b]{encounterName}[/b]\n{statsText}", widthOverride: TooltipHelper.EncounterTooltipWidth);
    }

    private static AggregationFilter BuildFilter()
    {
        var character = CardHoverShowPatch.IsInRun ? CardHoverShowPatch.RunCharacter : null;
        var filter = new AggregationFilter { Character = character };

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
        // Order by act number so biomes appear in gameplay order, not alphabetically
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
        // "ACT.UNDERDOCKS" -> "Underdocks"
        var dotIdx = biome.IndexOf('.');
        var name = dotIdx >= 0 ? biome[(dotIdx + 1)..] : biome;
        if (name.Length == 0) return biome;
        return char.ToUpper(name[0]) + name[1..].ToLower().Replace('_', ' ');
    }

    private static string FormatMonsterName(string monsterId)
    {
        // "MONSTER.CORPSE_SLUG" -> "Corpse Slug"
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

    private static string FormatCharacterName(string characterId)
    {
        var dotIdx = characterId.IndexOf('.');
        var name = dotIdx >= 0 ? characterId[(dotIdx + 1)..] : characterId;
        if (name.Length == 0) return characterId;
        return char.ToUpper(name[0]) + name[1..].ToLower().Replace('_', ' ');
    }
}
