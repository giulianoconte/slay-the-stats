using BaseLib.Utils;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
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

// In-combat compendium uses NRunSubmenuStack instead of NMainMenuSubmenuStack. Mirror the
// inject patch on its GetSubmenuType so the bestiary button works during a run too.
[HarmonyPatch(typeof(NRunSubmenuStack), nameof(NRunSubmenuStack.GetSubmenuType), typeof(Type))]
public static class InjectBestiarySubmenuRunPatch
{
    private static readonly SpireField<NRunSubmenuStack, NBestiaryStatsSubmenu> SubmenuField = new(CreateSubmenu);

    private static NBestiaryStatsSubmenu CreateSubmenu(NRunSubmenuStack stack)
    {
        var menu = new NBestiaryStatsSubmenu();
        menu.Visible = false;
        ((Node)stack).AddChild(menu);
        return menu;
    }

    public static bool Prefix(NRunSubmenuStack __instance, Type type, ref NSubmenu __result)
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
            // Anchor next to the existing bottom-row buttons (Statistics, Run History) rather than
            // the locked game bestiary button at the top.
            var runHistoryButton = ((Node)__instance).GetNodeOrNull<Control>("%RunHistoryButton");
            if (runHistoryButton == null) return;

            var buttonParent = ((Node)runHistoryButton).GetParent();
            if (buttonParent == null) return;

            // Clone the run history button so we inherit its bg panel, fonts, icon shader, and
            // any other styling the game's bottom-row buttons share. Duplicate copies child
            // nodes and serialized properties; runtime signal connections wired in code (e.g.
            // NCompendiumSubmenu._Ready) only apply to the original instance, not the clone.
            var ourButton = runHistoryButton.Duplicate() as Control;
            if (ourButton == null) return;
            ourButton.Name = "SlayTheStatsBestiaryButton";

            int runHistoryIdx = runHistoryButton.GetIndex();
            buttonParent.AddChild(ourButton);
            buttonParent.MoveChild(ourButton, runHistoryIdx + 1);

            // The cloned NCompendiumBottomButton would re-localize its label from the inherited
            // _locKeyPrefix on theme-changed notifications, undoing our text. Clear that field.
            var locField = AccessTools.Field(typeof(NCompendiumBottomButton), "_locKeyPrefix");
            locField?.SetValue(ourButton, null);

            // Override the visible text. Label child is named "Label" inside the scene.
            var labelNode = ((Node)ourButton).GetNodeOrNull<Label>("Label");
            if (labelNode != null)
                labelNode.Text = "Stats Bestiary";

            // Tint the background panel slightly more orange than the default stone color so
            // the button reads as "stats bestiary" rather than another generic compendium tile.
            // CRITICAL: the cloned BgPanel inherits a *shared* ShaderMaterial reference from
            // the source button. The HSV value parameter on that shader is what drives the
            // focus / hover / press tweens — and since it's shared, hovering this button
            // would also light up the original Run History button. Duplicate the material so
            // the clone has its own instance and the focus animation is isolated.
            var bgPanel = ((Node)ourButton).GetNodeOrNull<Control>("BgPanel");
            if (bgPanel != null)
            {
                if (((CanvasItem)bgPanel).Material is ShaderMaterial sharedMat)
                {
                    var ownMat = (ShaderMaterial)sharedMat.Duplicate(true);
                    ((CanvasItem)bgPanel).Material = ownMat;
                    // The cloned NCompendiumBottomButton's _hsv field still points at the
                    // *original* shared material — patch the field to the new copy so the
                    // tween-driven UpdateShaderParam targets the right instance.
                    var hsvField = AccessTools.Field(typeof(NCompendiumBottomButton), "_hsv");
                    hsvField?.SetValue(ourButton, ownMat);
                }
                // Slightly orange-warm tint, less saturated than before so it sits next to
                // the other compendium tiles without screaming.
                ((CanvasItem)bgPanel).SelfModulate = new Color(1.05f, 0.92f, 0.78f, 1f);
            }

            // Replace the icon with a square grid of boss icons (encountered in full color,
            // unencountered as silhouettes). The cloned TextureRect from the run-history
            // button has its own stretch config tuned for a square clipboard glyph; force
            // KeepAspectCentered so the square composite displays cleanly inside the slot.
            // Falls back to a single elite icon if no boss data is available yet.
            var iconNode = ((Node)ourButton).GetNodeOrNull<TextureRect>("Icon");
            if (iconNode != null)
            {
                // Larger per-cell so individual boss silhouettes are recognisable.
                Texture2D? newTex = EncounterIcons.BuildBossCompositeTexture(80);
                Color modulate    = Colors.White;

                if (newTex == null)
                {
                    // Fall back to elite.png tinted with the boss color so the button never
                    // ends up with a blank icon area.
                    newTex = ResourceLoader.Load<Texture2D>("res://images/ui/run_history/elite.png");
                    modulate = EncounterIcons.CategoryColor("boss");
                }

                if (newTex != null)
                {
                    iconNode.Texture     = newTex;
                    iconNode.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
                    iconNode.ExpandMode  = TextureRect.ExpandModeEnum.IgnoreSize;
                    ((CanvasItem)iconNode).SelfModulate = modulate;
                    iconNode.Visible = true;
                    iconNode.Modulate = Colors.White;
                }
                else
                {
                    MainFile.Logger.Warn("[SlayTheStats] BestiaryButtonPatch: no icon texture available (composite + elite fallback both null)");
                }
            }

