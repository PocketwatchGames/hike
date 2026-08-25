using Godot;

// One row of the controls help screen: what a control does, and the InputMap
// actions that name it on each device.
//
// The glyphs themselves are NOT authored here — they are resolved from the live
// InputMap, so the bindings InputBindings rewrites over project.godot at startup
// (and any future player rebind) show up with no edit to this resource.
//
// [Tool] so the editor materializes it as its real type under the screen's typed
// [Export]; see CLAUDE.md's [Tool]-closure rule.
[Tool]
[GlobalClass]
public partial class ControlBindingData : Resource
{
	// Localization key for the label column ("Interact / Dash").
	[Export] public StringName labelKey = "";
	// Actions naming this control per device. Empty — or an action carrying no
	// event for that device — renders as unbound.
	[Export] public string[] keyboardActions = System.Array.Empty<string>();
	[Export] public string[] gamepadActions = System.Array.Empty<string>();
	[Export] public EBindingJoin keyboardJoin = EBindingJoin.Alternatives;
	[Export] public EBindingJoin gamepadJoin = EBindingJoin.Alternatives;
}
