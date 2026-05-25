using Godot;

// Full-screen loading overlay for the new-game / load-game sequence.
// Lives at the Main level (not inside game.tscn) so it can show before
// worldgen runs and persist across the menu→game scene swap. Main drives
// the early phases (assets, worldgen, scene load); GameClient takes over
// once the game scene is instantiated and drives the chunk-fill and
// entity-drain phases. HideWithFade ramps the overlay + world audio in
// over fadeOutSeconds and QueueFrees the screen when done.
[GlobalClass]
public partial class LoadingScreen : CanvasLayer
{
	[Export] public ColorRect overlay;
	[Export] public Label statusLabel;
	[Export] public ProgressBar progressBar;
	[Export(PropertyHint.Range, "0.1,5,0.05")] public float fadeOutSeconds = 0.5f;
	// Decibels the World3D + Ambience2D buses are dropped to while the
	// screen is up. -80 dB is Godot's effective silence floor; matches the
	// DeathScreen audio fade so a respawn sequence and a fresh-spawn
	// sequence sound the same.
	[Export(PropertyHint.Range, "-80,0,0.5")] public float audioFadeFloorDb = -80f;

	enum EState
	{
		Hidden,
		Visible,
		FadingOut,
	}

	EState _state = EState.Hidden;
	float _darkness = 1f;
	bool _audioBaselineCaptured;
	int _world3DBusIdx = -1;
	int _ambience2DBusIdx = -1;
	float _world3DBaselineDb;
	float _ambience2DBaselineDb;

	public override void _Ready()
	{
		Visible = false;
	}

	public void Show(string status = "Loading...")
	{
		_state = EState.Visible;
		_darkness = 1f;
		Visible = true;
		SetStatus(status);
		SetProgress(0f);
		CaptureAudioBaseline();
		ApplyDarkness();
	}

	public void SetProgress(float frac, string status = null)
	{
		if (progressBar != null)
		{
			progressBar.Value = Mathf.Clamp(frac, 0f, 1f) * 100f;
		}
		if (status != null)
		{
			SetStatus(status);
		}
	}

	public void SetStatus(string status)
	{
		if (statusLabel != null && status != null)
		{
			statusLabel.Text = status;
		}
	}

	// Begins the opaque → transparent ramp. _Process drives the fade and
	// QueueFrees the screen once it hits 0.
	public void HideWithFade()
	{
		if (_state != EState.Visible)
		{
			return;
		}
		_state = EState.FadingOut;
	}

	public override void _Process(double delta)
	{
		if (_state != EState.FadingOut)
		{
			return;
		}
		float step = fadeOutSeconds > 0f ? (float)delta / fadeOutSeconds : 1f;
		_darkness = Mathf.Max(0f, _darkness - step);
		ApplyDarkness();
		if (_darkness <= 0f)
		{
			RestoreAudioBaseline();
			QueueFree();
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
		if (statusLabel != null)
		{
			Color c = statusLabel.Modulate;
			c.A = Mathf.Clamp(_darkness, 0f, 1f);
			statusLabel.Modulate = c;
		}
		if (progressBar != null)
		{
			Color c = progressBar.Modulate;
			c.A = Mathf.Clamp(_darkness, 0f, 1f);
			progressBar.Modulate = c;
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
