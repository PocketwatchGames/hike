using Godot;
using System.Collections.Generic;

// Centre-screen banner for things the party has just LEARNED (a recipe, a
// language, an identified item). Owns its own queue: each
// announcement fades in, holds, fades out, and only then does the next one
// start, so a burst of discoveries reads one at a time instead of overwriting.
//
// Presentational, so timing is the node's tween (wall clock) — not dragged by
// slow-mo. Visual style lives on the scene; only the text comes from the data.
[GlobalClass]
public partial class HudAnnouncementBanner : PanelContainer
{
	[Export] public Label label;
	// Optional default entry sound; an Announcement.sound overrides it.
	[Export] public AudioStreamPlayer sound;

	[Export(PropertyHint.Range, "0,5,0.05")] public float fadeInSeconds = 0.4f;
	[Export(PropertyHint.Range, "0,20,0.1")] public float holdSeconds = 2.5f;
	[Export(PropertyHint.Range, "0,5,0.05")] public float fadeOutSeconds = 0.8f;

	readonly Queue<Announcement> _queue = new();
	Tween _tween;
	bool _showing;
	AudioStream _defaultStream;

	public override void _Ready()
	{
		_defaultStream = sound?.Stream;
		Modulate = new Color(Modulate.R, Modulate.G, Modulate.B, 0f);
		Visible = false;
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public void Enqueue(Announcement a)
	{
		if (a == null)
		{
			return;
		}
		_queue.Enqueue(a);
		if (!_showing)
		{
			ShowNext();
		}
	}

	void ShowNext()
	{
		if (_queue.Count == 0)
		{
			_showing = false;
			Visible = false;
			return;
		}
		_showing = true;
		Announcement a = _queue.Dequeue();
		if (label != null)
		{
			label.Text = FormatText(a);
		}
		Visible = true;
		PlaySound(a.sound);

		_tween?.Kill();
		_tween = CreateTween();
		_tween.TweenProperty(this, "modulate:a", 1f, fadeInSeconds);
		_tween.TweenInterval(holdSeconds);
		_tween.TweenProperty(this, "modulate:a", 0f, fadeOutSeconds);
		// Deferred: starting the next tween from inside this one's completion
		// callback would kill the tween that is still finishing.
		_tween.TweenCallback(Callable.From(() => Callable.From(ShowNext).CallDeferred()));
	}

	void PlaySound(AudioStream overrideStream)
	{
		if (sound == null)
		{
			return;
		}
		sound.Stream = overrideStream ?? _defaultStream;
		if (sound.Stream != null)
		{
			sound.Play();
		}
	}

	// Title on the first line, subtitle (the thing learned) under it.
	static string FormatText(Announcement a)
	{
		string title = a.title ?? string.Empty;
		string subtitle = a.subtitle ?? string.Empty;
		if (string.IsNullOrEmpty(subtitle))
		{
			return title;
		}
		if (string.IsNullOrEmpty(title))
		{
			return subtitle;
		}
		return $"{title}\n{subtitle}";
	}
}
