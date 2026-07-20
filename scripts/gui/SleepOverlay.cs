using Godot;

// Full-screen black overlay for the sleep / rest time-skip (tents). Mirrors the
// DeathScreen fade pattern but is purely visual and self-driving: fade to black,
// run the world time-skip while opaque, then fade back in. If a status effect
// kills the player during the skip, the player wakes "at the appropriate time"
// — the skip already stopped at the moment of death (Sim.AdvanceTime) — and
// this overlay hands the screen to the DeathScreen rather than fading back in.
//
// Unlike DeathScreen this does NOT touch the audio buses: doing so would let the
// DeathScreen capture an already-lowered baseline on a die-in-sleep and restore
// to silence. The DeathScreen owns audio/slow-mo for the death case.
[GlobalClass]
public partial class SleepOverlay : Control
{
	[Export] public ColorRect overlay;
	[Export(PropertyHint.Range, "0.1,5,0.05")] public float fadeOutSeconds = 1.0f;
	[Export(PropertyHint.Range, "0.1,5,0.05")] public float fadeInSeconds = 1.0f;

	public enum EState
	{
		Hidden,
		FadingOut,
		FadingIn,
		// Player died mid-skip; hold fully black until the DeathScreen is opaque,
		// then release so the swap shows no frame of the dead body.
		DeathHandoff,
	}

	GameClient _gameClient;
	double _sleepHours;
	double _healFractionPerHour;
	EState _state = EState.Hidden;
	float _darkness;
	// Wall-clock stamp for the fade. _Process delta is scaled by Engine.TimeScale
	// (the death-cam slow-mo on a die-in-sleep), which would stretch this fade;
	// the UI fade should run at real speed regardless.
	ulong _lastRealMs;

	public bool Busy => _state != EState.Hidden;

	public override void _Ready()
	{
		Visible = false;
		SetOverlayAlpha(0f);
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public void Show(GameClient gameClient, double hours, double healFractionPerHour)
	{
		if (_state != EState.Hidden)
		{
			return;
		}
		_gameClient = gameClient;
		_sleepHours = hours;
		_healFractionPerHour = healFractionPerHour;
		_darkness = 0f;
		_state = EState.FadingOut;
		_lastRealMs = Time.GetTicksMsec();
		Visible = true;
	}

	public override void _Process(double delta)
	{
		if (_state == EState.Hidden)
		{
			return;
		}
		ulong nowMs = Time.GetTicksMsec();
		float dt = (nowMs - _lastRealMs) / 1000f;
		_lastRealMs = nowMs;
		switch (_state)
		{
			case EState.FadingOut:
			{
				float step = fadeOutSeconds > 0f ? dt / fadeOutSeconds : 1f;
				_darkness = Mathf.Min(1f, _darkness + step);
				SetOverlayAlpha(_darkness);
				if (_darkness >= 1f)
				{
					// Fully black: do the skip now, then wake or hand off.
					_gameClient?.PerformSleepAdvance(_sleepHours, _healFractionPerHour);
					bool died = _gameClient?.PlayerIsDead ?? false;
					_state = died ? EState.DeathHandoff : EState.FadingIn;
				}
				break;
			}
			case EState.FadingIn:
			{
				float step = fadeInSeconds > 0f ? dt / fadeInSeconds : 1f;
				_darkness = Mathf.Max(0f, _darkness - step);
				SetOverlayAlpha(_darkness);
				if (_darkness <= 0f)
				{
					Finish(wokeAlive: true);
				}
				break;
			}
			case EState.DeathHandoff:
			{
				// Stay fully black; the DeathScreen is fading up underneath us
				// (onDied fired inside PerformSleepAdvance). Release once it is
				// opaque so the two black overlays swap seamlessly.
				if (_gameClient == null || _gameClient.DeathScreenOpaque)
				{
					Finish(wokeAlive: false);
				}
				break;
			}
		}
	}

	void Finish(bool wokeAlive)
	{
		GameClient client = _gameClient;
		_state = EState.Hidden;
		_darkness = 0f;
		SetOverlayAlpha(0f);
		Visible = false;
		_gameClient = null;
		// On a clean wake we own the input gate; on a die-in-sleep the
		// DeathScreen took it over and clears it after respawn.
		if (wokeAlive)
		{
			client?.EndSleep();
		}
	}

	void SetOverlayAlpha(float a)
	{
		if (overlay == null)
		{
			return;
		}
		Color c = overlay.Color;
		c.A = Mathf.Clamp(a, 0f, 1f);
		overlay.Color = c;
	}
}
