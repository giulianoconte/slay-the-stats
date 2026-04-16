using BaseLib.Utils;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
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
            // User opted out via the mod config — skip injection entirely.
            if (!SlayTheStatsConfig.BestiaryEnabled) return;

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
                // Earthy dirt tone — darker, desaturated brown so it reads as
                // "field guide / soil" rather than the bronze-y warm-tan it used to.
                ((CanvasItem)bgPanel).SelfModulate = new Color(0.65f, 0.59f, 0.52f, 1f);
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
    /// <summary>SlayTheStats filter button anchored at the bottom-left of the bestiary
    /// submenu (mirroring NRelicCollection's filter button position). Cloned from the
    /// game-native NCardViewSortButton template when one is cached, otherwise a plain
    /// Button fallback. Toggles _filterPane.</summary>
    private Control? _filterButton;
    /// <summary>"?" info button next to the encounter title that toggles a popup
    /// describing every stat column. Lives inline in the title row of the right-hand
    /// stats panel.</summary>
    private Button? _legendButton;
    /// <summary>The active column-legend popup, if any. Tracked so the toggle handler
    /// can find and free it on second click.</summary>
    private PanelContainer? _legendPopup;
    /// <summary>Shared filter pane built via CompendiumFilterPatch.BuildFilterPane with
    /// the class dropdown and group-card-upgrades toggle suppressed (the bestiary's
    /// "Score by:" row covers per-character scoring, and group-upgrades is a card-only
    /// concern).</summary>
    private PanelContainer? _filterPane;
    /// <summary>Bestiary-specific settings pane (preview mode, future extras). Shown
    /// alongside <see cref="_filterPane"/> whenever the SlayTheStats filter button
    /// is toggled open; positioned directly above the filter pane.</summary>
    private PanelContainer? _bestiarySettingsPane;
    private HBoxContainer? _statsColumnHeaderRow;
    private VBoxContainer? _encounterList;
    private RichTextLabel? _statsLabel;
    private RichTextLabel? _statsTitleLabel;
    // Split labels for the scrollable all-characters table. When in all-chars mode,
    // _statsLabel is hidden and these three take over. _statsHeaderLabel and
    // _statsBaselineLabel stay pinned; _statsCharRowsLabel scrolls inside _statsCharRowsClipper.
    private RichTextLabel? _statsHeaderLabel;
    private RichTextLabel? _statsCharRowsLabel;
    private RichTextLabel? _statsBaselineLabel;
    private RichTextLabel? _statsFooterLabel;
    private Control? _statsCharRowsClipper;
    private Control? _statsCharRowsWrapper; // holds clipper + scrollbar
    private NScrollbar? _statsScrollbar;
    private HSeparator? _statsScrollTopSep;
    private HSeparator? _statsScrollBottomSep;
    private MarginContainer? _statsHeaderMarginContainer;
    private MarginContainer? _statsBaselineMarginContainer;
    private NScrollableContainer? _bestiaryScrollContainer;
    private MarginContainer? _headerMarginContainer;
    private Control? _renderArea;
    /// <summary>The currently-active SubViewport — the one whose texture is
    /// displayed in <see cref="_renderArea"/>. Tracked separately from the LRU
    /// cache so <see cref="ClearMonsterPreview"/> can flip the previous active
    /// to Disabled (no GPU cost) without evicting it.</summary>
    private SubViewport? _previewViewport;
    /// <summary>LRU cache of live SubViewports keyed by encounter id. Each
    /// cached viewport keeps its NCreatureVisuals children and Spine animation
    /// state alive in the scene tree; only the active one has
    /// <c>RenderTargetUpdateMode.Always</c>, the rest are <c>Disabled</c> so the
    /// GPU only pays for one render at a time. On cache hit we reactivate the
    /// cached viewport and get animated preview instantly with no
    /// re-instantiation cost.</summary>
    private readonly Dictionary<string, SubViewport> _liveViewports = new();
    /// <summary>LRU order tracking — head = most recently used, tail = evict
    /// next. Kept as a LinkedList so move-to-head is O(1) via the stored node
    /// reference.</summary>
    private readonly LinkedList<string> _lruOrder = new();
    /// <summary>Quick lookup from encounter id to the node inside
    /// <see cref="_lruOrder"/> so moves to head are O(1).</summary>
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
    /// <summary>Static-sprite cache used when
    /// <see cref="SlayTheStatsConfig.BestiaryPreviewMode"/> = Static. First
    /// hover of each encounter still builds a SubViewport and captures its
    /// texture into an ImageTexture a few frames later; subsequent hovers
    /// display the captured texture directly, no viewport involved. Keyed by
    /// encounter id, session-lifetime (cleared when the submenu rebuilds).
    /// </summary>
    private readonly Dictionary<string, ImageTexture> _staticSpriteCache = new();
    /// <summary>Debounce timer used to suppress render work during rapid hover
    /// scrolling. Restarted on every new hover; only fires its Timeout after
    /// the mouse has settled for <see cref="HoverDebounceSeconds"/>.</summary>
    private Godot.Timer? _hoverDebounceTimer;
    /// <summary>Encounter id queued up for render after the debounce fires.
    /// Cleared when the timer actually runs the render or when a cache hit
    /// short-circuits the debounce.</summary>
    private string? _pendingHoverEncounterId;
    private string? _selectedBiome;
    private string? _hoveredEncounterId;
    /// <summary>When non-null, a row has been click-locked — hover events no longer
    /// update the stats panel / preview, and unhover no longer clears them. Click a
    /// different row to switch the lock; click anywhere that isn't an encounter row
    /// to release it. Cleared automatically when filters/biome/sort changes wipe
    /// the list.</summary>
    private string? _lockedEncounterId;
    /// <summary>The PanelContainer of the currently locked row, so we can apply
    /// / remove the locked-row highlight style independently of hover.</summary>
    private PanelContainer? _lockedRowWrap;
    /// <summary>Locked category (mutually exclusive with _lockedEncounterId).
    /// When set, category hover stats are pinned.</summary>
    private string? _lockedCategory;
    private List<string>? _lockedCategoryEncIds;
    private PanelContainer? _lockedCategoryWrap;
    private EncounterSortMode _sortMode = EncounterSortMode.Name;
    // Default direction: A→Z for Name; high→low for stat-based modes (set on mode-change).
    private bool _sortDescending = false;
    /// <summary>null = all characters; otherwise a CHARACTER.* id used for sort scoring.</summary>
    private string? _sortCharacter;
    /// <summary>When true, stat-based sort modes order by a z-score
    /// (observed vs category baseline, weighted by sample size) instead of
    /// the raw metric value. Makes well-sampled encounters with meaningful
    /// deviation surface above low-N noise. Toggled by the "Raw/Sig" chip
    /// next to the sort row. Ignored for Name and Seen.</summary>
    private bool _sortBySignificance = false;
    private readonly HashSet<string> _collapsedCategories = new();
    /// <summary>When the user collapses/expands a category by clicking its header, the
    /// list is rebuilt and the old PanelContainer the mouse was hovering is replaced.
    /// Without intervention, the new header sits unhovered (the mouse hasn't moved, so
    /// no MouseEntered fires) and the highlight flickers off. We stash the toggled
    /// category here before the rebuild and reapply hover state to the matching
    /// freshly-built catWrap.</summary>
    private string? _pendingHoverCategory;
    private readonly Dictionary<string, Control> _rowsByEncounter = new();
    /// <summary>Maps encounter ID to the inner highlight panel — the narrower
    /// PanelContainer that receives hover/lock styles (covering only name + stat,
    /// not arrow or icon).</summary>
    private readonly Dictionary<string, PanelContainer> _highlightPanels = new();
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

    /// <summary>Global input handler. Right-click anywhere on the bestiary page
    /// unconditionally clears any row/category lock — no other action.</summary>
    public override void _Input(InputEvent ev)
    {
        if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
        {
            if (_lockedEncounterId != null || _lockedCategory != null)
            {
                ClearLockedEncounter();
                GetViewport().SetInputAsHandled();
            }
        }
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

    public override void OnSubmenuOpened()
    {
        base.OnSubmenuOpened();
        // First-time visitors get a one-shot explainer overlay covering the controls and
        // stat columns. Persisted via SlayTheStatsConfig.BestiaryTutorialSeen.
        if (!SlayTheStatsConfig.BestiaryTutorialSeen)
            MaybeShowBestiaryTutorial();
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
        // Clicks in the margin padding area (left 200px, etc.) that don't hit
        // any child control land here → unlock.
        margin.MouseFilter = MouseFilterEnum.Stop;
        margin.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                ClearLockedEncounter();
        };
        AddChild(margin);

        var outerVbox = new VBoxContainer();
        outerVbox.AddThemeConstantOverride("separation", 10);
        // Catch-all for click-to-unlock: left clicks that fall through every
        // interactive child (encounter rows AcceptEvent on their own click to
        // switch locks, so only "empty" clicks — gaps, panels, backgrounds —
        // reach us here) release the pinned row.
        outerVbox.MouseFilter = MouseFilterEnum.Stop;
        outerVbox.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                ClearLockedEncounter();
        };
        margin.AddChild(outerVbox);

        // ── Title ──
        var title = new Label();
        title.Text = "SlayTheStats Bestiary";
        title.AddThemeColorOverride("font_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        title.AddThemeFontSizeOverride("font_size", ThemeStyle.TitlePrimary);
        ApplyKreonFont(title, bold: true);
        ApplyTextShadow(title);
        outerVbox.AddChild(title);

        outerVbox.AddChild(new HSeparator());

        // ── Main content: left list | separator | right detail ──
        // contentSplit is created early (right after the title HSep) so the VSeparator
        // inside it extends from just below the title all the way to the bottom of the
        // page. The biome/sort/score tab rows live inside leftSection now (instead of
        // being siblings of contentSplit in outerVbox), so the right panel can start at
        // the top — giving the stats table and monster preview much more vertical room.
        var contentSplit = new HBoxContainer();
        contentSplit.AddThemeConstantOverride("separation", 16);
        contentSplit.SizeFlagsVertical = SizeFlags.ExpandFill;
        outerVbox.AddChild(contentSplit);

        // Left: top settings (biome/sort/score) + sticky stats-column header + scrollable list.
        var leftSection = new VBoxContainer();
        leftSection.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        leftSection.SizeFlagsVertical = SizeFlags.ExpandFill;
        // Stretch ratio tuned so the right-hand table + preview pane gets most
        // of the horizontal budget: encounter list is compact (icons + names)
        // while the stats table wants room for descriptors, 6 data columns, and
        // a sensible-sized monster preview underneath.
        leftSection.SizeFlagsStretchRatio = 0.85f;
        leftSection.AddThemeConstantOverride("separation", 4);
        contentSplit.AddChild(leftSection);

        // ── Biome tabs ──
        // The SlayTheStats filter button is NOT inserted here — it's placed at the
        // bottom-left of the submenu (matching NRelicCollection's anchored position) so
        // every compendium page shows the button in the same on-screen spot.
        _biomeTabRow = new HBoxContainer();
        _biomeTabRow.AddThemeConstantOverride("separation", 8);
        leftSection.AddChild(_biomeTabRow);

        // ── Sort tabs ──
        var sortRowOuter = new HBoxContainer();
        sortRowOuter.AddThemeConstantOverride("separation", 8);
        var sortLabel = new Label();
        sortLabel.Text = "Sort by:";
        sortLabel.AddThemeColorOverride("font_color", new Color(0.88f, 0.86f, 0.80f, 1f));
        sortLabel.AddThemeFontSizeOverride("font_size", 14);
        ApplyKreonFont(sortLabel);
        sortRowOuter.AddChild(sortLabel);

        _sortTabRow = new HBoxContainer();
        _sortTabRow.AddThemeConstantOverride("separation", 4);
        sortRowOuter.AddChild(_sortTabRow);
        leftSection.AddChild(sortRowOuter);

        // ── Focus character row ──
        // Moved to the right panel above the stats title so the selector sits next
        // to the table it controls. The row itself (_sortCharRow) is still built
        // here in RebuildSortCharRow; we only need to create the HBox container now
        // and add it to the right panel below.
        _sortCharRow = new HBoxContainer();
        _sortCharRow.AddThemeConstantOverride("separation", 4);

        // Separator between the top settings and the encounter list. Lives inside
        // leftSection (not outerVbox) so it doesn't span across the right panel —
        // the vertical divider runs uninterrupted from the title HSep down to the bottom.
        leftSection.AddChild(new HSeparator());

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
        // Use the FastScrollContainer subclass so wheel events scroll farther per click —
        // the encounter list is long and the base 40px-per-click feels slow.
        var bestiaryScroll = new FastScrollContainer
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
            handle.Scale = new Vector2(0.50f, 0.78f);
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
        // Catch clicks in the gaps between rows (empty list background) for the
        // click-to-unlock behaviour. Rows themselves call AcceptEvent so clicks on
        // a row never reach this handler — only empty-space clicks do.
        _encounterList.MouseFilter = MouseFilterEnum.Stop;
        _encounterList.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                ClearLockedEncounter();
        };
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
        rightPanel.SizeFlagsStretchRatio = 1.25f;
        rightPanel.AddThemeConstantOverride("separation", 8);
        contentSplit.AddChild(rightPanel);

        // ── Focus character bar (above the stats table) ──
        // Sits directly above the stats title so the selector is adjacent to the
        // table it controls. Choosing a character switches the table to focused-
        // character mode; leaving "All" shows the per-character comparison table.
        // Icon-only selector (Prismatic Gem for Overview + per-character head icons).
        // No label, no surrounding buttons — selected icon is opaque, unselected dimmed.
        var focusCharBar = new HBoxContainer();
        focusCharBar.AddThemeConstantOverride("separation", 8);
        focusCharBar.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        focusCharBar.AddChild(_sortCharRow);
        rightPanel.AddChild(focusCharBar);

        // Stats label — wrapped in a fixed-height container so the divider below it
        // doesn't move, and the preview area below is the same height regardless of
        // which table (focused / category / all-chars) is currently being rendered.
        // The minimum is the natural height of the tallest case (focused single-
        // character view, with tightened line_separation and no row-4 extra gap),
        // so the preview area always ends up the same size it had under the focused
        // mode before the fixed-height lock was introduced. Shorter modes (all-chars,
        // category) fit inside this slab and leave empty space at the bottom.
        var statsBox = new MarginContainer();
        statsBox.CustomMinimumSize = new Vector2(0, 360);
        statsBox.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        rightPanel.AddChild(statsBox);

        // Wrap title + table in a vbox so the title (Kreon, gold) sits above the monospace
        // table without sharing fonts with it.
        var statsVbox = new VBoxContainer();
        statsVbox.AddThemeConstantOverride("separation", 4);
        statsVbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        statsVbox.SizeFlagsVertical = SizeFlags.ExpandFill;
        statsBox.AddChild(statsVbox);

        // Title row: encounter / category name in Kreon gold + a small "?" legend button
        // on the right that toggles a popup explaining each stat column.
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 8);
        titleRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        statsVbox.AddChild(titleRow);

        _statsTitleLabel = new RichTextLabel();
        _statsTitleLabel.BbcodeEnabled = true;
        _statsTitleLabel.FitContent = true;
        _statsTitleLabel.ScrollActive = false;
        _statsTitleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _statsTitleLabel.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _statsTitleLabel.AddThemeColorOverride("default_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        _statsTitleLabel.AddThemeFontSizeOverride("normal_font_size", ThemeStyle.TitleSecondary);
        _statsTitleLabel.AddThemeFontSizeOverride("bold_font_size", ThemeStyle.TitleSecondary);
        ApplyKreonFont(_statsTitleLabel, bold: true);
        ApplyTextShadow(_statsTitleLabel);
        _statsTitleLabel.Text = "";
        titleRow.AddChild(_statsTitleLabel);

        _legendButton = BuildLegendButton();
        titleRow.AddChild(_legendButton);

        // Stats label — used for focused-character and category views (non-scrollable).
        TooltipHelper.TryLoadModFonts();
        _statsLabel = CreateStatsRichTextLabel();
        _statsLabel.Text = $"[color=#686868]Hover an encounter to see stats[/color]";
        statsVbox.AddChild(_statsLabel);

        // All-characters scrollable layout: header (sticky) + character rows (scrollable) + baseline (sticky) + footer.
        // These are hidden until the all-chars view is active.
        const float StatsScrollbarWidth = 20f;
        const float StatsScrollbarGap = 3f;

        _statsHeaderLabel = CreateStatsRichTextLabel();
        // Reserve right margin matching the scrollbar gutter so header columns
        // align with the character rows label inside the clipper.
        var statsHeaderMargin = new MarginContainer();
        statsHeaderMargin.Visible = false;
        statsHeaderMargin.AddThemeConstantOverride("margin_right", (int)(StatsScrollbarWidth + StatsScrollbarGap));
        statsHeaderMargin.AddChild(_statsHeaderLabel);
        statsVbox.AddChild(statsHeaderMargin);
        _statsHeaderMarginContainer = statsHeaderMargin;

        // Scrollable character rows: wrapper holds the clipped content + scrollbar.

        // Horizontal separator between header and scrollable rows
        var statsScrollTopSep = new HSeparator();
        statsScrollTopSep.Visible = false;
        statsScrollTopSep.AddThemeConstantOverride("separation", 0);
        statsScrollTopSep.AddThemeStyleboxOverride("separator", CreateThinSeparatorStyle());
        statsVbox.AddChild(statsScrollTopSep);
        _statsScrollTopSep = statsScrollTopSep;

        _statsCharRowsWrapper = new Control();
        _statsCharRowsWrapper.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _statsCharRowsWrapper.SizeFlagsVertical = SizeFlags.ExpandFill;
        _statsCharRowsWrapper.Visible = false;
        statsVbox.AddChild(_statsCharRowsWrapper);

        // Horizontal separator between scrollable rows and baseline
        var statsScrollBottomSep = new HSeparator();
        statsScrollBottomSep.Visible = false;
        statsScrollBottomSep.AddThemeConstantOverride("separation", 0);
        statsScrollBottomSep.AddThemeStyleboxOverride("separator", CreateThinSeparatorStyle());
        statsVbox.AddChild(statsScrollBottomSep);
        _statsScrollBottomSep = statsScrollBottomSep;

        // Clipper: clips the character rows label. Leaves room for scrollbar on the right.
        _statsCharRowsClipper = new Control();
        _statsCharRowsClipper.ClipContents = true;
        _statsCharRowsClipper.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _statsCharRowsClipper.OffsetRight = -(StatsScrollbarWidth + StatsScrollbarGap);
        _statsCharRowsClipper.MouseFilter = MouseFilterEnum.Stop;
        _statsCharRowsClipper.GuiInput += OnStatsCharRowsGuiInput;
        _statsCharRowsWrapper.AddChild(_statsCharRowsClipper);

        _statsCharRowsLabel = CreateStatsRichTextLabel();
        _statsCharRowsLabel.FitContent = false;  // prevent min-size propagation
        _statsCharRowsLabel.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _statsCharRowsClipper.AddChild(_statsCharRowsLabel);

        // Scrollbar: game's native NScrollbar, anchored to the right edge.
        _statsScrollbar = PreloadManager.Cache.GetScene(SceneHelper.GetScenePath("ui/scrollbar"))
            .Instantiate<NScrollbar>();
        _statsScrollbar.Name = "StatsScrollbar";
        _statsScrollbar.AnchorLeft   = 1f;
        _statsScrollbar.AnchorRight  = 1f;
        _statsScrollbar.AnchorTop    = 0f;
        _statsScrollbar.AnchorBottom = 1f;
        _statsScrollbar.OffsetLeft   = -StatsScrollbarWidth;
        _statsScrollbar.OffsetRight  = 0;
        _statsScrollbar.OffsetTop    = 0;
        _statsScrollbar.OffsetBottom = 0;
        _statsCharRowsWrapper.AddChild(_statsScrollbar);

        // Shrink the scrollbar handle after the scene's _Ready runs.
        var capturedStatsScrollbar = _statsScrollbar;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(capturedStatsScrollbar)) return;
            var handle = ((Node)capturedStatsScrollbar).GetNodeOrNull<Control>("%Handle");
            if (handle == null) return;
            DisableMouseRecursive(handle);
            handle.PivotOffset = handle.Size / 2f;
            handle.Scale = new Vector2(0.45f, 0.70f);
        }).CallDeferred();

        // Wire scrollbar value changes to update the scroll offset.
        _statsScrollbar.ValueChanged += (double value) =>
        {
            _statsCharRowsScroll = (float)value;
            ApplyStatsCharRowsScroll();
        };

        _statsBaselineLabel = CreateStatsRichTextLabel();
        // Reserve right margin matching the scrollbar gutter so baseline columns
        // align with the character rows label inside the clipper.
        var statsBaselineMargin = new MarginContainer();
        statsBaselineMargin.Visible = false;
        statsBaselineMargin.AddThemeConstantOverride("margin_right", (int)(StatsScrollbarWidth + StatsScrollbarGap));
        statsBaselineMargin.AddChild(_statsBaselineLabel);
        statsVbox.AddChild(statsBaselineMargin);
        _statsBaselineMarginContainer = statsBaselineMargin;

        _statsFooterLabel = CreateStatsRichTextLabel();
        _statsFooterLabel.Visible = false;
        statsVbox.AddChild(_statsFooterLabel);

        rightPanel.AddChild(new HSeparator());

        // Clicks on the right-hand stats / preview area also release a locked row
        // so the user can visit the right panel without the lock feeling sticky.
        // statsBox catches clicks on the stats label background; the render area
        // gets its own handler below.
        statsBox.MouseFilter = MouseFilterEnum.Stop;
        statsBox.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                ClearLockedEncounter();
        };

        // Render area — takes all vertical slack below the fixed-height stats label.
        // Since _statsLabel is ShrinkBegin + CustomMinimumSize.Y = 510, the preview
        // height is just (rightPanel height − 510 − title/separator). No explicit
        // CustomMinimumSize here so it can flex with the window size while still
        // being consistent across modes (stats area height is the same regardless
        // of which table is rendered).
        _renderArea = new PanelContainer();
        _renderArea.SizeFlagsVertical = SizeFlags.ExpandFill;
        _renderArea.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _renderArea.MouseFilter = MouseFilterEnum.Stop;
        _renderArea.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                ClearLockedEncounter();
        };
        var renderStyle = new StyleBoxFlat();
        // Match the rest of the bestiary chrome — same dark slate-blue hue as the
        // encounter list / category panels (0.10, 0.11, 0.15) so the preview area
        // reads as part of the same surface. Slightly darker than the list panels
        // (0.85α there) to give the rendered creature a stage to sit on.
        renderStyle.BgColor = new Color(0.08f, 0.09f, 0.12f, 1f);
        renderStyle.SetBorderWidthAll(1);
        renderStyle.BorderColor = new Color(0.22f, 0.20f, 0.16f, 0.6f);
        renderStyle.SetCornerRadiusAll(4);
        ((PanelContainer)_renderArea).AddThemeStyleboxOverride("panel", renderStyle);
        rightPanel.AddChild(_renderArea);

        // ── Filter pane ──
        // Built after the rest of the UI so it sits last in the child list and draws on
        // top of the encounter list/stats panel when visible. Subset pane: no class filter
        // (the bestiary's "Score by:" row covers per-character scoring) and no group-card-
        // upgrades toggle (encounter stats are unaffected by card aggregation).
        BuildAndAttachFilterPane();
    }

    private void BuildAndAttachFilterPane()
    {
        _filterPane = CompendiumFilterPatch.BuildFilterPane(
            onClose: () => { if (_filterPane != null) _filterPane.Visible = false; },
            includeClassFilter: false,
            includeGroupUpgrades: false,
            onFilterChanged: () =>
            {
                // Filters changed via the pane — re-aggregate and redraw the whole list,
                // and refresh the right-hand stats panel for whatever's currently hovered.
                RefreshEncounterList();
                if (_hoveredEncounterId != null)
                {
                    UpdateStatsPanel(_hoveredEncounterId);
                    RenderMonsterPreview(_hoveredEncounterId);
                }
            });

        AddChild(_filterPane);
        CompendiumFilterPatch.RegisterPane(_filterPane);

        BuildAndAttachBestiarySettingsPane();

        // Filter toggle button — uses the same shared helper as the relic
        // page (CompendiumFilterPatch.CreateAndAttachStyledFilterButton) so
        // the cloned NCardViewSortButton lands at the bottom-left with the
        // identical anchor/size/wiring. Three-strategy template resolution
        // (cached → live tree walk → cold NCardLibrary.Create()) handles the
        // case where the user opens the bestiary before any other compendium
        // page in a session. Falls through to the unstyled fallback only if
        // every strategy returns null.
        var template = CompendiumFilterPatch.ResolveSortButtonTemplate(this);
        if (template != null && _filterPane != null)
        {
            _filterButton = CompendiumFilterPatch.CreateAndAttachStyledFilterButton(this, _filterPane, template);
        }
        else
        {
            // Last-resort fallback — plain Godot Button. Should be unreachable
            // in practice now that ResolveSortButtonTemplate has the cold-
            // instance strategy, but kept defensively in case the cold load
            // also fails on some platform.
            var fallback = new Button();
            fallback.Text = "SlayTheStats";
            fallback.Name = "SlayTheStatsFilterButtonFallback";
            fallback.CustomMinimumSize = new Vector2(180, 50);

            const float padX = 20f;
            const float padY = 20f;
            fallback.AnchorLeft   = 0f;
            fallback.AnchorRight  = 0f;
            fallback.AnchorTop    = 1f;
            fallback.AnchorBottom = 1f;
            fallback.OffsetLeft   = padX;
            fallback.OffsetRight  = padX + 180;
            fallback.OffsetTop    = -50 - padY;
            fallback.OffsetBottom = -padY;

            ApplyKreonFont(fallback, bold: true);
            fallback.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f, 1f));
            fallback.AddThemeColorOverride("font_hover_color", new Color(1f, 0.9f, 0.7f, 1f));

            fallback.Pressed += () =>
            {
                if (_filterPane == null) return;
                _filterPane.Visible = !_filterPane.Visible;
                if (_filterPane.Visible)
                {
                    CompendiumFilterPatch.RepositionPaneNextToButton(fallback, _filterPane);
                    CompendiumFilterPatch.SyncAllControls();
                }
            };
            if (_filterPane is FilterPanelContainer fpc)
                fpc.AssociatedButton = fallback;

            AddChild(fallback);
            _filterButton = fallback;
        }

        if (SlayTheStatsConfig.DebugMode)
            MainFile.Logger.Info($"[SlayTheStats] Bestiary filter pane attached (styled={(_filterButton is NCardViewSortButton)}, active={CompendiumFilterPatch.HasActiveFilters()})");
    }

    /// <summary>
    /// Builds a small bestiary-specific settings pane (preview mode selector, etc.) and
    /// wires it to follow the main filter pane's visibility. Positioned directly above
    /// the filter pane after each show so both read as a stacked unit.
    /// </summary>
    private void BuildAndAttachBestiarySettingsPane()
    {
        if (_filterPane == null) return;

        var pane = new FilterPanelContainer();
        pane.Name = "SlayTheStatsBestiarySettingsPane";
        pane.Visible = false;
        pane.ZIndex = 90;
        pane.AnchorLeft = 0f;
        pane.AnchorTop = 0f;
        pane.AnchorRight = 0f;
        pane.AnchorBottom = 0f;
        pane.AddThemeStyleboxOverride("panel", CompendiumFilterPatch.BuildPanelStyle());
        pane.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        pane.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        // Title row (mirrors CompendiumFilterPatch.BuildFilterPane's title layout).
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(titleRow);

        var title = new Label();
        title.Text = "Bestiary Settings";
        title.AddThemeColorOverride("font_color", new Color("EFC851")); // Gold
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        CompendiumFilterPatch.ApplyGameFont(title, 24);
        titleRow.AddChild(title);

        CompendiumFilterPatch.AddSeparator(vbox);

        // ── Preview on/off checkbox (cloned game-native tickbox style) ──
        // Note: the BestiaryPreviewMode enum still has a Static value for a
        // possible post-v1.0.0 feature, but the UI here is binary (Live / None)
        // for v0.3.0 to keep the surface simple.
        var previewCheckbox = CompendiumFilterPatch.BuildStyledCheckbox(
            "Show monster preview",
            SlayTheStatsConfig.BestiaryPreviewEnabled,
            ticked =>
            {
                SlayTheStatsConfig.BestiaryPreviewMode = ticked
                    ? SlayTheStatsConfig.BestiaryPreviewModeEnum.Live
                    : SlayTheStatsConfig.BestiaryPreviewModeEnum.None;
                try { BaseLib.Config.ModConfig.SaveDebounced<SlayTheStatsConfig>(); }
                catch (Exception ex) { MainFile.Logger.Warn($"[SlayTheStats] Preview mode save failed: {ex.Message}"); }
                if (_hoveredEncounterId != null)
                    RenderMonsterPreview(_hoveredEncounterId);
            });
        vbox.AddChild(previewCheckbox);

        AddChild(pane);
        _bestiarySettingsPane = pane;

        // Link the two panes so click-outside dismissal considers both as one
        // logical unit, in either direction.
        if (_filterPane is FilterPanelContainer fFilter)
            fFilter.AssociatedSiblingPane = pane;
        pane.AssociatedSiblingPane = _filterPane;
        pane.AssociatedButton = _filterButton;

        _filterPane.VisibilityChanged += () =>
        {
            if (_bestiarySettingsPane == null || _filterPane == null) return;
            if (_filterPane.Visible)
            {
                // Keep invisible this frame; the deferred callback positions it
                // and flips visibility once we can read filterPane.GlobalPosition.
                _bestiarySettingsPane.Visible = false;
                RepositionBestiarySettingsPaneAboveFilterPane();
            }
            else
            {
                _bestiarySettingsPane.Visible = false;
            }
        };
    }

    /// <summary>
    /// Places the settings pane so its bottom edge sits a small gap above the
    /// filter pane's top edge, horizontally aligned to the filter pane's left.
    /// Reads the filter pane's GlobalPosition after its RepositionPaneNextToButton
    /// has run; deferred so the filter pane has finished its own layout.
    /// </summary>
    private void RepositionBestiarySettingsPaneAboveFilterPane()
    {
        Callable.From(() =>
        {
            if (_bestiarySettingsPane == null || _filterPane == null) return;
            if (!_filterPane.Visible) return;
            const float gap = 8f;
            var fpGlobal = _filterPane.GlobalPosition;
            var fpSize = _filterPane.Size;
            var spSize = _bestiarySettingsPane.GetCombinedMinimumSize();
            _bestiarySettingsPane.Size = spSize;
            _bestiarySettingsPane.GlobalPosition = new Vector2(fpGlobal.X, fpGlobal.Y - spSize.Y - gap);
            _bestiarySettingsPane.Visible = true;
            MainFile.Logger.Info($"[SlayTheStats] BestiarySettingsPane reposition: fpGlobal={fpGlobal} fpSize={fpSize} spSize={spSize} → spGlobal={_bestiarySettingsPane.GlobalPosition} spPosition={_bestiarySettingsPane.Position}");
        }).CallDeferred();
    }

    // ───────────────────────── Tutorial overlay ─────────────────────────

    /// <summary>The active tutorial CanvasLayer, if any. Tracked so the dismiss handler
    /// can find and free it on click.</summary>
    private CanvasLayer? _bestiaryTutorialLayer;

    /// <summary>
    /// Builds and shows a one-shot explainer overlay covering the bestiary controls and
    /// stat columns. Persists BestiaryTutorialSeen on dismiss so it never appears again
    /// unless the user toggles the setting back off in mod config.
    /// </summary>
    private void MaybeShowBestiaryTutorial()
    {
        if (_bestiaryTutorialLayer != null && GodotObject.IsInstanceValid(_bestiaryTutorialLayer))
            return;

        // Defer one frame so the bestiary's own layout has settled before we draw the
        // dimmer over it (otherwise the panel can render before the encounter list and
        // briefly show through).
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += DeferredBuild;
        void DeferredBuild()
        {
            tree.ProcessFrame -= DeferredBuild;
            BuildBestiaryTutorialPanel();
        }
    }

    private void BuildBestiaryTutorialPanel()
    {
        var layer = new CanvasLayer
        {
            Name  = "SlayTheStatsBestiaryTutorialLayer",
            Layer = 100,
        };

        // Full-screen click-catcher so any click dismisses.
        var dimmer = new ColorRect
        {
            Color       = new Color(0, 0, 0, 0.55f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        dimmer.AnchorRight  = 1f;
        dimmer.AnchorBottom = 1f;
        layer.AddChild(dimmer);

        // Centered panel — stone-textured to match the rest of the SlayTheStats UI.
        var panel = new PanelContainer { Name = "SlayTheStatsBestiaryTutorialPanel" };
        panel.AddThemeStyleboxOverride("panel", TooltipHelper.BuildPanelStyle());
        panel.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);
        panel.MouseFilter  = MouseFilterEnum.Ignore;
        panel.AnchorLeft   = 0.5f;
        panel.AnchorRight  = 0.5f;
        panel.AnchorTop    = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical   = GrowDirection.Both;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left",   24);
        margin.AddThemeConstantOverride("margin_right",  24);
        margin.AddThemeConstantOverride("margin_top",    20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(vbox);

        var title = new Label { Text = "Welcome to the Stats Bestiary!" };
        title.AddThemeColorOverride("font_color", new Color(0.95f, 0.78f, 0.32f, 1f));
        title.AddThemeFontSizeOverride("font_size", ThemeStyle.TitlePrimary);
        ApplyKreonFont(title, bold: true);
        ApplyTextShadow(title);
        vbox.AddChild(title);

        // Body — RichTextLabel so we can highlight key terms in gold.
        var body = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent    = true,
            ScrollActive  = false,
            AutowrapMode  = TextServer.AutowrapMode.Off,
            MouseFilter   = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(720, 0),
        };
        body.AddThemeColorOverride("default_color", new Color(0.95f, 0.93f, 0.88f, 1f));
        body.AddThemeFontSizeOverride("normal_font_size", 18);
        body.AddThemeFontSizeOverride("bold_font_size",   18);
        ApplyKreonFont(body);
        body.Text = string.Join('\n', new[]
        {
            "[b][color=#efc851]Encounter list[/color][/b] (left): every monster group you've ever fought, organised by biome and category.",
            "  • Hover any row to see per-character stats on the right.",
            "  • Hover a category header (Boss / Elite / Normal …) for an aggregated view.",
            "  • Click a category header to collapse / expand it.",
            "",
            "[b][color=#efc851]Biome tabs[/color][/b] (top): switch between Acts and individual biomes. \"All\" shows everything.",
            "[b][color=#efc851]Sort by[/color][/b] / [b][color=#efc851]Focus character[/color][/b]: pick how the encounter list is ordered. \"Focus character\" switches the stats table to show detailed per-act context for one character (vs the default multi-character comparison view).",
            "[b][color=#efc851]Filters[/color][/b] (bottom-left): the SlayTheStats button opens a pane to filter by ascension / version / profile.",
            "",
            "[b][color=#efc851]Stat columns:[/color][/b]",
            "  • [b]Dmg[/b] — average damage taken in this fight. Tints orange if higher than the category baseline, teal if lower.",
            "  • [b]Var[/b]  — variance of damage taken. High variance = swingy / inconsistent encounter.",
            "  • [b]Turns[/b] — average turns the fight lasts.",
            "  • [b]Pots[/b]  — average potions used per fight.",
            "  • [b]Deaths[/b] — runs that ended at this encounter / total runs. Color intensity grows with sample size.",
        });
        vbox.AddChild(body);

        var hint = new Label { Text = "(click anywhere to dismiss)" };
        hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.72f, 0.65f, 1f));
        hint.AddThemeFontSizeOverride("font_size", 13);
        ApplyKreonFont(hint);
        vbox.AddChild(hint);

        layer.AddChild(panel);

        var tree = (SceneTree)Engine.GetMainLoop();
        tree?.Root.AddChild(layer);
        _bestiaryTutorialLayer = layer;

        dimmer.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed)
                DismissBestiaryTutorial();
        };

        if (SlayTheStatsConfig.DebugMode)
            MainFile.Logger.Info("[SlayTheStats] Bestiary tutorial shown");
    }

    /// <summary>Creates a 1px warm-earth separator style for the stats scroll area borders.</summary>
    private static Godot.StyleBoxLine CreateThinSeparatorStyle()
    {
        var style = new Godot.StyleBoxLine();
        style.Color = new Color(0.22f, 0.20f, 0.16f, 0.5f);
        style.Thickness = 1;
        return style;
    }

    // ───────────────────────── Stats label factory ─────────────────────────

    /// <summary>Creates a RichTextLabel with the standard stats-table theme overrides
    /// (Kreon font, line separation, BBCode, etc.). All stats labels share these settings.</summary>
    private static RichTextLabel CreateStatsRichTextLabel()
    {
        var label = new RichTextLabel();
        label.BbcodeEnabled = true;
        label.FitContent = true;
        label.ScrollActive = false;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        label.AddThemeColorOverride("default_color", new Color(0.95f, 0.93f, 0.88f, 1f));
        label.AddThemeFontSizeOverride("normal_font_size", 18);
        label.AddThemeFontSizeOverride("bold_font_size", 18);
        label.AddThemeConstantOverride("line_separation", -6);
        var kreonFont = TooltipHelper.GetKreonFont();
        if (kreonFont != null)
        {
            label.AddThemeFontOverride("normal_font", kreonFont);
            var boldFont = TooltipHelper.GetKreonBoldFont();
            if (boldFont != null) label.AddThemeFontOverride("bold_font", boldFont);
        }
        // Drop shadow so the table text matches the rest of the bestiary (and the tooltip family).
        ApplyTextShadow(label);
        return label;
    }

    // ───────────────────────── Column legend popup ─────────────────────────

    private Button BuildLegendButton()
    {
        var btn = new Button
        {
            Text = "?",
            CustomMinimumSize = new Vector2(28, 28),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "What do these columns mean?",
        };

        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.10f, 0.11f, 0.15f, 0.85f);
        style.BorderColor = new Color(0.40f, 0.45f, 0.55f, 0.85f);
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(14);
        style.ContentMarginLeft = style.ContentMarginRight = 6;
        style.ContentMarginTop = style.ContentMarginBottom = 0;
        btn.AddThemeStyleboxOverride("normal", style);
        var hover = (StyleBoxFlat)style.Duplicate();
        hover.BorderColor = new Color(0.918f, 0.745f, 0.318f, 0.95f);
        hover.BgColor = new Color(0.15f, 0.16f, 0.22f, 1f);
        btn.AddThemeStyleboxOverride("hover", hover);

        btn.AddThemeColorOverride("font_color", new Color(0.80f, 0.78f, 0.72f, 1f));
        btn.AddThemeColorOverride("font_hover_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        btn.AddThemeFontSizeOverride("font_size", 16);
        ApplyKreonFont(btn, bold: true);
        ApplyTextShadow(btn);

        btn.Pressed += () =>
        {
            SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
            ToggleLegendPopup();
        };
        return btn;
    }

    private void ToggleLegendPopup()
    {
        if (_legendPopup != null && GodotObject.IsInstanceValid(_legendPopup))
        {
            _legendPopup.QueueFree();
            _legendPopup = null;
            return;
        }
        BuildLegendPopup();
    }

    private void BuildLegendPopup()
    {
        if (_legendButton == null) return;

        var popup = new PanelContainer { Name = "SlayTheStatsBestiaryLegendPopup" };
        popup.AddThemeStyleboxOverride("panel", TooltipHelper.BuildPanelStyle());
        popup.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);
        popup.MouseFilter = MouseFilterEnum.Stop;
        popup.ZIndex = 95;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        popup.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        margin.AddChild(vbox);

        var title = new Label { Text = "Stat columns" };
        title.AddThemeColorOverride("font_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        title.AddThemeFontSizeOverride("font_size", ThemeStyle.TitleSubsection);
        ApplyKreonFont(title, bold: true);
        ApplyTextShadow(title);
        vbox.AddChild(title);

        var body = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent    = true,
            ScrollActive  = false,
            AutowrapMode  = TextServer.AutowrapMode.WordSmart,
            MouseFilter   = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(520, 0),
        };
        body.AddThemeColorOverride("default_color", new Color(0.95f, 0.93f, 0.88f, 1f));
        body.AddThemeFontSizeOverride("normal_font_size", 16);
        body.AddThemeFontSizeOverride("bold_font_size", 16);
        ApplyKreonFont(body);
        body.Text = string.Join('\n', new[]
        {
            "[b][color=#efc851]Runs[/color][/b] — runs that encountered this. Color intensity grows with sample size.",
            "",
            "[b][color=#efc851]Dmg[/color][/b] — the [i]median[/i] damage taken across matching fights — half of fights",
            "dealt more, half dealt less. Tints orange above the category baseline, teal below.",
            "",
            "[b][color=#efc851]Mid 50%[/color][/b] — the interquartile range (p25–p75): the damage window that the",
            "middle half of all fights fell inside. A narrow range means consistent fights; a wide",
            "range means high variance.",
            "",
            "[b][color=#efc851]Spread[/color][/b] — how wide the middle 50% is relative to the typical fight, as a",
            "percentage (IQR / median). Lower = more consistent, higher = swingier. Tints orange",
            "when swingier than the category baseline, teal when more consistent.",
            "",
            "[b][color=#efc851]Turns[/color][/b] — average turns the fight lasts.",
            "[b][color=#efc851]Pots[/color][/b] — average potions used per fight.",
            "[b][color=#efc851]Deaths[/color][/b] — runs that ended at this encounter / total runs.",
            "",
            "[color=#9c9c9c]Pool rows (baselines) weight each encounter equally — an encounter you've",
            "fought 50 times counts the same as one you've fought 5 times, matching the",
            "game's uniform spawn rates.[/color]",
            "",
            "[color=#bfb7a6]Click ? again to dismiss.[/color]",
        });
        vbox.AddChild(body);

        // Anchor next to the legend button (top-right of stats panel). Add as a child of
        // the bestiary submenu so it floats above the encounter list/stats panel.
        AddChild(popup);
        _legendPopup = popup;

        // Defer position so we can read the button's global rect after layout settles.
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += DeferredPosition;
        void DeferredPosition()
        {
            tree.ProcessFrame -= DeferredPosition;
            if (!GodotObject.IsInstanceValid(popup) || !GodotObject.IsInstanceValid(_legendButton))
                return;

            var btnRect = _legendButton.GetGlobalRect();
            var popupSize = popup.GetCombinedMinimumSize();
            var viewport = popup.GetViewport();
            var vpSize = viewport?.GetVisibleRect().Size ?? new Vector2(1920, 1080);

            // Place below + right of the button, clamped to the viewport.
            float x = Math.Clamp(btnRect.Position.X + btnRect.Size.X - popupSize.X,
                                 16f, Math.Max(16f, vpSize.X - popupSize.X - 16f));
            float y = Math.Clamp(btnRect.Position.Y + btnRect.Size.Y + 8f,
                                 16f, Math.Max(16f, vpSize.Y - popupSize.Y - 16f));
            popup.AnchorLeft = popup.AnchorRight = popup.AnchorTop = popup.AnchorBottom = 0f;
            popup.GlobalPosition = new Vector2(x, y);
        }
    }

    private void DismissBestiaryTutorial()
    {
        SlayTheStatsConfig.BestiaryTutorialSeen = true;
        try { BaseLib.Config.ModConfig.SaveDebounced<SlayTheStatsConfig>(); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] Failed to save BestiaryTutorialSeen: {e.Message}");
        }
        if (_bestiaryTutorialLayer != null && GodotObject.IsInstanceValid(_bestiaryTutorialLayer))
            _bestiaryTutorialLayer.QueueFree();
        _bestiaryTutorialLayer = null;
    }

    // ───────────────────────── Public API ─────────────────────────

    public void Refresh()
    {
        RestoreBestiaryState();
        RefreshEncounterList();
    }

    /// <summary>Restores persisted bestiary view state from config on first open.
    /// Falls back to defaults if the persisted values are invalid.</summary>
    private void RestoreBestiaryState()
    {
        // Only restore on the very first Refresh (when _selectedBiome is still null).
        // Subsequent refreshes use the in-session values.
        if (_selectedBiome != null) return;

        var savedBiome = SlayTheStatsConfig.BestiarySelectedBiome;
        if (!string.IsNullOrEmpty(savedBiome))
        {
            var biomes = GetBiomes();
            _selectedBiome = biomes.Contains(savedBiome) ? savedBiome : GetDefaultBiome();
        }
        else
        {
            _selectedBiome = GetDefaultBiome();
        }

        // Migrate legacy "IQR" value from prior versions → "Spread"
        var savedSortMode = SlayTheStatsConfig.BestiarySortMode == "IQR"
            ? "Spread"
            : SlayTheStatsConfig.BestiarySortMode;
        if (Enum.TryParse<EncounterSortMode>(savedSortMode, out var mode))
            _sortMode = mode;
        _sortDescending = SlayTheStatsConfig.BestiarySortDescending;

        var savedChar = SlayTheStatsConfig.BestiarySortCharacter;
        _sortCharacter = string.IsNullOrEmpty(savedChar) ? null : savedChar;

        _sortBySignificance = SlayTheStatsConfig.BestiarySortBySignificance;
    }

    /// <summary>Persists the current bestiary view state to config.</summary>
    private static void SaveBestiaryState(string? biome, EncounterSortMode sortMode, bool sortDescending,
        string? sortCharacter, bool sortBySignificance)
    {
        SlayTheStatsConfig.BestiarySelectedBiome = biome ?? "";
        SlayTheStatsConfig.BestiarySortMode = sortMode.ToString();
        SlayTheStatsConfig.BestiarySortDescending = sortDescending;
        SlayTheStatsConfig.BestiarySortCharacter = sortCharacter ?? "";
        SlayTheStatsConfig.BestiarySortBySignificance = sortBySignificance;
        try { BaseLib.Config.ModConfig.SaveDebounced<SlayTheStatsConfig>(); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] Failed to save bestiary state: {e.Message}");
        }
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
        _highlightPanels.Clear();
        _iconBundles.Clear();
        _grownTargets.Clear();
        _animatingHandles.Clear();
        _highlightTween?.Kill();
        _highlightTween = null;

        ShowSingleLabelStats($"[color=#686868]Hover an encounter to see stats[/color]");
        SetStatsTitle("");
        _hoveredEncounterId = null;
        // Snapshot locked state before clearing — we'll re-apply after the list
        // rebuild if the locked category/encounter still exists in the new view.
        var pendingLockedCategory = _lockedCategory;
        var pendingLockedEncounterId = _lockedEncounterId;
        _lockedEncounterId = null;
        _lockedRowWrap = null;
        _lockedCategory = null;
        _lockedCategoryEncIds = null;
        _lockedCategoryWrap = null;

        RebuildBiomeTabs();
        RebuildSortTabs();
        RebuildSortCharRow();

        var encounters = GetEncountersForBiome(_selectedBiome);
        var categories = new[] { "weak", "normal", "elite", "boss", "event", "unknown" };
        var filter = BuildFilter();
        // Snapshot + clear so a stale pending value can't carry into a future rebuild
        // if the targeted category disappeared (filtered out / no encounters).
        var pendingHoverCategory = _pendingHoverCategory;
        _pendingHoverCategory = null;

        RefreshStatsColumnHeader();

        foreach (var category in categories)
        {
            // Use Derive() rather than meta.Category so overrides (e.g. OVERGROWTH_CRAWLERS →
            // normal) take effect even on data parsed before the override existed.
            var catEncounters = encounters
                .Where(e => EncounterCategory.Derive(e) == category)
                .ToList();
            catEncounters = SortEncounters(catEncounters, _sortMode, _sortDescending, filter, _sortCharacter, _sortBySignificance, _selectedBiome);

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

            // Collapse arrow indicator — plain label, no background. Click handling
            // is centralized on catWrap via position-based hit detection in GuiInput.
            var arrowLabel = new Label();
            arrowLabel.Text = collapsed ? "▶" : "▼";
            arrowLabel.AddThemeColorOverride("font_color", new Color(0.96f, 0.96f, 0.96f, 1f));
            arrowLabel.AddThemeFontSizeOverride("font_size", 15);
            arrowLabel.MouseFilter = MouseFilterEnum.Ignore;
            arrowLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            arrowLabel.VerticalAlignment = VerticalAlignment.Center;
            arrowLabel.HorizontalAlignment = HorizontalAlignment.Center;
            arrowLabel.CustomMinimumSize = new Vector2(20, 0);
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

            // Category highlight panel — covers only the category name text.
            // Arrow and icon sit outside so the highlight doesn't extend over them.
            // The highlight panel starts at the same x as encounter row highlights,
            // but the category text is indented slightly via an inner spacer so
            // category names sit visually offset from encounter names.
            var catHighlightPanel = new PanelContainer();
            catHighlightPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            catHighlightPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
            catHighlightPanel.MouseFilter = MouseFilterEnum.Ignore;
            catHighlightPanel.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
            catRow.AddChild(catHighlightPanel);

            var catInnerRow = new HBoxContainer();
            catInnerRow.MouseFilter = MouseFilterEnum.Ignore;
            catInnerRow.Alignment = BoxContainer.AlignmentMode.Center;
            catHighlightPanel.AddChild(catInnerRow);

            // Indent spacer — pushes the category text right of the highlight start
            // so category names are visually offset from encounter names below them.
            catInnerRow.AddChild(new Control { CustomMinimumSize = new Vector2(CategoryTextIndentPx, 0), MouseFilter = MouseFilterEnum.Ignore });

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
            catInnerRow.AddChild(catLabel);

            // Hover the category header to show pool-aggregate stats; click to collapse/expand.
            var capturedCategory = category;
            var capturedEncIds = catEncounters;
            void ApplyCategoryHover()
            {
                if (!GodotObject.IsInstanceValid(catHighlightPanel)) return;
                var style = (_lockedCategory == capturedCategory)
                    ? (StyleBox)s_rowLockedStyle
                    : s_rowHighlightStyle;
                catHighlightPanel.AddThemeStyleboxOverride("panel", style);
                HighlightCategory(capturedCategory, true);
                OnCategoryHover(capturedCategory, capturedEncIds);
            }
            catWrap.MouseEntered += ApplyCategoryHover;
            catWrap.MouseExited += () =>
            {
                var fallback = (_lockedCategory == capturedCategory)
                    ? (StyleBox)s_rowLockedStyle
                    : MakeRowEmptyStyle();
                catHighlightPanel.AddThemeStyleboxOverride("panel", fallback);
                HighlightCategory(capturedCategory, false);
                OnEncounterUnhover();
            };
            catWrap.GuiInput += (InputEvent ev) =>
            {
                // Click region for collapse/expand: arrow + icon. 20(arrow) + 8(sep)
                // + 62(icon column) = 90px. Matches the encounter row layout so the
                // clickable collapse area visually lines up with the icon column.
                const float CollapseThreshold = 90f;
                if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Position.X < CollapseThreshold)
                    {
                        ToggleCategoryCollapse(capturedCategory);
                    }
                    else if (_lockedCategory == capturedCategory)
                    {
                        ClearLockedEncounter();
                        catHighlightPanel.AddThemeStyleboxOverride("panel", s_rowHighlightStyle);
                    }
                    else
                    {
                        LockCategory(capturedCategory, capturedEncIds, catHighlightPanel);
                    }
                    catWrap.AcceptEvent();
                }
                // Double-click on the body (not the arrow/icon area) toggles collapse/expand.
                // Skip the collapse region — otherwise rapid clicking there fires
                // single-click (toggle) + double-click (toggle again) = net zero.
                if (ev is InputEventMouseButton db && db.DoubleClick && db.ButtonIndex == MouseButton.Left
                    && db.Position.X >= CollapseThreshold)
                {
                    ToggleCategoryCollapse(capturedCategory);
                    catWrap.AcceptEvent();
                }
            };

            // Make children click-transparent so catWrap.GuiInput handles all clicks.
            MakeRowChildrenClickTransparent(catWrap);

            _encounterList.AddChild(catWrap);

            // If we just rebuilt because the user toggled this category, hand the hover
            // state off to the new wrapper so the highlight stays on. Defer one frame so
            // it runs after the wrapper has been laid out and Godot has finished its own
            // mouse-enter dispatch for the rebuilt tree.
            if (pendingHoverCategory == category)
                Callable.From(ApplyCategoryHover).CallDeferred();

            // Re-apply category lock if the user had this category locked before
            // the list rebuild (e.g. biome/act tab switch).
            if (pendingLockedCategory == category)
            {
                _lockedCategory = capturedCategory;
                _lockedCategoryEncIds = capturedEncIds;
                _lockedCategoryWrap = catHighlightPanel;
                catHighlightPanel.AddThemeStyleboxOverride("panel", s_rowLockedStyle);
                // Force the stats panel to display this category's data.
                Callable.From(() => OnCategoryHoverForced(capturedCategory, capturedEncIds)).CallDeferred();
            }

            if (collapsed) continue;

            foreach (var encId in catEncounters)
            {
                var entry = BuildEncounterEntry(encId, _sortMode, filter, _sortCharacter, category, bundle, _selectedBiome);
                _rowsByEncounter[encId] = entry;
                _encounterList.AddChild(entry);
            }
        }

        // Re-apply encounter lock if the encounter still exists in the rebuilt list.
        if (pendingLockedEncounterId != null &&
            _lockedCategory == null && // category lock takes priority (set above)
            _highlightPanels.TryGetValue(pendingLockedEncounterId, out var lockedHighlight))
        {
            _lockedEncounterId = pendingLockedEncounterId;
            _lockedRowWrap = lockedHighlight;
            lockedHighlight.AddThemeStyleboxOverride("panel", s_rowLockedStyle);
            var capturedLockedId = pendingLockedEncounterId;
            Callable.From(() =>
            {
                UpdateStatsPanel(capturedLockedId);
                ScheduleMonsterPreviewRender(capturedLockedId);
            }).CallDeferred();
        }

        // Force the scroll container to recompute its content limit so scroll bounds match
        // the actual list size for the current biome (not the largest biome we've shown).
        //
        // The catch is NScrollableContainer.ScrollLimitBottom reads `_content.Size.Y`, but
        // VBoxContainer's Size.Y is only updated by Godot's container layout pass — which
        // runs after the current frame. So when we just removed/added rows, Size.Y is still
        // the OLD (potentially much larger) height for one frame. Instead of waiting two
        // frames, we explicitly set Size.Y to the new combined-min-height ourselves so the
        // scroll bound is correct from the very next frame onward. We preserve Size.X so
        // the rows don't collapse horizontally (the bug from the previous ResetSize attempt).
        if (_bestiaryScrollContainer != null && _encounterList != null)
        {
            var scroll = _bestiaryScrollContainer;
            var content = _encounterList;
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(scroll) || !GodotObject.IsInstanceValid(content)) return;
                try
                {
                    var minSize = content.GetCombinedMinimumSize();
                    if (minSize.Y > 0)
                        content.Size = new Vector2(content.Size.X, minSize.Y);
                    scroll.SetContent(content);
                    scroll.InstantlyScrollToTop();
                }
                catch { /* defensive — content tree may have changed since this was queued */ }
            }).CallDeferred();
        }
    }

    private const float StatColumnWidthPx = 70f;
    // Right padding in the column header row. Sized to align the header's stat-label
    // right edge with each encounter row's stat-label right edge. An encounter row's
    // stat right edge sits at (row.right - RowMarginRightPx - highlightPanel.CM_right)
    // = row.right - 12. The header ends at (row.right - StatColumnRightPadPx - 6sep),
    // so StatColumnRightPadPx = 6 makes the two right edges coincide.
    private const float StatColumnRightPadPx = 6f;
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
        // Match the tooltip shadow (TooltipHelper.Fonts.Shadow) so the bestiary and
        // tooltip surfaces feel like one family. Offsets (3, 2) also match.
        var shadow = TooltipHelper.Fonts.Shadow;
        switch (control)
        {
            case RichTextLabel rt:
                rt.AddThemeColorOverride("font_shadow_color", shadow);
                // Offset 3/2 — 1.5:1 right:down ratio.
                rt.AddThemeConstantOverride("shadow_offset_x", 3);
                rt.AddThemeConstantOverride("shadow_offset_y", 2);
                rt.AddThemeConstantOverride("shadow_outline_size", 0);
                break;
            case Label lb:
                lb.AddThemeColorOverride("font_shadow_color", shadow);
                lb.AddThemeConstantOverride("shadow_offset_x", 3);
                lb.AddThemeConstantOverride("shadow_offset_y", 2);
                lb.AddThemeConstantOverride("shadow_outline_size", 0);
                break;
            case Button bt:
                // Buttons use font_outline instead of font_shadow_offset because the
                // shadow offset reads poorly over variable button backgrounds (hover,
                // pressed, selected states); an outline stays legible on all of them.
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

        var displayMode = _sortMode == EncounterSortMode.Name ? EncounterSortMode.MedianDamage : _sortMode;

        // Left-side "Encounter" label so the list structure is self-documenting (the
        // right-side stat header was the only column header before). Same Kreon font /
        // size / muted-gold tint as the stat header for symmetry.
        // Plain Label (not RichTextLabel) so it reports a real minimum width from its
        // text content — with RichTextLabel + FitContent + no CustomMinimumSize Godot
        // collapses to a 0-wide thin vertical line, which is the regression we hit
        // the first time. The spacer to its right still ExpandFills to push the stat
        // header to the right edge.
        var encounterHeader = new Label();
        encounterHeader.Text = "Encounter";
        encounterHeader.MouseFilter = MouseFilterEnum.Ignore;
        encounterHeader.AddThemeColorOverride("font_color", new Color(0.88f, 0.85f, 0.76f, 1f));
        encounterHeader.AddThemeFontSizeOverride("font_size", 14);
        ApplyKreonFont(encounterHeader);
        ApplyTextShadow(encounterHeader);
        // Left margin matches the encounter rows below: 20px lead spacer + 62px icon
        // column + 8px HBox separation = 90px before the highlight panel starts, plus
        // the highlight panel's left content margin (RowMarginLeftPx). The header row
        // itself has separation=6, so subtract that from the spacer.
        _statsColumnHeaderRow.AddChild(new Control { CustomMinimumSize = new Vector2(84 + RowMarginLeftPx, 0) });
        _statsColumnHeaderRow.AddChild(encounterHeader);

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
        // Match the Encounter label color on the same row so both column headers read as one family.
        headerLabel.Text = $"[right][color=#e0d9c2]{EncounterSorting.Label(displayMode)}[/color][/right]";
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
        SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
        if (!_collapsedCategories.Add(category))
            _collapsedCategories.Remove(category);
        // The mouse is still over the (now-replaced) header; let RefreshEncounterList
        // hand the hover state off to the freshly-built catWrap so the highlight
        // doesn't flicker off and back on.
        _pendingHoverCategory = category;
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
                    SaveBestiaryState(_selectedBiome, _sortMode, _sortDescending, _sortCharacter, _sortBySignificance);
                    RefreshEncounterList();
                });
            _sortTabRow.AddChild(btn);
        }

        // ── Rank by: Raw / Significance chips ──
        // Only meaningful for stat-based sort modes. Name sorts alphabetically
        // regardless; Seen is the raw sample count and has no baseline comparison.
        // For the three rate/average modes, significance sorts by z-score vs the
        // category baseline, so well-sampled encounters with meaningful deviation
        // surface above low-N noise.
        var gap = new Control { CustomMinimumSize = new Vector2(16, 0) };
        _sortTabRow.AddChild(gap);

        var rankLabel = new Label();
        rankLabel.Text = "Rank by:";
        rankLabel.AddThemeColorOverride("font_color", new Color(0.88f, 0.86f, 0.80f, 1f));
        rankLabel.AddThemeFontSizeOverride("font_size", 14);
        ApplyKreonFont(rankLabel);
        _sortTabRow.AddChild(rankLabel);

        var rawBtn = MakeChipButton(
            "Raw",
            !_sortBySignificance,
            () =>
            {
                if (!_sortBySignificance) return;
                _sortBySignificance = false;
                SaveBestiaryState(_selectedBiome, _sortMode, _sortDescending, _sortCharacter, _sortBySignificance);
                RefreshEncounterList();
            });
        _sortTabRow.AddChild(rawBtn);

        var sigBtn = MakeChipButton(
            "Significance",
            _sortBySignificance,
            () =>
            {
                if (_sortBySignificance) return;
                _sortBySignificance = true;
                SaveBestiaryState(_selectedBiome, _sortMode, _sortDescending, _sortCharacter, _sortBySignificance);
                RefreshEncounterList();
            });
        _sortTabRow.AddChild(sigBtn);

        // "?" help chip — hover shows a tooltip explaining both Raw and Significance.
        var rankHelp = BuildRankByHelpButton();
        _sortTabRow.AddChild(rankHelp);
    }

    private Button BuildRankByHelpButton()
    {
        var btn = new Button
        {
            Text = "?",
            CustomMinimumSize = new Vector2(24, 24),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText =
                "Raw: sort by the raw metric value. Encounters with few fights can rank high " +
                "purely from sample-size noise.\n\n" +
                "Significance: sort by z-score vs the category baseline. Well-sampled encounters " +
                "whose metric meaningfully deviates from the pool baseline surface above low-N noise.",
        };

        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.10f, 0.11f, 0.15f, 0.85f);
        style.BorderColor = new Color(0.40f, 0.45f, 0.55f, 0.85f);
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(12);
        style.ContentMarginLeft = style.ContentMarginRight = 5;
        style.ContentMarginTop = style.ContentMarginBottom = 0;
        btn.AddThemeStyleboxOverride("normal", style);
        var hover = (StyleBoxFlat)style.Duplicate();
        hover.BorderColor = new Color(0.918f, 0.745f, 0.318f, 0.95f);
        hover.BgColor = new Color(0.15f, 0.16f, 0.22f, 1f);
        btn.AddThemeStyleboxOverride("hover", hover);

        btn.AddThemeColorOverride("font_color", new Color(0.80f, 0.78f, 0.72f, 1f));
        btn.AddThemeColorOverride("font_hover_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        btn.AddThemeFontSizeOverride("font_size", 14);
        ApplyKreonFont(btn, bold: true);
        ApplyTextShadow(btn);
        return btn;
    }

    private string BuildSortLabel(EncounterSortMode mode, bool selected)
    {
        var label = EncounterSorting.Label(mode);
        if (!selected) return label;
        // Name has its own arrow semantics (A↓ vs Z↑); other modes show high→low / low→high
        var arrow = _sortDescending ? "↓" : "↑";
        return $"{label} {arrow}";
    }

    // Icon-only Table Style selector — mimics NCardPoolFilter from the card library.
    // Scale-based selection: selected icon sits at 1.1× (slightly larger), unselected
    // at 0.95×. On hover, the current scale is multiplied by 1.2× using a SINE-OUT
    // tween (0.05s in / 0.3s out), matching NCardPoolFilter's tween values. No halo,
    // no alpha dimming — size alone cues selection + hover state. Pivot is set to
    // the icon center so the scale grows symmetrically.
    private const int SelectorIconSize = 56;
    private static readonly Vector2 SelectorSelectedScale   = Vector2.One * 1.1f;
    private static readonly Vector2 SelectorUnselectedScale = Vector2.One * 0.95f;
    // Prismatic Gem is the Overview slot — visually it's a chunky relic sprite and
    // reads larger than the character head icons at the same scale, so shrink it a
    // bit so it doesn't dominate the row.
    private const float SelectorGemScaleFactor = 0.75f;
    private const float SelectorHoverGrowth = 1.2f;
    private const double SelectorHoverInDuration = 0.05;
    private const double SelectorHoverOutDuration = 0.3;
    // Unselected icons fade to this alpha so the selected one stands out in addition
    // to the scale cue. Selected = full alpha.
    private const float SelectorUnselectedAlpha = 0.45f;

    private void RebuildSortCharRow()
    {
        if (_sortCharRow == null) return;

        while (_sortCharRow.GetChildCount() > 0)
        {
            var child = _sortCharRow.GetChild(0);
            _sortCharRow.RemoveChild(child);
            child.QueueFree();
        }

        // Overview slot → Prismatic Gem (the relic that grants access to every color in
        // STS — same symbol as the all-chars row on the stats table).
        var options = new List<(string? id, Texture2D? icon)>
        {
            (null, LoadPrismaticGemTexture()),
        };
        foreach (var (id, _) in EncounterTooltipHelper.CanonicalCharacters)
            options.Add((id, LoadCharacterIconTexture(id)));

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
            options.Add((id, LoadCharacterIconTexture(id)));

        foreach (var (id, icon) in options)
        {
            if (icon == null) continue;
            bool selected = id == _sortCharacter;
            var capturedId = id;

            // Overview (Prismatic Gem) slot gets an additional shrink factor so the
            // chunky relic sprite visually balances with the smaller character heads.
            float sizeFactor = id == null ? SelectorGemScaleFactor : 1f;
            var baseScale = (selected ? SelectorSelectedScale : SelectorUnselectedScale) * sizeFactor;

            // Wrapper so the shadow TextureRect can render behind the interactive
            // TextureButton and scale alongside it via a shared scale tween target.
            // Wrapper size matches SelectorIconSize; actual icon + shadow scale is
            // driven by the TextureButton's Scale (both rects share PivotOffset
            // and their parent transform scales them together).
            var slot = new Control
            {
                CustomMinimumSize = new Vector2(SelectorIconSize, SelectorIconSize),
                MouseFilter = Control.MouseFilterEnum.Pass,
                PivotOffset = new Vector2(SelectorIconSize / 2f, SelectorIconSize / 2f),
                Scale = baseScale,
                Modulate = new Color(1f, 1f, 1f, selected ? 1f : SelectorUnselectedAlpha),
            };

            // Drop shadow — same icon texture rendered black-with-alpha behind the
            // main one, offset a few pixels. Matches card library's shadowed icons.
            var shadowRect = new TextureRect
            {
                Texture = icon,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
                OffsetLeft = 3, OffsetTop = 3, OffsetRight = 3, OffsetBottom = 3,
                Modulate = new Color(0f, 0f, 0f, 0.45f),
            };
            slot.AddChild(shadowRect);

            var tb = new TextureButton
            {
                TextureNormal = icon,
                IgnoreTextureSize = true,
                StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
                AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
                OffsetLeft = 0, OffsetTop = 0, OffsetRight = 0, OffsetBottom = 0,
            };
            slot.AddChild(tb);

            var slotRef = slot;
            tb.Pressed += () =>
            {
                SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
                _sortCharacter = capturedId;
                SaveBestiaryState(_selectedBiome, _sortMode, _sortDescending, _sortCharacter, _sortBySignificance);
                RefreshEncounterList();
            };
            tb.MouseEntered += () =>
            {
                SfxCmd.Play("event:/sfx/ui/clicks/ui_hover");
                var t = slotRef.CreateTween();
                t.TweenProperty(slotRef, "scale", baseScale * SelectorHoverGrowth, SelectorHoverInDuration)
                 .SetTrans(Tween.TransitionType.Sine)
                 .SetEase(Tween.EaseType.Out);
            };
            tb.MouseExited += () =>
            {
                var t = slotRef.CreateTween();
                t.TweenProperty(slotRef, "scale", baseScale, SelectorHoverOutDuration)
                 .SetTrans(Tween.TransitionType.Sine);
            };
            _sortCharRow.AddChild(slot);
        }
        MainFile.Logger.Info($"[SlayTheStats] RebuildSortCharRow: {options.Count} options, selected={_sortCharacter ?? "<overview>"}");
    }

    private static Texture2D? _prismaticGemTexture;
    private static bool _prismaticGemProbed;

    /// <summary>Loads the Prismatic Gem relic icon as a Texture2D for the Overview
    /// slot of the Table Style selector. Cached after first probe.</summary>
    private static Texture2D? LoadPrismaticGemTexture()
    {
        if (_prismaticGemProbed) return _prismaticGemTexture;
        _prismaticGemProbed = true;
        // Candidate paths mirror EncounterTooltipHelper.AllCharsIcon's resolution logic:
        // the canonical name is prismatic_gem (PrismaticGem → StringHelper.Slugify → PRISMATIC_GEM → prismatic_gem).
        string[] candidates =
        {
            "res://images/relics/prismatic_gem.png",
            "res://images/relics/prismaticgem.png",
        };
        foreach (var path in candidates)
        {
            if (ResourceLoader.Exists(path))
            {
                _prismaticGemTexture = ResourceLoader.Load<Texture2D>(path);
                return _prismaticGemTexture;
            }
        }
        MainFile.Logger.Warn("[SlayTheStats] LoadPrismaticGemTexture: failed to resolve any candidate: " + string.Join(", ", candidates));
        return null;
    }

    /// <summary>Loads the top-panel character head sprite as a Texture2D for use
    /// as a button icon. Returns null if the path doesn't resolve (e.g. for modded
    /// characters without a matching asset).</summary>
    private static Texture2D? LoadCharacterIconTexture(string characterId)
    {
        var name = characterId.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
            ? characterId.Substring("CHARACTER.".Length)
            : characterId;
        var path = $"res://images/ui/top_panel/character_icon_{name.ToLowerInvariant()}.png";
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
    }

    private Button MakeChipButton(string text, bool selected, Action onPressed, Texture2D? icon = null)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(0, 28);
        if (icon != null)
        {
            btn.Icon = icon;
            btn.ExpandIcon = false;
            btn.IconAlignment = HorizontalAlignment.Left;
            btn.AddThemeConstantOverride("h_separation", 6);
            btn.AddThemeConstantOverride("icon_max_width", 20);
        }

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
            selected ? new Color(0.918f, 0.745f, 0.318f, 1f) : new Color(0.94f, 0.92f, 0.85f, 1f));
        btn.AddThemeColorOverride("font_hover_color", new Color(0.918f, 0.745f, 0.318f, 1f));
        btn.AddThemeFontSizeOverride("font_size", 14);
        ApplyKreonFont(btn);
        ApplyTextShadow(btn);

        btn.Pressed += () =>
        {
            // Match the native NButton click sfx so the bestiary's biome / sort / score-by
            // chip rows feel consistent with the rest of the game's UI buttons.
            SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
            onPressed();
        };
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
        string? sortCharacter,
        bool bySignificance,
        string? biome = null)
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
            .Select(id => (id, score: EncounterSorting.Score(id, mode, filter, sortCharacter, bySignificance, biome)))
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
                selected ? new Color(0.918f, 0.745f, 0.318f, 1f) : new Color(0.94f, 0.92f, 0.85f, 1f));
            btn.AddThemeColorOverride("font_hover_color", new Color(0.918f, 0.745f, 0.318f, 1f));
            btn.AddThemeFontSizeOverride("font_size", 18);
            ApplyKreonFont(btn, bold: true);
            ApplyTextShadow(btn);

            var capturedBiome = biome;
            btn.Pressed += () =>
            {
                SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
                _selectedBiome = capturedBiome;
                SaveBestiaryState(_selectedBiome, _sortMode, _sortDescending, _sortCharacter, _sortBySignificance);
                RefreshEncounterList();
            };
            _biomeTabRow.AddChild(btn);
        }
    }

    // ───────────────────────── Encounter Rows ─────────────────────────

    // Both styles use IDENTICAL content margins so swapping them on hover never moves layout.
    // The only visual difference is the highlight's background color and left accent border.
    private const int RowMarginLeftPx   = 10;
    private const int RowMarginRightPx  = 6;
    /// <summary>Extra left indent for category name text inside the highlight panel.
    /// The highlight background starts aligned with encounter rows, but the category
    /// text sits slightly further right for visual hierarchy.</summary>
    private const int CategoryTextIndentPx = 4;
    private const int RowMarginTopPx    = 0;
    private const int RowMarginBottomPx = 0;

    private static readonly StyleBoxFlat s_rowHighlightStyle = MakeRowHighlightStyle();
    private static readonly StyleBoxFlat s_rowLockedStyle = MakeRowLockedStyle();
    private static StyleBoxFlat MakeRowHighlightStyle()
    {
        var s = new StyleBoxFlat();
        s.BgColor      = new Color(0.918f, 0.745f, 0.318f, 0.18f);
        s.BorderColor  = new Color(0.918f, 0.745f, 0.318f, 0.55f);
        s.BorderWidthLeft   = 0;
        s.SetCornerRadiusAll(2);
        s.ContentMarginLeft   = RowMarginLeftPx;
        s.ContentMarginRight  = RowMarginRightPx;
        s.ContentMarginTop    = RowMarginTopPx;
        s.ContentMarginBottom = RowMarginBottomPx;
        return s;
    }
    private static StyleBoxFlat MakeRowLockedStyle()
    {
        var s = new StyleBoxFlat();
        s.BgColor      = new Color(0.85f, 0.85f, 0.85f, 0.12f);
        s.BorderColor  = new Color(0.85f, 0.85f, 0.85f, 0.35f);
        s.BorderWidthLeft   = 0;
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
    /// <summary>Recursively set MouseFilter.Ignore on every descendant of the given
    /// row wrapper (but not the wrapper itself) so clicks always land on wrap and
    /// reach its GuiInput handler. Used by encounter rows to make the entire row
    /// area click-lockable without the lead spacer / right pad / inner labels
    /// silently absorbing events.</summary>
    private static void MakeRowChildrenClickTransparent(Control wrap)
    {
        foreach (var child in wrap.GetChildren())
            SetMouseFilterIgnoreRecursive(child);
    }

    private static void SetMouseFilterIgnoreRecursive(Node node)
    {
        if (node is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in node.GetChildren())
            SetMouseFilterIgnoreRecursive(child);
    }

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
        CategoryHoverBundle bundle,
        string? biome = null)
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

        // Lead spacer mirrors the width of the category header's collapse arrow (20px) so
        // the icon column and highlight panel align with category rows.
        var leadSpacer = new Control { CustomMinimumSize = new Vector2(20, 0) };
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

        // Highlight panel — covers only the name + stat area. The arrow and icon
        // sit outside this panel so the hover/lock highlight doesn't extend over them.
        var highlightPanel = new PanelContainer();
        highlightPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        highlightPanel.SizeFlagsVertical = SizeFlags.ExpandFill;
        highlightPanel.MouseFilter = MouseFilterEnum.Ignore;
        highlightPanel.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
        row.AddChild(highlightPanel);
        _highlightPanels[encounterId] = highlightPanel;

        var innerRow = new HBoxContainer();
        innerRow.AddThemeConstantOverride("separation", 8);
        innerRow.MouseFilter = MouseFilterEnum.Ignore;
        innerRow.Alignment = BoxContainer.AlignmentMode.Center;
        highlightPanel.AddChild(innerRow);

        var nameLabel = new RichTextLabel();
        nameLabel.BbcodeEnabled = true;
        nameLabel.FitContent = true;
        nameLabel.ScrollActive = false;
        nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        nameLabel.MouseFilter = MouseFilterEnum.Ignore;
        nameLabel.AddThemeColorOverride("default_color", new Color(1.00f, 0.98f, 0.92f, 1f));
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
            monsterInfo = $"  [color=#686868]({string.Join(", ", monsterCounts)})[/color]";
        }
        nameLabel.Text = $"{encounterName}{monsterInfo}";
        innerRow.AddChild(nameLabel);

        // Stat column on the right — shows whichever stat the active sort mode is keyed on
        // (Dmg% by default), filtered by character if a sort character is selected.
        var statLabel = new RichTextLabel();
        statLabel.BbcodeEnabled = true;
        statLabel.FitContent = true;
        statLabel.ScrollActive = false;
        statLabel.CustomMinimumSize = new Vector2(StatColumnWidthPx, 0);
        statLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        statLabel.MouseFilter = MouseFilterEnum.Ignore;
        // ClipContents off so the text shadow (3px right / 2px down) isn't
        // clipped at the label's right edge — the shadow would otherwise get
        // cut off past the last glyph.
        statLabel.ClipContents = false;
        // Match the name label's font size (17pt) so the stat column's text baseline
        // aligns vertically with the encounter name on its left.
        statLabel.AddThemeFontSizeOverride("normal_font_size", 17);
        ApplyKreonFont(statLabel);
        ApplyTextShadow(statLabel);
        var displayMode = sortMode == EncounterSortMode.Name ? EncounterSortMode.MedianDamage : sortMode;
        // FormatScoreColored embeds its own color tags via TooltipHelper.ColWR — for
        // Name/Seen modes (no good/bad direction) it falls back to plain text, which we
        // wrap in the existing dim grey here.
        var scoreText = EncounterSorting.FormatScoreColored(encounterId, displayMode, filter, sortCharacter, biome);
        var hasBbcodeColor = scoreText.Contains("[color=");
        statLabel.Text = hasBbcodeColor
            ? $"[right]{scoreText}[/right]"
            : $"[right][color=#d8d3c8]{scoreText}[/color][/right]";
        innerRow.AddChild(statLabel);

        var capturedCategory = category;
        var capturedEncId = encounterId;
        var capturedHighlight = highlightPanel;
        wrap.MouseEntered += () =>
        {
            // If THIS row is locked, keep locked (white) style — don't override
            // with hover (yellow). For any other row, show hover.
            var style = (_lockedEncounterId == capturedEncId)
                ? (StyleBox)s_rowLockedStyle
                : s_rowHighlightStyle;
            capturedHighlight.AddThemeStyleboxOverride("panel", style);
            HighlightCategory(capturedCategory, true, capturedEncId);
            OnEncounterHover(capturedEncId);
        };
        wrap.MouseExited += () =>
        {
            // If THIS row is locked, keep locked style. Otherwise clear.
            var fallback = (_lockedEncounterId == capturedEncId)
                ? (StyleBox)s_rowLockedStyle
                : MakeRowEmptyStyle();
            capturedHighlight.AddThemeStyleboxOverride("panel", fallback);
            HighlightCategory(capturedCategory, false, capturedEncId);
            OnEncounterUnhover();
        };
        wrap.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                if (_lockedEncounterId == capturedEncId)
                {
                    // Toggle unlock — mouse is still over this row, so
                    // immediately show hover highlight instead of empty.
                    ClearLockedEncounter();
                    capturedHighlight.AddThemeStyleboxOverride("panel", s_rowHighlightStyle);
                }
                else
                {
                    LockEncounter(capturedEncId, capturedHighlight);
                }
                wrap.AcceptEvent();
            }
            // Double-click on an encounter row collapses its parent category.
            if (ev is InputEventMouseButton db && db.DoubleClick && db.ButtonIndex == MouseButton.Left)
            {
                ToggleCategoryCollapse(capturedCategory);
                wrap.AcceptEvent();
            }
        };

        // Force every visual descendant of the row to MouseFilter.Ignore so the
        // hit test walks past them and lands on `wrap` itself. Without this,
        // children like the lead spacer / right-pad spacer / boss icon wrapper
        // (all default MouseFilter.Stop) catch clicks on most of the row area
        // and silently absorb them — wrap.GuiInput never fires and the lock
        // behaviour gets wonky depending on which pixel the user happens to
        // click. Hover highlighting still works because MouseEntered/MouseExited
        // on `wrap` test against its full rect regardless of descendant filters.
        MakeRowChildrenClickTransparent(wrap);

        return wrap;
    }

    // ───────────────────────── Hover Logic ─────────────────────────

    private void OnEncounterHover(string encounterId)
    {
        _hoveredEncounterId = encounterId;
        // If any row (encounter or category) is click-locked, hover doesn't change
        // the displayed stats or preview.
        if (_lockedEncounterId != null || _lockedCategory != null) return;
        UpdateStatsPanel(encounterId);
        ScheduleMonsterPreviewRender(encounterId);
    }

    /// <summary>
    /// Route a hover through either the instant cache-hit path or the
    /// debounce timer. Cache hits swap to the already-live viewport without
    /// waiting (no reason to debounce — the cost is just a TextureRect swap).
    /// Cache misses start the debounce timer; only if the hover settles for
    /// <see cref="HoverDebounceSeconds"/> do we actually instantiate a new
    /// SubViewport. Rapid scrolling across unseen rows therefore costs ~zero.
    /// </summary>
    private void ScheduleMonsterPreviewRender(string encounterId)
    {
        // Cache hit (either mode): instant render, skip debounce.
        bool staticHit = SlayTheStatsConfig.BestiaryPreviewStatic &&
                         _staticSpriteCache.ContainsKey(encounterId);
        bool liveHit   = !SlayTheStatsConfig.BestiaryPreviewStatic &&
                         _liveViewports.ContainsKey(encounterId);
        if (staticHit || liveHit)
        {
            CancelPendingRender();
            RenderMonsterPreview(encounterId);
            return;
        }

        // Cache miss: tear down the visible rect immediately so the user gets
        // feedback that their hover registered, and deactivate any live
        // viewport that was displayed. Then arm the debounce.
        ClearMonsterPreview();

        _pendingHoverEncounterId = encounterId;
        EnsureHoverDebounceTimer();
        _hoverDebounceTimer!.Start(); // restarts the countdown
    }

    private void EnsureHoverDebounceTimer()
    {
        if (_hoverDebounceTimer != null) return;
        _hoverDebounceTimer = new Godot.Timer
        {
            OneShot = true,
            WaitTime = HoverDebounceSeconds,
            // Keep the timer ticking even if the submenu is Hidden momentarily
            // during a submenu transition; otherwise the Timeout signal can
            // drop on the floor and leave the debounce state half-armed.
            ProcessMode = Node.ProcessModeEnum.Always,
        };
        _hoverDebounceTimer.Timeout += OnHoverDebounceTimeout;
        ((Node)this).AddChild(_hoverDebounceTimer);
    }

    private void CancelPendingRender()
    {
        _pendingHoverEncounterId = null;
        _hoverDebounceTimer?.Stop();
    }

    private void OnHoverDebounceTimeout()
    {
        var enc = _pendingHoverEncounterId;
        _pendingHoverEncounterId = null;
        if (enc == null) return;
        // The user may have moved to a different row in the meantime — if
        // they're not still hovering this one, drop the render.
        if (_hoveredEncounterId != enc) return;
        RenderMonsterPreview(enc);
    }

    private void OnCategoryHover(string category, List<string> encounterIds)
    {
        if (_statsLabel == null) return;
        // Don't override a locked display (encounter or category).
        if (_lockedEncounterId != null || _lockedCategory != null) return;

        var filter = BuildFilter();
        var categoryLabel = EncounterCategory.FormatCategory(category);

        // Scope the category aggregation to whatever the user has filtered the encounter
        // list to: a specific biome ("BIOME.OVERGROWTH"), a synthetic act key ("act:1"
        // → MatchesBiome filters by act), or all encounters (null when "all:" or unset).
        // Previously this read firstMeta.Biome, which collapsed an "Act 1" view down to
        // whichever biome happened to come first in the encounter list — making the act
        // tab silently equivalent to a single biome tab.
        string? catBiome = _selectedBiome;
        if (string.IsNullOrEmpty(catBiome) || catBiome == AllBiomeKey)
            catBiome = null;

        string statsText;

        if (_sortCharacter != null)
        {
            // Focused single-character category view: biome pool → all pool → all chars
            var charFilter = new AggregationFilter
            {
                Character = _sortCharacter,
                GameMode = filter.GameMode,
                AscensionMin = filter.AscensionMin,
                AscensionMax = filter.AscensionMax,
                VersionMin = filter.VersionMin,
                VersionMax = filter.VersionMax,
                Profile = filter.Profile,
                Display = filter.Display,
            };

            // All pool aggregations are encounter-weighted so each encounter counts equally
            // regardless of how often the player has fought it.
            var poolBiome = !string.IsNullOrEmpty(catBiome)
                ? StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, charFilter, category, catBiome)
                : StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, charFilter, category);

            var poolAll = StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, charFilter, category);

            var allCharsPool = !string.IsNullOrEmpty(catBiome)
                ? StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, filter, category, catBiome)
                : StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, filter, category);

            var biomeLabel = !string.IsNullOrEmpty(catBiome) ? FormatBiomeName(catBiome) : null;
            var charLabel = FormatCharacterLabel(_sortCharacter);

            statsText = EncounterTooltipHelper.BuildCategoryStatsTextFocused(
                poolBiome, poolAll, allCharsPool,
                _sortCharacter, biomeLabel, categoryLabel, filter,
                category);

            var scopePrefix = biomeLabel ?? "All";
            SetStatsTitle($"{scopePrefix} {categoryLabel} Encounter Stats");
        }
        else
        {
            // All-characters view: per-character rows + All baseline
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
                    EncounterTooltipHelper.Accumulate(agg, stat);
                }
            }

            // Baselines use the encounter's actual biome (category biome), not the selected tab
            double deathRateBaseline = StatsAggregator.GetEncounterDeathRateBaseline(MainFile.Db, filter, category, catBiome);
            double dmgPctBaseline = StatsAggregator.GetEncounterDmgPctBaseline(MainFile.Db, filter, category, catBiome);
            double iqrcBaseline = StatsAggregator.GetEncounterIqrcBaseline(MainFile.Db, filter, category, catBiome);

            var biomeName = _selectedBiome != null && _selectedBiome != "all:" ? FormatBiomeName(_selectedBiome) : "All";
            SetStatsTitle($"{biomeName} {categoryLabel} Encounter Stats");

            var text = EncounterTooltipHelper.BuildEncounterStatsText(
                combined, deathRateBaseline, dmgPctBaseline, iqrcBaseline,
                filter.AscensionMin, filter.AscensionMax, categoryLabel,
                filter: filter);
            ShowSingleLabelStats(text);

            ClearMonsterPreview();
            return;
        }

        ShowSingleLabelStats(statsText);

        // Clear the monster preview area when hovering a category (no specific monsters to show)
        ClearMonsterPreview();
    }

    private void OnEncounterUnhover()
    {
        _hoveredEncounterId = null;
        // A locked row stays pinned on unhover — the user's click intent wins over
        // the pointer leaving the row. Filters/biome/sort changes call
        // ClearLockedEncounter explicitly to release the lock.
        if (_lockedEncounterId != null || _lockedCategory != null) return;
        CancelPendingRender();
        SetStatsTitle("");
        ShowSingleLabelStats($"[color=#686868]Hover an encounter to see stats[/color]");
        ClearMonsterPreview();
    }

    /// <summary>Pin an encounter row as the current display target. Subsequent
    /// hover/unhover events are suppressed until the lock is released (by clicking
    /// another row to switch the lock, or clicking an empty area to clear it).</summary>
    private void LockEncounter(string encounterId, PanelContainer wrap)
    {
        SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
        // Clear any existing category lock.
        if (_lockedCategoryWrap != null && GodotObject.IsInstanceValid(_lockedCategoryWrap))
            _lockedCategoryWrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
        _lockedCategory = null;
        _lockedCategoryEncIds = null;
        _lockedCategoryWrap = null;

        // Clear the old locked encounter row's highlight if it's a different row.
        if (_lockedRowWrap != null && _lockedRowWrap != wrap &&
            GodotObject.IsInstanceValid(_lockedRowWrap))
        {
            _lockedRowWrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
        }

        _lockedEncounterId = encounterId;
        _lockedRowWrap = wrap;
        _hoveredEncounterId = encounterId;
        // The mouse is still over the row we just clicked, so keep the hover
        // (yellow) highlight active — the locked (white) style shows once the
        // mouse leaves.
        UpdateStatsPanel(encounterId);
        ScheduleMonsterPreviewRender(encounterId);
    }

    /// <summary>Lock a category row's hover stats. Mutually exclusive with encounter lock.</summary>
    private void LockCategory(string category, List<string> encounterIds, PanelContainer catWrap)
    {
        SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
        // Clear any existing encounter lock first.
        if (_lockedRowWrap != null && GodotObject.IsInstanceValid(_lockedRowWrap))
            _lockedRowWrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
        _lockedEncounterId = null;
        _lockedRowWrap = null;

        // Clear a previous category lock if switching.
        if (_lockedCategoryWrap != null && _lockedCategoryWrap != catWrap &&
            GodotObject.IsInstanceValid(_lockedCategoryWrap))
            _lockedCategoryWrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());

        _lockedCategory = category;
        _lockedCategoryEncIds = encounterIds;
        _lockedCategoryWrap = catWrap;

        // Force the category hover stats to display.
        OnCategoryHoverForced(category, encounterIds);
    }

    /// <summary>Force-display category hover stats, bypassing the lock guard.
    /// Used by LockCategory to update the display on the frame the lock is set.</summary>
    private void OnCategoryHoverForced(string category, List<string> encounterIds)
    {
        // Temporarily clear the lock flag so OnCategoryHover doesn't early-return.
        var savedCat = _lockedCategory;
        var savedEnc = _lockedEncounterId;
        _lockedCategory = null;
        _lockedEncounterId = null;
        OnCategoryHover(category, encounterIds);
        _lockedCategory = savedCat;
        _lockedEncounterId = savedEnc;
    }

    /// <summary>Release any pinned row (encounter or category). If the user is
    /// currently hovering a row the stats panel / preview snap back to the hovered
    /// row; otherwise the placeholder returns.</summary>
    private void ClearLockedEncounter()
    {
        bool hadLock = _lockedEncounterId != null || _lockedCategory != null;
        if (!hadLock) return;
        SfxCmd.Play("event:/sfx/ui/clicks/ui_click");

        // Revert locked row/category highlight to empty.
        if (_lockedRowWrap != null && GodotObject.IsInstanceValid(_lockedRowWrap))
            _lockedRowWrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());
        if (_lockedCategoryWrap != null && GodotObject.IsInstanceValid(_lockedCategoryWrap))
            _lockedCategoryWrap.AddThemeStyleboxOverride("panel", MakeRowEmptyStyle());

        _lockedEncounterId = null;
        _lockedRowWrap = null;
        _lockedCategory = null;
        _lockedCategoryEncIds = null;
        _lockedCategoryWrap = null;

        if (_hoveredEncounterId != null)
        {
            UpdateStatsPanel(_hoveredEncounterId);
            ScheduleMonsterPreviewRender(_hoveredEncounterId);
        }
        else
        {
            SetStatsTitle("");
            ShowSingleLabelStats($"[color=#686868]Hover an encounter to see stats[/color]");
            ClearMonsterPreview();
        }
    }

    private void UpdateStatsPanel(string encounterId)
    {
        if (_statsLabel == null) return;

        if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap))
        {
            SetStatsTitle(EncounterCategory.FormatName(encounterId));
            ShowSingleLabelStats("[color=#686868]No data[/color]");
            return;
        }

        var filter = BuildFilter();

        string? category = null;
        string? encBiome = null;
        if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
        {
            category = meta.Category;
            encBiome = meta.Biome;
        }

        var categoryLabel = category != null ? EncounterCategory.FormatCategory(category) : "";
        var encounterName = EncounterCategory.FormatName(encounterId);

        string statsText;

        if (_sortCharacter != null)
        {
            // Focused single-character view: section-based layout
            var charFilter = new AggregationFilter
            {
                Character = _sortCharacter,
                GameMode = filter.GameMode,
                AscensionMin = filter.AscensionMin,
                AscensionMax = filter.AscensionMax,
                VersionMin = filter.VersionMin,
                VersionMax = filter.VersionMax,
                Profile = filter.Profile,
                Display = filter.Display,
            };

            // Row 1: this encounter, this character (single encounter — fight-weighted)
            var charStats = StatsAggregator.AggregateEncountersByCharacter(contextMap, filter);
            var encounterStat = charStats.TryGetValue(_sortCharacter, out var cs) ? cs : new EncounterEvent();

            // Row 2: act+category pool — encounter-weighted across all encounters in the
            // same category within the encounter's act (e.g., Act 1 includes Overgrowth +
            // Underdocks). Each encounter counts equally regardless of fight frequency.
            int encAct = meta?.Act ?? 0;
            PoolMetrics? poolAct = encAct > 0
                ? StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, charFilter, category, $"act:{encAct}")
                : (PoolMetrics?)null;

            // Row 3: all-acts category pool — encounter-weighted, always shown
            var poolAll = StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, charFilter, category);

            // Row 4: all characters vs this encounter — character-weighted aggregation.
            // Each character contributes one observation (their median, p25, p75, etc.) and
            // the percentile metrics use median-of-medians across characters; the rest
            // average across characters. Same encounter-weighted spirit as the pool rows
            // but applied to a single encounter type across characters.
            var allCharsMetrics = StatsAggregator.AggregateMetricsFromEvents(charStats.Values);

            var charLabel = FormatCharacterLabel(_sortCharacter);
            var actLabel = encAct > 0 ? $"Act {encAct}" : null;

            statsText = EncounterTooltipHelper.BuildEncounterStatsTextFocused(
                encounterStat, poolAct, poolAll, allCharsMetrics,
                encounterName, _sortCharacter, actLabel, categoryLabel, filter,
                category);

            SetStatsTitle($"{encounterName} Stats");
            ShowSingleLabelStats(statsText);
            return;
        }
        else
        {
            // All-characters view: one row per character + All baseline
            // Baselines use encounter's actual biome so tab selection doesn't affect data
            var charStats = StatsAggregator.AggregateEncountersByCharacter(contextMap, filter);

            double deathRateBaseline = StatsAggregator.GetEncounterDeathRateBaseline(MainFile.Db, filter, category, encBiome);
            double dmgPctBaseline = StatsAggregator.GetEncounterDmgPctBaseline(MainFile.Db, filter, category, encBiome);
            double iqrcBaseline = StatsAggregator.GetEncounterIqrcBaseline(MainFile.Db, filter, category, encBiome);

            SetStatsTitle($"{EncounterCategory.FormatName(encounterId)} Stats");

            if (charStats.Count == 0)
            {
                ShowSingleLabelStats(EncounterTooltipHelper.NoDataText(null, filter.AscensionMin, filter.AscensionMax));
            }
            else
            {
                var text = EncounterTooltipHelper.BuildEncounterStatsText(
                    charStats, deathRateBaseline, dmgPctBaseline, iqrcBaseline,
                    filter.AscensionMin, filter.AscensionMax, categoryLabel,
                    filter: filter);
                ShowSingleLabelStats(text);
            }
            return;
        }

        SetStatsTitle($"{EncounterCategory.FormatName(encounterId)} Stats");
        ShowSingleLabelStats(statsText);
    }

    /// <summary>
    /// Converts a character ID to a short display label for table rows.
    /// "CHARACTER.IRONCLAD" → "Ironclad", "CHARACTER.NECROBINDER" → "Necrobinder".
    /// </summary>
    private static string FormatCharacterLabel(string characterId)
    {
        foreach (var (id, label) in EncounterTooltipHelper.CanonicalCharacters)
        {
            if (id == characterId) return label;
        }
        // Modded/unknown character — strip prefix and title-case
        var dotIdx = characterId.IndexOf('.');
        var name = dotIdx >= 0 ? characterId[(dotIdx + 1)..] : characterId;
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpper(words[i][0]) + words[i][1..].ToLower();
        }
        return string.Join(' ', words);
    }

    private void SetStatsTitle(string title)
    {
        if (_statsTitleLabel == null) return;
        _statsTitleLabel.Text = string.IsNullOrEmpty(title) ? "" : $"[b]{title}[/b]";
    }

    // ───────────────────── All-chars scrollable stats ─────────────────────

    /// <summary>Current scroll offset (in pixels) for the all-characters stats table.
    /// Reset to 0 when switching to a different encounter or mode.</summary>
    private float _statsCharRowsScroll;

    /// <summary>Switches the stats area to all-characters mode with scrollable character rows.
    /// The header and baseline rows stay pinned; character rows scroll inside the clipper.</summary>
    private void ShowAllCharsStats(EncounterTooltipHelper.AllCharsTableParts parts)
    {
        if (_statsLabel != null) _statsLabel.Visible = false;

        if (_statsHeaderLabel != null) _statsHeaderLabel.Text = parts.Header;
        if (_statsHeaderMarginContainer != null) _statsHeaderMarginContainer.Visible = true;
        if (_statsScrollTopSep != null) _statsScrollTopSep.Visible = true;
        if (_statsCharRowsLabel != null) _statsCharRowsLabel.Text = parts.CharacterRows;
        if (_statsCharRowsWrapper != null) _statsCharRowsWrapper.Visible = true;
        if (_statsScrollBottomSep != null) _statsScrollBottomSep.Visible = true;
        if (_statsBaselineLabel != null) _statsBaselineLabel.Text = parts.BaselineRow;
        if (_statsBaselineMarginContainer != null) _statsBaselineMarginContainer.Visible = true;
        if (_statsFooterLabel != null)
        {
            _statsFooterLabel.Text = parts.Footer;
            _statsFooterLabel.Visible = parts.Footer.Length > 0;
        }

        _statsCharRowsScroll = 0;
        // Defer scroll setup so Godot has processed the layout and clipper has its final size.
        Godot.Callable.From(SetupStatsCharRowsLayout).CallDeferred();
    }

    /// <summary>Called deferred after layout to size the character rows label, configure
    /// the scrollbar range, and show/hide the scrollbar based on content overflow.</summary>
    private void SetupStatsCharRowsLayout()
    {
        if (_statsCharRowsLabel == null || _statsCharRowsClipper == null || _statsCharRowsWrapper == null) return;
        // Set the label width to match the clipper so BBCode tables render at the right width.
        float clipperWidth = _statsCharRowsClipper.Size.X;
        float contentHeight = _statsCharRowsLabel.GetContentHeight();
        _statsCharRowsLabel.Size = new Godot.Vector2(clipperWidth, contentHeight);

        float clipperHeight = _statsCharRowsClipper.Size.Y;
        bool needsScroll = contentHeight > clipperHeight;

        // When content is shorter than available space, shrink the wrapper so
        // the baseline/footer sit right below the last character row instead
        // of being pushed to the bottom of the fixed-height stats area.
        if (!needsScroll)
        {
            _statsCharRowsWrapper.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            _statsCharRowsWrapper.CustomMinimumSize = new Godot.Vector2(0, contentHeight);
        }
        else
        {
            _statsCharRowsWrapper.SizeFlagsVertical = SizeFlags.ExpandFill;
            _statsCharRowsWrapper.CustomMinimumSize = new Godot.Vector2(0, 0);
        }

        // Configure scrollbar range and visibility.
        if (_statsScrollbar != null)
        {
            _statsScrollbar.Visible = needsScroll;
            if (needsScroll)
            {
                _statsScrollbar.MinValue = 0;
                _statsScrollbar.MaxValue = contentHeight;
                _statsScrollbar.Page = clipperHeight;
                _statsScrollbar.Value = 0;
            }
        }

        ApplyStatsCharRowsScroll();
    }

    /// <summary>Switches the stats area back to the single-label mode (focused/category views).</summary>
    private void ShowSingleLabelStats(string text)
    {
        if (_statsLabel != null) { _statsLabel.Text = text; _statsLabel.Visible = true; }
        if (_statsHeaderMarginContainer != null) _statsHeaderMarginContainer.Visible = false;
        if (_statsScrollTopSep != null) _statsScrollTopSep.Visible = false;
        if (_statsCharRowsWrapper != null) _statsCharRowsWrapper.Visible = false;
        if (_statsScrollBottomSep != null) _statsScrollBottomSep.Visible = false;
        if (_statsBaselineMarginContainer != null) _statsBaselineMarginContainer.Visible = false;
        if (_statsFooterLabel != null) _statsFooterLabel.Visible = false;
    }

    /// <summary>Applies the current scroll offset to the character rows label inside the clipper,
    /// and syncs the scrollbar value.</summary>
    private void ApplyStatsCharRowsScroll()
    {
        if (_statsCharRowsLabel == null || _statsCharRowsClipper == null) return;

        // Clamp scroll: 0 = top, maxScroll = bottom.
        float contentHeight = _statsCharRowsLabel.GetContentHeight();
        float clipperHeight = _statsCharRowsClipper.Size.Y;
        float maxScroll = Math.Max(0, contentHeight - clipperHeight);
        _statsCharRowsScroll = Math.Clamp(_statsCharRowsScroll, 0, maxScroll);

        _statsCharRowsLabel.Position = new Godot.Vector2(0, -_statsCharRowsScroll);

        // Sync scrollbar without re-triggering the ValueChanged handler.
        if (_statsScrollbar != null && _statsScrollbar.Visible)
            _statsScrollbar.SetValueNoSignal(_statsCharRowsScroll);
    }

    /// <summary>Handles mouse wheel scroll on the all-characters stats area.</summary>
    private void OnStatsCharRowsGuiInput(Godot.InputEvent @event)
    {
        if (@event is Godot.InputEventMouseButton mb && mb.Pressed)
        {
            const float scrollStep = 30f;
            if (mb.ButtonIndex == Godot.MouseButton.WheelUp)
            {
                _statsCharRowsScroll -= scrollStep;
                ApplyStatsCharRowsScroll();
            }
            else if (mb.ButtonIndex == Godot.MouseButton.WheelDown)
            {
                _statsCharRowsScroll += scrollStep;
                ApplyStatsCharRowsScroll();
            }
        }
    }

    // ───────────────────────── Monster Preview ─────────────────────────

    /// <summary>Horizontal gap (pixels) between adjacent monsters in a multi-monster
    /// encounter preview.</summary>
    private const float MonsterPreviewGap = 40f;
    /// <summary>Padding inside the SubViewport canvas so sprite edges don't clip against
    /// the viewport border.</summary>
    private const float MonsterPreviewPad = 32f;
    /// <summary>Baseline height (in canvas pixels) used to size the SubViewport. The
    /// canvas width is derived from this + the render area's aspect ratio so the
    /// SubViewport output exactly matches the render area shape — no letterbox bars.
    /// Cross-encounter proportionality is preserved: the fixed base height means a
    /// small monster and a boss are always rendered at the same absolute pixel
    /// scale unless the encounter overflows (in which case the canvas grows in both
    /// dims together, preserving aspect).</summary>
    private const int MonsterPreviewBaseHeight = 800;
    /// <summary>Fallback aspect ratio used when the render area's size isn't yet
    /// known (e.g. first hover before the panel has been laid out).</summary>
    private const float MonsterPreviewFallbackAspect = 1.5f;
    /// <summary>Fixed pixel margin between sprite feet and the canvas bottom edge.
    /// Kept small and absolute (not a fraction of canvas height) so tall sprites
    /// don't get visually shrunk just because their bigger canvas also wastes a
    /// bigger fraction on floor space. 60px gives a pleasant "they're not pinned
    /// to the bottom" vibe without stealing screen real estate.</summary>
    private const float MonsterPreviewFloorMargin = 60f;
    /// <summary>Max number of live SubViewports kept alive in the LRU cache.
    /// Each cached viewport holds its NCreatureVisuals children (Spine scenes)
    /// in memory, but only the ACTIVE one has RenderTargetUpdateMode.Always —
    /// the rest sit at Disabled and cost nothing per frame. Memory footprint
    /// is dominated by cached Spine scene instances, which are already
    /// preloaded by the game. 40 comfortably covers a whole biome worth of
    /// encounters so most hovers are cache hits after a short warm-up.</summary>
    private const int MaxLiveViewports = 40;
    /// <summary>Debounce window in seconds. Hovers that happen while the mouse
    /// is moving rapidly get collapsed — we only instantiate a SubViewport once
    /// the hover has settled on a row for this long. 40 ms is low enough that
    /// the first-hover latency doesn't feel like lag (under the ~100 ms
    /// perception threshold) but high enough to collapse rapid scrolls into a
    /// single render of the final row.</summary>
    private const float HoverDebounceSeconds = 0.04f;
    /// <summary>Scale applied to each NCreatureVisuals so bestiary sprites match the
    /// apparent size they have in combat. The game's NCombatRoom.SceneContainer scales
    /// creature visuals up (see NMonsterDeathVfx bounds math), whereas Bounds.Size we
    /// read off the raw NCreatureVisuals is 1x. 1.40x calibrated against Seapunk in a
    /// real run — tweak if monsters still read too small or too large.</summary>
    private const float MonsterPreviewScale = 1.40f;

    /// <summary>
    /// Render the Spine visuals for every monster in the hovered encounter. If
    /// a live SubViewport for this encounter already exists in the LRU cache,
    /// reactivate it and swap the displayed TextureRect to its texture — the
    /// Spine animation state is preserved in the scene tree so it picks right
    /// up where it left off. Otherwise instantiate a fresh SubViewport, build
    /// the visuals, add to the cache, and evict the oldest entry if we're at
    /// capacity.
    /// </summary>
    private void RenderMonsterPreview(string encounterId)
    {
        ClearMonsterPreview();
        if (_renderArea == null) return;

        // Preview disabled entirely — no render path runs.
        if (!SlayTheStatsConfig.BestiaryPreviewEnabled) return;

        // GPU-friendly mode: static-sprite cache hit. Display the baked
        // ImageTexture directly, no SubViewport involved.
        if (SlayTheStatsConfig.BestiaryPreviewStatic &&
            _staticSpriteCache.TryGetValue(encounterId, out var bakedTex) &&
            bakedTex != null)
        {
            AttachStaticTextureRect(bakedTex);
            return;
        }

        // Live-animation mode: cache hit reuses the live SubViewport.
        if (!SlayTheStatsConfig.BestiaryPreviewStatic &&
            _liveViewports.TryGetValue(encounterId, out var cachedViewport) &&
            GodotObject.IsInstanceValid(cachedViewport))
        {
            TouchLru(encounterId);
            ActivateViewport(cachedViewport);
            AttachViewportTextureRect(cachedViewport);
            return;
        }

        if (!MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta)) return;
        if (meta.MonsterIds == null || meta.MonsterIds.Count == 0) return;

        // Resolve the MonsterModels. Unknown ids (modded monsters whose model isn't
        // loaded, malformed ids, etc.) are skipped silently — we still show whatever
        // we can resolve.
        var monsters = new List<MonsterModel>();
        foreach (var id in meta.MonsterIds)
        {
            MonsterModel? model = null;
            try
            {
                model = ModelDb.GetByIdOrNull<MonsterModel>(ModelId.Deserialize(id));
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"[SlayTheStats] RenderMonsterPreview: lookup failed for '{id}': {e.Message}");
            }
            if (model != null) monsters.Add(model);
        }
        if (monsters.Count == 0)
        {
            // Doormaker's encounter lists MONSTER.DOOR in the run file, but there
            // is no MonsterModel for DOOR (the Doormaker class transforms into it
            // at runtime). Similar cases may exist for future encounters. Fall
            // through to the static icon path instead of showing a blank pane.
            ShowStaticEncounterPreview(encounterId);
            return;
        }

        // Offscreen SubViewport — lives as a sibling under the submenu, not inside
        // _renderArea. SubViewport isn't a Control so PanelContainer wouldn't know
        // how to lay it out; instead we let the viewport render independently and
        // blit its texture into a TextureRect inside _renderArea.
        // Compute the canvas dims so the SubViewport aspect matches the render area
        // exactly → TextureRect with KeepAspectCentered produces no letterbox bars.
        // If the render area hasn't been laid out yet (Size == 0), fall back to the
        // default aspect. The canvas grows below if the scaled content overflows.
        float renderAspect = MonsterPreviewFallbackAspect;
        var raSize = ((Control)_renderArea).Size;
        if (raSize.X > 1f && raSize.Y > 1f) renderAspect = raSize.X / raSize.Y;
        int baselineH = MonsterPreviewBaseHeight;
        int baselineW = Mathf.CeilToInt(baselineH * renderAspect);

        var viewport = new SubViewport
        {
            Name = "MonsterPreviewViewport",
            // Solid (non-transparent) background. Transparent SubViewports break
            // particle effects and additive-blend sprites that some creatures rely
            // on (Queen's minion lantern glows, Ceremonial Beast sparkles) — the
            // particles end up drawing as black squares because their additive
            // contribution composites against an alpha=0 buffer. A solid background
            // colour matched to the render area's panel lets those effects
            // composite correctly; a full-size ColorRect child below renders the
            // same colour so the area reads as one continuous surface.
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            // Baseline canvas size matched to the render area's aspect — may grow
            // below if content overflows (grown in both dims to keep aspect).
            Size = new Vector2I(baselineW, baselineH),
        };
        ((Node)this).AddChild(viewport);
        _previewViewport = viewport;

        // Background fill inside the viewport — the first child so it renders at
        // the back and every creature / particle composites over it. Colour matches
        // _renderArea's StyleBoxFlat BgColor (0.08, 0.09, 0.12) so the TextureRect
        // that displays the viewport texture reads as one continuous surface with
        // the bestiary chrome.
        var bgFill = new ColorRect
        {
            Name = "PreviewBgFill",
            Color = new Color(0.08f, 0.09f, 0.12f, 1f),
            Size = new Vector2(viewport.Size.X, viewport.Size.Y),
            Position = Vector2.Zero,
        };
        viewport.AddChild(bgFill);

        // First pass: instantiate, wire up the Spine animator, set skin, start the
        // idle loop, and measure each monster's pixel-space bounds. Mirrors the
        // sequence from NBestiary.SelectMonster (GenerateAnimator → SetUpSkin →
        // SetAnimation("idle_loop")). Adding the NCreatureVisuals to the viewport
        // runs _Ready() synchronously so Bounds/SpineBody are valid immediately.
        //
        // We record a *Rect* per monster (not just a size), because the Rect's
        // origin-relative position is what lets us properly center off-axis
        // creatures (Kaiser Crab's Crusher/Rocket have their NCreatureVisuals
        // origin shifted far to one side — they live on the left/right of the
        // combat screen — so without honoring Bounds.Position we'd place them
        // outside the slot).
        var visualsList = new List<NCreatureVisuals>();
        var boundsRects = new List<Rect2>();   // bounds rect in viewport coords (scaled)
        float totalWidth = 0f;
        float maxHeight = 0f;
        int spineCount = 0;

        // Per-encounter counter of how many instances of each monster id we've
        // already processed. Used to alternate skin variants across repeated
        // monsters in the same encounter (e.g. 4x PhantasmalGardener with
        // tall/short/tall/short). Keyed on the ModelId string.
        var monsterInstanceIndex = new Dictionary<string, int>();

        foreach (var monster in monsters)
        {
            string monsterIdStr = monster.Id.ToString();
            if (!monsterInstanceIndex.TryGetValue(monsterIdStr, out int instanceIdx)) instanceIdx = 0;
            monsterInstanceIndex[monsterIdStr] = instanceIdx + 1;
            NCreatureVisuals visuals;
            try
            {
                visuals = monster.CreateVisuals();
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"[SlayTheStats] RenderMonsterPreview: CreateVisuals failed for {monster.Id}: {e.Message}");
                continue;
            }
            viewport.AddChild(visuals);

            if (visuals.HasSpineAnimation && visuals.SpineBody != null)
            {
                try
                {
                    monster.GenerateAnimator(visuals.SpineBody);
                }
                catch (Exception e)
                {
                    MainFile.Logger.Warn($"[SlayTheStats] RenderMonsterPreview: GenerateAnimator failed for {monster.Id}: {e.Message}");
                }

                // Skin: try the monster's own SetUpSkin first (the normal path). Many
                // monsters override SetupSkins and reference base.Creature.SlotName to
                // pick a per-slot variant (PhantasmalGardener, Vantom, TwoTailedRat,
                // etc.) — that throws here because we're not in combat and Creature
                // is null. Fall back to picking the first non-"default" skin so the
                // skeleton has *some* art bound and isn't invisible.
                bool skinSet = false;
                try
                {
                    visuals.SetUpSkin(monster);
                    skinSet = true;
                }
                catch
                {
                    // Expected for monsters whose SetupSkins override reads
                    // base.Creature.SlotName — fall back to a data-driven skin
                    // pick below.
                }
                if (!skinSet)
                {
                    TryApplyFallbackSkin(visuals, monsterIdStr, instanceIdx);
                }
                else
                {
                    // Even when SetUpSkin succeeds, some skeletons (SkulkingColony,
                    // etc.) end up with their "default" skin bound, which for skin-
                    // variant skeletons is empty and renders nothing. If the bound
                    // bounding rect is suspiciously tiny, try picking a non-default
                    // skin as a safety net.
                    EnsureNonEmptySkin(visuals, monsterIdStr);
                }

                // Try the standard idle_loop animation first; fall back to common
                // alternatives if it doesn't exist on this skeleton.
                TryPlayIdleAnimation(visuals, monsterIdStr);
                spineCount++;
            }
            // else: no Spine animation (e.g. Crusher/Rocket use
            // NKaiserCrabBossBackground for their body); if every monster in
            // the encounter is non-Spine, we'll fall back to a static icon
            // below.

            // Scale up to match in-combat apparent size. Layout math below uses the
            // scaled bounds so monsters don't overlap.
            ((Node2D)visuals).Scale = new Vector2(MonsterPreviewScale, MonsterPreviewScale);

            // Measure the sprite's extent in local (origin-relative) coords.
            Rect2 localBounds = MeasureLocalBounds(visuals);
            // Scale into viewport coords by the preview scale (we also apply this
            // scale to the Node2D below, so the measured rect has to reflect it).
            Rect2 scaledBounds = new Rect2(
                localBounds.Position * MonsterPreviewScale,
                localBounds.Size * MonsterPreviewScale);
            boundsRects.Add(scaledBounds);
            totalWidth += scaledBounds.Size.X;
            if (scaledBounds.Size.Y > maxHeight) maxHeight = scaledBounds.Size.Y;
            visualsList.Add(visuals);
        }

        // If none of the monsters resolved to a Spine-backed visual (e.g. Kaiser Crab
        // bosses Crusher/Rocket use NKaiserCrabBossBackground for their body; Doormaker
        // uses static placeholder images), free the useless viewport and fall back to
        // showing the boss icon as a static TextureRect — same art used in the row
        // list for boss encounters.
        if (spineCount == 0)
        {
            ((Node)viewport).QueueFree();
            _previewViewport = null;
            ShowStaticEncounterPreview(encounterId);
            return;
        }

        if (visualsList.Count == 0)
        {
            ((Node)viewport).QueueFree();
            _previewViewport = null;
            return;
        }

        // Compute the content span (sum of widths + gaps). Canvas starts at the
        // aspect-matched baseline and grows each dimension INDEPENDENTLY if
        // content overflows — coupling the growth (i.e. growing both dims to
        // preserve aspect) was shrinking tall sprites because the overflowing
        // vertical would also blow up the horizontal, cutting the render scale.
        // Independent growth reintroduces a bit of letterbox for off-aspect
        // encounters, but preserves sprite size which is what matters more.
        //
        // Vertical: feet sit at (vpH - floorMargin). Need canvas tall enough that
        // sprite top (feetY - maxHeight) >= pad. I.e. vpH >= maxHeight + pad + floorMargin.
        float contentWidth  = totalWidth + MonsterPreviewGap * Math.Max(0, visualsList.Count - 1);
        float neededW = contentWidth + MonsterPreviewPad * 2f;
        float neededH = maxHeight + MonsterPreviewPad + MonsterPreviewFloorMargin;
        int vpW = Math.Max(baselineW, Mathf.CeilToInt(neededW));
        int vpH = Math.Max(baselineH, Mathf.CeilToInt(neededH));
        if (vpW != baselineW || vpH != baselineH)
        {
            viewport.Size = new Vector2I(vpW, vpH);
            bgFill.Size = new Vector2(vpW, vpH);
        }

        // Second pass: position each NCreatureVisuals using its bounds RECT (not
        // just size), so monsters whose origin isn't at the visible centre of
        // their sprite (Kaiser Crab: Crusher's origin is far to the left of its
        // rendered body; Rocket's origin is far to the right) land with their
        // visible content in the correct slot. Vantom's tall tail-above-head is
        // also handled because the bounds rect captures the full vertical extent.
        //
        // For each slot we pick:
        //   target_visible_center_x = slot center
        //   target_visible_bottom_y = feetY (so the bottom of the sprite lands at
        //                                    the "ground line")
        //
        // visuals.Position + scaledBounds.Position is the top-left of the rendered
        // rect in viewport space, and visuals.Position + scaledBounds.End is the
        // bottom-right. Solving for visuals.Position:
        //   visuals.Position.x = slot_center_x - (scaledBounds.Position.x + scaledBounds.Size.x/2)
        //   visuals.Position.y = feetY         - (scaledBounds.Position.y + scaledBounds.Size.y)
        float startX = vpW * 0.5f - contentWidth * 0.5f;
        float cursor = startX;
        float feetY  = vpH - MonsterPreviewFloorMargin;
        for (int i = 0; i < visualsList.Count; i++)
        {
            var v = visualsList[i];
            var sb = boundsRects[i];
            float slotCenterX = cursor + sb.Size.X * 0.5f;
            float posX = slotCenterX - (sb.Position.X + sb.Size.X * 0.5f);
            float posY = feetY       - (sb.Position.Y + sb.Size.Y);
            ((Node2D)v).Position = new Vector2(posX, posY);
            cursor += sb.Size.X + MonsterPreviewGap;
        }

        if (SlayTheStatsConfig.BestiaryPreviewStatic)
        {
            // GPU-friendly path: show the live viewport temporarily, then a
            // few frames later capture its texture into an ImageTexture,
            // swap the displayed rect to the static capture, and free the
            // SubViewport. Subsequent hovers hit the static cache.
            ActivateViewport(viewport);
            AttachViewportTextureRect(viewport);
            _ = CaptureStaticSpriteAsync(viewport, encounterId);
        }
        else
        {
            // Live-animation path: hand the viewport to the LRU cache
            // (evicts the oldest entry if at capacity), mark it active, and
            // display its live texture.
            AddToLruCache(encounterId, viewport);
            ActivateViewport(viewport);
            AttachViewportTextureRect(viewport);
        }
    }

    /// <summary>
    /// GPU-friendly mode helper: wait a few process frames for Spine to
    /// initialize and the SubViewport to draw at least once, then capture
    /// its texture into an <see cref="ImageTexture"/>, store it in
    /// <see cref="_staticSpriteCache"/> under the encounter id, swap the
    /// displayed TextureRect to the static capture, and free the viewport.
    /// Bails out (freeing the viewport anyway) if the user has already
    /// moved to a different encounter by the time the capture is ready.
    /// </summary>
    private async Task CaptureStaticSpriteAsync(SubViewport viewport, string encounterId)
    {
        try
        {
            // Wait 4 process frames — same number NSettingsScreen uses
            // before its viewport-texture capture. Three is the documented
            // minimum for Spine initialization; four gives a safety margin.
            for (int i = 0; i < 4; i++)
            {
                await ((GodotObject)this).ToSignal(((Node)this).GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (!GodotObject.IsInstanceValid(viewport))
                return;

            var vpTex = viewport.GetTexture();
            var image = vpTex?.GetImage();
            if (image == null || image.IsEmpty())
            {
                if (GodotObject.IsInstanceValid(viewport))
                    ((Node)viewport).QueueFree();
                if (_previewViewport == viewport) _previewViewport = null;
                return;
            }
            var baked = ImageTexture.CreateFromImage(image);
            _staticSpriteCache[encounterId] = baked;

            // If the user is still hovering this encounter, swap the
            // TextureRect to the static capture so the visible preview
            // stops animating. Otherwise the user has moved on and
            // ClearMonsterPreview has already removed our TextureRect.
            bool stillHovered = _hoveredEncounterId == encounterId;
            if (stillHovered && _renderArea != null)
            {
                var oldRect = ((Node)_renderArea).GetNodeOrNull("MonsterPreviewTex");
                if (oldRect != null)
                {
                    ((Node)_renderArea).RemoveChild(oldRect);
                    ((Node)oldRect).QueueFree();
                }
                AttachStaticTextureRect(baked);
            }

            // Free the viewport regardless — static mode never keeps live
            // viewports around.
            if (GodotObject.IsInstanceValid(viewport))
                ((Node)viewport).QueueFree();
            if (_previewViewport == viewport) _previewViewport = null;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] CaptureStaticSpriteAsync failed for {encounterId}: {e.Message}");
        }
    }

    /// <summary>
    /// Add a <see cref="TextureRect"/> to the render area displaying a baked
    /// static ImageTexture. GPU-friendly mode's equivalent of
    /// <see cref="AttachViewportTextureRect"/>.
    /// </summary>
    private void AttachStaticTextureRect(Texture2D baked)
    {
        if (_renderArea == null) return;
        var rect = new TextureRect
        {
            Name = "MonsterPreviewTex",
            Texture = baked,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        ((Node)_renderArea).AddChild(rect);
    }

    // ─────────────────────────── LRU viewport cache ───────────────────────────

    /// <summary>
    /// Attach a TextureRect to <see cref="_renderArea"/> that displays the
    /// given SubViewport's live texture. Used on both cache-miss (fresh
    /// build) and cache-hit (reactivating a cached viewport) paths.
    /// </summary>
    private void AttachViewportTextureRect(SubViewport viewport)
    {
        if (_renderArea == null) return;
        var tex = new TextureRect
        {
            Name = "MonsterPreviewTex",
            Texture = viewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        ((Node)_renderArea).AddChild(tex);
    }

    /// <summary>
    /// Flip the given SubViewport to <c>Always</c> render mode (active) and
    /// track it in <see cref="_previewViewport"/>. Only one viewport at a
    /// time should be in Always mode — everyone else stays Disabled so the
    /// GPU only pays for a single Spine render per frame.
    /// </summary>
    private void ActivateViewport(SubViewport viewport)
    {
        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _previewViewport = viewport;
    }

    /// <summary>
    /// Insert a newly-built viewport into the LRU cache. If the cache is at
    /// capacity, evict the oldest entry (farthest from head) and free its
    /// SubViewport. Idempotent — re-adding an existing id just bumps it to
    /// the head.
    /// </summary>
    private void AddToLruCache(string encounterId, SubViewport viewport)
    {
        // If somehow already present, remove old entry first (should not
        // happen because the hit path handles cached entries before us).
        if (_liveViewports.TryGetValue(encounterId, out var existing))
        {
            RemoveFromLru(encounterId);
            if (GodotObject.IsInstanceValid(existing))
                ((Node)existing).QueueFree();
        }

        _liveViewports[encounterId] = viewport;
        var node = _lruOrder.AddFirst(encounterId);
        _lruNodes[encounterId] = node;

        // Evict from tail if over capacity.
        while (_liveViewports.Count > MaxLiveViewports && _lruOrder.Count > 0)
        {
            var evictId = _lruOrder.Last!.Value;
            var evictVp = _liveViewports[evictId];
            RemoveFromLru(evictId);
            if (GodotObject.IsInstanceValid(evictVp))
            {
                ((Node)evictVp).QueueFree();
            }
        }
    }

    /// <summary>Move an existing cache entry to the head of the LRU order (marks
    /// it as most-recently-used).</summary>
    private void TouchLru(string encounterId)
    {
        if (!_lruNodes.TryGetValue(encounterId, out var node)) return;
        _lruOrder.Remove(node);
        _lruOrder.AddFirst(node);
    }

    /// <summary>Remove an encounter from all three LRU bookkeeping structures
    /// (dict + linked list + node map). Does NOT free the viewport — the
    /// caller is responsible for deciding whether to.</summary>
    private void RemoveFromLru(string encounterId)
    {
        _liveViewports.Remove(encounterId);
        if (_lruNodes.TryGetValue(encounterId, out var node))
        {
            _lruOrder.Remove(node);
            _lruNodes.Remove(encounterId);
        }
    }

    /// <summary>
    /// Fallback preview for encounters whose monsters don't have standalone Spine
    /// visuals (e.g. Kaiser Crab: Crusher/Rocket render via NKaiserCrabBossBackground;
    /// Doormaker: swaps between static placeholder images per turn). Shows the
    /// pre-rendered boss icon from res://images/ui/run_history/ — the same art used
    /// on the encounter row in the list.
    /// </summary>
    private void ShowStaticEncounterPreview(string encounterId)
    {
        if (_renderArea == null) return;
        var attempted = new List<string>();
        Texture2D? tex = null;

        // 1) Try the run_history boss icon first (e.g. doormaker_boss.png / kaiser_crab_boss.png).
        //    This is the art used in the encounter row on the left list.
        string entryFromEncounter = encounterId;
        int dotEnc = entryFromEncounter.IndexOf('.');
        if (dotEnc >= 0) entryFromEncounter = entryFromEncounter[(dotEnc + 1)..];
        entryFromEncounter = entryFromEncounter.ToLowerInvariant();

        string[] encounterPaths =
        {
            $"res://images/ui/run_history/{entryFromEncounter}.png",
            // Some IDs end in "_boss" but the icon file drops it (or vice-versa).
            // Try both directions.
            entryFromEncounter.EndsWith("_boss")
                ? $"res://images/ui/run_history/{entryFromEncounter[..^5]}.png"
                : $"res://images/ui/run_history/{entryFromEncounter}_boss.png",
        };
        foreach (var p in encounterPaths)
        {
            attempted.Add(p);
            tex = EncounterIcons.LoadTextureExternal(p);
            if (tex != null) break;
        }

        // 2) Fall back to per-monster icons — try each monster in the encounter,
        //    looking under a handful of plausible paths.
        if (tex == null && MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta) && meta.MonsterIds != null)
        {
            foreach (var mid in meta.MonsterIds)
            {
                string entry = mid;
                int dot = entry.IndexOf('.');
                if (dot >= 0) entry = entry[(dot + 1)..];
                entry = entry.ToLowerInvariant();
                string[] monsterPaths =
                {
                    $"res://images/monsters/{entry}.png",
                    $"res://images/ui/run_history/{entry}.png",
                    $"res://images/ui/bestiary/{entry}.png",
                };
                foreach (var p in monsterPaths)
                {
                    attempted.Add(p);
                    tex = EncounterIcons.LoadTextureExternal(p);
                    if (tex != null) break;
                }
                if (tex != null) break;
            }
        }

        if (tex == null)
        {
            // No asset found for this encounter — log once so we know which
            // encounter needs an icon file checked in.
            MainFile.Logger.Warn($"[SlayTheStats] ShowStaticEncounterPreview: no static texture for {encounterId}. Tried: {string.Join(", ", attempted)}");
            return;
        }
        var rect = new TextureRect
        {
            Name = "MonsterPreviewTex",
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        ((Node)_renderArea).AddChild(rect);
    }

    /// <summary>
    /// Monster-specific skin patterns for encounters that normally pick a skin
    /// per combat slot (SetupSkins reads <c>base.Creature.SlotName</c>, which is
    /// null when we instantiate outside combat). For these monsters we replicate
    /// the game's per-slot selection using the visual's ordinal index within the
    /// same encounter. PhantasmalGardener: slots first/third → tall, slots
    /// second/fourth → short (matches the logic in PhantasmalGardener.SetupSkins).
    /// </summary>
    private static readonly Dictionary<string, string[]> SlotSkinPatterns = new()
    {
        { "MONSTER.PHANTASMAL_GARDENER", new[] { "tall", "short", "tall", "short" } },
    };

    /// <summary>
    /// Bind a skin from the skeleton's data to the visuals. Used when a monster's
    /// own SetupSkins override throws (most commonly because it references
    /// <c>base.Creature.SlotName</c>, which is null outside combat). If the
    /// monster has a known per-slot skin pattern (see SlotSkinPatterns), pick the
    /// skin for the given instance index; otherwise fall back to the first
    /// non-"default" skin in the skeleton data.
    /// </summary>
    private static void TryApplyFallbackSkin(NCreatureVisuals visuals, string idForLog, int instanceIdx)
    {
        try
        {
            var skeleton = visuals.SpineBody?.GetSkeleton();
            if (skeleton == null) return;

            // Per-monster pattern first, if one is registered for this id.
            string? pickedName = null;
            if (SlotSkinPatterns.TryGetValue(idForLog, out var pattern) && pattern.Length > 0)
            {
                var candidate = pattern[instanceIdx % pattern.Length];
                // Only use it if the skin actually exists on the skeleton.
                var data = skeleton.GetData();
                if (data.FindSkin(candidate) != null)
                    pickedName = candidate;
            }

            // Generic fallback: first non-"default" skin in the data resource.
            pickedName ??= PickSkinName(skeleton);

            if (pickedName == null) return;
            skeleton.SetSkinByName(pickedName);
            skeleton.SetSlotsToSetupPose();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] TryApplyFallbackSkin: {idForLog} failed: {e.Message}");
        }
    }

    /// <summary>
    /// If the skeleton's current bound skin looks empty (zero-area bounds), attempt
    /// to switch to a non-"default" skin. Safety net for vanilla monsters whose
    /// default skin has no art bound (SkulkingColony etc.).
    /// </summary>
    private static void EnsureNonEmptySkin(NCreatureVisuals visuals, string idForLog)
    {
        try
        {
            var skeleton = visuals.SpineBody?.GetSkeleton();
            if (skeleton == null) return;
            var bounds = skeleton.GetBounds();
            if (bounds.Size.X > 4f && bounds.Size.Y > 4f) return; // skin already has content
            var pickedName = PickSkinName(skeleton);
            if (pickedName == null) return;
            skeleton.SetSkinByName(pickedName);
            skeleton.SetSlotsToSetupPose();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] EnsureNonEmptySkin: {idForLog} failed: {e.Message}");
        }
    }

    /// <summary>
    /// Return the sprite's local-space bounding rect (origin-relative), in the
    /// same coordinate system used for positioning the NCreatureVisuals inside
    /// the viewport. We prefer <c>visuals.Bounds</c> (the UI %Bounds Control) —
    /// that's in post-body-scale pixel coords and matches the size used by the
    /// game's own combat layout (NCombatRoom references visuals.Bounds.Size.X
    /// throughout its creature placement math). The Spine skeleton's own
    /// GetBounds() is in pre-body-scale units and would need to be multiplied by
    /// <c>visuals.GetCurrentBody().Scale</c> to be comparable — too risky as a
    /// silent default. Skeleton bounds are only used if %Bounds is missing.
    /// </summary>
    private static Rect2 MeasureLocalBounds(NCreatureVisuals visuals)
    {
        if (visuals.Bounds != null)
        {
            var uiSize = visuals.Bounds.Size;
            if (uiSize.X > 4f && uiSize.Y > 4f)
                return new Rect2(visuals.Bounds.Position, uiSize);
        }

        // Fallback: scale the spine bounds by body.Scale so they come out in the
        // same pixel-space coords that NCreatureVisuals.Bounds uses.
        try
        {
            var skeleton = visuals.SpineBody?.GetSkeleton();
            if (skeleton != null)
            {
                var sb = skeleton.GetBounds();
                Vector2 bodyScale = new Vector2(1f, 1f);
                var body = visuals.GetCurrentBody();
                if (body != null) bodyScale = ((Node2D)body).Scale;
                return new Rect2(sb.Position * bodyScale, sb.Size * bodyScale);
            }
        }
        catch { }

        return new Rect2(-80f, -160f, 160f, 160f);
    }

    /// <summary>
    /// Start playing an idle animation on the visuals. Tries "idle_loop" first
    /// (the canonical name the game's own NBestiary uses), then a few common
    /// fallbacks. Some monsters use non-standard idle names or only have a
    /// default static pose.
    /// </summary>
    private static void TryPlayIdleAnimation(NCreatureVisuals visuals, string idForLog)
    {
        string[] candidates = { "idle_loop", "idle", "idle_1", "default" };
        foreach (var name in candidates)
        {
            try
            {
                var data = visuals.SpineBody?.GetSkeleton()?.GetData();
                if (data == null) return;
                if (data.FindAnimation(name) == null) continue;
                visuals.SpineAnimation.SetAnimation(name);
                return;
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"[SlayTheStats] TryPlayIdleAnimation: {idForLog} '{name}' failed: {e.Message}");
            }
        }
        // No idle animation found — leave the skeleton in its default pose.
    }

    /// <summary>
    /// Iterate the skeleton's skin list and return the first non-"default" skin
    /// name. Falls back to the first skin of any name if every skin is named
    /// "default". Returns null if the data resource has no skins at all.
    /// </summary>
    private static string? PickSkinName(MegaSkeleton skeleton)
    {
        var data = skeleton.GetData();
        Godot.Collections.Array<GodotObject> skins;
        try { skins = data.GetSkins(); }
        catch { return null; }
        string? firstAny = null;
        foreach (var skinObj in skins)
        {
            if (skinObj == null) continue;
            string? name = null;
            // Spine bindings expose the skin name via get_skin_name; fall back to
            // get_name if that's not the method on this binding version.
            try { name = skinObj.Call("get_skin_name").AsString(); } catch { }
            if (string.IsNullOrEmpty(name))
            {
                try { name = skinObj.Call("get_name").AsString(); } catch { }
            }
            if (string.IsNullOrEmpty(name)) continue;
            firstAny ??= name;
            if (name != "default") return name;
        }
        return firstAny;
    }

    /// <summary>
    /// Tear down the currently-displayed monster preview: removes the
    /// TextureRect from <see cref="_renderArea"/> and flips the active
    /// SubViewport to Disabled so it stops rendering (but stays alive in the
    /// LRU cache). Also clears any leftover placeholder label from older code
    /// paths. Does NOT free cached viewports — only <see cref="AddToLruCache"/>
    /// and the cache-clearing helpers touch the LRU.
    /// </summary>
    private void ClearMonsterPreview()
    {
        if (_renderArea != null)
        {
            foreach (var name in new[] { "MonsterPreviewLabel", "MonsterPreviewTex" })
            {
                var node = ((Node)_renderArea).GetNodeOrNull(name);
                if (node != null)
                {
                    ((Node)_renderArea).RemoveChild(node);
                    ((Node)node).QueueFree();
                }
            }
        }
        if (_previewViewport != null && GodotObject.IsInstanceValid(_previewViewport))
        {
            // Cached viewport — stop rendering to save GPU, but keep it alive
            // so re-hovering the same encounter is instant and resumes
            // animation from where it left off.
            _previewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }
        _previewViewport = null;
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
    MedianDamage,
    Spread,
    Turns,
}

