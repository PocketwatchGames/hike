using Godot;

// One of StoryScreen's two page layers; the screen crossfades between them.
[GlobalClass]
public partial class StoryPageView : Control
{
	[Export] public TextureRect image;
	[Export] public Label text;

	public void ShowPage(StoryPageData page)
	{
		if (image != null)
		{
			image.Texture = page?.image;
			image.Visible = page?.image != null;
		}
		if (text != null)
		{
			StringName key = page?.text;
			text.Text = key is null || key.IsEmpty ? "" : Loc.Get(key);
		}
	}

	public void SetAlpha(float alpha)
	{
		Modulate = new Color(1f, 1f, 1f, alpha);
	}
}
