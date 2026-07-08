using System;
using Godot;

// Reusable full-screen black fade: fade to black, run a callback while fully
// opaque (swap the world behind the curtain), hold briefly, then fade back in
// and run a completion callback. Wall-clock driven (Time.GetTicksMsec) so
// slow-mo or a frozen sim clock never stretches the fade. Modeled on
// SleepOverlay's shape but general-purpose and side-effect-free — it owns only
// the ColorRect alpha and the two callbacks.
[GlobalClass]
public partial class ScreenFade : Control
{
	[Export] public ColorRect overlay;
	[Export(PropertyHint.Range, "0.1,5,0.05")] public float fadeOutSeconds = 0.6f;
	[Export(PropertyHint.Range, "0.1,5,0.05")] public float fadeInSeconds = 0.6f;
	// Minimum time held fully black after onOpaque runs, so the swap behind the
	// curtain never flashes by too fast to read.
	[Export(PropertyHint.Range, "0,3,0.05")] public float holdSeconds = 0.2f;

	enum EState
	{
		Idle,
		FadingOut,
		Holding,
		FadingIn,
	}

	EState _state = EState.Idle;
	float _darkness;
	// Wall-clock stamp: _Process delta is scaled by Engine.TimeScale and the fade
	// must run at real speed regardless of any slow-mo.
	ulong _lastRealMs;
	ulong _holdUntilMs;
	Action _onOpaque;
	Action _onComplete;

	public bool Busy => _state != EState.Idle;

	public override void _Ready()
	{
		Visible = false;
		SetAlpha(0f);
		MouseFilter = MouseFilterEnum.Ignore;
	}

	// Begin the cycle. onOpaque runs once while the screen is fully black — do the
	// scene swap there. onComplete (optional) runs after the fade back in finishes.
	public void Play(Action onOpaque, Action onComplete = null)
	{
		if (_state != EState.Idle)
		{
			return;
		}
		_onOpaque = onOpaque;
		_onComplete = onComplete;
		_darkness = 0f;
		_state = EState.FadingOut;
		_lastRealMs = Time.GetTicksMsec();
		Visible = true;
	}

	public override void _Process(double delta)
	{
		if (_state == EState.Idle)
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
				SetAlpha(_darkness);
				if (_darkness >= 1f)
				{
					Action onOpaque = _onOpaque;
					_onOpaque = null;
					onOpaque?.Invoke();
					_holdUntilMs = nowMs + (ulong)(holdSeconds * 1000f);
					_state = EState.Holding;
				}
				break;
			}
			case EState.Holding:
			{
				if (nowMs >= _holdUntilMs)
				{
					_state = EState.FadingIn;
				}
				break;
			}
			case EState.FadingIn:
			{
				float step = fadeInSeconds > 0f ? dt / fadeInSeconds : 1f;
				_darkness = Mathf.Max(0f, _darkness - step);
				SetAlpha(_darkness);
				if (_darkness <= 0f)
				{
					_state = EState.Idle;
					Visible = false;
					Action onComplete = _onComplete;
					_onComplete = null;
					onComplete?.Invoke();
				}
				break;
			}
		}
	}

	void SetAlpha(float a)
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
