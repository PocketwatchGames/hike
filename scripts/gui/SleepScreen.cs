using Godot;
using System;

// "Sleep Until Sunrise" and "Sleep 1 hour", each handing its duration to the
// onSleep callback supplied by the opener: the camp screen's Sleep tab, or a Bed
// opening it standalone from the HUD. Whether a sleep is a rest is Sim.Sleep's
// call — it is one exactly when it reaches the sunrise.
//
// Opened with an onCancel callback (standalone) it dismisses itself on
// ui_cancel; opened without one (the camp tab) its host owns ui_cancel.
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
	// Invoked with (hours, healFractionPerHour).
	Action<double, double> _onSleep;
	Action _onCancel;

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

	public void Open(Player player, float healFractionPerHour, Action<double, double> onSleep, Action onCancel = null)
	{
		_player = player;
		_healFractionPerHour = healFractionPerHour;
		_onSleep = onSleep;
		_onCancel = onCancel;
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
		_onCancel = null;
	}

	void OnOneHourPressed()
	{
		_onSleep?.Invoke(NapHours, _healFractionPerHour);
	}

	void OnUntilSunPressed()
	{
		_onSleep?.Invoke(Sim.SleepUntilDawn, _healFractionPerHour);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible || _onCancel == null)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			Action cancel = _onCancel;
			Close();
			cancel.Invoke();
			GetViewport().SetInputAsHandled();
		}
	}
}
