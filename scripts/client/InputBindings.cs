using Godot;

// Applies the player's input bindings at startup.
//
// This is NOT a new layer over Godot's InputMap — the InputMap already IS the
// indirection between physical buttons and named actions, and gameplay code
// only ever asks for actions ("Dash", "Interact"). This just rewrites the
// bindings for the handful of actions whose defaults live in code rather than
// in project.godot.
//
// THIS CODE OWNS the bindings for Interact / Dash / Lantern / UseItem / Sneak:
// it overwrites whatever project.godot authored for them on startup. Edit the
// set here, not the editor's Input Map, or your change is silently undone.
// If player-facing rebinding lands later, this becomes the supplier of defaults
// that user overrides are layered on top of.
public static class InputBindings
{
    private const string Interact = "Interact";
    private const string Dash = "Dash";
    private const string Lantern = "Lantern";
    private const string UseItem = "UseItem";
    private const string Sneak = "Sneak";

    public static void Apply()
    {
        // The spacebar and the pad's primary face button carry the CONTEXT
        // button: one press means interact, climb or dash, ranked in
        // Player.ProcessInput. It is still the Dash action because dash is what
        // it does when nothing else claims the press.
        //
        // Interact is unbound: everything it did that the player still needs is
        // on the context button. What it alone reached — the self-action menu
        // (Pray, Dig) in open space — is unreachable until it gets a home of
        // its own.
        SetBindings(Interact);
        SetBindings(Dash, Key.Space, JoyButton.A);
        SetBindings(Lantern, Key.Q, JoyButton.B);
        SetBindings(UseItem, Key.Ctrl, JoyButton.Y);
        SetBindings(Sneak, Key.Shift, JoyAxis.TriggerLeft);
    }

    // Replaces every binding on the action. Passing no key/button clears it,
    // which is how Interact is switched off — a bound-but-inert Interact would
    // still cancel queued input on press.
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