            ourButton.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ =>
                {
                    // _stack is an NSubmenuStack base reference — could be either
                    // NMainMenuSubmenuStack (out-of-run compendium) or NRunSubmenuStack
                    // (in-combat compendium). Both override PushSubmenuType<T>() and we have
                    // matching inject patches, so the base call works in either case.
                    var stackField = AccessTools.Field(typeof(NSubmenu), "_stack");
                    if (stackField?.GetValue(__instance) is NSubmenuStack stack)
                    {
                        try
                        {
                            var submenu = stack.PushSubmenuType<NBestiaryStatsSubmenu>();
                            submenu.Refresh();
                        }
                        catch (Exception e)
                        {
                            MainFile.Logger.Warn($"[SlayTheStats] PushSubmenuType failed: {e.Message}");
                        }
                    }
                    else
                    {
                        MainFile.Logger.Warn("[SlayTheStats] BestiaryButton click: no NSubmenuStack on _stack");
                    }
                }),
                0u);
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
    private HBoxContainer? _sortTabRow;
    private HBoxContainer? _sortCharRow;
    private HBoxContainer? _statsColumnHeaderRow;
    private VBoxContainer? _encounterList;
    private RichTextLabel? _statsLabel;
    private RichTextLabel? _statsTitleLabel;
    private NScrollableContainer? _bestiaryScrollContainer;
    private MarginContainer? _headerMarginContainer;
    private Control? _renderArea;
    private string? _selectedBiome;
    private string? _hoveredEncounterId;
    private EncounterSortMode _sortMode = EncounterSortMode.Name;
    // Default direction: A→Z for Name; high→low for stat-based modes (set on mode-change).
    private bool _sortDescending = false;
    /// <summary>null = all characters; otherwise a CHARACTER.* id used for sort scoring.</summary>
    private string? _sortCharacter;
    private readonly HashSet<string> _collapsedCategories = new();
    private readonly Dictionary<string, Control> _rowsByEncounter = new();
    /// <summary>Per-category bundles of icon hover handles, used to drive the run-history-style
    /// scale-up tween whenever the player hovers a category header or any encounter row inside
    /// that category.</summary>
    private readonly Dictionary<string, CategoryHoverBundle> _iconBundles = new();
    private bool _built;

    internal sealed class CategoryHoverBundle
    {
        /// <summary>The category header icon (the one that lives on the bold "Boss" / "Elite"
        /// row at the top of the group).</summary>
        public IconHoverHandle? HeaderIcon;
        /// <summary>Per-encounter row icons keyed by encounter id (only populated for boss
        /// rows currently).</summary>
        public readonly Dictionary<string, IconHoverHandle> RowIcons = new();
    }

    public sealed class IconHoverHandle
    {
        public required Control Wrapper;
        public required TextureRect Icon;
        /// <summary>The silhouette glow drawn behind the icon. May be null for handles that
        /// only want the scale animation without a halo (e.g. boss row icons).</summary>
        public Control? Highlight;
    }

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
        title.Text = "SlayTheStats Bestiary";
        title.AddThemeColorOverride("font_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        title.AddThemeFontSizeOverride("font_size", 26);
        ApplyKreonFont(title, bold: true);
        ApplyTextShadow(title);
        outerVbox.AddChild(title);

        outerVbox.AddChild(new HSeparator());

        // ── Biome tabs ──
        _biomeTabRow = new HBoxContainer();
        _biomeTabRow.AddThemeConstantOverride("separation", 8);
        outerVbox.AddChild(_biomeTabRow);

        // ── Sort tabs ──
        var sortRowOuter = new HBoxContainer();
        sortRowOuter.AddThemeConstantOverride("separation", 8);
        var sortLabel = new Label();
        sortLabel.Text = "Sort by:";
        sortLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f, 0.9f));
        sortLabel.AddThemeFontSizeOverride("font_size", 14);
        ApplyKreonFont(sortLabel);
        sortRowOuter.AddChild(sortLabel);

        _sortTabRow = new HBoxContainer();
        _sortTabRow.AddThemeConstantOverride("separation", 4);
        sortRowOuter.AddChild(_sortTabRow);
        outerVbox.AddChild(sortRowOuter);

        // ── Sort character tabs ──
        var sortCharOuter = new HBoxContainer();
        sortCharOuter.AddThemeConstantOverride("separation", 8);
        var sortCharLabel = new Label();
        sortCharLabel.Text = "Score by:";
        sortCharLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f, 0.9f));
        sortCharLabel.AddThemeFontSizeOverride("font_size", 14);
        ApplyKreonFont(sortCharLabel);
        sortCharOuter.AddChild(sortCharLabel);

        _sortCharRow = new HBoxContainer();
        _sortCharRow.AddThemeConstantOverride("separation", 4);
        sortCharOuter.AddChild(_sortCharRow);
        outerVbox.AddChild(sortCharOuter);

        outerVbox.AddChild(new HSeparator());

        // ── Main content: left list | separator | right detail ──
        var contentSplit = new HBoxContainer();
        contentSplit.AddThemeConstantOverride("separation", 16);
        contentSplit.SizeFlagsVertical = SizeFlags.ExpandFill;
        outerVbox.AddChild(contentSplit);

        // Left: sticky stats-column header on top + scrollable encounter list below.
        var leftSection = new VBoxContainer();
        leftSection.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        leftSection.SizeFlagsVertical = SizeFlags.ExpandFill;
        leftSection.SizeFlagsStretchRatio = 1.4f;
        leftSection.AddThemeConstantOverride("separation", 4);
        contentSplit.AddChild(leftSection);

        // Sticky stats-column header — labels whichever stat the right-hand stat column is
        // currently displaying so the column is never mysterious. Wrapped in a margin
        // container so its right edge lines up with the row stat column (which sits to the
        // left of the scrollbar gutter).
        var headerMargin = new MarginContainer();
        headerMargin.AddThemeConstantOverride("margin_right", 0); // populated below
        leftSection.AddChild(headerMargin);

        _statsColumnHeaderRow = new HBoxContainer();
        _statsColumnHeaderRow.CustomMinimumSize = new Vector2(0, 22);
        _statsColumnHeaderRow.AddThemeConstantOverride("separation", 6);
        _statsColumnHeaderRow.MouseFilter = MouseFilterEnum.Ignore;
        headerMargin.AddChild(_statsColumnHeaderRow);

        _headerMarginContainer = headerMargin;

        leftSection.AddChild(new HSeparator());

        // Use NScrollableContainer + NScrollbar so the scroll bar matches the rest of the
        // game (settings page / Mod Configuration page) instead of Godot's default chrome.
        // Required structure (driven by NScrollableContainer._Ready):
        //   ScrollContainer (NScrollableContainer)
        //     ├─ Scrollbar         (NScrollbar — must be a direct child before _Ready runs)
        //     └─ Clipper (Control with ClipContents)
        //          └─ EncounterList (the actual scrollable content; passed to SetContent)
        var bestiaryScroll = new NScrollableContainer
        {
            Name = "ScrollContainer",
            ClipChildren = CanvasItem.ClipChildrenMode.Only,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };

        // Slimmer than the BaseLib mod-config scroll (48px) — encounter list cells are short
        // and we want most of the horizontal space for the names + stat column. Track is a
        // bit wider than before; the grabber Handle inside it is shrunk via Scale to make it
        // smaller than the track.
        const float ScrollbarWidth = 26f;
        const float ScrollbarGap   = 4f;

        // Instantiate the game's scrollbar scene so we get the same texture/animation chrome
        // used by the settings and Mod Configuration pages.
        var bestiaryScrollbar = PreloadManager.Cache.GetScene(SceneHelper.GetScenePath("ui/scrollbar"))
            .Instantiate<NScrollbar>();
        bestiaryScrollbar.Name = "Scrollbar";
        bestiaryScrollbar.AnchorLeft   = 1f;
        bestiaryScrollbar.AnchorRight  = 1f;
        bestiaryScrollbar.AnchorTop    = 0f;
        bestiaryScrollbar.AnchorBottom = 1f;
        bestiaryScrollbar.OffsetLeft   = -ScrollbarWidth;
        bestiaryScrollbar.OffsetRight  = 0;
        bestiaryScrollbar.OffsetTop    = 0;
        bestiaryScrollbar.OffsetBottom = 0;
        // No outer Scale on the whole scrollbar — that would also shrink the track. Instead
        // we shrink the %Handle child after the scene's _Ready runs (deferred via callable).
        bestiaryScroll.AddChild(bestiaryScrollbar);

        var capturedScrollbar = bestiaryScrollbar;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(capturedScrollbar)) return;
            var handle = ((Node)capturedScrollbar).GetNodeOrNull<Control>("%Handle");
            if (handle == null) return;

            // Disable mouse events on the handle itself. The game's native scrollbar wires
            // a hover animation on the handle that expands it back to full size on hover,
            // overriding our Scale. Setting MouseFilter.Ignore prevents that animation from
            // firing; the parent NScrollbar handles drag/click input on its own whole rect
            // via _GuiInput, so the scrollbar still works without the handle absorbing
            // events directly. Also recurse into any children (the handle is often a
            // composite of TextureRect + overlay nodes, each of which may have its own
            // MouseEntered wiring).
            DisableMouseRecursive(handle);

            // Shrink the grabber so it sits smaller than the track. Pivot at center so the
            // shrink happens symmetrically inside the track column.
            handle.PivotOffset = handle.Size / 2f;
            handle.Scale = new Vector2(0.55f, 0.85f);
        }).CallDeferred();

        _bestiaryScrollContainer = bestiaryScroll;

        // Clipper sits to the left of the scrollbar; its ClipContents=true keeps the long
        // encounter list from drawing outside the visible area while it's scrolled.
        var bestiaryClipper = new Control
        {
            Name = "Clipper",
            ClipContents = true,
            AnchorLeft   = 0f,
            AnchorTop    = 0f,
            AnchorRight  = 1f,
            AnchorBottom = 1f,
            OffsetRight  = -(ScrollbarWidth + ScrollbarGap),
            MouseFilter  = Control.MouseFilterEnum.Stop,
        };
        bestiaryScroll.AddChild(bestiaryClipper);

        _encounterList = new VBoxContainer();
        _encounterList.Name = "EncounterList";
        _encounterList.AnchorLeft   = 0f;
        _encounterList.AnchorTop    = 0f;
        _encounterList.AnchorRight  = 1f;
        _encounterList.AnchorBottom = 0f;
        // Let the list grow downward as content fills in; NScrollableContainer reads its
        // ItemRectChanged signal to recalculate scroll bounds.
        _encounterList.GrowVertical   = Control.GrowDirection.End;
        _encounterList.GrowHorizontal = Control.GrowDirection.End;
        _encounterList.AddThemeConstantOverride("separation", 0);
        bestiaryClipper.AddChild(_encounterList);

        leftSection.AddChild(bestiaryScroll);
        // Tell NScrollableContainer which Control to scroll. Must run after the scrollbar
        // child is in place (otherwise its _Ready throws looking up "Scrollbar").
        bestiaryScroll.SetContent(_encounterList);
        bestiaryScroll.DisableScrollingIfContentFits();

        // Match the column header's right inset to the clipper's right inset so that the
        // header text column lines up with the row stat column instead of bleeding out into
        // the scrollbar gutter.
        if (_headerMarginContainer != null)
            _headerMarginContainer.AddThemeConstantOverride("margin_right", (int)(ScrollbarWidth + ScrollbarGap));

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

        // Wrap title + table in a vbox so the title (Kreon, gold) sits above the monospace
        // table without sharing fonts with it.
        var statsVbox = new VBoxContainer();
        statsVbox.AddThemeConstantOverride("separation", 4);
        statsVbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        statsVbox.SizeFlagsVertical = SizeFlags.ExpandFill;
        statsBox.AddChild(statsVbox);

        // Title — encounter / category name in Kreon, golden, matching stats tooltip headers.
        _statsTitleLabel = new RichTextLabel();
        _statsTitleLabel.BbcodeEnabled = true;
        _statsTitleLabel.FitContent = true;
        _statsTitleLabel.ScrollActive = false;
        _statsTitleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _statsTitleLabel.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _statsTitleLabel.AddThemeColorOverride("default_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        _statsTitleLabel.AddThemeFontSizeOverride("normal_font_size", 22);
        _statsTitleLabel.AddThemeFontSizeOverride("bold_font_size", 22);
        ApplyKreonFont(_statsTitleLabel, bold: true);
        ApplyTextShadow(_statsTitleLabel);
        _statsTitleLabel.Text = "";
        statsVbox.AddChild(_statsTitleLabel);

        // Stats label uses the monospace font (the table is the only Kreon-exempt UI element).
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
        statsVbox.AddChild(_statsLabel);

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
        _rowsByEncounter.Clear();
        _iconBundles.Clear();
        _grownTargets.Clear();
        _animatingHandles.Clear();
        _highlightTween?.Kill();
        _highlightTween = null;

        if (_statsLabel != null)
            _statsLabel.Text = $"[color=#606060]Hover an encounter to see stats[/color]";
        SetStatsTitle("");
        _hoveredEncounterId = null;

        RebuildBiomeTabs();
        RebuildSortTabs();
        RebuildSortCharRow();

        var encounters = GetEncountersForBiome(_selectedBiome);
        var categories = new[] { "weak", "normal", "elite", "boss", "event", "unknown" };
        var filter = BuildFilter();

        RefreshStatsColumnHeader();

        foreach (var category in categories)
        {
            // Use Derive() rather than meta.Category so overrides (e.g. OVERGROWTH_CRAWLERS →
            // normal) take effect even on data parsed before the override existed.
            var catEncounters = encounters
                .Where(e => EncounterCategory.Derive(e) == category)
                .ToList();
            catEncounters = SortEncounters(catEncounters, _sortMode, _sortDescending, filter, _sortCharacter);

            if (catEncounters.Count == 0) continue;

            bool collapsed = _collapsedCategories.Contains(category);
            var bundle = new CategoryHoverBundle();
            _iconBundles[category] = bundle;

            // ── Category header row, wrapped in a PanelContainer so the same hover-highlight
            // background as encounter rows applies. The panel uses identical content margins
            // to the empty default so swapping styles doesn't shift any layout.
            var catWrap = new PanelContainer();
            catWrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
            catWrap.MouseFilter = MouseFilterEnum.Stop;

            var catRow = new HBoxContainer();
            // Use a compact row height for category headers — the icon wrapper now uses a
            // fixed CategoryIconRowHeightPx and lets larger icons overflow vertically into
            // the row margin, so we don't need the row to be as tall as the icon itself.
            catRow.CustomMinimumSize = new Vector2(0, CategoryRowMinHeightPx);
            catRow.AddThemeConstantOverride("separation", 8);
            catRow.MouseFilter = MouseFilterEnum.Ignore;
            // Vertically center children inside the row.
            catRow.Alignment = BoxContainer.AlignmentMode.Center;
            catWrap.AddChild(catRow);

            // Collapse arrow indicator
            var arrowLabel = new Label();
            arrowLabel.Text = collapsed ? "▶" : "▼";
            arrowLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f, 0.9f));
            arrowLabel.AddThemeFontSizeOverride("font_size", 13);
            arrowLabel.CustomMinimumSize = new Vector2(14, 0);
            arrowLabel.MouseFilter = MouseFilterEnum.Ignore;
            arrowLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            arrowLabel.VerticalAlignment = VerticalAlignment.Center;
            ApplyKreonFont(arrowLabel);
            ApplyTextShadow(arrowLabel);
            catRow.AddChild(arrowLabel);

            // Build the category icon inside a hover wrapper so we can scale + outline it.
            var catSize = EncounterIcons.CategoryIconSize(category);
            var catHandle = EncounterIcons.MakeCategoryHoverIcon(category, catSize);
            if (catHandle != null)
            {
                catHandle.Wrapper.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                catRow.AddChild(catHandle.Wrapper);
                bundle.HeaderIcon = catHandle;
            }

            var catLabel = new RichTextLabel();
            catLabel.BbcodeEnabled = true;
            catLabel.FitContent = true;
            catLabel.ScrollActive = false;
            catLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            catLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            catLabel.MouseFilter = MouseFilterEnum.Ignore;
            catLabel.AddThemeFontSizeOverride("normal_font_size", 19);
            catLabel.AddThemeFontSizeOverride("bold_font_size", 19);
            ApplyKreonFont(catLabel);
            ApplyTextShadow(catLabel);
            var catColorHex = EncounterIcons.CategoryColorHex(category);
            catLabel.Text = $"[b][color={catColorHex}]{EncounterCategory.FormatCategory(category)}[/color][/b]";
            catRow.AddChild(catLabel);

            // Hover the category header to show pool-aggregate stats; click to collapse/expand.
            var capturedCategory = category;
            var capturedEncIds = catEncounters;
            catWrap.MouseEntered += () =>
            {
                catWrap.AddThemeStyleboxOverride("panel", s_rowHighlightStyle);
                HighlightCategory(capturedCategory, true);
                OnCategoryHover(capturedCategory, capturedEncIds);
            };
            catWrap.MouseExited += () =>
            {
                catWrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
                HighlightCategory(capturedCategory, false);
                OnEncounterUnhover();
            };
            catWrap.GuiInput += (InputEvent ev) =>
            {
                if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    ToggleCategoryCollapse(capturedCategory);
            };

            _encounterList.AddChild(catWrap);

            if (collapsed) continue;

            foreach (var encId in catEncounters)
            {
                var entry = BuildEncounterEntry(encId, _sortMode, filter, _sortCharacter, category, bundle);
                _rowsByEncounter[encId] = entry;
                _encounterList.AddChild(entry);
            }
        }

        // Force the scroll container to recompute its content limit so scroll bounds match
        // the actual list size for the current biome (not the largest biome we've shown).
        // We re-call SetContent on a deferred frame: NScrollableContainer's
        // UpdateScrollLimitBottom reads _content.Size.Y, which is only accurate after the
        // VBoxContainer has finished re-laying out its children — i.e. next frame.
        if (_bestiaryScrollContainer != null && _encounterList != null)
        {
            var scroll = _bestiaryScrollContainer;
            var content = _encounterList;
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(scroll) || !GodotObject.IsInstanceValid(content)) return;
                try
                {
                    scroll.SetContent(content);
                    scroll.InstantlyScrollToTop();
                }
                catch { /* defensive — content tree may have changed since this was queued */ }
            }).CallDeferred();
        }
    }

    private const float StatColumnWidthPx = 70f;
    private const float StatColumnRightPadPx = 24f;
    private const int RowHeightPx = 30;
    /// <summary>Boss rows get a bit more vertical room than text rows so the per-boss icon
    /// has breathing space without forcing the text rows to grow with it.</summary>
    private const int BossRowHeightPx = 40;
    /// <summary>Category header rows are kept tight; the icon wrapper handles its own
    /// vertical extent and overflows into the row margin if needed.</summary>
    private const int CategoryRowMinHeightPx = 34;

    /// <summary>
    /// Applies the same drop shadow used by the in-game stats tooltip text. Centralised here
    /// so every label / RichTextLabel / Button in the bestiary picks up consistent text shadows.
    /// </summary>
    internal static void ApplyTextShadow(Control control)
    {
        var shadow = new Color(0f, 0f, 0f, 0.55f);
        switch (control)
        {
            case RichTextLabel rt:
                rt.AddThemeColorOverride("font_shadow_color", shadow);
                rt.AddThemeConstantOverride("shadow_offset_x", 2);
                rt.AddThemeConstantOverride("shadow_offset_y", 2);
                rt.AddThemeConstantOverride("shadow_outline_size", 0);
                break;
            case Label lb:
                lb.AddThemeColorOverride("font_shadow_color", shadow);
                lb.AddThemeConstantOverride("shadow_offset_x", 2);
                lb.AddThemeConstantOverride("shadow_offset_y", 2);
                lb.AddThemeConstantOverride("shadow_outline_size", 0);
                break;
            case Button bt:
                // Buttons use Godot's font outline rather than shadow_offset_x/y. Use a small
                // outline as the "shadow" so the chip button text reads against any background.
                bt.AddThemeColorOverride("font_outline_color", shadow);
                bt.AddThemeConstantOverride("outline_size", 4);
                break;
        }
    }

    private void RefreshStatsColumnHeader()
    {
        if (_statsColumnHeaderRow == null) return;

        while (_statsColumnHeaderRow.GetChildCount() > 0)
        {
            var child = _statsColumnHeaderRow.GetChild(0);
            _statsColumnHeaderRow.RemoveChild(child);
            child.QueueFree();
        }

        var displayMode = _sortMode == EncounterSortMode.Name ? EncounterSortMode.DmgPct : _sortMode;

        var spacer = new Control();
        spacer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _statsColumnHeaderRow.AddChild(spacer);

        var headerLabel = new RichTextLabel();
        headerLabel.BbcodeEnabled = true;
        headerLabel.FitContent = true;
        headerLabel.ScrollActive = false;
        headerLabel.CustomMinimumSize = new Vector2(StatColumnWidthPx, 0);
        headerLabel.MouseFilter = MouseFilterEnum.Ignore;
        headerLabel.AddThemeFontSizeOverride("normal_font_size", 14);
        headerLabel.AddThemeFontSizeOverride("bold_font_size", 14);
        ApplyKreonFont(headerLabel);
        headerLabel.Text = $"[right][color=#9a9484]{EncounterSorting.Label(displayMode)}[/color][/right]";
        _statsColumnHeaderRow.AddChild(headerLabel);

        _statsColumnHeaderRow.AddChild(new Control { CustomMinimumSize = new Vector2(StatColumnRightPadPx, 0) });
    }

    // Global highlight state.
    //
    //   _grownTargets    — the icons that *should currently* be at the grown scale (i.e. the
    //                       latest hover target). Cleared on unhover.
    //   _animatingHandles — every handle that's been touched by a tween and may not yet be
    //                       at its base resting state. Used to make sure a row-to-row hover
    //                       doesn't strand a partially-shrunk icon when its tween is killed
    //                       and replaced with a new one.
    //
    // Both sets are diffed inside TransitionHighlight, which kills any in-flight tween and
    // builds a fresh one targeting the union (so every relevant icon gets animated to its
    // correct destination, regardless of where its previous tween left it).
    private readonly HashSet<IconHoverHandle> _grownTargets    = new();
    private readonly HashSet<IconHoverHandle> _animatingHandles = new();
    private Tween? _highlightTween;

    /// <summary>
    /// Highlights a subset of icons with the run-history-style scale + outline fade tween.
    /// Hovering a category header highlights only the header icon. Hovering an encounter row
    /// highlights the header icon plus that row's own icon. Going from one row to another
    /// (or from a row to a header) animates the previous icons back to base in the same
    /// tween that grows the new ones, so transitions are seamless.
    /// </summary>
    private void HighlightCategory(string category, bool on, string? encounterId = null)
    {
        var newGrown = new HashSet<IconHoverHandle>();
        if (on && _iconBundles.TryGetValue(category, out var bundle))
        {
            if (bundle.HeaderIcon != null) newGrown.Add(bundle.HeaderIcon);
            if (encounterId != null && bundle.RowIcons.TryGetValue(encounterId, out var rowIcon))
                newGrown.Add(rowIcon);
        }

        TransitionHighlight(newGrown);
    }

    /// <summary>
    /// Cancels any in-flight tween and builds a single new tween that animates *every*
    /// handle the bestiary cares about — both the new grown target set and any leftover
    /// in-flight handles — to its correct destination. Run-history feel: 0.05s ease in on
    /// highlight, 1.0s ease out on unhighlight.
    /// </summary>
    private void TransitionHighlight(HashSet<IconHoverHandle> newGrown)
    {
        _grownTargets.RemoveWhere(h => !GodotObject.IsInstanceValid(h.Icon));
        _animatingHandles.RemoveWhere(h => !GodotObject.IsInstanceValid(h.Icon));

        // Anything we were animating that isn't in the new grown set must shrink. Anything in
        // the new grown set must grow (even if it was already grown — it's a no-op tween).
        var allHandles = new HashSet<IconHoverHandle>(_animatingHandles);
        foreach (var h in newGrown) allHandles.Add(h);

        if (allHandles.Count == 0) return;

        _highlightTween?.Kill();
        var tween = CreateTween().SetParallel(true);
        _highlightTween = tween;

        var grownScale = Vector2.One * 1.3f;
        var baseScale  = Vector2.One;

        foreach (var h in allHandles)
        {
            if (!GodotObject.IsInstanceValid(h.Icon)) continue;

            bool shouldBeGrown = newGrown.Contains(h);
            var targetScale = shouldBeGrown ? grownScale : baseScale;
            var targetAlpha = shouldBeGrown ? 1.0f : 0f;
            // Run history feel: snap up on hover, smooth ease back on unhover. The user
            // asked for the unhover to be a bit quicker than run history's full second.
            var dur         = shouldBeGrown ? 0.05 : 0.40;

            tween.TweenProperty(h.Icon, "scale", targetScale, dur)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quart);

            // Handles without a silhouette overlay (e.g. boss row icons) only animate scale.
            if (h.Highlight != null && GodotObject.IsInstanceValid(h.Highlight))
            {
                // Treat the icon + silhouette as one sprite — both scale together so the
                // halo stays at the same relative offset at any scale; only modulate alpha
                // animates independently to fade the halo in and out.
                tween.TweenProperty(h.Highlight, "scale",      targetScale, dur)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quart);
                tween.TweenProperty(h.Highlight, "modulate:a", targetAlpha, dur)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quart);
            }
        }

        // _grownTargets is the latest desired-grown set. _animatingHandles is the union of
        // everything we just touched — these stay until the next refresh clears them or
        // they're explicitly told to shrink in a future transition.
        _grownTargets.Clear();
        foreach (var h in newGrown) _grownTargets.Add(h);
        _animatingHandles.Clear();
        foreach (var h in allHandles) _animatingHandles.Add(h);
    }

    private void ToggleCategoryCollapse(string category)
    {
        if (!_collapsedCategories.Add(category))
            _collapsedCategories.Remove(category);
        RefreshEncounterList();
    }

    private void RebuildSortTabs()
    {
        if (_sortTabRow == null) return;

        while (_sortTabRow.GetChildCount() > 0)
        {
            var child = _sortTabRow.GetChild(0);
            _sortTabRow.RemoveChild(child);
            child.QueueFree();
        }

        foreach (var mode in EncounterSorting.AllModes)
        {
            bool selected = mode == _sortMode;
            var btn = MakeChipButton(
                BuildSortLabel(mode, selected),
                selected,
                () =>
                {
                    if (_sortMode == mode)
                        _sortDescending = !_sortDescending;
                    else
                    {
                        _sortMode = mode;
                        _sortDescending = mode != EncounterSortMode.Name; // Name defaults A→Z
                    }
                    RefreshEncounterList();
                });
            _sortTabRow.AddChild(btn);
        }
    }

    private string BuildSortLabel(EncounterSortMode mode, bool selected)
    {
        var label = EncounterSorting.Label(mode);
        if (!selected) return label;
        // Name has its own arrow semantics (A↓ vs Z↑); other modes show high→low / low→high
        var arrow = _sortDescending ? "↓" : "↑";
        return $"{label} {arrow}";
    }

    private void RebuildSortCharRow()
    {
        if (_sortCharRow == null) return;

        while (_sortCharRow.GetChildCount() > 0)
        {
            var child = _sortCharRow.GetChild(0);
            _sortCharRow.RemoveChild(child);
            child.QueueFree();
        }

        // "All" pseudo-character + canonical roster + any modded characters present in data
        var options = new List<(string? id, string label)> { (null, "All") };
        foreach (var (id, label) in EncounterTooltipHelper.CanonicalCharacters)
            options.Add((id, label));

        // Append any non-canonical characters that show up in EncounterMeta data
        var canonicalIds = EncounterTooltipHelper.CanonicalCharacters.Select(t => t.id).ToHashSet();
        var extraChars = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var contextMap in MainFile.Db.Encounters.Values)
            foreach (var key in contextMap.Keys)
            {
                var ctx = RunContext.Parse(key);
                if (!canonicalIds.Contains(ctx.Character))
                    extraChars.Add(ctx.Character);
            }
        foreach (var id in extraChars)
            options.Add((id, FormatCharacterShortName(id)));

        foreach (var (id, label) in options)
        {
            bool selected = id == _sortCharacter;
            var capturedId = id;
            var btn = MakeChipButton(label, selected, () =>
            {
                _sortCharacter = capturedId;
                RefreshEncounterList();
            });
            _sortCharRow.AddChild(btn);
        }
    }

    private Button MakeChipButton(string text, bool selected, Action onPressed)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(0, 28);

        var style = new StyleBoxFlat();
        style.BgColor = selected
            ? new Color(0.20f, 0.22f, 0.30f, 1f)
            : new Color(0.10f, 0.11f, 0.15f, 0.85f);
        style.BorderColor = selected
            ? new Color(0.918f, 0.745f, 0.318f, 0.8f)
            : new Color(0.30f, 0.33f, 0.40f, 0.6f);
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(3);
        style.ContentMarginLeft = 8;
        style.ContentMarginRight = 8;
        style.ContentMarginTop = 2;
        style.ContentMarginBottom = 2;
        btn.AddThemeStyleboxOverride("normal", style);

        var hover = (StyleBoxFlat)style.Duplicate();
        hover.BgColor = new Color(0.22f, 0.24f, 0.32f, 1f);
        btn.AddThemeStyleboxOverride("hover", hover);

        btn.AddThemeColorOverride("font_color",
            selected ? new Color(0.918f, 0.745f, 0.318f, 1f) : new Color(0.75f, 0.73f, 0.68f, 1f));
        btn.AddThemeColorOverride("font_hover_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        btn.AddThemeFontSizeOverride("font_size", 14);
        ApplyKreonFont(btn);
        ApplyTextShadow(btn);

        btn.Pressed += () => onPressed();
        return btn;
    }

    /// <summary>
    /// Walks a node subtree and sets every Control's MouseFilter to Ignore. Used to pin the
    /// native scrollbar handle visuals so no hover animation can override our Scale.
    /// </summary>
    private static void DisableMouseRecursive(Node node)
    {
        if (node is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in node.GetChildren())
            DisableMouseRecursive(child);
    }

    private static string FormatCharacterShortName(string charId)
    {
        var entry = charId.StartsWith("CHARACTER.") ? charId["CHARACTER.".Length..] : charId;
        if (entry.Length == 0) return charId;
        return char.ToUpper(entry[0]) + entry[1..].ToLowerInvariant();
    }

    /// <summary>
    /// Applies the Kreon font (game's primary serif) to a Control. Used for everything in the
    /// bestiary page except the stats table on the right, which keeps the monospace font.
    /// </summary>
    internal static void ApplyKreonFont(Control control, bool bold = false)
    {
        var font = bold ? (TooltipHelper.Fonts.Bold ?? TooltipHelper.Fonts.Normal)
                        : (TooltipHelper.Fonts.Normal ?? TooltipHelper.Fonts.Bold);
        if (font == null) return;

        switch (control)
        {
            case RichTextLabel rt:
                rt.AddThemeFontOverride("normal_font", font);
                if (TooltipHelper.Fonts.Bold != null)
                    rt.AddThemeFontOverride("bold_font", TooltipHelper.Fonts.Bold);
                break;
            case Label lb:
                lb.AddThemeFontOverride("font", font);
                break;
            case Button bt:
                bt.AddThemeFontOverride("font", font);
                break;
            default:
                control.AddThemeFontOverride("font", font);
                break;
        }
    }

    private static List<string> SortEncounters(
        List<string> encounters,
        EncounterSortMode mode,
        bool descending,
        AggregationFilter filter,
        string? sortCharacter)
    {
        if (mode == EncounterSortMode.Name)
        {
            // For Name, "descending" toggle means Z→A; default "ascending" is A→Z. The state
            // flag is named after the high→low convention used by the stat-based sorts, so for
            // Name we treat _sortDescending=false as the natural A→Z direction.
            return descending
                ? encounters.OrderByDescending(EncounterCategory.FormatName, StringComparer.OrdinalIgnoreCase).ToList()
                : encounters.OrderBy(EncounterCategory.FormatName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // For stat-based sorts, compute a single aggregate score per encounter under the filter
        // (optionally restricted to a specific character) and sort. Encounters with no data fall
        // to the bottom but remain alphabetically grouped among themselves.
        var scored = encounters
            .Select(id => (id, score: EncounterSorting.Score(id, mode, filter, sortCharacter)))
            .ToList();

        IOrderedEnumerable<(string id, double? score)> sorted =
            scored.OrderByDescending(t => t.score.HasValue);
        sorted = descending
            ? sorted.ThenByDescending(t => t.score ?? 0)
            : sorted.ThenBy(t => t.score ?? 0);
        return sorted
            .ThenBy(t => EncounterCategory.FormatName(t.id), StringComparer.OrdinalIgnoreCase)
            .Select(t => t.id)
            .ToList();
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
            ApplyKreonFont(btn, bold: true);
            ApplyTextShadow(btn);

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

    // Both styles use IDENTICAL content margins so swapping them on hover never moves layout.
    // The only visual difference is the highlight's background color and left accent border.
    private const int RowMarginLeftPx   = 4;
    private const int RowMarginRightPx  = 4;
    private const int RowMarginTopPx    = 1;
    private const int RowMarginBottomPx = 1;

    private static readonly StyleBoxFlat s_rowHighlightStyle = MakeRowHighlightStyle();
    private static StyleBoxFlat MakeRowHighlightStyle()
    {
        var s = new StyleBoxFlat();
        s.BgColor      = new Color(0.918f, 0.745f, 0.318f, 0.18f);
        s.BorderColor  = new Color(0.918f, 0.745f, 0.318f, 0.55f);
        s.BorderWidthLeft   = 0; // border drawn via stylebox border affects content margins; keep 0
        s.SetCornerRadiusAll(2);
        s.ContentMarginLeft   = RowMarginLeftPx;
        s.ContentMarginRight  = RowMarginRightPx;
        s.ContentMarginTop    = RowMarginTopPx;
        s.ContentMarginBottom = RowMarginBottomPx;
        return s;
    }

    /// <summary>
    /// Empty stylebox configured with the same content margins as the highlight, so a row's
    /// inner layout doesn't shift when we swap between hovered/unhovered states.
    /// </summary>
    private static StyleBoxEmpty MakeRowEmptyStyle()
    {
        var s = new StyleBoxEmpty();
        s.ContentMarginLeft   = RowMarginLeftPx;
        s.ContentMarginRight  = RowMarginRightPx;
        s.ContentMarginTop    = RowMarginTopPx;
        s.ContentMarginBottom = RowMarginBottomPx;
        return s;
    }

    private PanelContainer BuildEncounterEntry(
        string encounterId,
        EncounterSortMode sortMode,
        AggregationFilter filter,
        string? sortCharacter,
        string category,
        CategoryHoverBundle bundle)
    {
        // Wrap each row in a PanelContainer so we can flip a highlight stylebox on hover/select.
        var wrap = new PanelContainer();
        wrap.MouseFilter = MouseFilterEnum.Stop;
        wrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());

        // Boss rows get a slightly taller row height so the per-boss icon has breathing
        // space without crowding the row's text. Other rows stay at the compact text-row
        // height so the list reads tightly.
        int rowHeight = category == "boss" ? BossRowHeightPx : RowHeightPx;
        var row = new HBoxContainer();
        row.CustomMinimumSize = new Vector2(0, rowHeight);
        // Match the category header row's separation so the icon column lines up.
        row.AddThemeConstantOverride("separation", 8);
        row.MouseFilter = MouseFilterEnum.Ignore;
        row.Alignment = BoxContainer.AlignmentMode.Center;
        wrap.AddChild(row);

        MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta);

        // Lead spacer mirrors the width of the category header's collapse arrow (14px) so
        // the icon column that comes next sits at the same x as the category headers.
        var leadSpacer = new Control { CustomMinimumSize = new Vector2(14, 0) };
        row.AddChild(leadSpacer);

        if (category == "boss")
        {
            var bossSize = EncounterIcons.BossRowIconSize;
            // Boss row icons get scale-only hover (no silhouette halo) — the per-boss
            // images are distinctive enough that we don't need a glow, but the size pop
            // still cues which row is active. The wrapper width is pinned to the same
            // column as the category icons so boss icons sit on the same vertical line.
            var bossHandle = EncounterIcons.MakePlainBossIcon(encounterId, bossSize, rowHeight, EncounterIcons.CategoryIconColumnWidthPx);
            if (bossHandle != null)
            {
                bossHandle.Wrapper.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                row.AddChild(bossHandle.Wrapper);
                bundle.RowIcons[encounterId] = bossHandle;
            }
            else
            {
                row.AddChild(new Control { CustomMinimumSize = new Vector2(EncounterIcons.CategoryIconColumnWidthPx, 0) });
            }
        }
        else
        {
            // Non-boss rows reserve the same column width so their text aligns past the
            // shared icon column.
            row.AddChild(new Control { CustomMinimumSize = new Vector2(EncounterIcons.CategoryIconColumnWidthPx, 0) });
        }

        var nameLabel = new RichTextLabel();
        nameLabel.BbcodeEnabled = true;
        nameLabel.FitContent = true;
        nameLabel.ScrollActive = false;
        nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        nameLabel.MouseFilter = MouseFilterEnum.Ignore;
        nameLabel.AddThemeColorOverride("default_color", new Color(0.90f, 0.88f, 0.82f, 1f));
        nameLabel.AddThemeFontSizeOverride("normal_font_size", 17);
        ApplyKreonFont(nameLabel);
        ApplyTextShadow(nameLabel);

        var encounterName = EncounterCategory.FormatName(encounterId);
        string monsterInfo = "";
        if (meta != null)
        {
            var monsterCounts = meta.MonsterIds
                .GroupBy(m => m)
                .Select(g => g.Count() > 1 ? $"{FormatMonsterName(g.Key)} x{g.Count()}" : FormatMonsterName(g.Key));
            monsterInfo = $"  [color=#505050]({string.Join(", ", monsterCounts)})[/color]";
        }
        nameLabel.Text = $"{encounterName}{monsterInfo}";
        row.AddChild(nameLabel);

        // Stat column on the right — shows whichever stat the active sort mode is keyed on
        // (Dmg% by default), filtered by character if a sort character is selected.
        var statLabel = new RichTextLabel();
        statLabel.BbcodeEnabled = true;
        statLabel.FitContent = true;
        statLabel.ScrollActive = false;
        statLabel.CustomMinimumSize = new Vector2(StatColumnWidthPx, 0);
        statLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        statLabel.MouseFilter = MouseFilterEnum.Ignore;
        statLabel.AddThemeFontSizeOverride("normal_font_size", 15);
        ApplyKreonFont(statLabel);
        ApplyTextShadow(statLabel);
        var displayMode = sortMode == EncounterSortMode.Name ? EncounterSortMode.DmgPct : sortMode;
        statLabel.Text = $"[right][color=#a8a39a]{EncounterSorting.FormatScore(encounterId, displayMode, filter, sortCharacter)}[/color][/right]";
        row.AddChild(statLabel);

        // Right pad so the stat column doesn't kiss the panel edge.
        row.AddChild(new Control { CustomMinimumSize = new Vector2(StatColumnRightPadPx, 0) });

        var capturedCategory = category;
        wrap.MouseEntered += () =>
        {
            wrap.AddThemeStyleboxOverride("panel", s_rowHighlightStyle);
            // Hovering an encounter row → highlight the category header icon AND this row's
            // own icon (selective: not every row in the category).
            HighlightCategory(capturedCategory, true, encounterId);
            OnEncounterHover(encounterId);
        };
        wrap.MouseExited += () =>
        {
            wrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
            HighlightCategory(capturedCategory, false, encounterId);
            OnEncounterUnhover();
        };

        return wrap;
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
        SetStatsTitle($"All {categoryLabel} Encounters — {biomeName}");
        _statsLabel.Text = statsText;

        // Clear the monster preview area when hovering a category (no specific monsters to show)
        ClearMonsterPreview();
    }

    private void OnEncounterUnhover()
    {
        _hoveredEncounterId = null;
        SetStatsTitle("");
        if (_statsLabel != null)
            _statsLabel.Text = $"[color=#606060]Hover an encounter to see stats[/color]";
        ClearMonsterPreview();
    }

    private void UpdateStatsPanel(string encounterId)
    {
        if (_statsLabel == null) return;

        if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap))
        {
            SetStatsTitle(EncounterCategory.FormatName(encounterId));
            _statsLabel.Text = "[color=#606060]No data[/color]";
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

        SetStatsTitle(EncounterCategory.FormatName(encounterId));
        _statsLabel.Text = statsText;
    }

    private void SetStatsTitle(string title)
    {
        if (_statsTitleLabel == null) return;
        _statsTitleLabel.Text = string.IsNullOrEmpty(title) ? "" : $"[b]{title}[/b]";
    }

    // ───────────────────────── Monster Preview ─────────────────────────

    private void RenderMonsterPreview(string encounterId)
    {
        ClearMonsterPreview();
        if (_renderArea == null) return;
        if (!MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta)) return;

        // TODO: implement on-hover Spine rendering via SubViewport.
        // Until then, render the monster names as a wrapping line — autowrap so the text
        // never pushes the render-area panel wider than its allotted column.
        var label = new Label();
        label.Name = "MonsterPreviewLabel";
        label.Text = string.Join("   ", meta.MonsterIds.Select(FormatMonsterName));
        label.AddThemeColorOverride("font_color", new Color(0.45f, 0.45f, 0.45f, 0.6f));
        label.AddThemeFontSizeOverride("font_size", 16);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SizeFlagsVertical = SizeFlags.ExpandFill;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        // Clip the text inside the render area so a single very long word can never balloon
        // the panel's width.
        label.ClipContents = true;
        ApplyKreonFont(label);
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

    private static AggregationFilter BuildFilter() => SlayTheStatsConfig.BuildSafeFilter();

    // Synthetic biome key for act-aggregated tabs (e.g. "act:1" merges Overgrowth + Underdocks).
    private const string ActPrefix = "act:";
    /// <summary>Synthetic biome key for the "All" tab — every encounter regardless of biome.</summary>
    private const string AllBiomeKey = "all:";

    private static List<string> GetBiomes()
    {
        // The biome list is built dynamically from whatever encounters appear in
        // EncounterMeta, so future content (e.g. a second Act 2 biome) appears automatically:
        //   1. group raw biomes by Act in first-seen order
        //   2. for any act with >1 biome, prepend a synthetic "act:N" combined tab
        //   3. always lead with a synthetic "all" tab that aggregates everything
        var biomeAct = new Dictionary<string, int>();
        foreach (var meta in MainFile.Db.EncounterMeta.Values)
        {
            if (!string.IsNullOrEmpty(meta.Biome) && !biomeAct.ContainsKey(meta.Biome))
                biomeAct[meta.Biome] = meta.Act;
        }

        var result = new List<string> { AllBiomeKey };
        foreach (var actGroup in biomeAct.GroupBy(kvp => kvp.Value).OrderBy(g => g.Key))
        {
            var biomesInAct = actGroup.Select(kvp => kvp.Key).ToList();
            if (biomesInAct.Count > 1)
                result.Add($"{ActPrefix}{actGroup.Key}");
            result.AddRange(biomesInAct);
        }
        return result;
    }

    private static string? GetDefaultBiome()
    {
        // Prefer "All" — every encounter regardless of biome — as the initial view.
        var biomes = GetBiomes();
        if (biomes.Count == 0) return null;
        return biomes.Contains(AllBiomeKey) ? AllBiomeKey : biomes[0];
    }

    private static List<string> GetEncountersForBiome(string? biome)
    {
        if (biome == null || biome == AllBiomeKey)
            return MainFile.Db.EncounterMeta.Keys.ToList();

        if (biome.StartsWith(ActPrefix) && int.TryParse(biome[ActPrefix.Length..], out var act))
        {
            return MainFile.Db.EncounterMeta
                .Where(kvp => kvp.Value.Act == act)
                .Select(kvp => kvp.Key)
                .ToList();
        }

        return MainFile.Db.EncounterMeta
            .Where(kvp => kvp.Value.Biome == biome)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    private static string FormatBiomeName(string biome)
    {
        if (biome == AllBiomeKey) return "All Acts";
        if (biome.StartsWith(ActPrefix) && int.TryParse(biome[ActPrefix.Length..], out var act))
            return $"Act {act}";

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

// ─────────────────────────────────────────────────────────────────────
// Encounter sorting
// ─────────────────────────────────────────────────────────────────────

internal enum EncounterSortMode
{
    Name,
    Seen,
    DeathRate,
    AvgDamage,
    DmgPct,
    Variance,
}

internal static class EncounterSorting
{
    public static readonly EncounterSortMode[] AllModes =
    {
        EncounterSortMode.Name,
        EncounterSortMode.Seen,
        EncounterSortMode.DeathRate,
        EncounterSortMode.AvgDamage,
        EncounterSortMode.DmgPct,
        EncounterSortMode.Variance,
    };

    public static string Label(EncounterSortMode mode) => mode switch
    {
        EncounterSortMode.Name      => "Name",
        EncounterSortMode.Seen      => "Seen",
        EncounterSortMode.DeathRate => "Death%",
        EncounterSortMode.AvgDamage => "Dmg",
        EncounterSortMode.DmgPct    => "Dmg%",
        EncounterSortMode.Variance  => "Variance",
        _ => mode.ToString(),
    };

    /// <summary>
    /// Computes a single sortable score for an encounter under the given filter. If a sort
    /// character is provided, the score is restricted to that character; otherwise it sums
    /// across all characters that pass the filter. Returns null if there's no data.
    /// </summary>
    public static double? Score(string encounterId, EncounterSortMode mode, AggregationFilter filter, string? sortCharacter)
    {
        if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap)) return null;

        var perChar = StatsAggregator.AggregateEncountersByCharacter(contextMap, filter);
        if (perChar.Count == 0) return null;

        long fought = 0, died = 0;
        long damageTakenSum = 0;
        double dmgPctSum = 0;
        double dmgPctSqSum = 0;

        if (sortCharacter != null)
        {
            if (!perChar.TryGetValue(sortCharacter, out var stat) || stat.Fought == 0) return null;
            fought         = stat.Fought;
            died           = stat.Died;
            damageTakenSum = stat.DamageTakenSum;
            dmgPctSum      = stat.DmgPctSum;
            dmgPctSqSum    = stat.DmgPctSqSum;
        }
        else
        {
            foreach (var stat in perChar.Values)
            {
                fought         += stat.Fought;
                died           += stat.Died;
                damageTakenSum += stat.DamageTakenSum;
                dmgPctSum      += stat.DmgPctSum;
                dmgPctSqSum    += stat.DmgPctSqSum;
            }
        }

        if (fought == 0) return null;

        return mode switch
        {
            EncounterSortMode.Seen      => fought,
            EncounterSortMode.DeathRate => (double)died / fought,
            EncounterSortMode.AvgDamage => (double)damageTakenSum / fought,
            EncounterSortMode.DmgPct    => dmgPctSum / fought,
            EncounterSortMode.Variance  => Math.Max(0, dmgPctSqSum / fought - Math.Pow(dmgPctSum / fought, 2)),
            _ => null,
        };
    }

    /// <summary>
    /// Formats the per-row stat displayed alongside an encounter name in the bestiary list.
    /// </summary>
    public static string FormatScore(string encounterId, EncounterSortMode mode, AggregationFilter filter, string? sortCharacter)
    {
        var score = Score(encounterId, mode, filter, sortCharacter);
        if (score == null) return "—";
        return mode switch
        {
            EncounterSortMode.Seen      => ((long)score.Value).ToString(),
            EncounterSortMode.DeathRate => $"{score.Value * 100:0}%",
            EncounterSortMode.AvgDamage => $"{score.Value:0.0}",
            EncounterSortMode.DmgPct    => $"{score.Value * 100:0}%",
            EncounterSortMode.Variance  => $"{score.Value * 10000:0.0}",
            _ => "—",
        };
    }
}

