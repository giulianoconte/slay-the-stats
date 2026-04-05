using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace SlayTheStats;

/// <summary>
/// Injects a "Slay the Stats" button into the Card Library sidebar and creates
/// a floating filter/aggregation pane that opens when clicked.
/// </summary>
[HarmonyPatch(typeof(NCardLibrary), "_Ready")]
public static class CompendiumFilterPatch
{
    private static PanelContainer? _filterPane;

    static void Postfix(NCardLibrary __instance)
    {
        try
        {
            InjectButton(__instance);
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] CompendiumFilterPatch failed: {e.Message}");
        }
    }

    private static void InjectButton(NCardLibrary library)
    {
        // Find the MultiplayerCards tickbox and its parent container (the sidebar VBox).
        var multiplayerCards = library.FindChild("MultiplayerCards", true, false);
        if (multiplayerCards == null)
        {
            MainFile.Logger.Warn("[SlayTheStats] Could not find MultiplayerCards node in card library");
            return;
        }
        var sidebar = multiplayerCards.GetParent();
        if (sidebar == null) return;

        // Create the sidebar button.
        var button = new Button();
        button.Text = "Slay the Stats";
        button.Name = "SlayTheStatsFilterButton";
        button.CustomMinimumSize = new Vector2(0, 32);
        // Match the game's muted sidebar style.
        button.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f, 1f));
        button.AddThemeColorOverride("font_hover_color", new Color(1f, 0.9f, 0.7f, 1f));
        button.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.85f, 0.5f, 1f));
        sidebar.AddChild(button);

        // Create the filter pane (starts hidden).
        var pane = BuildFilterPane();
        library.AddChild(pane);
        _filterPane = pane;

        // Wire button click to toggle pane visibility.
        button.Pressed += () =>
        {
            if (GodotObject.IsInstanceValid(pane))
                pane.Visible = !pane.Visible;
        };

        if (SlayTheStatsConfig.DebugMode)
            MainFile.Logger.Info("[SlayTheStats] CompendiumFilterPatch: button injected into card library sidebar");
    }

    private static PanelContainer BuildFilterPane()
    {
        var pane = new PanelContainer();
        pane.Name = "SlayTheStatsFilterPane";
        pane.Visible = false;
        pane.ZIndex = 90;

        // Position: floating over the card grid, offset from the sidebar.
        pane.AnchorLeft = 0.5f;
        pane.AnchorTop = 0.5f;
        pane.AnchorRight = 0.5f;
        pane.AnchorBottom = 0.5f;
        pane.OffsetLeft = -200;
        pane.OffsetTop = -150;
        pane.OffsetRight = 200;
        pane.OffsetBottom = 150;
        // Steel-blue tint to match the tooltip panel style.
        pane.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);

        // Margin container for inner padding.
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        pane.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        // Header row: title + close button.
        var headerRow = new HBoxContainer();
        vbox.AddChild(headerRow);

        var title = new Label();
        title.Text = "Slay the Stats";
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.AddThemeColorOverride("font_color", new Color(0.918f, 0.745f, 0.318f, 1f));
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

        // Separator.
        vbox.AddChild(new HSeparator());

        // Toggle: Only Highest Won Ascension.
        var toggle = new CheckButton();
        toggle.Text = "Only Highest Won Ascension";
        toggle.ButtonPressed = SlayTheStatsConfig.OnlyHighestWonAscension;
        toggle.AddThemeColorOverride("font_color", new Color(1f, 0.9647f, 0.8863f, 1f));
        toggle.Toggled += (pressed) =>
        {
            SlayTheStatsConfig.OnlyHighestWonAscension = pressed;
            if (SlayTheStatsConfig.DebugMode)
                MainFile.Logger.Info($"[SlayTheStats] OnlyHighestWonAscension toggled to {pressed}");
        };
        vbox.AddChild(toggle);

        return pane;
    }
}
