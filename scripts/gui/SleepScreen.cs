using Godot;
using System;

// Sleep tab of the camp screen. Two rest options, connected in _Ready:
//   - "Sleep Until Sunrise" advances to the next day's sunrise (the only path
//     that rolls the day), clearing the player's afflictions and full-healing.
//   - "Sleep 1 hour" is a short in-day nap that integrates status effects over
//     the skipped hour and heals a fraction, clamped so it never passes midnight.
// Each hands its choice to the onSleep callback supplied by CampScreen, which
// tears down the camp and starts the sleep overlay.
[GlobalClass]
public partial class SleepScreen : Control
{
	[Export] Button _oneHourButton;
	[Export] Button _untilSunButton;

	// In-world hours a single "Sleep 1 hour" nap advances.
	const double NapHours = 1.0;

	Player _player;
	// Rest heal rate (fraction of max health per in-world hour) for the fire the
	// player is camped at — supplied by CampScreen from the campfire. Used only by
	// the 1-hour nap; the until-sunrise rest full-heals regardless.
	float _healFractionPerHour;
	// Supplied by CampScreen.Open; invoked with (hours, healFractionPerHour,
	// toSunrise). Until-sunrise ignores `hours`.
	Action<double, double, bool> _onSleep;

	public override void _Ready()
	{
		Visible = false;
		if (_oneHourButton != null) { _oneHourButton.Pressed += OnOneHourPressed; }
		if (_untilSunButton != null) { _untilSunButton.Pressed += OnUntilSunPressed; }
	}

	public override void _ExitTree()
	{
		if (_oneHourButton != null) { _oneHourButton.Pressed -= OnOneHourPressed; }
		if (_untilSunButton != null) { _untilSunButton.Pressed -= OnUntilSunPressed; }
	}

	public void Open(Player player, float healFractionPerHour, Action<double, double, bool> onSleep)
	{
		_player = player;
		_healFractionPerHour = healFractionPerHour;
		_onSleep = onSleep;
		Visible = true;
		// Focus the first button so keyboard / gamepad can drive the menu the
		// moment the tab opens. Deferred — the control only just became visible
		// this frame, and GrabFocus needs it visible-in-tree to take.
		_untilSunButton?.CallDeferred(Control.MethodName.GrabFocus);
	}

	public void Close()
	{
		Visible = false;
		_player = null;
		_onSleep = null;
	}

	void OnOneHourPressed()
	{
		_onSleep?.Invoke(NapHours, _healFractionPerHour, false);
	}

	void OnUntilSunPressed()
	{
		_onSleep?.Invoke(0.0, _healFractionPerHour, true);
	}
}
