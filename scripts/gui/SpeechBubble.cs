using Godot;

// A comic speech bubble whose tail points at an IInteractive's hudPosition
// (above a speaker's head, a sign's plate): body, name tab, tail, and a continue hint.
// Runs the typewriter for whatever owns it (ConversationController) — the
// owner says what to show and when to advance; the bubble reveals, sizes and
// places itself.
//
// The root is a full-screen, input-ignoring Control, so its local space IS
// the space GameClient.ProjectToScreen returns; body, tab and hint are
// positioned in it by hand every frame.
//
// Size is reserved up front: `sizerLabel` holds the whole line, drawn
// transparent, and is what the body lays out around; `textLabel` sits in the
// same PanelContainer cell and reveals through VisibleCharacters. The sizer
// must stay VISIBLE (a hidden control contributes no minimum size) — its
// transparency is authored as modulate alpha in the scene.
[GlobalClass]
public partial class SpeechBubble : Control
{
	[Export] PanelContainer body;
	[Export] RichTextLabel sizerLabel;
	[Export] RichTextLabel textLabel;
	[Export] Control nameTab;
	[Export] Label nameLabel;
	// The framed continue hint (a ButtonHint inside its own backing panel).
	[Export] Control continueHint;
	// Drawn after the body so its fill covers the body's border where the
	// tail joins it.
	[Export] Control tail;

	// Widest the text runs before it wraps; shorter lines shrink the bubble.
	[Export(PropertyHint.Range, "100,1600,1")] public float maxTextWidth = 560f;
	// Narrowest the text area gets, so a one-word line still has room for the
	// tail and the continue hint along its bottom edge.
	[Export(PropertyHint.Range, "0,1600,1")] public float minTextWidth = 200f;
	// Screen pixels kept clear at every window edge.
	[Export(PropertyHint.Range, "0,200,1")] public float screenMargin = 24f;
	// Vertical distance from the anchor's projected point to the body's edge.
	[Export(PropertyHint.Range, "0,200,1")] public float tailLength = 36f;
	// Width of the tail where it meets the body.
	[Export(PropertyHint.Range, "4,200,1")] public float tailBaseWidth = 28f;
	// Name tab's top-left, relative to the body's top-left.
	[Export] public Vector2 nameTabOffset = new Vector2(16f, -28f);
	// Hint sits inside the body's bottom-right corner by this much, straddling
	// the bottom border vertically.
	[Export(PropertyHint.Range, "0,200,1")] public float continueHintInset = 16f;

	IInteractive _anchor;
	float _revealedChars;
	int _lineLength;
	bool _hasText;
	bool _flipped;
	Vector2 _tailTip;

	// While true, the hint shows once the line is fully revealed. The owner
	// turns it off while the player is choosing a response.
	public bool AwaitingContinue { get; set; }

	public bool IsFullyRevealed => _revealedChars >= _lineLength;

	// False once the anchor's node has been freed — the speaker died or its
	// chunk unloaded. The owner closes on it.
	public bool HasLiveAnchor => _anchor != null && (_anchor is not GodotObject obj || IsInstanceValid(obj));

	public override void _Ready()
	{
		Visible = false;
		MouseFilter = MouseFilterEnum.Ignore;
		if (tail != null)
		{
			tail.Draw += DrawTail;
		}
	}

	public void Open(IInteractive anchor)
	{
		_anchor = anchor;
		Visible = true;
	}

	public void Close()
	{
		Visible = false;
		_anchor = null;
		_hasText = false;
		AwaitingContinue = false;
	}

	// The tab shows `name`; null or empty hides it.
	public void SetSpeakerName(string name)
	{
		if (nameTab == null || nameLabel == null)
		{
			return;
		}
		bool has = !string.IsNullOrEmpty(name);
		nameLabel.Text = has ? name : string.Empty;
	}

	// Loads a whole BBCode line with nothing revealed yet, and sizes the body
	// for all of it. An empty line hides the bubble's body until the next one.
	public void Begin(string bbcode)
	{
		_revealedChars = 0f;
		_lineLength = 0;
		_hasText = !string.IsNullOrEmpty(bbcode);
		if (sizerLabel == null || textLabel == null || body == null)
		{
			return;
		}
		string text = bbcode ?? string.Empty;
		// Visible before measuring: a hidden container does not hear its
		// children's minimum size change and would keep the last line's size.
		body.Visible = _hasText;
		sizerLabel.Text = text;
		textLabel.Text = text;
		textLabel.VisibleCharacters = 0;
		_lineLength = textLabel.GetTotalCharacterCount();
		FitSizer();
		UpdateLayout();
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
	// rather than after the container's deferred sort — the bubble is right
	// on its first drawn frame. +1 keeps float rounding from re-wrapping it.
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
		if (!Visible)
		{
			return;
		}
		// Presentational, so wall clock — matches the old panel's typewriter.
		if (_revealedChars < _lineLength && textLabel != null)
		{
			float speed = Mathf.Max(0f, CVars.dialogueTypingSpeed.Value);
			_revealedChars = Mathf.Min(_lineLength, _revealedChars + speed * (float)delta);
			textLabel.VisibleCharacters = _revealedChars >= _lineLength ? -1 : Mathf.FloorToInt(_revealedChars);
		}
		UpdateLayout();
	}