// ─────────────────────────────────────────────────────────────────────
// Encounter category icons / colors
// Reuses the run history icon set: res://images/ui/run_history/{slug}.png
// ─────────────────────────────────────────────────────────────────────

internal static class EncounterIcons
{
    private static readonly Dictionary<string, Texture2D?> _cache = new();

    // Per-category icon sizes. Elite and boss bumped a decent bit; weak/normal smaller bump;
    // event question mark stays small.
    public static int CategoryIconSize(string category) => category switch
    {
        "event"  => 24,
        "weak"   => 32,
        "normal" => 32,
        "elite"  => 42,
        "boss"   => 42,
        _        => 30,
    };

    /// <summary>Per-row boss icon size (used inside individual encounter entries).</summary>
    public const int BossRowIconSize = 36;

    public static string CategoryColorHex(string category) => category switch
    {
        "weak"    => "#7ea88a",  // slight green to differentiate from normal
        "normal"  => "#e0d4ad",  // warmer / more saturated brownish gold so it reads stronger
        "elite"   => "#c557b8",  // fuchsia/purple — matches run history elite color
        "boss"    => "#d6614f",
        "event"   => "#f4dc4a",  // pure-ish yellow matching the run history question mark
        _         => "#a0a0a0",
    };

    public static Color CategoryColor(string category) => category switch
    {
        "weak"    => new Color(0.494f, 0.659f, 0.541f, 1f),
        "normal"  => new Color(0.878f, 0.831f, 0.678f, 1f),
        "elite"   => new Color(0.773f, 0.341f, 0.722f, 1f),
        "boss"    => new Color(0.839f, 0.380f, 0.310f, 1f),
        "event"   => new Color(0.957f, 0.863f, 0.290f, 1f),
        _         => new Color(0.627f, 0.627f, 0.627f, 1f),
    };

