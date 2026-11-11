using Godot;
using System;

// Full-screen story (a StoryData) over a paused, silent world. GameClient
// pauses the tree and holds InputSuppressed before Show; this node runs with
// ProcessMode Always. The first page fades in from black, each click (or
// ui_accept) crossfades to the next page, and on the last page onContinue
// hands the world back and the whole card fades out over it while the world
// audio ramps up.
[GlobalClass]
public partial class StoryScreen : Control
{
	// Fades in from black as a whole; holds the two page layers.
	[Export] public Control content;
	// Two layers so a page can be drawn over the one it replaces.
	[Export] public StoryPageView pageA;
	[Export] public StoryPageView pageB;
	[Export] public ButtonHint continueHint;
	[Export(PropertyHint.Range, "0,5,0.05")] public float fadeInSeconds = 1.0f;
	[Export(PropertyHint.Range, "0,5,0.05")] public float crossfadeSeconds = 0.75f;
	[Export(PropertyHint.Range, "0,5,0.05")] public float fadeOutSeconds = 1.0f;
	// Duck (dB) on the World3D + Ambience2D buses while the card is opaque;
	// matches LoadingScreen so the hand-off between the two is silent.
	[Export(PropertyHint.Range, "-80,0,0.5")] public float audioFadeFloorDb = -80f;

	enum EState
	{
		Hidden,
		FadingIn,
		Waiting,
		Crossfading,
		FadingOut,
	}

	EState _state = EState.Hidden;
	float _fade;
	Action _onContinue;
	StoryData _story;
	int _pageIndex;
	StoryPageView _front;
	StoryPageView _back;

	public override void _Ready()
	{
		Visible = false;
		continueHint?.SetHint("ui_accept", Loc.Get(Loc.Keys.story_continue));
	}

	public void Show(StoryData story, Action onContinue)
	{
		_story = story;
		_onContinue = onContinue;
		_pageIndex = 0;
		_front = pageA;
		_back = pageB;
		_front.ShowPage(PageAt(0));
		_front.SetAlpha(1f);
		_back.SetAlpha(0f);
		SetHintVisible(false);
		Visible = true;
		MouseFilter = MouseFilterEnum.Stop;
		Modulate = Colors.White;
		_fade = 0f;
		SetContentAlpha(0f);
		AudioVolume.SetDuck(this, ESfxBus.All, audioFadeFloorDb);
		_state = EState.FadingIn;
	}

	StoryPageData PageAt(int index)
	{
		return _story != null && index < _story.pages.Count ? _story.pages[index] : null;
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
				_fade = Step(_fade, delta, fadeInSeconds);
				SetContentAlpha(_fade);
				if (_fade >= 1f)
				{
					EnterWaiting();
				}
				break;
			}
			case EState.Crossfading:
			{
				// The incoming page is drawn OVER the outgoing one, which stays
				// opaque until the end — a true crossfade, with no dip to black
				// halfway the way two opposing alphas would give.
				_fade = Step(_fade, delta, crossfadeSeconds);
				_back.SetAlpha(_fade);
				if (_fade >= 1f)
				{
					_front.SetAlpha(0f);
					(_front, _back) = (_back, _front);
					EnterWaiting();
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
					_story = null;
					AudioVolume.ClearDuck(this);
				}
				break;
			}
		}
	}

	static float Step(float fade, double delta, float seconds)
	{
		return seconds > 0f ? Mathf.Min(1f, fade + (float)delta / seconds) : 1f;
	}

	void EnterWaiting()
	{
		_state = EState.Waiting;
		SetHintVisible(true);
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

	// Ignored until the current page has fully faded in, so the press that
	// started the game (or a double-click) can't skip a page nobody has seen.
	void Continue()
	{
		if (_state != EState.Waiting)
		{
			return;
		}
		SetHintVisible(false);
		_fade = 0f;

		if (_story != null && _pageIndex + 1 < _story.pages.Count)
		{
			_pageIndex++;
			_back.ShowPage(PageAt(_pageIndex));
			_back.SetAlpha(0f);
			_back.MoveToFront();
			_state = EState.Crossfading;
			return;
		}

		_state = EState.FadingOut;
		_fade = 1f;
		MouseFilter = MouseFilterEnum.Ignore;
		Action onContinue = _onContinue;
		_onContinue = null;
		onContinue?.Invoke();
	}

	void SetHintVisible(bool visible)
	{
		if (continueHint != null)
		{
			continueHint.Visible = visible;
		}
	}

	void SetContentAlpha(float alpha)
	{
		if (content != null)
		{
			content.Modulate = new Color(1f, 1f, 1f, alpha);
		}
	}
}