	void UpdateLayout()
	{
		bool show = _hasText && HasLiveAnchor && body != null;
		if (body != null)
		{
			body.Visible = show;
		}
		if (tail != null)
		{
			tail.Visible = show;
		}
		if (nameTab != null && !show)
		{
			nameTab.Visible = false;
		}
		else if (nameTab != null)
		{
			nameTab.Visible = nameLabel != null && !string.IsNullOrEmpty(nameLabel.Text);
		}
		if (continueHint != null)
		{
			continueHint.Visible = show && AwaitingContinue && IsFullyRevealed;
		}
		if (!show || GameClient.Current == null)
		{
			return;
		}

		// Free-floating controls (no container parent) only take their minimum
		// size when told to.
		body.ResetSize();
		nameTab?.ResetSize();
		continueHint?.ResetSize();

		Vector2 anchor = GameClient.Current.ProjectToScreen(_anchor.hudPosition);
		Vector2 screen = Size;
		Vector2 bodySize = body.Size;
		float tabRise = nameTab != null && nameTab.Visible ? Mathf.Max(0f, -nameTabOffset.Y) : 0f;

		float x = anchor.X - bodySize.X * 0.5f;
		x = Mathf.Clamp(x, screenMargin, Mathf.Max(screenMargin, screen.X - screenMargin - bodySize.X));
		float y = anchor.Y - tailLength - bodySize.Y;
		// No room above the speaker: hang the bubble below and point the tail up.
		_flipped = y - tabRise < screenMargin;
		if (_flipped)
		{
			y = anchor.Y + tailLength;
		}
		y = Mathf.Clamp(y, screenMargin + tabRise, Mathf.Max(screenMargin + tabRise, screen.Y - screenMargin - bodySize.Y));
		Vector2 bodyPos = new Vector2(Mathf.Round(x), Mathf.Round(y));
		body.Position = bodyPos;

		if (nameTab != null && nameTab.Visible)
		{
			nameTab.Position = bodyPos + nameTabOffset;
		}
		if (continueHint != null && continueHint.Visible)
		{
			Vector2 hintSize = continueHint.Size;
			continueHint.Position = new Vector2(
				bodyPos.X + bodySize.X - continueHintInset - hintSize.X,
				bodyPos.Y + bodySize.Y - hintSize.Y * 0.5f);
		}
		_tailTip = anchor;
		tail?.QueueRedraw();
	}

	// A triangle from the body's near edge to the anchor. The base slides
	// along that edge to stay under the speaker, kept clear of the rounded
	// corners and (on the bottom edge) the continue hint.
	void DrawTail()
	{
		if (body == null || !body.Visible)
		{
			return;
		}
		StyleBoxFlat style = body.GetThemeStylebox("panel") as StyleBoxFlat;
		Color fill = style?.BgColor ?? Colors.Black;
		Color border = style?.BorderColor ?? Colors.White;
		float borderWidth = style != null ? style.BorderWidthBottom : 0f;
		float corner = style != null ? Mathf.Max(style.CornerRadiusBottomLeft, style.CornerRadiusBottomRight) : 0f;

		Vector2 bodyPos = body.Position;
		Vector2 bodySize = body.Size;
		float half = tailBaseWidth * 0.5f;
		float minX = bodyPos.X + corner + half;
		float maxX = bodyPos.X + bodySize.X - corner - half;
		if (!_flipped && continueHint != null)
		{
			maxX -= continueHintInset + continueHint.Size.X;
		}
		float baseX = maxX >= minX ? Mathf.Clamp(_tailTip.X, minX, maxX) : bodyPos.X + bodySize.X * 0.5f;
		// The base sits inside the border so the fill erases it across the gap.
		float edgeY = _flipped ? bodyPos.Y + borderWidth : bodyPos.Y + bodySize.Y - borderWidth;

		Vector2 left = new Vector2(baseX - half, edgeY);
		Vector2 right = new Vector2(baseX + half, edgeY);
		tail.DrawColoredPolygon(new[] { left, right, _tailTip }, fill);
		if (borderWidth > 0f)
		{
			tail.DrawLine(left, _tailTip, border, borderWidth, true);
			tail.DrawLine(right, _tailTip, border, borderWidth, true);
		}
	}
}
