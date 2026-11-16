using Godot;

// A panel that types a BBCode line out one character at a time, at
// CVars.dialogueTypingSpeed. The speech bubble's body and the merchant's
// remark box are both one.
//
// Size is reserved up front: `sizerLabel` holds the whole line, drawn
// transparent, and is what the panel lays out around; `textLabel` sits in the
// same PanelContainer cell and reveals through VisibleCharacters. The sizer
// must stay VISIBLE (a hidden control contributes no minimum size) — its
// transparency is authored as modulate alpha in the scene.
[GlobalClass]
public partial class TypingText : PanelContainer
{
	[Export] RichTextLabel sizerLabel;
	[Export] RichTextLabel textLabel;
	// Played once per frame that reveals a non-whitespace character. Leave
	// unset for a silent panel.
	[Export] AudioStreamPlayer typeTick;
	// Each tick's pitch is 1 ± this, so a long line doesn't drone.
	[Export(PropertyHint.Range, "0,0.5,0.01")] public float tickPitchJitter = 0.08f;

	// Widest the text runs before it wraps; shorter lines shrink the panel.
	[Export(PropertyHint.Range, "100,1600,1")] public float maxTextWidth = 560f;
	// Narrowest the text area gets, however short the line.
	[Export(PropertyHint.Range, "0,1600,1")] public float minTextWidth = 200f;

	float _revealedChars;
	int _lineLength;
	// The line with its BBCode stripped, indexed like VisibleCharacters.
	string _parsedText = string.Empty;

	public bool HasText { get; private set; }

	public bool IsFullyRevealed => _revealedChars >= _lineLength;

	// Loads a whole BBCode line with nothing revealed yet, and sizes the panel
	// for all of it. An empty line hides the panel until the next one.
	public void Begin(string bbcode)
	{
		_revealedChars = 0f;
		_lineLength = 0;
		HasText = !string.IsNullOrEmpty(bbcode);
		if (sizerLabel == null || textLabel == null)
		{
			return;
		}
		string text = bbcode ?? string.Empty;
		// Visible before measuring: a hidden container does not hear its
		// children's minimum size change and would keep the last line's size.
		Visible = HasText;
		sizerLabel.Text = text;
		textLabel.Text = text;
		textLabel.VisibleCharacters = 0;
		_lineLength = textLabel.GetTotalCharacterCount();
		_parsedText = textLabel.GetParsedText();
		FitSizer();
	}

	// Restyles both labels together — the sizer must measure in the face the
	// text is drawn in. Empty is the plain RichTextLabel style.
	public void SetTextVariation(StringName variation)
	{
		if (sizerLabel != null)
		{
			sizerLabel.ThemeTypeVariation = variation;
		}
		if (textLabel != null)
		{
			textLabel.ThemeTypeVariation = variation;
		}
	}

	public void RevealAll()
	{
		_revealedChars = _lineLength;
		if (textLabel != null)
		{
			textLabel.VisibleCharacters = -1;
		}
	}

	// Measures the line at the max width, then narrows to its widest wrapped
	// row. Done by setting the sizer's Size directly so the label shapes now,
	// rather than after the container's deferred sort — the panel is right on
	// its first drawn frame. +1 keeps float rounding from re-wrapping it.
	void FitSizer()
	{
		sizerLabel.CustomMinimumSize = Vector2.Zero;
		sizerLabel.Size = new Vector2(maxTextWidth, sizerLabel.Size.Y);
		float width = Mathf.Clamp(Mathf.Ceil(sizerLabel.GetContentWidth()) + 1f, Mathf.Min(minTextWidth, maxTextWidth), maxTextWidth);
		sizerLabel.Size = new Vector2(width, sizerLabel.Size.Y);
		float height = sizerLabel.GetContentHeight();
		sizerLabel.CustomMinimumSize = new Vector2(width, height);
	}

	public override void _Process(double delta)
	{
		if (!IsVisibleInTree() || _revealedChars >= _lineLength || textLabel == null)
		{
			return;
		}
		// Presentational, so wall clock.
		float speed = Mathf.Max(0f, CVars.dialogueTypingSpeed.Value);
		int before = Mathf.FloorToInt(_revealedChars);
		_revealedChars = Mathf.Min(_lineLength, _revealedChars + speed * (float)delta);
		int after = Mathf.FloorToInt(_revealedChars);
		textLabel.VisibleCharacters = _revealedChars >= _lineLength ? -1 : after;
		if (RevealsPrintable(before, after))
		{
			PlayTick();
		}
	}

	// Spaces and line breaks type silently, which gives the ticks the rhythm
	// of the words.
	bool RevealsPrintable(int from, int to)
	{
		int end = Mathf.Min(to, _parsedText.Length);
		for (int i = from; i < end; i++)
		{
			if (!char.IsWhiteSpace(_parsedText[i]))
			{
				return true;
			}
		}
		return false;
	}

	void PlayTick()
	{
		if (typeTick == null)
		{
			return;
		}
		typeTick.PitchScale = 1f + (float)GD.RandRange(-tickPitchJitter, tickPitchJitter);
		typeTick.Play();
	}
}
