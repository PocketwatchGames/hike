using Godot;
using System;

// Full-screen death overlay. GameClient.Show(this) starts the FadingOut
// phase: the black ColorRect ramps from invisible to opaque and the World3D
// + Ambience2D audio buses fade to silence. Once opaque, the "YOU DIED"
// prompt + Respawn button hint appear and the screen accepts ui_accept.
// On press the player is respawned (camera teleport handled by GameClient),
// the prompt hides, and the FadingIn phase ramps everything back up over
// `fadeInSeconds`. InputSuppressed is held on GameClient for the entire
// life of the screen so gameplay input is rejected throughout.
[GlobalClass]
public partial class DeathScreen : Control
{
	[Export] public ColorRect overlay;
	[Export] public Control promptRoot;
	[Export] public Label titleLabel;
	[Export] public ButtonHint respawnHint;
	[Export(PropertyHint.Range, "0.1,5,0.05")] public float fadeOutSeconds = 1.0f;
	[Export(PropertyHint.Range, "0.1,5,0.05")] public float fadeInSeconds = 1.0f;
	// Decibels the World3D + Ambience2D buses are dropped to at full fade.
	// -80 dB is Godot's effective silence floor.
	[Export(PropertyHint.Range, "-80,0,0.5")] public float audioFadeFloorDb = -80f;

	public enum EState
	{
		Hidden,
		FadingOut,
		Prompt,
		FadingIn,
	}

	GameClient _gameClient;
	EState _state = EState.Hidden;
	float _darkness;
	bool _audioBaselineCaptured;
	int _world3DBusIdx = -1;
	int _ambience2DBusIdx = -1;
	float _world3DBaselineDb;
	float _ambience2DBaselineDb;

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
		if (promptRoot != null)
		{
			promptRoot.Visible = false;
		}
		CaptureAudioBaseline();
	}

	public override void _Process(double delta)
	{
		if (_state == EState.Hidden)
		{
			return;
		}

		float dt = (float)delta;
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
					}
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
		// Player teleport + camera snap happen synchronously here so the first
		// frame of the fade-in already shows the spawn point. Input stays
		// suppressed by GameClient for the full fade-in window.
		_gameClient.RespawnPlayer();
		if (promptRoot != null)
		{
			promptRoot.Visible = false;
		}
		_state = EState.FadingIn;
	}

	void FinishFadeIn()
	{
		_state = EState.Hidden;
		_darkness = 0f;
		ApplyDarkness();
		Visible = false;
		RestoreAudioBaseline();
		if (_gameClient != null)
		{
			_gameClient.InputSuppressed = false;
		}
	}

	void ApplyDarkness()
	{
		if (overlay != null)
		{
			Color c = overlay.Color;
			c.A = Mathf.Clamp(_darkness, 0f, 1f);
			overlay.Color = c;
		}
		if (_audioBaselineCaptured)
		{
			if (_world3DBusIdx >= 0)
			{
				float db = Mathf.Lerp(_world3DBaselineDb, audioFadeFloorDb, _darkness);
				AudioServer.SetBusVolumeDb(_world3DBusIdx, db);
			}
			if (_ambience2DBusIdx >= 0)
			{
				float db = Mathf.Lerp(_ambience2DBaselineDb, audioFadeFloorDb, _darkness);
				AudioServer.SetBusVolumeDb(_ambience2DBusIdx, db);
			}
		}
	}

	void CaptureAudioBaseline()
	{
		if (_audioBaselineCaptured)
		{
			return;
		}
		_world3DBusIdx = AudioServer.GetBusIndex("World3D");
		_ambience2DBusIdx = AudioServer.GetBusIndex("Ambience2D");
		if (_world3DBusIdx >= 0)
		{
			_world3DBaselineDb = AudioServer.GetBusVolumeDb(_world3DBusIdx);
		}
		if (_ambience2DBusIdx >= 0)
		{
			_ambience2DBaselineDb = AudioServer.GetBusVolumeDb(_ambience2DBusIdx);
		}
		_audioBaselineCaptured = true;
	}

	void RestoreAudioBaseline()
	{
		if (!_audioBaselineCaptured)
		{
			return;
		}
		if (_world3DBusIdx >= 0)
		{
			AudioServer.SetBusVolumeDb(_world3DBusIdx, _world3DBaselineDb);
		}
		if (_ambience2DBusIdx >= 0)
		{
			AudioServer.SetBusVolumeDb(_ambience2DBusIdx, _ambience2DBaselineDb);
		}
		_audioBaselineCaptured = false;
	}
}
