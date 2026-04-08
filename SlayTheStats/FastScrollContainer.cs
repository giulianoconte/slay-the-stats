using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace SlayTheStats;

/// <summary>
/// NScrollableContainer subclass that amplifies mouse wheel scroll deltas. The base
/// game's ScrollHelper.GetDragForScrollEvent returns ±40px per wheel click, which feels
/// slow on the long bestiary encounter list. After the base handler runs we add an
/// extra constant to _targetDragPosY (via reflection — it's private on the base) so
/// each wheel click moves ~3 rows instead of ~1.3.
///
/// Top-level partial class so Godot's C# source generator emits the binding for the
/// _GuiInput override (nested partial classes inside a static class don't always
/// generate one).
/// </summary>
public partial class FastScrollContainer : NScrollableContainer
{
    /// <summary>Extra scroll distance added on top of the base 40px wheel delta.
    /// 0 = use the base game speed (the +60 boost was too fast).</summary>
    private const float ExtraWheelScrollPx = 0f;

    private static readonly System.Reflection.FieldInfo? _targetDragPosYField =
        AccessTools.Field(typeof(NScrollableContainer), "_targetDragPosY");

    public override void _GuiInput(InputEvent inputEvent)
    {
        base._GuiInput(inputEvent);

        if (inputEvent is not InputEventMouseButton mb || !mb.Pressed)
            return;

        float extra = mb.ButtonIndex switch
        {
            MouseButton.WheelUp   =>  ExtraWheelScrollPx,
            MouseButton.WheelDown => -ExtraWheelScrollPx,
            _                     =>  0f,
        };
        if (extra == 0f) return;

        if (_targetDragPosYField == null) return;
        var current = (float)(_targetDragPosYField.GetValue(this) ?? 0f);
        _targetDragPosYField.SetValue(this, current + extra);
    }
}
