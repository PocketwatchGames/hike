using System.Text;
using Godot;

// Every control the game describes to the player: the rows of the controls help
// screen, and what an [input:<id>] token in authored text names (see
// LanguageText). One set, so a line of text and the controls screen can never
// disagree about which button does a thing.
//
// Glyphs are resolved from the live InputMap, so the bindings InputBindings
// writes over project.godot at startup are what both show.
//
// [Tool] so the editor materializes it as its real type; see CLAUDE.md's
// [Tool]-closure rule.
[Tool]
[GlobalClass]
public partial class ControlSetData : Resource
{
	// Installed by Main at startup, for display paths with no node to hang an
	// [Export] on (LanguageText).
	public static ControlSetData Current { get; set; }

	[Export] public ControlBindingData[] bindings = System.Array.Empty<ControlBindingData>();
	// Between two alternative glyphs ("Z / C") and between the parts of a chord
	// ("LMB+RMB").
	[Export] public string alternativeSeparator = " / ";
	[Export] public string chordSeparator = "+";

	public ControlBindingData Find(string id)
	{
		if (string.IsNullOrEmpty(id) || bindings == null)
		{
			return null;
		}
		foreach (ControlBindingData binding in bindings)
		{
			if (binding?.id != null && binding.id.ToString() == id)
			{
				return binding;
			}
		}
		return null;
	}

	// The glyphs `binding` reads as on `device` ("LMB+RMB", "Z / C"). False when
	// nothing on that device performs it.
	public bool TryCompose(ControlBindingData binding, InputDevice.EDevice device, out string glyphs)
	{
		glyphs = string.Empty;
		if (binding == null)
		{
			return false;
		}
		bool gamepad = device == InputDevice.EDevice.Gamepad;
		string[] actions = gamepad ? binding.gamepadActions : binding.keyboardActions;
		EBindingJoin join = gamepad ? binding.gamepadJoin : binding.keyboardJoin;
		if (actions == null || actions.Length == 0)
		{
			return false;
		}
		string separator = join == EBindingJoin.Chord ? chordSeparator : alternativeSeparator;
		var text = new StringBuilder();
		// Set while every alternative so far is a direction of this one stick.
		string commonStick = null;
		bool allOneStick = join == EBindingJoin.Alternatives;
		foreach (string action in actions)
		{
			if (!InputGlyph.TryResolve(action, device, out string glyph))
			{
				// A chord needs every part: with one half unbound on this device
				// the input can't be performed at all.
				if (join == EBindingJoin.Chord)
				{
					return false;
				}
				continue;
			}
			if (allOneStick)
			{
				string stick = StickOf(glyph);
				allOneStick = stick != null && (commonStick == null || commonStick == stick);
				commonStick = stick;
			}
			if (text.Length > 0)
			{
				text.Append(separator);
			}
			text.Append(glyph);
		}
		// "RS-Left / RS-Right / RS-Up / RS-Down" is just the stick: aiming, looking.
		glyphs = allOneStick && commonStick != null ? commonStick : text.ToString();
		return text.Length > 0;
	}

	// "RS" for "RS-Left" — the stick a direction glyph belongs to (InputGlyph's
	// JoyMotionGlyph spelling), or null for anything else.
	static string StickOf(string glyph)
	{
		int dash = glyph.IndexOf('-');
		if (dash <= 0)
		{
			return null;
		}
		string stick = glyph.Substring(0, dash);
		return stick == "LS" || stick == "RS" ? stick : null;
	}
}
