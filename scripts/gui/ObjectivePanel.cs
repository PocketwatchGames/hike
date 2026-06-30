using Godot;
using System;

// One transient combat-objective row: a species the player is currently
// fighting, showing its name and bestiary-level kill progress. Spawned and
// refreshed by Hud on combat engagement (player attacks the mob or is attacked
// by it). Holds for _holdSeconds after the most recent engagement, then fades
// out and frees itself, notifying Hud via OnDismiss so it drops its entry.
public partial class ObjectivePanel : PanelContainer
{
	[Export] private ProgressBar _progressBar;
	[Export] private Label _titleLabel;
	[Export] private Label _countLabel;
	[Export] private Node _countContainer;

	// Seconds the panel stays fully visible after the most recent engagement
	// before it begins to fade.
	[Export] private float _holdSeconds = 30f;
	// Seconds the fade-out takes once the hold window elapses.
	[Export] private float _fadeSeconds = 1f;

	// Invoked just before the panel frees itself so the owning Hud can drop its
	// dictionary entry. Cleared after firing so it runs at most once.
	public Action OnDismiss;

	// Wall-clock countdown — purely presentational HUD timing, so it runs on
	// _Process delta rather than the sim clock. Counts down hold + fade; the
	// fade window is the final _fadeSeconds of it.
	private float _remaining;

	public override void _Process(double delta)
	{
		if (_remaining <= 0f)
		{
			return;
		}
		_remaining -= (float)delta;
		if (_remaining <= 0f)
		{
			Dismiss();
			return;
		}
		float alpha = _remaining >= _fadeSeconds ? 1f : _remaining / _fadeSeconds;
		Modulate = new Color(1f, 1f, 1f, alpha);
	}

	// (Re)bind the panel to a species' current progress and restart the hold
	// timer at full opacity. `countText` is the kill readout ("3/5" or the
	// localized "MAX LEVEL"); `fraction` is the 0..1 bar fill within the level.
	public void Set(string title, float fraction, string countText)
	{
		if (_titleLabel != null)
		{
			_titleLabel.Text = title;
		}
		if (_countLabel != null)
		{
			_countLabel.Text = countText;
		}
		if (_progressBar != null)
		{
			_progressBar.MinValue = 0;
			_progressBar.MaxValue = 1;
			_progressBar.Value = fraction;
		}
		_remaining = _holdSeconds + _fadeSeconds;
		Modulate = Colors.White;
	}

	private void Dismiss()
	{
		Action cb = OnDismiss;
		OnDismiss = null;
		cb?.Invoke();
		QueueFree();
	}
}