    public static TextureRect? MakeCategoryIcon(string category, int sizePx)
    {
        var tex = LoadCategoryTexture(category);
        if (tex == null) return null;
        return MakeIconRect(tex, sizePx, CategoryColor(category));
    }

    public static TextureRect? MakeBossIcon(string encounterId, int sizePx)
    {
        var perBoss = LoadBossTexture(encounterId);
        // Per-boss textures are full-color in run history. If unavailable, fall back to the
        // generic elite icon tinted with the boss color.
        if (perBoss != null)
            return MakeIconRect(perBoss, sizePx, Colors.White);
        var fallback = LoadCategoryTexture("boss");
        if (fallback == null) return null;
        return MakeIconRect(fallback, sizePx, CategoryColor("boss"));
    }

    /// <summary>
    /// Width of the icon column on category header rows. Every category icon is wrapped at
    /// this exact width so their visual centers line up regardless of which icon (weak,
    /// elite, event, etc.) is being rendered. Sized to fit the largest category icon
    /// (elite/boss = 42px) plus its breathing room.
    /// </summary>
    public const int CategoryIconColumnWidthPx = 62;

    /// <summary>
    /// Vertical extent of the category icon wrapper. Smaller than the column width so the
    /// row reads compact; larger icons overflow vertically (siblings don't clip), keeping
    /// the visible row tight while still showing the full icon + halo.
    /// </summary>
    public const int CategoryIconRowHeightPx = 40;

