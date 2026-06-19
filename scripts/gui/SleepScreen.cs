using Godot;
using System;

// Sleep tab of the camp screen. Four duration buttons whose Pressed signals are
// connected in _Ready: a fixed 1-hour and 6-hour rest, "Until Healed" (disabled
// at full health, sleeps just long enough to top off), and a sun button that
// reads "Until Sunrise" at night / "Until Sunset" by day and rests to that next
// transition. Each computes a duration and hands it to the onSleep callback
// supplied by CampScreen, which tears down the camp and starts the sleep overlay.
[GlobalClass]
public partial class SleepScreen : Control
{
	[Export] Button _oneHourButton;
	[Export] Button _sixHourButton;
	[Export] Button _untilHealedButton;
	[Export] Button _untilSunButton;

	const double HoursPerDay = 24.0;
	// Fallback sunrise/sunset (0=midnight, 0.5=noon) when SimData is unavailable;
	// the live values come from SimData.SunriseTimeOfDay / SunsetTimeOfDay.
	const float DefaultSunrise = 0.25f;
	const float DefaultSunset = 0.75f;

	Player _player;
	// Rest heal rate (fraction of max health per in-world hour) for the fire the
	// player is camped at — supplied by CampScreen from the campfire. "Until
	// Healed" divides the missing fraction by this to pick its duration.
	float _healFractionPerHour;
	// Supplied by CampScreen.Open; invoked with (hours, healFractionPerHour).
	Action<double, double> _onSleep;

	public override void _Ready()
	{
		Visible = false;
		if (_oneHourButton != null) { _oneHourButton.Pressed += OnOneHourPressed; }
		if (_sixHourButton != null) { _sixHourButton.Pressed += OnSixHourPressed; }
		if (_untilHealedButton != null) { _untilHealedButton.Pressed += OnUntilHealedPressed; }
		if (_untilSunButton != null) { _untilSunButton.Pressed += OnUntilSunPressed; }
	}

	public override void _ExitTree()
	{
		if (_oneHourButton != null) { _oneHourButton.Pressed -= OnOneHourPressed; }
		if (_sixHourButton != null) { _sixHourButton.Pressed -= OnSixHourPressed; }
		if (_untilHealedButton != null) { _untilHealedButton.Pressed -= OnUntilHealedPressed; }
		if (_untilSunButton != null) { _untilSunButton.Pressed -= OnUntilSunPressed; }
	}

	public void Open(Player player, float healFractionPerHour, Action<double, double> onSleep)
	{
		_player = player;
		_healFractionPerHour = healFractionPerHour;
		_onSleep = onSleep;
		Visible = true;
		RefreshButtons();
		// Focus the first button so keyboard / gamepad can drive the menu the
		// moment the tab opens. Deferred — the control only just became visible
		// this frame, and GrabFocus needs it visible-in-tree to take.
		_oneHourButton?.CallDeferred(Control.MethodName.GrabFocus);
	}

	public void Close()
	{
		Visible = false;
		_player = null;
		_onSleep = null;
	}

	// Reconcile button state with current health and time of day (computed at
	// open; cheap to re-run if the tab is re-shown).
	void RefreshButtons()
	{
		if (_untilHealedButton != null)
		{
			_untilHealedButton.Disabled = _player == null || _player.Health >= _player.MaxHealth;
		}
		if (_untilSunButton != null)
		{
			_untilSunButton.Text = IsDaytime() ? "Until Sunset" : "Until Sunrise";
		}
	}

	void OnOneHourPressed()
	{
		Sleep(1.0);
	}

	void OnSixHourPressed()
	{
		Sleep(6.0);
	}

	void OnUntilHealedPressed()
	{
		if (_player == null || _healFractionPerHour <= 0f || _player.MaxHealth <= 0f)
		{
			return;
		}
		float missing = 1f - _player.Health / _player.MaxHealth;
		if (missing <= 0f)
		{
			return;
		}
		Sleep(missing / _healFractionPerHour);
	}

	void OnUntilSunPressed()
	{
		Sleep(HoursUntil(IsDaytime() ? SunsetTimeOfDay() : SunriseTimeOfDay()));
	}

	void Sleep(double hours)
	{
		if (hours <= 0.0)
		{
			return;
		}
		_onSleep?.Invoke(hours, _healFractionPerHour);
	}

	bool IsDaytime()
	{
		double tod = Tod();
		return tod >= SunriseTimeOfDay() && tod < SunsetTimeOfDay();
	}

	SimData SimData => _player?.World?.SimData;
	float SunriseTimeOfDay() => SimData?.SunriseTimeOfDay ?? DefaultSunrise;
	float SunsetTimeOfDay() => SimData?.SunsetTimeOfDay ?? DefaultSunset;

	// In-world hours from now until the clock next reaches targetTod, wrapping
	// past midnight. TimeOfDay01 spans one 24-hour day over [0,1).
	double HoursUntil(double targetTod)
	{
		double delta = targetTod - Tod();
		while (delta <= 0.0)
		{
			delta += 1.0;
		}
		return delta * HoursPerDay;
	}

	double Tod()
	{
		return _player?.World?.WorldState?.TimeOfDay01 ?? 0.0;
	}
}
