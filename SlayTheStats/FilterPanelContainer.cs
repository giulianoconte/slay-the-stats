using Godot;

namespace SlayTheStats;

/// <summary>
/// PanelContainer subclass that auto-closes when the user clicks anywhere
/// outside its rect. Top-level (not nested) so Godot's C# source generator
/// emits the binding for our _Input override — nested partial classes inside
/// a static class don't always get the binding generated.
///
/// Uses _Input rather than _UnhandledInput because the compendium has a
/// full-screen background Control that consumes mouse events first, so
/// _UnhandledInput would never fire on "blank" areas. _Input runs before GUI
/// dispatch and lets us peek at every mouse press, while still passing it on
/// to the underlying card/relic.
/// </summary>
public partial class FilterPanelContainer : PanelContainer
{
    /// <summary>
    /// The toggle button associated with this pane (the cloned SlayTheStats
    /// sort button in the compendium sidebar). When set, clicks on this
    /// button's rect are NOT treated as outside-clicks — the button's own
    /// toggle handler closes the pane, and we don't want to also close it
    /// here (which would let the toggle re-open it on the same frame).
    /// </summary>
    public Control? AssociatedButton { get; set; }

    public override void _Input(InputEvent ev)
    {
        if (!Visible) return;
        if (ev is not InputEventMouseButton mb || !mb.Pressed) return;

        // Use the global mouse position rather than mb.Position so the rect
        // check works regardless of which viewport the event came from.
        var globalMouse = GetGlobalMousePosition();
        if (GetGlobalRect().HasPoint(globalMouse)) return;

        // Don't double-close when the user clicks the toggle button — let the
        // button's Released handler do it instead, so toggling doesn't immediately
        // re-open via the same press.
        if (AssociatedButton != null
            && GodotObject.IsInstanceValid(AssociatedButton)
            && AssociatedButton.GetGlobalRect().HasPoint(globalMouse))
            return;

        Visible = false;
    }
}
