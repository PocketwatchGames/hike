using Godot;
using System;

public static class InputDevice
{
	public enum EDevice
	{
		KeyboardMouse,
		Gamepad,
	}

	// Joystick deadzone for gamepad detection. Below this, joypad motion
	// events are treated as drift and don't flip the active device — otherwise
	// any controller plugged in but untouched would compete with the keyboard
	// every frame.
	const float JOYPAD_MOTION_THRESHOLD = 0.5f;

	public static EDevice Current { get; private set; } = EDevice.KeyboardMouse;
	public static Action<EDevice> OnChanged;

	public static void HandleInputEvent(InputEvent e)
	{
		EDevice next;
		switch (e)
		{
			case InputEventKey:
			case InputEventMouseButton:
			case InputEventMouseMotion:
				next = EDevice.KeyboardMouse;
				break;
			case InputEventJoypadButton:
				next = EDevice.Gamepad;
				break;
			case InputEventJoypadMotion motion:
				if (Mathf.Abs(motion.AxisValue) < JOYPAD_MOTION_THRESHOLD)
				{
					return;
				}
				next = EDevice.Gamepad;
				break;
			default:
				return;
		}

		if (Current == next)
		{
			return;
		}
		Current = next;
		OnChanged?.Invoke(Current);
	}
}
