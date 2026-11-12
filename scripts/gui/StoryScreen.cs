using Godot;
using System;

// Full-screen story (a StoryData) over a paused, silent world. GameClient
// pauses the tree and holds InputSuppressed before Show; this node runs with
// ProcessMode Always. The first picture fades in from black and its text types
// in. A press while typing reveals the rest; a press on a finished page fades
// the text out, then types the next page's text in while its picture
// crossfades over the last. On the last page onContinue hands the world back
// and the whole card fades out over it while the world audio ramps up.
[GlobalClass]
public partial class StoryScreen : Control
{
	// Fades in from black as a whole.
	[Export] public Control content;
	// Two stacked layers so a picture can be drawn over the one it replaces.
	[Export] public TextureRect imageA;
	[Export] public TextureRect imageB;
	// Holds the whole page in a transparent colour so the box is laid out at
	// its final size from the first character; `text` fills it, left-aligned,
	// so typing never re-centres a line. It must stay VISIBLE (a hidden control
	// reserves no size) and be cleared by font colour, not Modulate, which
	// would also hide `text`.
	[Export] public Label textSizer;
	[Export] public Label text;
	[Export] public ButtonHint continueHint;
	// The box shrinks to a short page's width, centred, and wraps past this.
	[Export(PropertyHint.Range, "100,1920,1")] public float maxTextWidth = 1100f;
	// Reserved even for a one-line page, so the picture doesn't move between pages.
	[Export(PropertyHint.Range, "0,1080,1")] public float minTextHeight = 160f;
	[Export(PropertyHint.Range, "0,5,0.05")] public float fadeInSeconds = 1.0f;
	[Export(PropertyHint.Range, "0,5,0.05")] public float textFadeOutSeconds = 0.5f;
	[Export(PropertyHint.Range, "0,5,0.05")] public float imageCrossfadeSeconds = 0.75f;
	[Export(PropertyHint.Range, "0,5,0.05")] public float fadeOutSeconds = 1.0f;
	// Duck (dB) on the World3D + Ambience2D buses while the card is opaque;
	// matches LoadingScreen so the hand-off between the two is silent.
	[Export(PropertyHint.Range, "-80,0,0.5")] public float audioFadeFloorDb = -80f;

	enum EState
	{
		Hidden,
		FadingIn,
		Typing,
		Waiting,
		TextFadingOut,
		FadingOut,
	}

	EState _state = EState.Hidden;
	float _fade;
	Action _onContinue;
	StoryData _story;
	int _pageIndex;
	float _revealedChars;
	int _textLength;
	// The picture on screen, and the one fading in over it (null when idle).
	TextureRect _frontImage;
	TextureRect _incomingImage;
	float _imageFade;

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
		StoryPageData page = PageAt(0);

		_frontImage = imageA;
		_incomingImage = null;
		SetImage(imageA, page?.image, 1f);
		SetImage(imageB, null, 0f);
		LoadText(page);

