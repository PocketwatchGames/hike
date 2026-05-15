using Godot;
using System;

// HUD banner that announces the named region the player just entered.
// Driven by the Hud announcement queue: the queue calls Show with the
// region payload and an onDone callback that advances to the next entry
// once the fade-out completes. The visual style (font, fade timings,
// color) and entry sound are baked into the banner scene itself —
// authoring a new look means editing region_banner.tscn, not the data.
// RegionData supplies only the displayName text shown on the label.
public partial class HudRegionBanner : Control
{
	[Export] public Label label;
	[Export] public AudioStreamPlayer sound;

	// Tween phases. Held in seconds and exposed so the scene can tune
	// the feel without touching code. Total visible time =
	// fadeIn + hold + fadeOut.
	[Export] public float fadeInSeconds = 0.5f;
	[Export] public float holdSeconds = 2.0f;
	[Export] public float fadeOutSeconds = 1.0f;

	Tween _tween;
	Action _onDone;

	public override void _Ready()
	{
		Modulate = new Color(Modulate.R, Modulate.G, Modulate.B, 0f);
		Visible = false;
	}

	public void Show(RegionData region, Action onDone = null)
	{
		if (region == null)
		{
			onDone?.Invoke();
			return;
		}
		if (label != null)
		{
			label.Text = region.displayName.ToString();
		}
		// If a previous banner is still in flight, drop its callback so the
		// queue advances exactly once per Show — the new banner takes over
		// the slot. The new onDone fires when the new tween completes.
		_onDone = onDone;
		Visible = true;
		sound?.Play();

		_tween?.Kill();
		_tween = CreateTween();
		_tween.TweenProperty(this, "modulate:a", 1f, fadeInSeconds);
		_tween.TweenInterval(holdSeconds);
		_tween.TweenProperty(this, "modulate:a", 0f, fadeOutSeconds);
		_tween.TweenCallback(Callable.From(OnTweenComplete));
	}

	void OnTweenComplete()
	{
		Visible = false;
		Action cb = _onDone;
		_onDone = null;
		cb?.Invoke();
	}
}
