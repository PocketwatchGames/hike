using Godot;

// One entry in the HUD's status-effect strip. Renders a single icon for one
// StatusEffectData, with a count badge when multiple instances of the same
// data are stacked and a progress bar showing the time-to-removal of the
// instance closest to expiring. Hud owns instantiation + grouping; this class
// is a passive view.
[GlobalClass]
public partial class StatusEffectHud : BoxContainer
{
	[Export] TextureRect _icon;
	[Export] Label _count;
	[Export] ProgressBar _progressBar;
	[Export] Control _countContainer;

	public StatusEffectData Data { get; private set; }

	public void Set(StatusEffectData data, int count, float removalProgress, bool hasTimer)
	{
		Data = data;
		if (_icon != null)
		{
			_icon.Texture = data?.icon;
		}
		if (_count != null)
		{
			_count.Text = count.ToString();
		}
		if (_countContainer != null)
		{
			_countContainer.Visible = count > 1;
		}
		if (_progressBar != null)
		{
			_progressBar.Visible = hasTimer;
			if (hasTimer)
			{
				_progressBar.MinValue = 0;
				_progressBar.MaxValue = 1;
				_progressBar.Value = Mathf.Clamp(removalProgress, 0f, 1f);
			}
		}
	}
}