		SetHintVisible(false);
		Visible = true;
		MouseFilter = MouseFilterEnum.Stop;
		Modulate = Colors.White;
		_fade = 0f;
		SetAlpha(content, 0f);
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
		StepImageCrossfade(delta);
		switch (_state)
		{
			case EState.FadingIn:
			{
				_fade = Step(_fade, delta, fadeInSeconds);
				SetAlpha(content, _fade);
				if (_fade >= 1f)
				{
					_state = EState.Typing;
				}
				break;
			}
			case EState.Typing:
			{
				// The player's reading speed, shared with conversations; <= 0 is instant.
				float speed = CVars.dialogueTypingSpeed.Value;
				_revealedChars = speed > 0f ? _revealedChars + speed * (float)delta : _textLength;
				RevealText();
				break;
			}
			case EState.TextFadingOut:
			{
				_fade = Step(_fade, delta, textFadeOutSeconds);
				SetAlpha(text, 1f - _fade);
				if (_fade >= 1f)
				{
					BeginNextPage();
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

	void LoadText(StoryPageData page)
	{
		_revealedChars = 0f;
		_textLength = 0;
		if (text == null)
		{
			return;
		}
		StringName key = page?.text;
		string line = key is null || key.IsEmpty ? "" : Loc.Get(key);
		SizeTextBox(line);
		text.Text = line;
		text.VisibleCharacters = 0;
		_textLength = text.GetTotalCharacterCount();
		SetAlpha(text, 1f);
	}

	// The unwrapped line's width, capped at maxTextWidth; both labels then wrap
	// at that same width, so their lines break identically.
	void SizeTextBox(string line)
	{
		if (textSizer == null)
		{
			return;
		}
		textSizer.Text = line;
		textSizer.AutowrapMode = TextServer.AutowrapMode.Off;
		float width = Mathf.Min(Mathf.Ceil(textSizer.GetMinimumSize().X), maxTextWidth);
		textSizer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		textSizer.CustomMinimumSize = new Vector2(width, minTextHeight);
	}

	void RevealText()
	{
		if (_revealedChars >= _textLength)
		{
			_revealedChars = _textLength;
			_state = EState.Waiting;
			SetHintVisible(true);
		}
		if (text != null)
		{
			text.VisibleCharacters = _state == EState.Waiting ? -1 : Mathf.FloorToInt(_revealedChars);
		}
	}

	// The old text is gone: start the next picture fading in over the current
	// one and type the next text in while it does.
	void BeginNextPage()
	{
		_pageIndex++;
		StoryPageData page = PageAt(_pageIndex);
		Texture2D next = page?.image;
		if (_frontImage != null && next != _frontImage.Texture)
		{
			_incomingImage = _frontImage == imageA ? imageB : imageA;
			SetImage(_incomingImage, next, 0f);
			_incomingImage.MoveToFront();
			_imageFade = 0f;
		}
		LoadText(page);
		_state = EState.Typing;
	}

	// The incoming picture is drawn OVER the current one, which stays opaque
	// until the end — a true crossfade, with no dip to black halfway. Fading to
	// no picture fades the current one out instead.
	void StepImageCrossfade(double delta)
	{
		if (_incomingImage == null)
		{
			return;
		}
		_imageFade = Step(_imageFade, delta, imageCrossfadeSeconds);
		SetAlpha(_incomingImage, _imageFade);
		if (_incomingImage.Texture == null)
		{
			SetAlpha(_frontImage, 1f - _imageFade);
		}
		if (_imageFade >= 1f)
		{
			SetImage(_frontImage, null, 0f);
			_frontImage = _incomingImage;
			_incomingImage = null;
		}
	}

	// Mouse clicks land here: the card is the topmost full-screen Control.
	public override void _GuiInput(InputEvent e)
	{
		if (e is InputEventMouseButton { Pressed: true })
		{
			AcceptEvent();
			Advance();
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (_state != EState.Hidden && e.IsActionPressed("ui_accept"))
		{
			GetViewport().SetInputAsHandled();
			Advance();
		}
	}

	// A press while typing reveals the rest of the page. Presses during any
	// fade are ignored, so the press that started the game (or a double-click)
	// can't skip a page nobody has seen.
	void Advance()
	{
		if (_state == EState.Typing)
		{
			_revealedChars = _textLength;
			RevealText();
			return;
		}
		if (_state != EState.Waiting)
		{
			return;
		}
		SetHintVisible(false);
		_fade = 0f;

		if (_story != null && _pageIndex + 1 < _story.pages.Count)
		{
			_state = EState.TextFadingOut;
			return;
		}

		_state = EState.FadingOut;
		_fade = 1f;
		MouseFilter = MouseFilterEnum.Ignore;
		Action onContinue = _onContinue;
		_onContinue = null;
		onContinue?.Invoke();
	}

	static void SetImage(TextureRect rect, Texture2D texture, float alpha)
	{
		if (rect == null)
		{
			return;
		}
		rect.Texture = texture;
		SetAlpha(rect, alpha);
	}

	static void SetAlpha(CanvasItem item, float alpha)
	{
		if (item != null)
		{
			item.Modulate = new Color(1f, 1f, 1f, alpha);
		}
	}

	void SetHintVisible(bool visible)
	{
		if (continueHint != null)
		{
			continueHint.Visible = visible;
		}
	}
}