    /// <summary>
    /// Builds a category icon wrapped in a hover-handle Control. The handle's icon and
    /// highlight overlay are exposed so the bestiary can run scale + outline tweens on hover.
    /// All category icons use the same wrapper width so their centers align across rows.
    /// </summary>
    public static NBestiaryStatsSubmenu.IconHoverHandle? MakeCategoryHoverIcon(string category, int sizePx)
    {
        var tex = LoadCategoryTexture(category);
        if (tex == null) return null;
        return BuildHoverHandle(
            tex,
            sizePx,
            CategoryColor(category),
            wrapperHeightOverride: CategoryIconRowHeightPx,
            wrapperWidthOverride:  CategoryIconColumnWidthPx);
    }

    /// <summary>
    /// Builds a boss row icon. <paramref name="rowHeightOverridePx"/> is passed through so the
    /// wrapper can claim less vertical space than the icon size, letting the icon overflow
    /// the row vertically while keeping the row text tightly packed.
    /// </summary>
    public static NBestiaryStatsSubmenu.IconHoverHandle? MakeBossHoverIcon(string encounterId, int sizePx, int? rowHeightOverridePx = null)
    {
        var perBoss = LoadBossTexture(encounterId);
        if (perBoss != null)
            return BuildHoverHandle(perBoss, sizePx, Colors.White, rowHeightOverridePx);
        var fallback = LoadCategoryTexture("boss");
        if (fallback == null) return null;
        return BuildHoverHandle(fallback, sizePx, CategoryColor("boss"), rowHeightOverridePx);
    }

