using Godot;
using System;
using System.Text;

// Full-screen story card (a StoryData) over a paused, silent world. GameClient
// pauses the tree and holds InputSuppressed before Show; this node runs with
// ProcessMode Always. The picture and text fade in, the player clicks (or
// presses ui_accept) through, onContinue hands the world back, and the whole
// card fades out over it while the world audio ramps up.
[GlobalClass]
public partial class StoryScreen : Control
{
	[Export] public Control content;
	[Export] public TextureRect image;
	[Export] public Label text;
	[Export] public ButtonHint continueHint;
	[Export(PropertyHint.Range, "0,5,0.05")] public float fadeInSeconds = 1.0f;
	[Export(PropertyHint.Range, "0,5,0.05")] public float fadeOutSeconds = 1.0f;
	// Duck (dB) on the World3D + Ambience2D buses while the card is opaque;
	// matches LoadingScreen so the hand-off between the two is silent.
	[Export(PropertyHint.Range, "-80,0,0.5")] public float audioFadeFloorDb = -80f;

	enum EState
	{
		Hidden,
		FadingIn,
		Waiting,
		FadingOut,
	}

	EState _state = EState.Hidden;
	float _fade;
	Action _onContinue;

	public override void _Ready()
	{
		Visible = false;
		continueHint?.SetHint("ui_accept", Loc.Get(Loc.Keys.story_continue));
	}

	public void Show(StoryData story, Action onContinue)
	{
		_onContinue = onContinue;
		if (image != null)
		{
			image.Texture = story.image;
			image.Visible = story.image != null;
		}
		if (text != null)
		{
			text.Text = ComposeText(story);
		}
		if (continueHint != null)
		{
			continueHint.Visible = false;
		}
		Visible = true;
		MouseFilter = MouseFilterEnum.Stop;
		Modulate = Colors.White;
		_fade = 0f;
		SetContentAlpha(0f);
		AudioVolume.SetDuck(this, ESfxBus.All, audioFadeFloorDb);
		_state = EState.FadingIn;
	}

	static string ComposeText(StoryData story)
	{
		var sb = new StringBuilder();
		foreach (StringName key in story.paragraphs)
		{
			if (sb.Length > 0)
			{
				sb.Append("\n\n");
			}
			sb.Append(Loc.Get(key));
		}
		return sb.ToString();
	}

	public override void _ExitTree()
	{
		AudioVolume.ClearDuck(this);
	}

	public override void _Process(double delta)
	{
		switch (_state)
		{
			case EState.FadingIn:
			{
				_fade = fadeInSeconds > 0f ? Mathf.Min(1f, _fade + (float)delta / fadeInSeconds) : 1f;
				SetContentAlpha(_fade);
				if (_fade >= 1f)
				{
					_state = EState.Waiting;
					if (continueHint != null)
					{
						continueHint.Visible = true;
					}
				}
				break;
			}
			case EState.FadingOut:
			{
				_fade = fadeOutSeconds > 0f ? Mathf.Max(0f, _fade - (float)delta / fadeOutSeconds) : 0f;
				Modulate = new Color(1f, 1f, 1f, _fade);
				AudioVolume.SetDuck(this, ESfxBus.All, audioFadeFloorDb * _fade);
				if (_fade <= 0f)
				{
					_state = EState.Hidden;
					Visible = false;
					AudioVolume.ClearDuck(this);
				}
				break;
			}
		}
	}

	// Mouse clicks land here: the card is the topmost full-screen Control.
	public override void _GuiInput(InputEvent e)
	{
		if (e is InputEventMouseButton { Pressed: true })
		{
			AcceptEvent();
			Continue();
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (_state != EState.Hidden && e.IsActionPressed("ui_accept"))
		{
			GetViewport().SetInputAsHandled();
			Continue();
		}
	}

	// Ignored until the card has fully faded in, so the click or key press that
	// started the game can't skip a story nobody has seen yet.
	void Continue()
	{
		if (_state != EState.Waiting)
		{
			return;
		}
		_state = EState.FadingOut;
		_fade = 1f;
		MouseFilter = MouseFilterEnum.Ignore;
		Action onContinue = _onContinue;
		_onContinue = null;
		onContinue?.Invoke();
	}

	void SetContentAlpha(float alpha)
	{
		if (content != null)
		{
			content.Modulate = new Color(1f, 1f, 1f, alpha);
		}
	}
}
