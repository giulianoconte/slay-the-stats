using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;

namespace SlayTheStats;

/// <summary>
/// Injects a "SlayTheStats" button into the Card Library sidebar and the Relic Collection
/// screen, each opening a shared floating filter/aggregation pane.
/// </summary>
[HarmonyPatch(typeof(NCardLibrary), "_Ready")]
public static class CompendiumFilterPatch
{
    // ── Shared colours ──────────────────────────────────────────────────────
    private static readonly Color GoldTitle   = new(0.918f, 0.745f, 0.318f, 1f);
    private static readonly Color LightText   = new(1f, 0.9647f, 0.8863f, 1f);
    private static readonly Color MutedButton = new(0.85f, 0.75f, 0.55f, 1f);
    private static readonly Color HoverButton = new(1f, 0.9f, 0.7f, 1f);
    private static readonly Color ActiveFilterColor = new(0.4f, 0.85f, 0.5f, 1f);

    // Track active pane instances so they can be hidden from external patches.
    private static readonly List<PanelContainer> _activePanes = new();

    /// <summary>
    /// Hides all filter panes. Called when a card/relic is hovered to avoid
    /// the pane blocking inspect mode.
    /// </summary>
    internal static void HideAllPanes()
    {
        foreach (var pane in _activePanes)
        {
            if (GodotObject.IsInstanceValid(pane))
                pane.Visible = false;
        }
    }

    // ── Card Library patch ──────────────────────────────────────────────────

