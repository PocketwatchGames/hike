using Godot;
using System;

// Sleep tab of the camp screen: "Sleep Until Sunrise" and "Sleep 1 hour", each
// handing its duration to the onSleep callback supplied by CampScreen, which
// hides the camp and starts the sleep overlay. Whether a sleep is a rest is
// Sim.Sleep's call — it is one exactly when it reaches the sunrise.
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
	// Supplied by CampScreen.Open; invoked with (hours, healFractionPerHour).
	Action<double, double> _onSleep;

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

	public void Open(Player player, float healFractionPerHour, Action<double, double> onSleep)
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
		_onSleep?.Invoke(NapHours, _healFractionPerHour);
	}

	void OnUntilSunPressed()
	{
		_onSleep?.Invoke(Sim.SleepUntilDawn, _healFractionPerHour);
	}
}
