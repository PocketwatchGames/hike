using Godot;

// Applies the player's input bindings at startup.
//
// This is NOT a new layer over Godot's InputMap — the InputMap already IS the
// indirection between physical buttons and named actions, and gameplay code
// only ever asks for actions ("Dash", "Interact"). This just rewrites the
// bindings for the handful of actions whose defaults live in code rather than
// in project.godot.
//
// THIS CODE OWNS the bindings for Interact / InteractCancel / Dash / Lantern /
// UseItem / Sneak: it overwrites whatever project.godot authored for them on
// startup. Edit the set here, not the editor's Input Map, or your change is
// silently undone.
// If player-facing rebinding lands later, this becomes the supplier of defaults
// that user overrides are layered on top of.
public static class InputBindings
{
    private const string Interact = "Interact";
    private const string InteractCancel = "InteractCancel";
    private const string Dash = "Dash";
    private const string Lantern = "Lantern";
    private const string UseItem = "UseItem";
    private const string Sneak = "Sneak";

    public static void Apply()
    {
        // Dash carries traversal as well: one press means climb, mantle or dash,
        // ranked in Player.ProcessInput. It is the Dash action because dash is
        // what it does when there is no wall or ledge to take, and it holds the
        // spacebar and the pad's primary face button.
        SetBindings(Dash, Key.Space, JoyButton.B);
        // Interact is a button of its own and only interacts.
        SetBindings(Interact, Key.E, JoyButton.A);
        // Cancel shares the interact button (plus Escape), so backing out of an
        // interactive or a weapon charge is the same button that started it.
        // Player.ProcessInput only lets it consume the frame when there is
        // something to abort, so an ordinary interact press still falls through.
        SetBindings(InteractCancel, Key.Escape, JoyButton.B);
        SetBindings(Lantern, Key.Q, JoyButton.RightShoulder);
        SetBindings(UseItem, Key.Ctrl, JoyButton.Y);
        SetBindings(Sneak, Key.Shift, JoyAxis.TriggerLeft);
    }

    // Replaces every binding on the action. Passing no key/button clears it.
    private static void SetBindings(string action, Key key = Key.None, JoyButton button = JoyButton.Invalid)
    {
        if (!BeginRebind(action, out StringName name))
        {
            return;
        }
        AddKey(name, key);
        if (button != JoyButton.Invalid)
        {
            InputMap.ActionAddEvent(name, new InputEventJoypadButton { ButtonIndex = button });
        }
    }

    // Same, for a pad AXIS rather than a button.
    //
    // The triggers are axes — there is no JoyButton for them, and an
    // InputEventJoypadButton can never match one, so binding a trigger through
    // the overload above silently produces an action nothing can press.
    //
    // `direction` is which end of the axis counts. Triggers rest at 0 and travel
    // to +1, so the default is right for them; a stick half-axis wants -1 for
    // left/up. How far along the travel it registers as "pressed" is the
    // action's deadzone, which stays as authored in project.godot — this only
    // replaces events.
    //
    // No default arguments here, deliberately: giving them defaults would make
    // a one-argument SetBindings(Interact) ambiguous between the two overloads.
    private static void SetBindings(string action, Key key, JoyAxis axis, float direction = 1f)
    {
        if (!BeginRebind(action, out StringName name))
        {
            return;
        }
        AddKey(name, key);
        if (axis != JoyAxis.Invalid)
        {
            InputMap.ActionAddEvent(name, new InputEventJoypadMotion
            {
                Axis = axis,
                AxisValue = direction,
            });
        }
    }

    private static bool BeginRebind(string action, out StringName name)
    {
        name = action;
        if (!InputMap.HasAction(name))
        {
            GD.PushWarning($"InputBindings: no action '{action}' in the InputMap; binding skipped.");
            return false;
        }
        InputMap.ActionEraseEvents(name);
        return true;
    }

    private static void AddKey(StringName name, Key key)
    {
        if (key == Key.None)
        {
            return;
        }
        // Physical keycode, so the binding follows the key's POSITION and
        // stays correct on non-QWERTY layouts.
        InputMap.ActionAddEvent(name, new InputEventKey { PhysicalKeycode = key });
    }
}