    /// <summary>
    /// Builds a plain boss row icon — texture + scale-only hover handle, NO silhouette
    /// overlay. The returned handle can be registered with the bestiary's bundle so the
    /// icon still grows on hover, but no halo fades in around it.
    /// <paramref name="wrapperWidthOverride"/> pins the wrapper width to a uniform column
    /// (typically <see cref="CategoryIconColumnWidthPx"/>) so boss icons sit on the same
    /// vertical line as the category icons.
    /// </summary>
    public static NBestiaryStatsSubmenu.IconHoverHandle? MakePlainBossIcon(
        string encounterId,
        int sizePx,
        int? rowHeightOverridePx = null,
        int? wrapperWidthOverride = null)
    {
        var perBoss = LoadBossTexture(encounterId);
        Color modulate;
        Texture2D? tex;
        if (perBoss != null)
        {
            tex = perBoss;
            modulate = Colors.White;
        }
        else
        {
            tex = LoadCategoryTexture("boss");
            if (tex == null) return null;
            modulate = CategoryColor("boss");
        }

        int padding = 4;
        int outerW = wrapperWidthOverride  ?? (sizePx + padding * 2);
        int outerH = rowHeightOverridePx   ?? (sizePx + padding * 2);

        var wrapper = new Control();
        wrapper.CustomMinimumSize = new Vector2(outerW, outerH);
        wrapper.MouseFilter = Control.MouseFilterEnum.Ignore;

        var icon = new TextureRect();
        icon.Texture       = tex;
        icon.AnchorLeft    = 0.5f; icon.AnchorRight = 0.5f;
        icon.AnchorTop     = 0.5f; icon.AnchorBottom = 0.5f;
        icon.OffsetLeft    = -sizePx / 2f;
        icon.OffsetRight   =  sizePx / 2f;
        icon.OffsetTop     = -sizePx / 2f;
        icon.OffsetBottom  =  sizePx / 2f;
        icon.StretchMode   = TextureRect.StretchModeEnum.KeepAspectCentered;
        icon.ExpandMode    = TextureRect.ExpandModeEnum.IgnoreSize;
        icon.MouseFilter   = Control.MouseFilterEnum.Ignore;
        ((CanvasItem)icon).Modulate = modulate;
        // Pivot at center so scale grows symmetrically.
        icon.PivotOffset   = new Vector2(sizePx / 2f, sizePx / 2f);
        wrapper.AddChild(icon);

        return new NBestiaryStatsSubmenu.IconHoverHandle
        {
            Wrapper   = wrapper,
            Icon      = icon,
            Highlight = null,
        };
    }

