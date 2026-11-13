using Godot;

// A comic speech bubble whose tail points at an IInteractive's hudPosition
// (above a speaker's head, a sign's plate): body, name tab, tail, and a continue hint.
// The owner (ConversationController) says what to show and when to advance;
// the body (a TypingText) types and sizes the line, the bubble places it.
//
// The root is a full-screen, input-ignoring Control, so its local space IS
// the space GameClient.ProjectToScreen returns; body, tab and hint are
// positioned in it by hand every frame.
[GlobalClass]
public partial class SpeechBubble : Control
{
	[Export] TypingText body;
	[Export] Control nameTab;
	[Export] Label nameLabel;
	// The framed continue hint (a ButtonHint inside its own backing panel).
	[Export] Control continueHint;
	// Drawn after the body so its fill covers the body's border where the
	// tail joins it.
	[Export] Control tail;

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
	bool _flipped;
	Vector2 _tailTip;

	// While true, the hint shows once the line is fully revealed. The owner
	// turns it off while the player is choosing a response.
	public bool AwaitingContinue { get; set; }

	public bool IsFullyRevealed => body == null || body.IsFullyRevealed;

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
		body?.Begin(null);
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

	// Loads a whole BBCode line with nothing revealed yet; an empty line hides
	// the bubble until the next one.
	public void Begin(string bbcode)
	{
		body?.Begin(bbcode);
		UpdateLayout();
	}

	public void RevealAll()
	{
		body?.RevealAll();
	}

	public override void _Process(double delta)
	{
		if (Visible)
		{
			UpdateLayout();
		}
	}

	void UpdateLayout()
	{
		bool show = body != null && body.HasText && HasLiveAnchor;
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