internal static class EncounterSorting
{
    public static readonly EncounterSortMode[] AllModes =
    {
        EncounterSortMode.Name,
        EncounterSortMode.Seen,
        EncounterSortMode.MedianDamage,
        EncounterSortMode.Spread,
        EncounterSortMode.Turns,
        EncounterSortMode.DeathRate,
    };

    public static string Label(EncounterSortMode mode) => mode switch
    {
        EncounterSortMode.Name         => "Name",
        EncounterSortMode.Seen         => "Runs",
        EncounterSortMode.DeathRate    => "Death%",
        EncounterSortMode.MedianDamage => "Dmg",
        EncounterSortMode.Spread       => "Spread",
        EncounterSortMode.Turns        => "Turns",
        _ => mode.ToString(),
    };

    /// <summary>
    /// Computes a single sortable score for an encounter under the given filter. If a sort
    /// character is provided, the score is restricted to that character; otherwise it sums
    /// across all characters that pass the filter. Returns null if there's no data.
    ///
    /// When <paramref name="bySignificance"/> is true, stat-based modes return a z-score
    /// (<c>(observed − baseline) / standard_error</c>) instead of the raw rate/average —
    /// well-sampled encounters with meaningful deviation from the category baseline
    /// surface above low-N noise. Name/Seen ignore the flag because the concept doesn't
    /// apply (Name is alphabetic; Seen already IS the sample size).
    /// </summary>
    public static double? Score(string encounterId, EncounterSortMode mode, AggregationFilter filter, string? sortCharacter, bool bySignificance = false, string? biome = null)
    {
        if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap)) return null;

        var perChar = StatsAggregator.AggregateEncountersByCharacter(contextMap, filter);
        if (perChar.Count == 0) return null;

        // Per-character branch uses the raw EncounterEvent directly. The all-chars
        // branch uses character-weighted aggregation (AggregateMetricsFromEvents) to
        // match the display path's all-chars baseline row. Earlier versions pooled
        // raw DamageValues across characters via AddRange, which inflated IQRC
        // dramatically when characters had different damage distributions (e.g.
        // Ironclad 5-15 + Defect 30-80 → pooled IQR 5-80, IQRC ~5.5).
        long fought;
        double raw;
        if (sortCharacter != null)
        {
            if (!perChar.TryGetValue(sortCharacter, out var stat) || stat.Fought == 0) return null;
            fought = stat.Fought;
            raw = mode switch
            {
                EncounterSortMode.Seen         => fought,
                EncounterSortMode.DeathRate    => (double)stat.Died / fought,
                EncounterSortMode.MedianDamage => stat.DamageMedian() ?? (double)stat.DamageTakenSum / fought,
                EncounterSortMode.Spread       => ComputeIqrc(stat),
                EncounterSortMode.Turns        => (double)stat.TurnsTakenSum / fought,
                _ => double.NaN,
            };
        }
        else
        {
            var agg = StatsAggregator.AggregateMetricsFromEvents(perChar.Values);
            if (agg.Fought == 0) return null;
            fought = agg.Fought;
            raw = mode switch
            {
                EncounterSortMode.Seen         => fought,
                EncounterSortMode.DeathRate    => agg.DeathRate / 100.0,
                EncounterSortMode.MedianDamage => agg.Median,
                EncounterSortMode.Spread       => agg.Iqrc,
                EncounterSortMode.Turns        => agg.AvgTurns,
                _ => double.NaN,
            };
            MainFile.Logger.Info($"[SlayTheStats] Score(all-chars) enc={encounterId} mode={mode} raw={raw:F3} fought={fought} chars={perChar.Count}");
        }
        if (double.IsNaN(raw)) return null;

        if (!bySignificance || mode == EncounterSortMode.Seen)
            return raw;

        // Significance: z-score against the encounter-weighted category baseline,
        // weighted by sample size. Uses AggregateEncounterPoolWeighted so the
        // baseline matches the display code's median-of-medians / mean-of-means
        // aggregation (each encounter contributes equally, not fight-weighted).
        string? category = null;
        if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
            category = meta.Category;

        var pool = StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, filter, category, biome);

        switch (mode)
        {
            case EncounterSortMode.DeathRate:
            {
                double p0 = pool.DeathRate / 100.0; // PoolMetrics stores 0–100, raw is 0–1
                double se = Math.Sqrt(Math.Max(1e-9, p0 * (1 - p0) / fought));
                return (raw - p0) / se;
            }
            case EncounterSortMode.MedianDamage:
            {
                double mu0 = pool.Median;
                double se = Math.Sqrt(Math.Max(1e-9, Math.Max(raw, mu0) / fought));
                return (raw - mu0) / se;
            }
            case EncounterSortMode.Spread:
            {
                double iqrcBase = pool.Iqrc;
                double se = Math.Sqrt(Math.Max(1e-9, Math.Max(raw, iqrcBase) / fought));
                return (raw - iqrcBase) / se;
            }
            case EncounterSortMode.Turns:
            {
                double mu0 = pool.AvgTurns;
                double se = Math.Sqrt(Math.Max(1e-9, Math.Max(raw, mu0) / fought));
                return (raw - mu0) / se;
            }
        }
        return raw;
    }

    /// <summary>IQRC (IQR coefficient) = (p75 - p25) / max(median, 1). Returns 0
    /// when there's no valid IQR. Matches the display-side FormatSpreadCell logic
    /// so sort order aligns with what the Spread column shows.</summary>
    private static double ComputeIqrc(EncounterEvent stat)
    {
        var iqr = stat.DamageIQR();
        if (!iqr.HasValue) return 0;
        var median = stat.DamageMedian();
        if (!median.HasValue) return 0;
        return (iqr.Value.p75 - iqr.Value.p25) / Math.Max(median.Value, 1.0);
    }

    /// <summary>
    /// Formats the per-row stat displayed alongside an encounter name in the bestiary list.
    /// </summary>
    public static string FormatScore(string encounterId, EncounterSortMode mode, AggregationFilter filter, string? sortCharacter, string? biome = null)
    {
        var score = Score(encounterId, mode, filter, sortCharacter, biome: biome);
        if (score == null) return "—";
        return mode switch
        {
            EncounterSortMode.Seen         => ((long)score.Value).ToString(),
            EncounterSortMode.DeathRate    => $"{score.Value * 100:0}%",
            EncounterSortMode.MedianDamage => $"{score.Value:0}",
            EncounterSortMode.Spread       => $"{score.Value * 100:0}%",
            EncounterSortMode.Turns        => $"{score.Value:F1}",
            _ => "—",
        };
    }

    /// <summary>
    /// Formats the per-row stat with significance coloration matching the focused-view
    /// data cells: high = bad (orange/red), low = good (teal/green), against the
    /// act-pool baseline for the encounter's category. Falls back to neutral grey for
    /// Name/Seen modes (no good/bad direction) and when there's no data. Returns BBCode.
    /// </summary>
    public static string FormatScoreColored(string encounterId, EncounterSortMode mode, AggregationFilter filter, string? sortCharacter, string? biome = null)
    {
        var raw = FormatScore(encounterId, mode, filter, sortCharacter, biome: biome);
        if (raw == "—" || mode == EncounterSortMode.Name || mode == EncounterSortMode.Seen)
            return raw;

        var score = Score(encounterId, mode, filter, sortCharacter, biome: biome);
        if (score == null) return raw;

        // Need the encounter's fought count for the n weighting in significance.
        if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap)) return raw;
        var perChar = StatsAggregator.AggregateEncountersByCharacter(contextMap, filter);
        int fought = 0;
        if (sortCharacter != null)
        {
            if (perChar.TryGetValue(sortCharacter, out var s)) fought = s.Fought;
        }
        else
        {
            foreach (var s in perChar.Values) fought += s.Fought;
        }
        if (fought == 0) return raw;

        string? category = null;
        if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
            category = meta.Category;

        // Pick the matching baseline per mode using encounter-weighted pool metrics
        // so coloration matches the display code's aggregation approach.
        var pool = StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, filter, category, biome);
        double value, baseline;
        bool useLog = false;
        switch (mode)
        {
            case EncounterSortMode.MedianDamage:
                value = score.Value;
                baseline = pool.Median;
                break;
            case EncounterSortMode.DeathRate:
                // Score returns died/fought (fraction); pool.DeathRate is 0–100.
                value = score.Value * 100.0;
                baseline = pool.DeathRate;
                break;
            case EncounterSortMode.Spread:
                // Score already returns IQRC (ratio); pool.Iqrc is the baseline IQRC.
                // Use log-space coloration so 4× and 1/4× color symmetrically, matching
                // the Spread column's FormatSpreadCell in the stats table.
                value = score.Value;
                baseline = pool.Iqrc;
                useLog = true;
                break;
            case EncounterSortMode.Turns:
                value = score.Value;
                baseline = pool.AvgTurns;
                break;
            default:
                return raw;
        }
        if (baseline <= 0) return raw;

        double pct;
        if (useLog)
        {
            // Same formula as EncounterTooltipHelper.ColBadLog with scale=20.
            double ratio = Math.Max(value, 1e-6) / baseline;
            pct = 100.0 + Math.Log(ratio) * 20.0;
        }
        else
        {
            pct = value / baseline * 100.0;
        }
        // ColBad direction (high = bad): swap the deviation around the baseline before
        // handing to ColWR (which uses high = good).
        return TooltipHelper.ColWR(raw, 100.0 + (100.0 - pct), fought, 100.0);
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
        "weak"    => "#6fdb8f",  // saturated green
        "normal"  => "#3da4f0",  // saturated sky blue — differentiates from megacrit golden used for headers/buttons
        "elite"   => "#e84ad5",  // saturated fuchsia
        "boss"    => "#fc6e58",  // saturated warm red
        "event"   => "#ffde3e",  // saturated yellow
        _         => "#c8c8c8",
    };

    public static Color CategoryColor(string category) => category switch
    {
        "weak"    => new Color(0.435f, 0.859f, 0.561f, 1f),
        "normal"  => new Color(0.239f, 0.643f, 0.941f, 1f),  // saturated sky blue
        "elite"   => new Color(0.910f, 0.290f, 0.835f, 1f),
        "boss"    => new Color(0.988f, 0.431f, 0.345f, 1f),
        "event"   => new Color(1.000f, 0.871f, 0.243f, 1f),
        _         => new Color(0.784f, 0.784f, 0.784f, 1f),
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
        var outline = LoadCategoryOutlineTexture(category);
        // Elite uses the base texture's pre-baked coloration (the game's run history
        // leaves secondary details — eyes, mount — at a paler tone, which a uniform
        // modulate would kill). All other categories still get their category tint
        // so weak / normal / event / boss are visually distinguishable.
        var tint = category == "elite" ? Colors.White : CategoryColor(category);
        return BuildHoverHandle(
            tex,
            sizePx,
            tint,
            wrapperHeightOverride: CategoryIconRowHeightPx,
            wrapperWidthOverride:  CategoryIconColumnWidthPx,
            outlineTex: outline);
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
            return BuildHoverHandle(perBoss, sizePx, Colors.White, rowHeightOverridePx,
                outlineTex: LoadBossOutlineTexture(encounterId));
        var fallback = LoadCategoryTexture("boss");
        if (fallback == null) return null;
        return BuildHoverHandle(fallback, sizePx, CategoryColor("boss"), rowHeightOverridePx,
            outlineTex: LoadCategoryOutlineTexture("boss"));
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
        Texture2D? outlineTex;
        if (perBoss != null)
        {
            tex = perBoss;
            modulate = Colors.White;
            outlineTex = LoadBossOutlineTexture(encounterId);
        }
        else
        {
            tex = LoadCategoryTexture("boss");
            if (tex == null) return null;
            modulate = CategoryColor("boss");
            outlineTex = LoadCategoryOutlineTexture("boss");
        }

        int padding = 4;
        int outerW = wrapperWidthOverride  ?? (sizePx + padding * 2);
        int outerH = rowHeightOverridePx   ?? (sizePx + padding * 2);

        var wrapper = new Control();
        wrapper.CustomMinimumSize = new Vector2(outerW, outerH);
        wrapper.MouseFilter = Control.MouseFilterEnum.Ignore;

        if (outlineTex != null)
        {
            // Shrink factor matches BuildHoverHandle so outline thickness is uniform across icons.
            const float OutlineShrink = 0.94f;
            float outlineSize = sizePx * OutlineShrink;
            var outline = new TextureRect();
            outline.Texture       = outlineTex;
            outline.AnchorLeft    = 0.5f; outline.AnchorRight = 0.5f;
            outline.AnchorTop     = 0.5f; outline.AnchorBottom = 0.5f;
            outline.OffsetLeft    = -outlineSize / 2f;
            outline.OffsetRight   =  outlineSize / 2f;
            outline.OffsetTop     = -outlineSize / 2f;
            outline.OffsetBottom  =  outlineSize / 2f;
            outline.StretchMode   = TextureRect.StretchModeEnum.KeepAspectCentered;
            outline.ExpandMode    = TextureRect.ExpandModeEnum.IgnoreSize;
            outline.MouseFilter   = Control.MouseFilterEnum.Ignore;
            ((CanvasItem)outline).Modulate = Colors.Black;
            outline.PivotOffset   = new Vector2(outlineSize / 2f, outlineSize / 2f);
            wrapper.AddChild(outline);
        }

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
        int? wrapperWidthOverride  = null,
        Texture2D? outlineTex = null)
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

        // Black outline (matches the base game's run history). Rendered *below* the
        // halo highlight so the glow occludes it on hover, and sized slightly smaller
        // than the icon so the visible black ring peeks thinner around the silhouette.
        // Source PNG has the icon silhouette pre-dilated, so shrinking the display
        // rect pulls the edge inward — OutlineShrink tuned to ~6% gives a 1-2px ring
        // at the normal icon size.
        const float OutlineShrink = 0.94f;
        if (outlineTex != null)
        {
            float outlineSize = sizePx * OutlineShrink;
            var outline = new TextureRect();
            outline.Texture     = outlineTex;
            outline.AnchorLeft  = 0.5f; outline.AnchorRight  = 0.5f;
            outline.AnchorTop   = 0.5f; outline.AnchorBottom = 0.5f;
            outline.OffsetLeft  = -outlineSize / 2f;
            outline.OffsetRight =  outlineSize / 2f;
            outline.OffsetTop   = -outlineSize / 2f;
            outline.OffsetBottom=  outlineSize / 2f;
            outline.PivotOffset = new Vector2(outlineSize / 2f, outlineSize / 2f);
            outline.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            outline.ExpandMode  = TextureRect.ExpandModeEnum.IgnoreSize;
            outline.MouseFilter = Control.MouseFilterEnum.Ignore;
            ((CanvasItem)outline).Modulate = Colors.Black;
            wrapper.AddChild(outline);
        }

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

    private static string CategorySlug(string category) => category switch
    {
        "weak"   => "monster",
        "normal" => "monster",
        "elite"  => "elite",
        "boss"   => "elite",
        "event"  => "event",
        _        => "unknown_monster",
    };

    private static Texture2D? LoadCategoryTexture(string category)
    {
        return LoadTexture($"res://images/ui/run_history/{CategorySlug(category)}.png");
    }

    /// <summary>Black outline texture that juxtaposes the main icon against any
    /// background — same asset the base game's run history uses via
    /// `ImageHelper.GetRoomIconOutlinePath`. Returns null if the file is missing.</summary>
    private static Texture2D? LoadCategoryOutlineTexture(string category)
    {
        return LoadTexture($"res://images/ui/run_history/{CategorySlug(category)}_outline.png");
    }

    internal static Texture2D? LoadBossTexture(string encounterId)
    {
        // Strip ENCOUNTER. prefix; the rest lowercased should match the per-boss filename.
        // E.g. "ENCOUNTER.LAGAVULIN_MATRIARCH_BOSS" -> "lagavulin_matriarch_boss.png"
        var entry = encounterId.StartsWith("ENCOUNTER.")
            ? encounterId["ENCOUNTER.".Length..]
            : encounterId;
        var slug = entry.ToLowerInvariant();
        return LoadTexture($"res://images/ui/run_history/{slug}.png");
    }

    internal static Texture2D? LoadBossOutlineTexture(string encounterId)
    {
        var entry = encounterId.StartsWith("ENCOUNTER.")
            ? encounterId["ENCOUNTER.".Length..]
            : encounterId;
        var slug = entry.ToLowerInvariant();
        return LoadTexture($"res://images/ui/run_history/{slug}_outline.png");
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

    /// <summary>Public wrapper for other classes (e.g. NBestiaryStatsSubmenu's static
    /// preview fallback) to load textures through the same cached loader.</summary>
    internal static Texture2D? LoadTextureExternal(string path) => LoadTexture(path);

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
