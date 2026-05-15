using Godot;
using System;

// Generic upper-quarter-of-screen announcement surface used for everything
// the Hud queue dispatches that ISN'T a region entry — recipe discovery,
// item identification, language learning, future level-up / boss intros.
// Lives on the AnnouncementCanvas (CanvasLayer above all modals, below the
// pause menu) so it remains visible regardless of which screen is open.
//
// The Hud owns the queue; this script owns one in-flight presentation.
// Show(announcement, onDone) plays the entry sound, fades in, holds, fades
// out, and invokes onDone exactly once so the queue can advance. A new
// Show while a previous animation is in flight kills the previous tween,
// drops its callback, and starts the new entry — the queue still gets
// exactly one onDone for the new entry.
//
// The optional almanac shortcut surfaces as a ButtonHint along the
// bottom of the panel. While the hint is visible, _UnhandledInput peeks
// the bound action and routes a press through GameClient.OpenAlmanac;
// the action lives on the panel's [Export] so authors can rebind without
// touching code. Hidden entirely when announcement.showAlmanacHint is
// false.
[GlobalClass]
public partial class HudAnnouncementPanel : Control
{
	[Export] public Label titleLabel;
	[Export] public Label subtitleLabel;
	[Export] public TextureRect iconRect;
	[Export] public ButtonHint almanacHint;
	[Export] public AudioStreamPlayer sound;

	// Tween phases. Match the region banner's defaults so the two
	// surfaces feel like one announcement system. Authors can override
	// per-instance in the .tscn.
	[Export] public float fadeInSeconds = 0.4f;
	[Export] public float holdSeconds = 3.0f;
	[Export] public float fadeOutSeconds = 0.8f;

	// Input action whose press, while the almanac hint is visible, opens
	// the announcement's target almanac tab. "Map" reuses the button
	// players already associate with the almanac — pressing it during a
	// recipe announcement opens the Recipe tab instead of WorldMap.
	[Export] public string almanacHintAction = "Map";

	Tween _tween;
	Action _onDone;
	GameClient _gameClient;
	AlmanacScreen.EAlmanacTab _pendingTab;
	MobData _pendingFocusMob;
	bool _hintActive;

	public override void _Ready()
	{
		Modulate = new Color(Modulate.R, Modulate.G, Modulate.B, 0f);
		Visible = false;
		if (almanacHint != null)
		{
			almanacHint.Visible = false;
		}
	}

	public void Bind(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public void Show(Announcement a, Action onDone = null)
	{
		if (a == null)
		{
			onDone?.Invoke();
			return;
		}

		if (titleLabel != null)
		{
			titleLabel.Text = a.title ?? string.Empty;
		}
		if (subtitleLabel != null)
		{
			subtitleLabel.Text = a.subtitle ?? string.Empty;
			subtitleLabel.Visible = !string.IsNullOrEmpty(a.subtitle);
		}
		if (iconRect != null)
		{
			iconRect.Texture = a.icon;
			iconRect.Visible = a.icon != null;
		}

		_hintActive = a.showAlmanacHint;
		_pendingTab = a.almanacTab;
		_pendingFocusMob = a.almanacFocusMob;
		if (almanacHint != null)
		{
			almanacHint.Visible = _hintActive;
			if (_hintActive)
			{
				almanacHint.SetHint(almanacHintAction, a.almanacHintLabel ?? string.Empty);
			}
		}

		// Per-announcement sound override beats the scene-baked default —
		// authors can leave the player empty and route audio entirely
		// through the announcement payload.
		if (sound != null)
		{
			if (a.sound != null)
			{
				sound.Stream = a.sound;
			}
			sound.Play();
		}

		_onDone = onDone;
		Visible = true;

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
		_hintActive = false;
		if (almanacHint != null)
		{
			almanacHint.Visible = false;
		}
		Action cb = _onDone;
		_onDone = null;
		cb?.Invoke();
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!_hintActive || _gameClient == null)
		{
			return;
		}
		if (string.IsNullOrEmpty(almanacHintAction))
		{
			return;
		}
		if (e.IsActionPressed(almanacHintAction))
		{
			_gameClient.OpenAlmanac(_pendingTab, _pendingFocusMob);
			GetViewport().SetInputAsHandled();
		}
	}
}