    /// <summary>
    /// Pre-rendered DILATED + ANTI-ALIASED silhouette of an icon texture, cached by source
    /// texture and dilation radius. For each output pixel we find the Euclidean distance to
    /// the nearest opaque source pixel; if that distance is within the dilation radius the
    /// pixel becomes a soft-gray with alpha falling off linearly across the outermost band.
    /// The fade band gives anti-aliased edges, and the gray-not-pure-white color matches the
    /// run-history hover halo's softer look.
    ///
    /// Why this approach instead of a runtime scale or shader:
    ///   • Scaling a TextureRect bilinearly interpolates the source's anti-aliased edges,
    ///     making the rim a soft gradient that's hard to see and fights the dilation.
    ///   • A shader that recolors to white still inherits the source's anti-aliased alpha.
    ///   • Distance-based dilation in pixel space gives uniform thickness with smooth edges
    ///     that reads cleanly at any icon size.
    /// </summary>
    private static readonly Dictionary<(Texture2D src, int dilation), Texture2D> _silhouetteCache = new();

    /// <summary>Pre-multiplied silhouette tint — half-transparent white, matching the
    /// run-history halo's <c>StsColors.halfTransparentWhite</c> look.</summary>
    private static readonly Color SilhouetteColor = new(1f, 1f, 1f, 0.5f);

    /// <summary>Width of the linear alpha fade at the dilation boundary (in source pixels).</summary>
    private const float SilhouetteFadeWidth = 1.5f;

