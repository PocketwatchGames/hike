using Godot;

// Applies the input bindings a movement model needs (see CVars.climbMovement).
//
// The two models want different buttons, not just different behaviour: removing
// jump frees the spacebar and the pad's A button, and those are the natural
// homes for dash and interact respectively. Leaving Jump bound while its
// buttons were reassigned would fire two actions from one press.
//
// This is NOT a new layer over Godot's InputMap — the InputMap already IS the
// indirection between physical buttons and named actions, and gameplay code
// only ever asks for actions ("Dash", "Interact"). This just rewrites the
// bindings for the handful of actions the model switch moves.
//
// WHILE THAT SWITCH EXISTS, this code owns the bindings for Jump / Interact /
// Dash: it overwrites whatever project.godot authored for them on startup. Edit
// the sets here, not the editor's Input Map, or your change is silently undone.
// If player-facing rebinding lands later, this becomes the supplier of defaults
// that user overrides are layered on top of.
public static class InputBindings
{
    private const string Jump = "Jump";
    private const string Interact = "Interact";
    private const string Dash = "Dash";
    private const string Lantern = "Lantern";
    private const string UseItem = "UseItem";
    private const string Sneak = "Sneak";

    // Both branches bind EVERY action listed above, even where the two models
    // agree. Leaving one out does not fall back to project.godot — it leaves
    // whatever the other model last wrote, so bindings would depend on the
    // toggle history rather than the current model. That produced a real
    // collision: Sneak set to Shift by the climb model survived a switch to
    // legacy, where Shift is Dash, and both actions fired on one key.
    public static void Apply(bool climbMovement)
    {
        if (climbMovement)
        {
            // No jump: the spacebar goes to dash, and interact takes the pad's
            // primary face button where a player expects a context action.
            SetBindings(Jump);
            SetBindings(Interact, Key.E, JoyButton.Y);
            SetBindings(Dash, Key.Space, JoyButton.A);
            SetBindings(Lantern, Key.Q, JoyButton.B);
            SetBindings(UseItem, Key.Ctrl, JoyButton.RightShoulder);
            SetBindings(Sneak, Key.Shift, JoyAxis.TriggerLeft);
        }
        else
        {
            // The set project.godot ships, reproduced exactly — legacy IS the
            // previous model, so it should play identically to before this
            // switch existed. Conflict-free by construction for the same reason.
            SetBindings(Jump, Key.Space, JoyButton.A);
            SetBindings(Interact, Key.E, JoyButton.Y);
            SetBindings(Dash, Key.Shift, JoyButton.B);
            SetBindings(Lantern, Key.L, JoyButton.LeftShoulder);
            SetBindings(UseItem, Key.Q, JoyButton.RightShoulder);
            SetBindings(Sneak, Key.Ctrl, JoyAxis.TriggerLeft);
        }
    }

    // Replaces every binding on the action. Passing no key/button clears it,
    // which is how Jump is switched off in the climb model — a bound-but-inert
    // Jump would still break sneak and cancel queued input on press.
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
    // a one-argument SetBindings(Jump) ambiguous between the two overloads.
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
