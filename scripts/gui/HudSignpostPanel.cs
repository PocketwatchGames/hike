using Godot;

// HUD panel that appears when the player interacts with a readable
// interactive (Signpost, KnowledgeStone, etc.). Opens via
// Hud.ShowSignpost(text, source). Stays up while the source remains the
// player's highlighted (or in-progress) interactive; auto-closes as soon
// as the player walks away or aims at a different interactive. Does NOT
// suppress input — the player keeps full gameplay control while reading.
[GlobalClass]
public partial class HudSignpostPanel : Control
{
	[Export] public Label label;
	[Export] public GameClient gameClient;

	public bool IsOpen => Visible;

	IInteractive _source;
	// Grace window after Open before the highlight check arms. The press
	// flow briefly clears Player._highlightInteractive between Interact and
	// the next UpdateHighlightInteractive pass; without the grace, the
	// panel would close on the same frame it opened.
	const ulong GraceMs = 250;
	ulong _openedAtMs;

	public override void _Ready()
	{
		Visible = false;
	}

	public void Open(string text, IInteractive source)
	{
		if (label != null)
		{
			label.Text = text ?? string.Empty;
		}
		_source = source;
		_openedAtMs = Time.GetTicksMsec();
		Visible = true;
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		_source = null;
	}

	public override void _Process(double delta)
	{
		if (!Visible || _source == null)
		{
			return;
		}
		if (Time.GetTicksMsec() - _openedAtMs < GraceMs)
		{
			return;
		}
		Player player = gameClient?.Player;
		if (player == null)
		{
			return;
		}
		if (player.HighlightInteractive == _source || player.CurInteractive == _source)
		{
			return;
		}
		Close();
	}
}
