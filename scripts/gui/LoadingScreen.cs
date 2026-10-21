using Godot;
using System.Diagnostics;

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
	// Duck (dB) on the World3D + Ambience2D buses while the screen is up.
	// -80 dB reaches Godot's silence floor from any setting; matches the
	// DeathScreen audio fade so a respawn sequence and a fresh-spawn
	// sequence sound the same.
	[Export(PropertyHint.Range, "-80,0,0.5")] public float audioFadeFloorDb = -80f;

	enum EState
	{
		Hidden,
		Visible,
		FadingOut,
	}

	// Started in Show() so the per-phase timing prints in Main.StartGame and
	// GameClient.Init can share a single wall-clock for the [Load] Total line.
	public Stopwatch LoadStopwatch { get; private set; }

	EState _state = EState.Hidden;
	float _darkness = 1f;

	public override void _Ready()
	{
		Visible = false;
	}

	public void Show(string status = "Loading...")
	{
		_state = EState.Visible;
		_darkness = 1f;
		Visible = true;
		LoadStopwatch = Stopwatch.StartNew();
		SetStatus(status);
		SetProgress(0f);
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
			QueueFree();
		}
	}

	// Not on fade completion: StartGame's failure paths free the screen
	// mid-load, and a duck left behind silences every later session.
	public override void _ExitTree()
	{
		AudioVolume.ClearDuck(this);
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
		AudioVolume.SetDuck(this, ESfxBus.All, audioFadeFloorDb * Mathf.Clamp(_darkness, 0f, 1f));
	}
}