    static void Postfix(NCardLibrary __instance)
    {
        try { InjectCardLibraryButton(__instance); }
        catch (System.Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] CompendiumFilterPatch failed: {e.Message}");
        }
    }

    private static void InjectCardLibraryButton(NCardLibrary library)
    {
        var multiplayerCards = library.FindChild("MultiplayerCards", true, false);
        if (multiplayerCards == null)
        {
            MainFile.Logger.Warn("[SlayTheStats] Could not find MultiplayerCards node in card library");
            return;
        }
        var sidebar = multiplayerCards.GetParent();
        if (sidebar == null) return;

        var button = CreateSidebarButton();
        sidebar.AddChild(button);

        var pane = BuildFilterPane();
        library.AddChild(pane);
        RegisterPane(pane);

        WireButtonToPane(button, pane);

        if (SlayTheStatsConfig.DebugMode)
            MainFile.Logger.Info("[SlayTheStats] CompendiumFilterPatch: button injected into card library sidebar");
    }

    // ── Card Library OnSubmenuOpened — hide pane each time the page is shown ─
    [HarmonyPatch(typeof(NCardLibrary), "OnSubmenuOpened")]
    public static class CardLibraryOpenedPatch
    {
        static void Postfix() => HideAllPanes();
    }

    // ── Relic Collection patch ──────────────────────────────────────────────

    [HarmonyPatch(typeof(NRelicCollection), "_Ready")]
    public static class RelicCollectionFilterPatch
    {
        static void Postfix(NRelicCollection __instance)
        {
            try { InjectRelicCollectionButton(__instance); }
            catch (System.Exception e)
            {
                MainFile.Logger.Warn($"[SlayTheStats] RelicCollectionFilterPatch failed: {e.Message}");
            }
        }
    }

    // ── Relic Collection OnSubmenuOpened — hide pane each time the page is shown ─
    [HarmonyPatch(typeof(NRelicCollection), "OnSubmenuOpened")]
    public static class RelicCollectionOpenedPatch
    {
        static void Postfix() => HideAllPanes();
    }

    // ── Relic inspect — hide pane when a relic is inspected ─
    [HarmonyPatch(typeof(NInspectRelicScreen), "Open")]
    public static class InspectRelicOpenPatch
    {
        static void Postfix() => HideAllPanes();
    }

    private static void InjectRelicCollectionButton(NRelicCollection collection)
    {
        var button = CreateFloatingButton();
        collection.AddChild(button);

        var pane = BuildFilterPane();
        collection.AddChild(pane);
        RegisterPane(pane);

        WireButtonToPane(button, pane);

        if (SlayTheStatsConfig.DebugMode)
            MainFile.Logger.Info("[SlayTheStats] RelicCollectionFilterPatch: button injected into relic collection");
    }

    private static void RegisterPane(PanelContainer pane)
    {
        // Clean up any stale references.
        _activePanes.RemoveAll(p => !GodotObject.IsInstanceValid(p));
        _activePanes.Add(pane);
    }

    // ── Shared UI builders ──────────────────────────────────────────────────

    private static Button CreateSidebarButton()
    {
        var button = new Button();
        button.Text = "SlayTheStats";
        button.Name = "SlayTheStatsFilterButton";
        button.CustomMinimumSize = new Vector2(0, 32);
        ApplyButtonStyle(button);
        return button;
    }

    private static Button CreateFloatingButton()
    {
        var button = new Button();
        button.Text = "SlayTheStats";
        button.Name = "SlayTheStatsFilterButton";
        button.CustomMinimumSize = new Vector2(160, 36);
        ApplyButtonStyle(button);
        // Bottom-left corner positioning.
        button.AnchorLeft = 0f;
        button.AnchorTop = 1f;
        button.AnchorRight = 0f;
        button.AnchorBottom = 1f;
        button.OffsetLeft = 20;
        button.OffsetTop = -56;
        button.OffsetRight = 180;
        button.OffsetBottom = -20;
        return button;
    }

    private static void ApplyButtonStyle(Button button)
    {
        button.AddThemeColorOverride("font_color", MutedButton);
        button.AddThemeColorOverride("font_hover_color", HoverButton);
        button.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.85f, 0.5f, 1f));

        var normal = new StyleBoxFlat();
        normal.BgColor = new Color(0.15f, 0.16f, 0.22f, 0.85f);
        normal.BorderColor = new Color(0.45f, 0.50f, 0.60f, 1f);
        normal.SetBorderWidthAll(1);
        normal.SetCornerRadiusAll(4);
        normal.ContentMarginLeft = 8;
        normal.ContentMarginRight = 8;
        normal.ContentMarginTop = 4;
        normal.ContentMarginBottom = 4;
        button.AddThemeStyleboxOverride("normal", normal);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BorderColor = new Color(0.6f, 0.65f, 0.75f, 1f);
        hover.BgColor = new Color(0.18f, 0.19f, 0.26f, 0.9f);
        button.AddThemeStyleboxOverride("hover", hover);

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.12f, 0.13f, 0.18f, 0.95f);
        button.AddThemeStyleboxOverride("pressed", pressed);
    }

    private static void WireButtonToPane(Button button, PanelContainer pane)
    {
        button.Pressed += () =>
        {
            if (GodotObject.IsInstanceValid(pane))
                pane.Visible = !pane.Visible;
        };

        // Update button color when filters are active.
        pane.VisibilityChanged += () =>
        {
            if (!GodotObject.IsInstanceValid(button)) return;
            UpdateButtonActiveState(button);
        };
    }

    private static void UpdateButtonActiveState(Button button)
    {
        bool active = HasActiveFilters();
        button.AddThemeColorOverride("font_color", active ? ActiveFilterColor : MutedButton);
    }

    internal static bool HasActiveFilters()
    {
        return SlayTheStatsConfig.AscensionMin > 0
            || SlayTheStatsConfig.AscensionMax < 10
            || !string.IsNullOrEmpty(SlayTheStatsConfig.VersionMin)
            || !string.IsNullOrEmpty(SlayTheStatsConfig.VersionMax)
            || !string.IsNullOrEmpty(SlayTheStatsConfig.FilterCharacters)
            || !string.IsNullOrEmpty(SlayTheStatsConfig.FilterProfile);
    }

    // ── Filter pane builder ─────────────────────────────────────────────────

    private static PanelContainer BuildFilterPane()
    {
        var pane = new PanelContainer();
        pane.Name = "SlayTheStatsFilterPane";
        pane.Visible = false;
        pane.ZIndex = 90;

        // Position: centered, sized for controls.
        pane.AnchorLeft = 0.5f;
        pane.AnchorTop = 0.5f;
        pane.AnchorRight = 0.5f;
        pane.AnchorBottom = 0.5f;
        pane.OffsetLeft = -220;
        pane.OffsetTop = -200;
        pane.OffsetRight = 220;
        pane.OffsetBottom = 200;

        // Solid dark background.
        var bg = new StyleBoxFlat();
        bg.BgColor = new Color(0.12f, 0.13f, 0.18f, 0.95f);
        bg.BorderColor = new Color(0.35f, 0.40f, 0.55f, 1f);
        bg.BorderWidthBottom = 2;
        bg.BorderWidthTop = 2;
        bg.BorderWidthLeft = 2;
        bg.BorderWidthRight = 2;
        bg.CornerRadiusBottomLeft = 6;
        bg.CornerRadiusBottomRight = 6;
        bg.CornerRadiusTopLeft = 6;
        bg.CornerRadiusTopRight = 6;
        pane.AddThemeStyleboxOverride("panel", bg);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        pane.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        // ── Header row ──
        var headerRow = new HBoxContainer();
        vbox.AddChild(headerRow);

        var title = new Label();
        title.Text = "SlayTheStats";
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.AddThemeColorOverride("font_color", GoldTitle);
        title.AddThemeFontSizeOverride("font_size", 22);
        headerRow.AddChild(title);

        var closeButton = new Button();
        closeButton.Text = "X";
        closeButton.CustomMinimumSize = new Vector2(32, 32);
        closeButton.Pressed += () =>
        {
            if (GodotObject.IsInstanceValid(pane))
                pane.Visible = false;
        };
        headerRow.AddChild(closeButton);

        vbox.AddChild(new HSeparator());

        // ── Ascension range ──
        var ascRow = new HBoxContainer();
        ascRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(ascRow);

        var ascLabel = MakeLabel("Ascension:");
        ascLabel.CustomMinimumSize = new Vector2(110, 0);
        ascRow.AddChild(ascLabel);

        var ascMin = new SpinBox();
        ascMin.MinValue = 0;
        ascMin.MaxValue = 10;
        ascMin.Value = SlayTheStatsConfig.AscensionMin;
        ascMin.CustomMinimumSize = new Vector2(70, 0);
        ascMin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        ascRow.AddChild(ascMin);

        ascRow.AddChild(MakeLabel("—"));

        var ascMax = new SpinBox();
        ascMax.MinValue = 0;
        ascMax.MaxValue = 10;
        ascMax.Value = SlayTheStatsConfig.AscensionMax;
        ascMax.CustomMinimumSize = new Vector2(70, 0);
        ascMax.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        ascRow.AddChild(ascMax);

        HighlightIfNonDefault(ascMin, SlayTheStatsConfig.AscensionMin != 0);
        HighlightIfNonDefault(ascMax, SlayTheStatsConfig.AscensionMax != 10);

        ascMin.ValueChanged += (val) =>
        {
            SlayTheStatsConfig.AscensionMin = (int)val;
            HighlightIfNonDefault(ascMin, (int)val != 0);
        };
        ascMax.ValueChanged += (val) =>
        {
            SlayTheStatsConfig.AscensionMax = (int)val;
            HighlightIfNonDefault(ascMax, (int)val != 10);
        };

        // ── Version range ──
        var verRow = new HBoxContainer();
        verRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(verRow);

        var verLabel = MakeLabel("Version:");
        verLabel.CustomMinimumSize = new Vector2(110, 0);
        verRow.AddChild(verLabel);

        var versions = StatsAggregator.GetDistinctVersions(MainFile.Db);

        var verMin = new OptionButton();
        verMin.CustomMinimumSize = new Vector2(100, 0);
        verMin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        verMin.AddItem("Any", 0);
        for (int i = 0; i < versions.Count; i++)
            verMin.AddItem(versions[i], i + 1);
        SelectOptionByText(verMin, SlayTheStatsConfig.VersionMin);
        verRow.AddChild(verMin);

        verRow.AddChild(MakeLabel("—"));

        var verMax = new OptionButton();
        verMax.CustomMinimumSize = new Vector2(100, 0);
        verMax.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        verMax.AddItem("Any", 0);
        for (int i = 0; i < versions.Count; i++)
            verMax.AddItem(versions[i], i + 1);
        SelectOptionByText(verMax, SlayTheStatsConfig.VersionMax);
        verRow.AddChild(verMax);

        HighlightIfNonDefault(verMin, !string.IsNullOrEmpty(SlayTheStatsConfig.VersionMin));
        HighlightIfNonDefault(verMax, !string.IsNullOrEmpty(SlayTheStatsConfig.VersionMax));

        verMin.ItemSelected += (idx) =>
        {
            SlayTheStatsConfig.VersionMin = idx == 0 ? "" : verMin.GetItemText((int)idx);
            HighlightIfNonDefault(verMin, idx != 0);
        };
        verMax.ItemSelected += (idx) =>
        {
            SlayTheStatsConfig.VersionMax = idx == 0 ? "" : verMax.GetItemText((int)idx);
            HighlightIfNonDefault(verMax, idx != 0);
        };

        // ── Character filter ──
        var charRow = new HBoxContainer();
        charRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(charRow);

        var charLabel = MakeLabel("Character:");
        charLabel.CustomMinimumSize = new Vector2(110, 0);
        charRow.AddChild(charLabel);

        var characters = GetOrderedCharacters(MainFile.Db);

        var charSelect = new OptionButton();
        charSelect.CustomMinimumSize = new Vector2(180, 0);
        charSelect.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        charSelect.AddItem("All", 0);
        for (int i = 0; i < characters.Count; i++)
            charSelect.AddItem(FormatCharName(characters[i]), i + 1);
        SelectOptionByText(charSelect, FormatCharName(SlayTheStatsConfig.FilterCharacters));
        charRow.AddChild(charSelect);

        HighlightIfNonDefault(charSelect, !string.IsNullOrEmpty(SlayTheStatsConfig.FilterCharacters));

        charSelect.ItemSelected += (idx) =>
        {
            if (idx == 0)
                SlayTheStatsConfig.FilterCharacters = "";
            else
            {
                var selectedLabel = charSelect.GetItemText((int)idx);
                var rawId = characters.FirstOrDefault(c => FormatCharName(c) == selectedLabel) ?? "";
                SlayTheStatsConfig.FilterCharacters = rawId;
            }
            HighlightIfNonDefault(charSelect, idx != 0);
        };

        // ── Profile filter ──
        var profiles = StatsAggregator.GetDistinctProfiles(MainFile.Db);

        var profRow = new HBoxContainer();
        profRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(profRow);

        var profLabel = MakeLabel("Profile:");
        profLabel.CustomMinimumSize = new Vector2(110, 0);
        profRow.AddChild(profLabel);

        var profSelect = new OptionButton();
        profSelect.CustomMinimumSize = new Vector2(180, 0);
        profSelect.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        profSelect.AddItem("All", 0);
        for (int i = 0; i < profiles.Count; i++)
            profSelect.AddItem(profiles[i], i + 1);
        SelectOptionByText(profSelect, SlayTheStatsConfig.FilterProfile);
        profRow.AddChild(profSelect);

        HighlightIfNonDefault(profSelect, !string.IsNullOrEmpty(SlayTheStatsConfig.FilterProfile));

        profSelect.ItemSelected += (idx) =>
        {
            SlayTheStatsConfig.FilterProfile = idx == 0 ? "" : profSelect.GetItemText((int)idx);
            HighlightIfNonDefault(profSelect, idx != 0);
        };

        vbox.AddChild(new HSeparator());

        // ── Toggles ──
        var groupUpgrades = MakeCheckButton("Group Card Upgrades", SlayTheStatsConfig.GroupCardUpgrades);
        groupUpgrades.Toggled += (pressed) => SlayTheStatsConfig.GroupCardUpgrades = pressed;
        vbox.AddChild(groupUpgrades);

        return pane;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Label MakeLabel(string text)
    {
        var label = new Label();
        label.Text = text;
        label.AddThemeColorOverride("font_color", LightText);
        return label;
    }

    private static CheckButton MakeCheckButton(string text, bool initial)
    {
        var cb = new CheckButton();
        cb.Text = text;
        cb.ButtonPressed = initial;
        cb.AddThemeColorOverride("font_color", LightText);
        return cb;
    }

    // Canonical character order matching the character select screen (from ModelDb.AllCharacters).
    private static readonly string[] CharacterOrder =
    [
        "CHARACTER.IRONCLAD",
        "CHARACTER.SILENT",
        "CHARACTER.REGENT",
        "CHARACTER.NECROBINDER",
        "CHARACTER.DEFECT",
    ];

    /// <summary>
    /// Returns characters present in the DB, sorted in canonical game order.
    /// Characters not in the canonical list are appended alphabetically at the end.
    /// </summary>
    private static List<string> GetOrderedCharacters(StatsDb db)
    {
        var present = new HashSet<string>(StatsAggregator.GetDistinctCharacters(db));
        var ordered = new List<string>();
        foreach (var c in CharacterOrder)
        {
            if (present.Remove(c))
                ordered.Add(c);
        }
        // Append any unknown characters alphabetically.
        var remaining = present.ToList();
        remaining.Sort(StringComparer.Ordinal);
        ordered.AddRange(remaining);
        return ordered;
    }

    private static string FormatCharName(string character)
    {
        if (string.IsNullOrEmpty(character)) return "";
        var name = character.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
            ? character["CHARACTER.".Length..]
            : character;
        if (name.Length == 0) return character;
        return char.ToUpper(name[0]) + name[1..].ToLower();
    }

    /// <summary>
    /// Colors a control's text green when its value differs from the default, or
    /// restores the default light text color when it matches.
    /// Works with SpinBox (targets the inner LineEdit) and OptionButton.
    /// </summary>
    private static void HighlightIfNonDefault(Control control, bool isNonDefault)
    {
        var color = isNonDefault ? ActiveFilterColor : LightText;

        if (control is SpinBox spinBox)
        {
            spinBox.GetLineEdit()?.AddThemeColorOverride("font_color", color);
        }
        else if (control is OptionButton optBtn)
        {
            optBtn.AddThemeColorOverride("font_color", color);
            optBtn.AddThemeColorOverride("font_hover_color", color);
            optBtn.AddThemeColorOverride("font_focus_color", color);
        }
    }

    private static void SelectOptionByText(OptionButton button, string text)
    {
        if (string.IsNullOrEmpty(text)) { button.Selected = 0; return; }
        for (int i = 0; i < button.ItemCount; i++)
        {
            if (string.Equals(button.GetItemText(i), text, StringComparison.OrdinalIgnoreCase))
            {
                button.Selected = i;
                return;
            }
        }
        button.Selected = 0;
    }
}