    private static Texture2D? GetSilhouetteTexture(Texture2D source, int dilationPx = 4)
    {
        var key = (source, dilationPx);
        if (_silhouetteCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var src = source.GetImage();
            if (src == null || src.IsEmpty()) return null;
            if (src.IsCompressed())
            {
                if (src.Decompress() != Error.Ok) return null;
            }

            // Convert to RGBA8 so GetPixel returns sane values regardless of source format.
            var srcRgba = (Image)src.Duplicate();
            if (srcRgba.GetFormat() != Image.Format.Rgba8)
                srcRgba.Convert(Image.Format.Rgba8);

            int srcW = srcRgba.GetWidth();
            int srcH = srcRgba.GetHeight();
            const float opaqueThreshold = 0.5f;

            // Build the destination LARGER than the source by 2*dilationPx in each dim, with
            // the source content centered inside via a (dilationPx, dilationPx) offset. This
            // gives the dilation room to expand outward even when the source icon's shape
            // touches the source texture's edges (e.g. the event question mark).
            int dstW = srcW + 2 * dilationPx;
            int dstH = srcH + 2 * dilationPx;

            // Mask is built in destination coordinates so the distance check below doesn't
            // need to translate between source/dest spaces.
            var mask = new bool[dstW * dstH];
            for (int y = 0; y < srcH; y++)
                for (int x = 0; x < srcW; x++)
                    if (srcRgba.GetPixel(x, y).A >= opaqueThreshold)
                        mask[(y + dilationPx) * dstW + (x + dilationPx)] = true;

            var dst = Image.CreateEmpty(dstW, dstH, false, Image.Format.Rgba8);
            int r2 = dilationPx * dilationPx;
            float fadeStart = Math.Max(0f, dilationPx - SilhouetteFadeWidth);
            float fadeRange = dilationPx - fadeStart;
            if (fadeRange <= 0f) fadeRange = 1f; // safety against div-by-zero
            int fadeStartSq = (int)(fadeStart * fadeStart);

            for (int y = 0; y < dstH; y++)
            {
                int yMin = Math.Max(0, y - dilationPx);
                int yMax = Math.Min(dstH - 1, y + dilationPx);
                for (int x = 0; x < dstW; x++)
                {
                    int xMin = Math.Max(0, x - dilationPx);
                    int xMax = Math.Min(dstW - 1, x + dilationPx);

                    // Find the smallest squared distance to any opaque mask pixel within
                    // the dilation radius. Break early once we hit the fully-opaque interior.
                    int bestD2 = int.MaxValue;
                    bool fullyInside = false;
                    for (int yy = yMin; yy <= yMax && !fullyInside; yy++)
                    {
                        int dy = yy - y;
                        int dy2 = dy * dy;
                        int rowOffset = yy * dstW;
                        for (int xx = xMin; xx <= xMax; xx++)
                        {
                            int dx = xx - x;
                            int d2 = dx * dx + dy2;
                            if (d2 > r2) continue;
                            if (!mask[rowOffset + xx]) continue;
                            if (d2 < bestD2) bestD2 = d2;
                            if (d2 <= fadeStartSq) { fullyInside = true; break; }
                        }
                    }

                    if (bestD2 == int.MaxValue) continue; // no opaque pixel within radius

                    float alpha;
                    if (fullyInside)
                    {
                        alpha = 1f;
                    }
                    else
                    {
                        float dist = MathF.Sqrt(bestD2);
                        // Linear falloff across the last fadeRange source pixels.
                        alpha = 1f - Math.Clamp((dist - fadeStart) / fadeRange, 0f, 1f);
                    }

                    if (alpha < 0.01f) continue;

                    var c = SilhouetteColor;
                    c.A *= alpha;
                    dst.SetPixel(x, y, c);
                }
            }

            var tex = ImageTexture.CreateFromImage(dst);
            _silhouetteCache[key] = tex;
            return tex;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] Silhouette build failed: {e.Message}");
            return null;
        }
    }

    private static NBestiaryStatsSubmenu.IconHoverHandle BuildHoverHandle(
        Texture2D tex,
        int sizePx,
        Color iconModulate,
        int? wrapperHeightOverride = null,
        int? wrapperWidthOverride  = null)
    {
        // Wrap the icon in a fixed-size Control so layout doesn't shift when the icon scales.
        // The padding gives the halo + scale tween enough room to render without being
        // cropped by neighbouring siblings or by parent clip rects (e.g. the encounter list
        // scroll clipper). Padding is sized as a fraction of the icon so larger icons get
        // proportionally more room.
        int padding = Math.Max(10, sizePx / 4);
        // Width override lets callers pin every category icon to the same column width so
        // their centers line up vertically across rows; height override lets boss rows stay
        // compact while the icon overflows. Either falls back to a square (sizePx + 2*padding).
        int outerW = wrapperWidthOverride  ?? (sizePx + padding * 2);
        int outerH = wrapperHeightOverride ?? (sizePx + padding * 2);

        var wrapper = new Control();
        wrapper.CustomMinimumSize = new Vector2(outerW, outerH);
        wrapper.MouseFilter = Control.MouseFilterEnum.Ignore;

        // Highlight overlay — same texture as the icon but rendered through the silhouette
        // shader so the result is a uniform white glow shaped like the icon. Pre-scaled
        // slightly larger than the icon so the white halo peeks out around the silhouette
        // when its alpha fades in.
        // Position the icon (and the highlight overlay) absolutely with anchors at the
        // wrapper's center, so when wrapperHeightOverride < sizePx the icon overflows the
        // wrapper bounds vertically (centered) instead of being squashed to fit.
        void AnchorCenteredIcon(Control rect)
        {
            rect.AnchorLeft = 0.5f; rect.AnchorRight = 0.5f;
            rect.AnchorTop  = 0.5f; rect.AnchorBottom = 0.5f;
            rect.OffsetLeft   = -sizePx / 2f;
            rect.OffsetRight  =  sizePx / 2f;
            rect.OffsetTop    = -sizePx / 2f;
            rect.OffsetBottom =  sizePx / 2f;
            rect.PivotOffset = new Vector2(sizePx / 2f, sizePx / 2f);
        }

        // Highlight uses a pre-DILATED + ANTI-ALIASED silhouette of the icon texture (built
        // once and cached). The silhouette texture is rendered into its own slightly-larger
        // rect so its added padding shows up as the halo at the same scale as the icon.
        //
        // Sizing math:
        //   • Dilation in SOURCE pixel space:
        //         dilation_src = TargetHaloScreenPx × source_size / display_size
        //     so the rendered halo is TargetHaloScreenPx screen pixels regardless of how
        //     high-res the source texture is.
        //   • The silhouette texture is built with dilation_src pixels of padding on every
        //     side (see GetSilhouetteTexture). Its dimensions are (source + 2*dilation_src).
        //   • To render that texture so the inner icon area lines up with the icon's own
        //     rect, the silhouette TextureRect must be sized to (sizePx + 2*halo_screen_px).
        const float TargetHaloScreenPx = 2.5f;
        int sourceSize = Math.Max(tex.GetWidth(), tex.GetHeight());
        if (sourceSize == 0) sourceSize = sizePx;
        int dilation = Math.Max(2, (int)Math.Round(TargetHaloScreenPx * sourceSize / (float)sizePx));
        // The actual screen halo width given our rounded dilation; usually ≈ TargetHaloScreenPx.
        float haloScreen = dilation * sizePx / (float)sourceSize;

        var silhouetteTex = GetSilhouetteTexture(tex, dilation) ?? tex;
        var highlight = new TextureRect();
        highlight.Texture      = silhouetteTex;
        // Sized larger than the icon by haloScreen on every side so the silhouette texture's
        // built-in padding renders as the halo at the same scale as the icon's content.
        highlight.AnchorLeft   = 0.5f; highlight.AnchorRight = 0.5f;
        highlight.AnchorTop    = 0.5f; highlight.AnchorBottom = 0.5f;
        highlight.OffsetLeft   = -(sizePx / 2f + haloScreen);
        highlight.OffsetRight  =   sizePx / 2f + haloScreen;
        highlight.OffsetTop    = -(sizePx / 2f + haloScreen);
        highlight.OffsetBottom =   sizePx / 2f + haloScreen;
        highlight.PivotOffset  = new Vector2(sizePx / 2f + haloScreen, sizePx / 2f + haloScreen);
        highlight.StretchMode  = TextureRect.StretchModeEnum.KeepAspectCentered;
        highlight.ExpandMode   = TextureRect.ExpandModeEnum.IgnoreSize;
        highlight.MouseFilter  = Control.MouseFilterEnum.Ignore;
        ((CanvasItem)highlight).Modulate = new Color(1f, 1f, 1f, 0f);
        wrapper.AddChild(highlight);

        var icon = new TextureRect();
        icon.Texture       = tex;
        AnchorCenteredIcon(icon);
        icon.StretchMode   = TextureRect.StretchModeEnum.KeepAspectCentered;
        icon.ExpandMode    = TextureRect.ExpandModeEnum.IgnoreSize;
        icon.MouseFilter   = Control.MouseFilterEnum.Ignore;
        ((CanvasItem)icon).Modulate = iconModulate;
        wrapper.AddChild(icon);

        return new NBestiaryStatsSubmenu.IconHoverHandle
        {
            Wrapper   = wrapper,
            Icon      = icon,
            Highlight = highlight,
        };
    }

    private static TextureRect MakeIconRect(Texture2D tex, int sizePx, Color modulate)
    {
        var rect = new TextureRect();
        rect.Texture = tex;
        rect.CustomMinimumSize = new Vector2(sizePx, sizePx);
        rect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        rect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        rect.MouseFilter = Control.MouseFilterEnum.Ignore;
        ((CanvasItem)rect).Modulate = modulate;
        return rect;
    }

    private static Texture2D? LoadCategoryTexture(string category)
    {
        // Run history icon slugs: monster.png, elite.png, event.png. Bosses use per-id images,
        // but we tint elite.png with the boss color for the category header.
        string slug = category switch
        {
            "weak"   => "monster",
            "normal" => "monster",
            "elite"  => "elite",
            "boss"   => "elite",
            "event"  => "event",
            _        => "unknown_monster",
        };
        return LoadTexture($"res://images/ui/run_history/{slug}.png");
    }

    private static Texture2D? LoadBossTexture(string encounterId)
    {
        // Strip ENCOUNTER. prefix; the rest lowercased should match the per-boss filename.
        // E.g. "ENCOUNTER.LAGAVULIN_MATRIARCH_BOSS" -> "lagavulin_matriarch_boss.png"
        var entry = encounterId.StartsWith("ENCOUNTER.")
            ? encounterId["ENCOUNTER.".Length..]
            : encounterId;
        var slug = entry.ToLowerInvariant();
        return LoadTexture($"res://images/ui/run_history/{slug}.png");
    }

    private static Texture2D? LoadTexture(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            if (ResourceLoader.Exists(path))
                tex = ResourceLoader.Load<Texture2D>(path);
        }
        catch
        {
            tex = null;
        }
        _cache[path] = tex;
        return tex;
    }

    private static Texture2D? _bossCompositeCache;

    /// <summary>
    /// Builds a square-ish grid composite ImageTexture containing every boss icon the game
    /// has registered. Encountered bosses use their full-color icon; unencountered ones are
    /// shown as a darkened silhouette so the button still hints at the full roster.
    /// Used as the icon for the Stats Bestiary button. Cells are <paramref name="cellPx"/>
    /// per side. Returns null if no boss icons can be loaded at all.
    /// </summary>
    public static Texture2D? BuildBossCompositeTexture(int cellPx)
    {
        if (_bossCompositeCache != null) return _bossCompositeCache;

        // Pull the full boss roster from ModelDb (every encounter the game registers with
        // RoomType.Boss). This is the master list — if a boss is here but not in
        // EncounterMeta, the player hasn't fought it yet and we render a silhouette.
        var allBossIds = new List<string>();
        var encounteredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var enc in MegaCrit.Sts2.Core.Models.ModelDb.AllEncounters)
            {
                if (enc.RoomType != MegaCrit.Sts2.Core.Rooms.RoomType.Boss) continue;
                // EncounterModel.Id is a ModelId; ToString() / .ToString() yields "ENCOUNTER.X".
                var idStr = enc.Id?.ToString();
                if (string.IsNullOrEmpty(idStr)) continue;
                allBossIds.Add(idStr.ToUpperInvariant());
            }
            allBossIds = allBossIds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] Boss composite: failed to enumerate ModelDb bosses ({e.Message}); falling back to encountered set");
            allBossIds.Clear();
        }

        foreach (var (encId, _) in MainFile.Db.EncounterMeta)
            if (EncounterCategory.Derive(encId) == "boss")
                encounteredIds.Add(encId);

        // If ModelDb enumeration produced nothing, fall back to whatever the player has
        // already fought so the button is still populated.
        if (allBossIds.Count == 0)
            allBossIds = encounteredIds.OrderBy(s => s, StringComparer.Ordinal).ToList();

        if (allBossIds.Count == 0)
        {
            MainFile.Logger.Info("[SlayTheStats] Boss composite: no bosses available from ModelDb or EncounterMeta");
            return null;
        }

        // Square-ish grid: cols = ceil(sqrt(N)), rows = ceil(N / cols).
        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(allBossIds.Count)));
        int rows = (int)Math.Ceiling((double)allBossIds.Count / cols);

        int gridW = cols * cellPx;
        int gridH = rows * cellPx;
        var composite = Image.CreateEmpty(gridW, gridH, false, Image.Format.Rgba8);

        int loaded = 0, missing = 0, silhouettes = 0;
        for (int i = 0; i < allBossIds.Count; i++)
        {
            var id = allBossIds[i];
            var tex = LoadBossTexture(id);
            if (tex == null) { missing++; continue; }
            var img = tex.GetImage();
            if (img == null || img.IsEmpty()) { missing++; continue; }
            if (img.IsCompressed())
            {
                if (img.Decompress() != Error.Ok) { missing++; continue; }
            }
            // Clone + convert to RGBA8 so we can mutate without touching the cached source.
            var cell = (Image)img.Duplicate();
            if (cell.GetFormat() != Image.Format.Rgba8)
                cell.Convert(Image.Format.Rgba8);

            // Resize to fit cellPx, preserving aspect.
            int srcW = cell.GetWidth();
            int srcH = cell.GetHeight();
            if (srcW == 0 || srcH == 0) { missing++; continue; }
            double scale = Math.Min((double)cellPx / srcW, (double)cellPx / srcH);
            int newW = Math.Max(1, (int)Math.Round(srcW * scale));
            int newH = Math.Max(1, (int)Math.Round(srcH * scale));
            cell.Resize(newW, newH, Image.Interpolation.Bilinear);

            // Silhouette pass for unencountered bosses: zero out RGB but keep alpha so the
            // shape remains visible as a black blob.
            if (!encounteredIds.Contains(id))
            {
                SilhouetteImage(cell);
                silhouettes++;
            }

            // Add a black outline around every boss icon (encountered or silhouette) so the
            // icons read clearly against the warm-stone bestiary button background. Thickness
            // is 3 source pixels — visibly distinct without swallowing the icon detail.
            cell = AddBlackOutline(cell, thicknessPx: 3);

            int col = i % cols;
            int row = i / cols;
            int dstX = col * cellPx + (cellPx - newW) / 2;
            int dstY = row * cellPx + (cellPx - newH) / 2;
            composite.BlitRect(cell,
                new Rect2I(0, 0, newW, newH),
                new Vector2I(dstX, dstY));
            loaded++;
        }

        MainFile.Logger.Info($"[SlayTheStats] Boss composite: bosses={allBossIds.Count} loaded={loaded} missing={missing} silhouettes={silhouettes} grid={cols}x{rows}");

        if (loaded == 0) return null;

        _bossCompositeCache = ImageTexture.CreateFromImage(composite);
        return _bossCompositeCache;
    }

    /// <summary>
    /// In-place silhouette: collapses every visible pixel to black while preserving alpha,
    /// so the shape remains recognisable but the boss is clearly "locked".
    /// </summary>
    private static void SilhouetteImage(Image img)
    {
        int w = img.GetWidth();
        int h = img.GetHeight();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = img.GetPixel(x, y);
                if (c.A > 0)
                    img.SetPixel(x, y, new Color(0f, 0f, 0f, c.A));
            }
        }
    }

    /// <summary>
    /// Adds a solid-black outline of the given thickness (in source pixels) around every
    /// alpha-positive pixel. Returns a new image so the source image is left untouched.
    /// Pixels are filled with black wherever any source pixel within the thickness radius
    /// (Euclidean) is opaque.
    /// </summary>
    private static Image AddBlackOutline(Image src, int thicknessPx = 1)
    {
        int w = src.GetWidth();
        int h = src.GetHeight();
        var dst = (Image)src.Duplicate();
        const float threshold = 0.05f;
        var black = new Color(0f, 0f, 0f, 1f);

        // Pre-build an opaque mask so the dilation pass is O(thickness²) without GetPixel
        // overhead per neighbour.
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                mask[y * w + x] = src.GetPixel(x, y).A >= threshold;

        int r2 = thicknessPx * thicknessPx;
        for (int y = 0; y < h; y++)
        {
            int yMin = Math.Max(0, y - thicknessPx);
            int yMax = Math.Min(h - 1, y + thicknessPx);
            for (int x = 0; x < w; x++)
            {
                if (mask[y * w + x]) continue; // already opaque, skip
                int xMin = Math.Max(0, x - thicknessPx);
                int xMax = Math.Min(w - 1, x + thicknessPx);

                bool nearOpaque = false;
                for (int yy = yMin; yy <= yMax && !nearOpaque; yy++)
                {
                    int dy = yy - y;
                    int rowOffset = yy * w;
                    for (int xx = xMin; xx <= xMax; xx++)
                    {
                        int dx = xx - x;
                        if (dx * dx + dy * dy > r2) continue;
                        if (mask[rowOffset + xx]) { nearOpaque = true; break; }
                    }
                }
                if (nearOpaque)
                    dst.SetPixel(x, y, black);
            }
        }
        return dst;
    }
}
