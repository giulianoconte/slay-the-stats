using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
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
    // ── Shared colours (game palette from StsColors) ──────────────────────
    private static readonly Color Cream       = new("FFF6E2");        // primary text
    private static readonly Color Gold        = new("EFC851");        // titles, highlights
    private static readonly Color MutedButton = new(0.85f, 0.75f, 0.55f, 1f);
    private static readonly Color HoverButton = new(1f, 0.9f, 0.7f, 1f);
    private static readonly Color ActiveFilterColor = new(0.4f, 0.85f, 0.5f, 1f);

    // Track active pane instances so they can be hidden from external patches.
    private static readonly List<PanelContainer> _activePanes = new();
    // Callbacks to sync pane controls with current config values.
    private static readonly List<Action> _syncCallbacks = new();
    // Cached sort-button template so the relic page can reuse it.
    private static NCardViewSortButton? _sortButtonTemplate;

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

        // Clone an existing NCardViewSortButton to get the exact game style
        // (textures, fonts, HSV shader, tween animations, SFX).
        var template = library.FindChild("CardTypeSorter", true, false) as NCardViewSortButton;
        if (template != null)
            _sortButtonTemplate = template; // cache for the relic page

        var pane = BuildFilterPane();
        library.AddChild(pane);
        RegisterPane(pane);

        if (template != null)
        {
            var button = CloneSortButton(template);
            sidebar.AddChild(button);
            WireSortButtonToPane(button, pane);
        }
        else
        {
            MainFile.Logger.Warn("[SlayTheStats] Could not find CardTypeSorter; using fallback button");
            var button = CreateFallbackSidebarButton();
            sidebar.AddChild(button);
            WireButtonToPane(button, pane);
        }

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
        var pane = BuildFilterPane();
        collection.AddChild(pane);
        RegisterPane(pane);

        // Reuse the cached sort-button template from the card library for
        // a consistent game-native look.
        if (_sortButtonTemplate != null && GodotObject.IsInstanceValid(_sortButtonTemplate))
        {
            var button = CloneSortButton(_sortButtonTemplate);

            // Match the card button's size: read it from the cached template,
            // which has been laid out by the time the relic page opens.
            var templateSize = _sortButtonTemplate.Size;
            if (templateSize.X < 10) templateSize.X = _sortButtonTemplate.CustomMinimumSize.X;
            if (templateSize.Y < 10) templateSize.Y = _sortButtonTemplate.CustomMinimumSize.Y;
            if (templateSize.X < 10) templateSize.X = 180;
            if (templateSize.Y < 10) templateSize.Y = 50;
            button.CustomMinimumSize = templateSize;

            // Anchor to bottom-left, sized to match the card button.
            const float padX = 20f;
            const float padY = 20f;
            button.AnchorLeft = 0f;
            button.AnchorRight = 0f;
            button.AnchorTop = 1f;
            button.AnchorBottom = 1f;
            button.OffsetLeft = padX;
            button.OffsetRight = padX + templateSize.X;
            button.OffsetTop = -templateSize.Y - padY;
            button.OffsetBottom = -padY;

            collection.AddChild(button);
            WireSortButtonToPane(button, pane);
        }
        else
        {
            var button = CreateFloatingButton();
            collection.AddChild(button);
            WireButtonToPane(button, pane);
        }

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

    /// <summary>
    /// Duplicates an existing NCardViewSortButton to get the exact game style:
    /// textures, game fonts (Kreon), HSV shader material, tween animations, and SFX.
    /// </summary>
    private static NCardViewSortButton CloneSortButton(NCardViewSortButton template)
    {
        // Duplicate nodes + scripts but NOT signal connections — ConnectSignals()
        // in _Ready will wire them fresh on the clone.  Include UseInstantiation
        // so the duplicate is re-created from the original PackedScene.
        var clone = (NCardViewSortButton)template.Duplicate(
            (int)(Node.DuplicateFlags.Groups
                | Node.DuplicateFlags.Scripts
                | Node.DuplicateFlags.UseInstantiation));
        clone.Name = "SlayTheStatsFilterButton";

        // Godot copies the "already readied" flag from the template.
        // RequestReady() clears it so _Ready fires when the clone enters the tree,
        // which initialises _label, _button, _hsv, and all signal connections.
        clone.RequestReady();

        // Set label, make shader unique, and hide sort icon after _Ready has run.
        clone.Ready += () =>
        {
            clone.SetLabel("SlayTheStats");

            // The HSV ShaderMaterial on %ButtonImage is shared by reference with
            // the template. Duplicate it so our hover animations don't bleed into
            // the original CardTypeSorter button.
            var buttonImage = clone.FindChild("ButtonImage", true, false);
            if (buttonImage is CanvasItem ci && ci.Material is ShaderMaterial sm)
                ci.Material = (ShaderMaterial)sm.Duplicate();

            // Hide the sort arrow — our button opens a pane, not a sorter.
            var sortIcon = clone.FindChild("Image", true, false);
            if (sortIcon is CanvasItem icon)
                icon.Visible = false;
        };

        return clone;
    }

    /// <summary>
    /// Wires a cloned NCardViewSortButton to toggle a filter pane.
    /// Uses the game's Released signal (NClickableControl) which fires after
    /// the built-in press/release animations and SFX.
    /// </summary>
    private static void WireSortButtonToPane(NCardViewSortButton button, PanelContainer pane)
    {
        ((NClickableControl)button).Released += (_) =>
        {
            if (GodotObject.IsInstanceValid(pane))
            {
                pane.Visible = !pane.Visible;
                if (pane.Visible)
                {
                    RepositionPaneNextToButton(button, pane);
                    SyncAllControls();
                }
            }
        };

        pane.VisibilityChanged += () =>
        {
            if (!GodotObject.IsInstanceValid(button)) return;
            UpdateSortButtonActiveState(button);
        };
    }

    /// <summary>
    /// Positions the filter pane just to the right of the sidebar button at toggle time
    /// using the button's actual global rect (not hard-coded offsets).  The pane's bottom
    /// is aligned with the button's bottom so it grows upward and stays on-screen.
    /// </summary>
    private static void RepositionPaneNextToButton(Control button, PanelContainer pane)
    {
        if (!GodotObject.IsInstanceValid(button) || !GodotObject.IsInstanceValid(pane))
            return;

        const float padding = 24f;

        // Use top-left anchors so GlobalPosition controls the placement directly
        // and the pane is sized by its content (PanelContainer wraps its child).
        pane.AnchorLeft = 0f;
        pane.AnchorTop = 0f;
        pane.AnchorRight = 0f;
        pane.AnchorBottom = 0f;

        // Compute the pane's natural height from its content so we can align its
        // bottom with the button's bottom (the pane grows upward, not downward).
        var paneHeight = pane.GetCombinedMinimumSize().Y;
        var btnRight = button.GlobalPosition.X + button.Size.X;
        var btnBottom = button.GlobalPosition.Y + button.Size.Y;

        pane.GlobalPosition = new Vector2(btnRight + padding, btnBottom - paneHeight);
    }

    /// <summary>
    /// Updates the cloned sort button's label color to indicate active filters.
    /// Uses SelfModulate on the MegaLabel child to tint without fighting the HSV shader.
    /// </summary>
    private static void UpdateSortButtonActiveState(NCardViewSortButton button)
    {
        bool active = HasActiveFilters();
        var label = button.FindChild("Label", true, false);
        if (label is CanvasItem labelItem)
            labelItem.SelfModulate = active ? ActiveFilterColor : Colors.White;
    }

    private static Button CreateFallbackSidebarButton()
    {
        var button = new Button();
        button.Text = "SlayTheStats";
        button.Name = "SlayTheStatsFilterButton";
        button.CustomMinimumSize = new Vector2(0, 32);
        ApplyPlainButtonStyle(button);
        return button;
    }

    private static Button CreateFloatingButton()
    {
        var button = new Button();
        button.Text = "SlayTheStats";
        button.Name = "SlayTheStatsFilterButton";
        button.CustomMinimumSize = new Vector2(160, 36);
        ApplyPlainButtonStyle(button);
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

    private static void ApplyPlainButtonStyle(Button button)
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

    /// <summary>Wires a plain Godot Button to toggle a filter pane (fallback / relic collection).</summary>
    private static void WireButtonToPane(Button button, PanelContainer pane)
    {
        button.Pressed += () =>
        {
            if (GodotObject.IsInstanceValid(pane))
            {
                pane.Visible = !pane.Visible;
                if (pane.Visible) SyncAllControls();
            }
        };

        // Update button color when filters are active.
        pane.VisibilityChanged += () =>
        {
            if (!GodotObject.IsInstanceValid(button)) return;
            UpdateButtonActiveState(button);
        };
    }

    private static void SyncAllControls()
    {
        foreach (var cb in _syncCallbacks) cb();
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
            || SlayTheStatsConfig.ClassSpecificStats
            || !string.IsNullOrEmpty(SlayTheStatsConfig.FilterProfile);
    }

    // ── Filter pane builder ─────────────────────────────────────────────��───

    private static PanelContainer BuildFilterPane()
    {
        var pane = new PanelContainer();
        pane.Name = "SlayTheStatsFilterPane";
        pane.Visible = false;
        pane.ZIndex = 90;

        // Position is set dynamically by RepositionPaneNextToButton when the
        // pane is shown, based on the sidebar button's actual global rect.
        pane.GrowHorizontal = Control.GrowDirection.End;
        pane.GrowVertical = Control.GrowDirection.End;

        // Use the game's tooltip stone texture for the panel background
        // (same asset the tooltips use — matches the game aesthetic).
        pane.AddThemeStyleboxOverride("panel", BuildPanelStyle());
        pane.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f); // cooler steel-blue tint

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        pane.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        margin.AddChild(vbox);

        // ── Title ──
        var title = new Label();
        title.Text = "SlayTheStats Filters";
        title.AddThemeColorOverride("font_color", Gold);
        ApplyGameFont(title, 20);
        vbox.AddChild(title);

        AddSeparator(vbox);

        // ── Version range ──
        var verRow = new HBoxContainer();
        verRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(verRow);
        verRow.AddChild(MakeLabel("Version:", 100));

        var versions = StatsAggregator.GetDistinctVersions(MainFile.Db);

        var verMin = MakeDropdown(versions, SlayTheStatsConfig.VersionMin);
        verRow.AddChild(verMin);
        verRow.AddChild(MakeLabel("to"));
        var verMax = MakeDropdown(versions, SlayTheStatsConfig.VersionMax);
        verRow.AddChild(verMax);

        HighlightIfNonDefault(verMin, SlayTheStatsConfig.IsNonDefault("VersionMin"));
        HighlightIfNonDefault(verMax, SlayTheStatsConfig.IsNonDefault("VersionMax"));

        verMin.ItemSelected += (idx) =>
        {
            SlayTheStatsConfig.VersionMin = idx == 0 ? "" : verMin.GetItemText((int)idx);
            HighlightIfNonDefault(verMin, SlayTheStatsConfig.IsNonDefault("VersionMin"));
        };
        verMax.ItemSelected += (idx) =>
        {
            SlayTheStatsConfig.VersionMax = idx == 0 ? "" : verMax.GetItemText((int)idx);
            HighlightIfNonDefault(verMax, SlayTheStatsConfig.IsNonDefault("VersionMax"));
        };

        // ── Profile filter ──
        var profiles = StatsAggregator.GetDistinctProfiles(MainFile.Db);

        var profRow = new HBoxContainer();
        profRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(profRow);
        profRow.AddChild(MakeLabel("Profile:", 100));

        var profSelect = MakeDropdown(profiles, SlayTheStatsConfig.FilterProfile);
        profSelect.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        profRow.AddChild(profSelect);

        HighlightIfNonDefault(profSelect, SlayTheStatsConfig.IsNonDefault("FilterProfile"));

        profSelect.ItemSelected += (idx) =>
        {
            SlayTheStatsConfig.FilterProfile = idx == 0 ? "" : profSelect.GetItemText((int)idx);
            HighlightIfNonDefault(profSelect, SlayTheStatsConfig.IsNonDefault("FilterProfile"));
        };

        // ── Ascension range ──
        var ascRow = new HBoxContainer();
        ascRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(ascRow);
        ascRow.AddChild(MakeLabel("Ascension:", 100));

        var ascMin = MakeSpinBox(0, 10, SlayTheStatsConfig.AscensionMin);
        ascRow.AddChild(ascMin);
        ascRow.AddChild(MakeLabel("to"));
        var ascMax = MakeSpinBox(0, 10, SlayTheStatsConfig.AscensionMax);
        ascRow.AddChild(ascMax);

        HighlightIfNonDefault(ascMin, SlayTheStatsConfig.IsNonDefault("AscensionMin"));
        HighlightIfNonDefault(ascMax, SlayTheStatsConfig.IsNonDefault("AscensionMax"));

        ascMin.ValueChanged += (val) =>
        {
            SlayTheStatsConfig.AscensionMin = (int)val;
            if (ascMax.Value < val) ascMax.Value = val;
            HighlightIfNonDefault(ascMin, SlayTheStatsConfig.IsNonDefault("AscensionMin"));
        };
        ascMax.ValueChanged += (val) =>
        {
            SlayTheStatsConfig.AscensionMax = (int)val;
            if (ascMin.Value > val) ascMin.Value = val;
            HighlightIfNonDefault(ascMax, SlayTheStatsConfig.IsNonDefault("AscensionMax"));
        };

        AddSeparator(vbox);

        // ── Toggles ──
        var classSpecific = MakeCheckButton("Class-specific stats", SlayTheStatsConfig.ClassSpecificStats);
        classSpecific.Toggled += (pressed) => SlayTheStatsConfig.ClassSpecificStats = pressed;
        vbox.AddChild(classSpecific);

        var groupUpgrades = MakeCheckButton("Group card upgrades", SlayTheStatsConfig.GroupCardUpgrades);
        groupUpgrades.Toggled += (pressed) => SlayTheStatsConfig.GroupCardUpgrades = pressed;
        vbox.AddChild(groupUpgrades);

        AddSeparator(vbox);

        // ── Action buttons ──
        var row1 = new HBoxContainer();
        row1.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(row1);

        row1.AddChild(MakeActionButton("Clear Filters", () =>
        {
            SlayTheStatsConfig.ClearAllFilters();
            SyncAllControls();
        }));
        row1.AddChild(MakeActionButton("Reset", () =>
        {
            SlayTheStatsConfig.RestoreDefaults();
            SyncAllControls();
        }));
        row1.AddChild(MakeActionButton("Save Defaults", () =>
        {
            SlayTheStatsConfig.SaveDefaults();
            try { BaseLib.Config.ModConfig.SaveDebounced<SlayTheStatsConfig>(); }
            catch (System.Exception e)
            {
                MainFile.Logger.Warn($"[SlayTheStats] Failed to save config: {e.Message}");
            }
            SyncAllControls();
        }));

        // ── Sync callback: refresh all controls from config when pane opens ──
        _syncCallbacks.Add(() =>
        {
            if (!GodotObject.IsInstanceValid(pane)) return;

            SelectOptionByText(verMin, SlayTheStatsConfig.VersionMin);
            SelectOptionByText(verMax, SlayTheStatsConfig.VersionMax);
            HighlightIfNonDefault(verMin, SlayTheStatsConfig.IsNonDefault("VersionMin"));
            HighlightIfNonDefault(verMax, SlayTheStatsConfig.IsNonDefault("VersionMax"));

            SelectOptionByText(profSelect, SlayTheStatsConfig.FilterProfile);
            HighlightIfNonDefault(profSelect, SlayTheStatsConfig.IsNonDefault("FilterProfile"));

            ascMax.SetValueNoSignal(SlayTheStatsConfig.AscensionMax);
            ascMin.SetValueNoSignal(SlayTheStatsConfig.AscensionMin);
            HighlightIfNonDefault(ascMin, SlayTheStatsConfig.IsNonDefault("AscensionMin"));
            HighlightIfNonDefault(ascMax, SlayTheStatsConfig.IsNonDefault("AscensionMax"));

            classSpecific.SetPressedNoSignal(SlayTheStatsConfig.ClassSpecificStats);
            groupUpgrades.SetPressedNoSignal(SlayTheStatsConfig.GroupCardUpgrades);
        });

        return pane;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the panel background using the game's stone tooltip texture.
    /// Falls back to a dark flat style if the texture can't be loaded.
    /// </summary>
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
            sb.ContentMarginRight  = 16f;
            sb.ContentMarginTop    = 16f;
            sb.ContentMarginBottom = 12f;
            return sb;
        }
        var flat = new StyleBoxFlat();
        flat.BgColor = new Color(0.11f, 0.07f, 0.06f, 0.94f);
        flat.SetContentMarginAll(12);
        flat.SetCornerRadiusAll(3);
        return flat;
    }

    private static void ApplyGameFont(Control control, int size = 18)
    {
        var font = TooltipHelper.Fonts.Bold ?? TooltipHelper.Fonts.Normal;
        if (font != null)
            control.AddThemeFontOverride("font", font);
        control.AddThemeFontSizeOverride("font_size", size);
    }

    private static Label MakeLabel(string text, float minWidth = 0)
    {
        var label = new Label();
        label.Text = text;
        label.AddThemeColorOverride("font_color", Cream);
        ApplyGameFont(label, 16);
        if (minWidth > 0)
            label.CustomMinimumSize = new Vector2(minWidth, 0);
        return label;
    }

    private static OptionButton MakeDropdown(List<string> items, string selected)
    {
        var btn = new OptionButton();
        btn.CustomMinimumSize = new Vector2(90, 0);
        btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        btn.AddThemeColorOverride("font_color", Cream);
        btn.AddThemeColorOverride("font_hover_color", Gold);
        btn.AddThemeColorOverride("font_focus_color", Gold);
        ApplyGameFont(btn, 16);
        btn.AddItem("Any", 0);
        for (int i = 0; i < items.Count; i++)
            btn.AddItem(items[i], i + 1);
        SelectOptionByText(btn, selected);
        return btn;
    }

    private static SpinBox MakeSpinBox(int min, int max, int value)
    {
        var sb = new SpinBox();
        sb.MinValue = min;
        sb.MaxValue = max;
        sb.Value = value;
        sb.CustomMinimumSize = new Vector2(65, 0);
        sb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return sb;
    }

    private static CheckButton MakeCheckButton(string text, bool initial)
    {
        var cb = new CheckButton();
        cb.Text = text;
        cb.ButtonPressed = initial;
        cb.AddThemeColorOverride("font_color", Cream);
        cb.AddThemeColorOverride("font_hover_color", Gold);
        cb.AddThemeColorOverride("font_pressed_color", Gold);
        ApplyGameFont(cb, 16);
        return cb;
    }

    private static Button MakeActionButton(string text, Action action)
    {
        var btn = new Button();
        btn.Text = text;
        btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        btn.AddThemeColorOverride("font_color", Cream);
        btn.AddThemeColorOverride("font_hover_color", Gold);
        btn.AddThemeColorOverride("font_pressed_color", Gold);
        ApplyGameFont(btn, 14);

        var normal = new StyleBoxFlat();
        normal.BgColor = new Color(0.08f, 0.06f, 0.05f, 0.6f);
        normal.SetBorderWidthAll(1);
        normal.BorderColor = new Color(0.4f, 0.35f, 0.25f, 0.5f);
        normal.SetCornerRadiusAll(3);
        normal.ContentMarginLeft = 6;
        normal.ContentMarginRight = 6;
        normal.ContentMarginTop = 4;
        normal.ContentMarginBottom = 4;
        btn.AddThemeStyleboxOverride("normal", normal);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.12f, 0.09f, 0.07f, 0.7f);
        hover.BorderColor = new Color(0.6f, 0.5f, 0.3f, 0.6f);
        btn.AddThemeStyleboxOverride("hover", hover);

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.05f, 0.04f, 0.03f, 0.8f);
        btn.AddThemeStyleboxOverride("pressed", pressed);

        btn.Pressed += action;
        return btn;
    }

    private static void AddSeparator(VBoxContainer vbox)
    {
        var sep = new HSeparator();
        sep.AddThemeConstantOverride("separation", 4);
        sep.AddThemeStyleboxOverride("separator", new StyleBoxLine
        {
            Color = new Color(0.5f, 0.45f, 0.35f, 0.3f),
            Thickness = 1,
        });
        vbox.AddChild(sep);
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
        var color = isNonDefault ? ActiveFilterColor : Cream;

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
