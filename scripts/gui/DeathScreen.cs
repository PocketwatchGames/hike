using Godot;
using System;

// Full-screen death overlay. GameClient.Show(this) starts the FadingOut
// phase: the black ColorRect ramps from invisible to opaque and the World3D
// + Ambience2D audio buses fade to silence. Once opaque, the "YOU DIED"
// prompt + Respawn button hint appear and the screen accepts ui_accept.
// On press GameClient runs the death wake (sack, rest, campfire), the
// prompt hides, Settling holds black until the world there has spawned, and
// the FadingIn phase ramps everything back up over
// `fadeInSeconds`, then hands off to the wake's camp screen. InputSuppressed
// is held on GameClient for the entire life of the screen.
[GlobalClass]
public partial class DeathScreen : Control
{
	[Export] public ColorRect overlay;
	[Export] public Control promptRoot;
	[Export] public Label titleLabel;
	[Export] public ButtonHint respawnHint;
	[Export(PropertyHint.Range, "0.1,5,0.05")] public float fadeOutSeconds = 1.0f;
	[Export(PropertyHint.Range, "0.1,5,0.05")] public float fadeInSeconds = 1.0f;
	// Duck (dB) on the World3D + Ambience2D buses at full fade. -80 dB
	// reaches Godot's silence floor from any setting.
	[Export(PropertyHint.Range, "-80,0,0.5")] public float audioFadeFloorDb = -80f;

	public enum EState
	{
		Hidden,
		FadingOut,
		Prompt,
		// Woken, still black while the world around the campfire spawns in.
		Settling,
		FadingIn,
	}

	GameClient _gameClient;
	EState _state = EState.Hidden;
	float _darkness;
	// Wall-clock stamp for the fade. _Process delta is scaled by Engine.TimeScale,
	// so during the slow-mo death cam it would stretch this overlay's fade (a 1s
	// fade became ~5s at timeScale 0.2). The world stays slowed; this UI does not.
	ulong _lastRealMs;

	public EState State => _state;

	public override void _Ready()
	{
		Visible = false;
		if (overlay != null)
		{
			Color c = overlay.Color;
			c.A = 0f;
			overlay.Color = c;
		}
		if (promptRoot != null)
		{
			promptRoot.Visible = false;
		}
		respawnHint?.SetHint("ui_accept", "Respawn");
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public void Show(GameClient gameClient)
	{
		if (_state != EState.Hidden)
		{
			return;
		}
		_gameClient = gameClient;
		Visible = true;
		_darkness = 0f;
		_state = EState.FadingOut;
		_lastRealMs = Time.GetTicksMsec();
		if (promptRoot != null)
		{
			promptRoot.Visible = false;
		}
	}

	// A quit to menu mid-death never reaches FinishFadeIn.
	public override void _ExitTree()
	{
		AudioVolume.ClearDuck(this);
	}

	public override void _Process(double delta)
	{
		if (_state == EState.Hidden)
		{
			return;
		}

		// Wall-clock delta (see _lastRealMs) so the slow-mo death cam's
		// Engine.TimeScale doesn't stretch the fade. Advanced every frame —
		// including the Prompt wait — so the FadingIn handoff starts fresh.
		ulong nowMs = Time.GetTicksMsec();
		float dt = (nowMs - _lastRealMs) / 1000f;
		_lastRealMs = nowMs;
		switch (_state)
		{
			case EState.FadingOut:
			{
				float step = fadeOutSeconds > 0f ? dt / fadeOutSeconds : 1f;
				_darkness = Mathf.Min(1f, _darkness + step);
				ApplyDarkness();
				if (_darkness >= 1f)
				{
					_state = EState.Prompt;
					if (promptRoot != null)
					{
						promptRoot.Visible = true;
					}				}
				break;
			}
			case EState.Settling:
			{
				if (!(_gameClient?.WorldSettling ?? false))
				{
					_state = EState.FadingIn;
				}
				break;
			}
			case EState.FadingIn:
			{
				float step = fadeInSeconds > 0f ? dt / fadeInSeconds : 1f;
				_darkness = Mathf.Max(0f, _darkness - step);
				ApplyDarkness();
				if (_darkness <= 0f)
				{
					FinishFadeIn();
				}
				break;
			}
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (_state != EState.Prompt)
		{
			return;
		}
		if (e.IsActionPressed("ui_accept"))
		{
			GetViewport().SetInputAsHandled();
			BeginRespawn();
		}
	}

	void BeginRespawn()
	{
		if (_gameClient == null)
		{
			return;
		}
		// The whole wake happens synchronously here; Settling then holds black
		// until the world around the campfire has spawned, so the first frame of
		// the fade-in already shows it complete. Input stays suppressed by
		// GameClient for the full window.
		_gameClient.RespawnAtHome();
		if (promptRoot != null)
		{
			promptRoot.Visible = false;
		}
		_state = EState.Settling;
	}

	void FinishFadeIn()
	{
		_state = EState.Hidden;
		_darkness = 0f;
		ApplyDarkness();
		Visible = false;
		AudioVolume.ClearDuck(this);
		// The campfire is revealed — the camp screen takes the input gate from here.
		_gameClient?.OnRespawnRevealed();
	}

	void ApplyDarkness()
	{
		if (overlay != null)
		{
			Color c = overlay.Color;
			c.A = Mathf.Clamp(_darkness, 0f, 1f);
			overlay.Color = c;
		}
		AudioVolume.SetDuck(this, ESfxBus.All, audioFadeFloorDb * Mathf.Clamp(_darkness, 0f, 1f));
	}
}
