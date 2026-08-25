using Godot;

// Resolves an InputMap action to a short glyph string (e.g. "A", "LMB", "Q")
// for the active input device. Picks the first event in the action that
// matches the device class. Falls back to the action name if no event matches
// or the action isn't registered.
public static class InputGlyph
{
	public static string Resolve(string actionName, InputDevice.EDevice device)
	{
		return TryResolve(actionName, device, out string glyph) ? glyph : actionName;
	}

	// Same lookup, but says whether the action actually has an event for this
	// device instead of falling back to its name. The controls help screen needs
	// the distinction: a mouse-only binding would otherwise print the raw
	// "AttackContextSensitive" in its gamepad column.
	public static bool TryResolve(string actionName, InputDevice.EDevice device, out string glyph)
	{
		glyph = string.Empty;
		if (string.IsNullOrEmpty(actionName))
		{
			return false;
		}
		StringName action = actionName;
		if (!InputMap.HasAction(action))
		{
			return false;
		}
		foreach (InputEvent e in InputMap.ActionGetEvents(action))
		{
			string resolved = TryGlyph(e, device);
			if (resolved != null)
			{
				glyph = resolved;
				return true;
			}
		}
		return false;
	}

	static string TryGlyph(InputEvent e, InputDevice.EDevice device)
	{
		switch (device)
		{
			case InputDevice.EDevice.KeyboardMouse:
				if (e is InputEventKey key)
				{
					return KeyGlyph(key);
				}
				if (e is InputEventMouseButton mouse)
				{
					return MouseGlyph(mouse.ButtonIndex);
				}
				return null;
			case InputDevice.EDevice.Gamepad:
				if (e is InputEventJoypadButton btn)
				{
					return JoyButtonGlyph(btn.ButtonIndex);
				}
				if (e is InputEventJoypadMotion motion)
				{
					return JoyMotionGlyph(motion.Axis, motion.AxisValue);
				}
				return null;
		}
		return null;
	}

	static string KeyGlyph(InputEventKey key)
	{
		Key code = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
		if (code == Key.None)
		{
			return null;
		}
		return OS.GetKeycodeString(code);
	}

	static string MouseGlyph(MouseButton button)
	{
		return button switch
		{
			MouseButton.Left => "LMB",
			MouseButton.Right => "RMB",
			MouseButton.Middle => "MMB",
			MouseButton.WheelUp => "MWU",
			MouseButton.WheelDown => "MWD",
			MouseButton.Xbutton1 => "M4",
			MouseButton.Xbutton2 => "M5",
			_ => "M?",
		};
	}

	// Xbox-style labels — map to PS glyphs later if a controller-type CVar
	// is added.
	static string JoyButtonGlyph(JoyButton b)
	{
		return b switch
		{
			JoyButton.A => "A",
			JoyButton.B => "B",
			JoyButton.X => "X",
			JoyButton.Y => "Y",
			JoyButton.LeftShoulder => "LB",
			JoyButton.RightShoulder => "RB",
			JoyButton.LeftStick => "LS",
			JoyButton.RightStick => "RS",
			JoyButton.Back => "Back",
			JoyButton.Start => "Start",
			JoyButton.Guide => "Guide",
			JoyButton.DpadUp => "D-Up",
			JoyButton.DpadDown => "D-Down",
			JoyButton.DpadLeft => "D-Left",
			JoyButton.DpadRight => "D-Right",
			_ => b.ToString(),
		};
	}

	static string JoyMotionGlyph(JoyAxis axis, float value)
	{
		switch (axis)
		{
			case JoyAxis.TriggerLeft: return "LT";
			case JoyAxis.TriggerRight: return "RT";
			case JoyAxis.LeftX: return value < 0 ? "LS-Left" : "LS-Right";
			case JoyAxis.LeftY: return value < 0 ? "LS-Up" : "LS-Down";
			case JoyAxis.RightX: return value < 0 ? "RS-Left" : "RS-Right";
			case JoyAxis.RightY: return value < 0 ? "RS-Up" : "RS-Down";
			default: return axis.ToString();
		}
	}
}
