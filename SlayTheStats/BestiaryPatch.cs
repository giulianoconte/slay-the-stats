using BaseLib.Utils;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
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

            // Replace the icon with a composite of the boss icons we have data for. The cloned
            // TextureRect from the run-history button has its own stretch/expand config tuned
            // for a square clipboard glyph; force a stretch mode that lets a wide composite
            // display correctly inside the existing slot. Falls back to a single elite icon
            // if no boss data is loaded yet.
            var iconNode = ((Node)ourButton).GetNodeOrNull<TextureRect>("Icon");
            if (iconNode != null)
            {
                Texture2D? newTex = EncounterIcons.BuildBossCompositeTexture(48);
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
                    var stackField = AccessTools.Field(typeof(NSubmenu), "_stack");
                    if (stackField?.GetValue(__instance) is NMainMenuSubmenuStack stack)
                    {
                        var submenu = stack.PushSubmenuType<NBestiaryStatsSubmenu>();
                        submenu.Refresh();
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
        public readonly List<IconHoverHandle> Handles = new();
        public Tween? ActiveTween;
    }

    public sealed class IconHoverHandle
    {
        public required Control Wrapper;
        public required TextureRect Icon;
        public required Control Highlight;
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
        title.Text = "Bestiary — Encounter Stats";
        title.AddThemeColorOverride("font_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        title.AddThemeFontSizeOverride("font_size", 26);
        ApplyKreonFont(title, bold: true);
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
        // currently displaying so the column is never mysterious.
        _statsColumnHeaderRow = new HBoxContainer();
        _statsColumnHeaderRow.CustomMinimumSize = new Vector2(0, 22);
        _statsColumnHeaderRow.AddThemeConstantOverride("separation", 6);
        _statsColumnHeaderRow.MouseFilter = MouseFilterEnum.Ignore;
        leftSection.AddChild(_statsColumnHeaderRow);

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

        const float ScrollbarWidth = 28f;
        const float ScrollbarGap   = 6f;

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
        bestiaryScroll.AddChild(bestiaryScrollbar);

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
        _encounterList.AddThemeConstantOverride("separation", 1);
        bestiaryClipper.AddChild(_encounterList);

        leftSection.AddChild(bestiaryScroll);
        // Tell NScrollableContainer which Control to scroll. Must run after the scrollbar
        // child is in place (otherwise its _Ready throws looking up "Scrollbar").
        bestiaryScroll.SetContent(_encounterList);
        bestiaryScroll.DisableScrollingIfContentFits();

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
        _rowsByEncounter.Clear();
        _iconBundles.Clear();

        if (_statsLabel != null)
            _statsLabel.Text = $"[color=#606060]Hover an encounter to see stats[/color]";
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
            catRow.CustomMinimumSize = new Vector2(0, 34);
            catRow.AddThemeConstantOverride("separation", 8);
            catRow.MouseFilter = MouseFilterEnum.Ignore;
            catWrap.AddChild(catRow);

            // Collapse arrow indicator
            var arrowLabel = new Label();
            arrowLabel.Text = collapsed ? "▶" : "▼";
            arrowLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f, 0.9f));
            arrowLabel.AddThemeFontSizeOverride("font_size", 13);
            arrowLabel.CustomMinimumSize = new Vector2(14, 0);
            arrowLabel.MouseFilter = MouseFilterEnum.Ignore;
            ApplyKreonFont(arrowLabel);
            catRow.AddChild(arrowLabel);

            // Build the category icon inside a hover wrapper so we can scale + outline it.
            var catSize = EncounterIcons.CategoryIconSize(category);
            var catHandle = EncounterIcons.MakeCategoryHoverIcon(category, catSize);
            if (catHandle != null)
            {
                catRow.AddChild(catHandle.Wrapper);
                bundle.Handles.Add(catHandle);
            }

            var catLabel = new RichTextLabel();
            catLabel.BbcodeEnabled = true;
            catLabel.FitContent = true;
            catLabel.ScrollActive = false;
            catLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            catLabel.MouseFilter = MouseFilterEnum.Ignore;
            catLabel.AddThemeFontSizeOverride("normal_font_size", 19);
            catLabel.AddThemeFontSizeOverride("bold_font_size", 19);
            ApplyKreonFont(catLabel);
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
    }

    private const float StatColumnWidthPx = 70f;
    private const float StatColumnRightPadPx = 24f;

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

    private void HighlightCategory(string category, bool on)
    {
        if (!_iconBundles.TryGetValue(category, out var bundle)) return;

        bundle.ActiveTween?.Kill();
        var tween = CreateTween().SetParallel(true);
        bundle.ActiveTween = tween;

        var targetScale = on ? Vector2.One * 1.5f : Vector2.One;
        var targetAlpha = on ? 0.5f  : 0f;
        var dur         = on ? 0.05  : 0.20;

        foreach (var h in bundle.Handles)
        {
            if (!GodotObject.IsInstanceValid(h.Icon) || !GodotObject.IsInstanceValid(h.Highlight))
                continue;
            tween.TweenProperty(h.Icon,      "scale",      targetScale, dur)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quart);
            tween.TweenProperty(h.Highlight, "modulate:a", targetAlpha, dur)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quart);
        }
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

        btn.Pressed += () => onPressed();
        return btn;
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

        var row = new HBoxContainer();
        row.CustomMinimumSize = new Vector2(0, 30);
        row.AddThemeConstantOverride("separation", 6);
        row.MouseFilter = MouseFilterEnum.Ignore;
        wrap.AddChild(row);

        MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta);

        // Indent so encounter rows align under their category header. Bosses get their own
        // per-encounter icon (run history's per-boss images) wrapped in a hover handle.
        var leadSpacer = new Control { CustomMinimumSize = new Vector2(20, 0) };
        row.AddChild(leadSpacer);

        if (category == "boss")
        {
            var bossSize = EncounterIcons.BossRowIconSize;
            var bossHandle = EncounterIcons.MakeBossHoverIcon(encounterId, bossSize);
            if (bossHandle != null)
            {
                row.AddChild(bossHandle.Wrapper);
                bundle.Handles.Add(bossHandle);
            }
            else
            {
                row.AddChild(new Control { CustomMinimumSize = new Vector2(bossSize + 8, 0) });
            }
        }
        else
        {
            // Reserve the same horizontal slot used by boss rows so encounter names align.
            var iconSlot = EncounterIcons.CategoryIconSize(category) + 8;
            row.AddChild(new Control { CustomMinimumSize = new Vector2(iconSlot, 0) });
        }

        var nameLabel = new RichTextLabel();
        nameLabel.BbcodeEnabled = true;
        nameLabel.FitContent = true;
        nameLabel.ScrollActive = false;
        nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameLabel.MouseFilter = MouseFilterEnum.Ignore;
        nameLabel.AddThemeColorOverride("default_color", new Color(0.90f, 0.88f, 0.82f, 1f));
        nameLabel.AddThemeFontSizeOverride("normal_font_size", 17);
        ApplyKreonFont(nameLabel);

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
        statLabel.MouseFilter = MouseFilterEnum.Ignore;
        statLabel.AddThemeFontSizeOverride("normal_font_size", 15);
        ApplyKreonFont(statLabel);
        var displayMode = sortMode == EncounterSortMode.Name ? EncounterSortMode.DmgPct : sortMode;
        statLabel.Text = $"[right][color=#909090]{EncounterSorting.FormatScore(encounterId, displayMode, filter, sortCharacter)}[/color][/right]";
        row.AddChild(statLabel);

        // Right pad so the stat column doesn't kiss the panel edge.
        row.AddChild(new Control { CustomMinimumSize = new Vector2(StatColumnRightPadPx, 0) });

        var capturedCategory = category;
        wrap.MouseEntered += () =>
        {
            wrap.AddThemeStyleboxOverride("panel", s_rowHighlightStyle);
            HighlightCategory(capturedCategory, true);
            OnEncounterHover(encounterId);
        };
        wrap.MouseExited += () =>
        {
            wrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
            HighlightCategory(capturedCategory, false);
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

    // Synthetic biome key for act-aggregated tabs (e.g. "act:1" merges Overgrowth + Underdocks).
    private const string ActPrefix = "act:";

    private static List<string> GetBiomes()
    {
        // Map biome → act, preserving first-seen order so we can group consistently.
        var biomeAct = new Dictionary<string, int>();
        foreach (var meta in MainFile.Db.EncounterMeta.Values)
        {
            if (!string.IsNullOrEmpty(meta.Biome) && !biomeAct.ContainsKey(meta.Biome))
                biomeAct[meta.Biome] = meta.Act;
        }

        // Group by act, then for any act with multiple biomes prepend a synthetic combined tab.
        var result = new List<string>();
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
        // Prefer the Act 1 combined view if present (Overgrowth + Underdocks), otherwise the
        // first biome.
        var biomes = GetBiomes();
        if (biomes.Count == 0) return null;
        var firstAct = biomes.FirstOrDefault(b => b.StartsWith(ActPrefix));
        return firstAct ?? biomes[0];
    }

    private static List<string> GetEncountersForBiome(string? biome)
    {
        if (biome == null) return MainFile.Db.EncounterMeta.Keys.ToList();

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
        "normal"  => "#bcb29c",  // brownish grey
        "elite"   => "#c557b8",  // fuchsia/purple — matches run history elite color
        "boss"    => "#d6614f",
        "event"   => "#e8c247",  // event question mark in run history is yellow
        _         => "#a0a0a0",
    };

    public static Color CategoryColor(string category) => category switch
    {
        "weak"    => new Color(0.494f, 0.659f, 0.541f, 1f),
        "normal"  => new Color(0.737f, 0.698f, 0.612f, 1f),
        "elite"   => new Color(0.773f, 0.341f, 0.722f, 1f),
        "boss"    => new Color(0.839f, 0.380f, 0.310f, 1f),
        "event"   => new Color(0.910f, 0.760f, 0.278f, 1f),
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
    /// Builds a category icon wrapped in a hover-handle Control. The handle's icon and
    /// highlight overlay are exposed so the bestiary can run scale + outline tweens on hover.
    /// </summary>
    public static NBestiaryStatsSubmenu.IconHoverHandle? MakeCategoryHoverIcon(string category, int sizePx)
    {
        var tex = LoadCategoryTexture(category);
        if (tex == null) return null;
        return BuildHoverHandle(tex, sizePx, CategoryColor(category));
    }

    public static NBestiaryStatsSubmenu.IconHoverHandle? MakeBossHoverIcon(string encounterId, int sizePx)
    {
        var perBoss = LoadBossTexture(encounterId);
        if (perBoss != null)
            return BuildHoverHandle(perBoss, sizePx, Colors.White);
        var fallback = LoadCategoryTexture("boss");
        if (fallback == null) return null;
        return BuildHoverHandle(fallback, sizePx, CategoryColor("boss"));
    }

    private static NBestiaryStatsSubmenu.IconHoverHandle BuildHoverHandle(Texture2D tex, int sizePx, Color iconModulate)
    {
        // Wrap the icon in a fixed-size Control so layout doesn't shift when the icon scales.
        // The wrapper has padding around the icon to give the scale tween room to breathe
        // without being clipped by neighbouring siblings.
        int padding = Math.Max(6, sizePx / 4);
        int outer = sizePx + padding * 2;

        var wrapper = new Control();
        wrapper.CustomMinimumSize = new Vector2(outer, outer);
        wrapper.MouseFilter = Control.MouseFilterEnum.Ignore;

        // Highlight overlay — drawn behind the icon, alpha animated 0→0.5 on hover. Mirrors the
        // run-history "outline modulate to halfTransparentWhite" effect.
        var highlight = new Panel();
        highlight.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        highlight.MouseFilter = Control.MouseFilterEnum.Ignore;
        var hlStyle = new StyleBoxFlat();
        hlStyle.BgColor       = new Color(1f, 1f, 1f, 0.18f);
        hlStyle.BorderColor   = new Color(1f, 1f, 1f, 0.85f);
        hlStyle.SetBorderWidthAll(2);
        hlStyle.SetCornerRadiusAll(6);
        // Insets so the border doesn't touch the wrapper's edges.
        hlStyle.ContentMarginLeft   = 0;
        hlStyle.ContentMarginRight  = 0;
        hlStyle.ContentMarginTop    = 0;
        hlStyle.ContentMarginBottom = 0;
        highlight.AddThemeStyleboxOverride("panel", hlStyle);
        ((CanvasItem)highlight).Modulate = new Color(1f, 1f, 1f, 0f);
        wrapper.AddChild(highlight);

        var icon = new TextureRect();
        icon.Texture       = tex;
        icon.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        icon.OffsetLeft    = padding;
        icon.OffsetTop     = padding;
        icon.OffsetRight   = -padding;
        icon.OffsetBottom  = -padding;
        icon.StretchMode   = TextureRect.StretchModeEnum.KeepAspectCentered;
        icon.ExpandMode    = TextureRect.ExpandModeEnum.IgnoreSize;
        icon.MouseFilter   = Control.MouseFilterEnum.Ignore;
        ((CanvasItem)icon).Modulate = iconModulate;
        // Pivot at center of the wrapper so scale grows from the middle.
        icon.PivotOffset   = new Vector2(sizePx / 2f, sizePx / 2f);
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
    /// Builds a horizontal composite ImageTexture containing every boss icon we have data for,
    /// each scaled to <paramref name="iconHeightPx"/> tall. Returns null if no boss icons load.
    /// Used as the icon for the Stats Bestiary button so the button hints at what's inside.
    /// </summary>
    public static Texture2D? BuildBossCompositeTexture(int iconHeightPx)
    {
        if (_bossCompositeCache != null) return _bossCompositeCache;

        var bossIds = MainFile.Db.EncounterMeta
            .Where(kvp => EncounterCategory.Derive(kvp.Key) == "boss")
            .Select(kvp => kvp.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (bossIds.Count == 0)
        {
            MainFile.Logger.Info("[SlayTheStats] Boss composite: no boss encounters in EncounterMeta");
            return null;
        }

        var images = new List<Image>();
        int loaded = 0, missing = 0, noImage = 0;
        foreach (var encounterId in bossIds)
        {
            var tex = LoadBossTexture(encounterId);
            if (tex == null) { missing++; continue; }
            // CompressedTexture2D.GetImage() may return null if the underlying image stream
            // wasn't kept; in that case we can't blit it into the composite.
            var img = tex.GetImage();
            if (img == null || img.IsEmpty()) { noImage++; continue; }
            // Decompress if the source was a compressed format — BlitRect requires uncompressed.
            if (img.IsCompressed())
            {
                var err = img.Decompress();
                if (err != Error.Ok) { noImage++; continue; }
            }
            images.Add(img);
            loaded++;
        }

        MainFile.Logger.Info($"[SlayTheStats] Boss composite: bosses={bossIds.Count} loaded={loaded} missing={missing} noImage={noImage}");

        if (images.Count == 0) return null;

        // Scale each image so its height matches iconHeightPx, preserving aspect ratio.
        int totalWidth = 0;
        var scaled = new List<Image>();
        foreach (var src in images)
        {
            int srcW = src.GetWidth();
            int srcH = src.GetHeight();
            if (srcH == 0) continue;
            int newW = Math.Max(1, (int)Math.Round((double)srcW * iconHeightPx / srcH));

            // Image.Resize works in place; clone first so we don't mutate the cached source.
            var copy = (Image)src.Duplicate();
            copy.Resize(newW, iconHeightPx, Image.Interpolation.Bilinear);
            scaled.Add(copy);
            totalWidth += newW;
        }

        if (scaled.Count == 0 || totalWidth == 0) return null;

        var composite = Image.CreateEmpty(totalWidth, iconHeightPx, false, Image.Format.Rgba8);
        int x = 0;
        foreach (var s in scaled)
        {
            // Convert to RGBA8 if needed so the formats match (BlitRect requires identical formats).
            if (s.GetFormat() != Image.Format.Rgba8)
                s.Convert(Image.Format.Rgba8);
            composite.BlitRect(s,
                new Rect2I(0, 0, s.GetWidth(), s.GetHeight()),
                new Vector2I(x, 0));
            x += s.GetWidth();
        }

        _bossCompositeCache = ImageTexture.CreateFromImage(composite);
        return _bossCompositeCache;
    }
}
