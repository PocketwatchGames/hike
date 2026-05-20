using Godot;
using System;
using System.Collections.Generic;

// HUD panel that types out a ConversationData one branch at a time. Resolves
// the opening branch via the conversation's entryBranches list (first valid
// condition wins), pulls each line through Loc.Get, and scrambles untaught
// portions via TextScrambler based on the branch's resolved language. The
// typewriter rate is driven by CVars.dialogueTypingSpeed (characters per
// second); ui_accept reveals-then-advances exactly as the legacy dialogue
// panel did.
//
// Responses aren't rendered yet — when the last line of the entry branch
// finishes typing the conversation closes. Wiring a response chooser is the
// next step.
//
// While open, gameClient.InputSuppressed flips on so the same press that
// reveals / advances the line doesn't also fall through to Jump / Interact
// in Player.ProcessInput.
[GlobalClass]
public partial class ConversationController : Control
{
	[Export] public Label label;
	[Export] public GameClient gameClient;

	readonly List<string> _lines = new();
	int _lineIndex;
	// Float so the per-tick advance can roll fractional characters in
	// without dropping a glyph per frame when speed × dt < 1.
	float _revealedChars;
	Action _onClose;

	public bool IsOpen => Visible;

	public override void _Ready()
	{
		Visible = false;
	}

	// Open the panel on a conversation. Picks the entry branch, fires its
	// actions, resolves + scrambles its lines, and kicks the typewriter —
	// does nothing if no entry condition matches or the resolved branch is
	// empty.
	public void Show(ConversationData conversation, ConversationContext ctx, Action onClose = null)
	{
		if (conversation == null)
		{
			return;
		}
		ConversationEntry entry = SelectEntry(conversation, ctx);
		if (entry == null)
		{
			return;
		}
		ConversationBranch branch = FindBranch(conversation, entry.branch);
		if (branch == null)
		{
			return;
		}
		FireActions(entry.actions, ctx);
		ResolveAndScrambleLines(branch, ctx);
		if (_lines.Count == 0)
		{
			return;
		}
		_lineIndex = 0;
		_revealedChars = 0f;
		_onClose = onClose;
		if (label != null)
		{
			label.Text = string.Empty;
		}
		if (gameClient != null)
		{
			gameClient.InputSuppressed = true;
		}
		Visible = true;
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		_lines.Clear();
		_lineIndex = 0;
		_revealedChars = 0f;
		if (gameClient != null)
		{
			gameClient.InputSuppressed = false;
		}
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	public override void _Process(double delta)
	{
		if (!Visible || label == null || _lineIndex >= _lines.Count)
		{
			return;
		}
		string line = _lines[_lineIndex];
		if (_revealedChars >= line.Length)
		{
			return;
		}
		float speed = Mathf.Max(0f, CVars.dialogueTypingSpeed.Value);
		_revealedChars = Mathf.Min(line.Length, _revealedChars + speed * (float)delta);
		int reveal = Mathf.Min(line.Length, Mathf.FloorToInt(_revealedChars));
		label.Text = line.Substring(0, reveal);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			GetViewport().SetInputAsHandled();
			Close();
			return;
		}
		if (!e.IsActionPressed("ui_accept"))
		{
			return;
		}
		GetViewport().SetInputAsHandled();

		if (_lineIndex >= _lines.Count)
		{
			Close();
			return;
		}
		string line = _lines[_lineIndex];
		if (_revealedChars < line.Length)
		{
			_revealedChars = line.Length;
			if (label != null)
			{
				label.Text = line;
			}
			return;
		}
		_lineIndex++;
		if (_lineIndex >= _lines.Count)
		{
			Close();
			return;
		}
		_revealedChars = 0f;
		if (label != null)
		{
			label.Text = string.Empty;
		}
	}

	// Walks entryBranches in order, returning the first entry whose condition
	// passes (or whose condition is null). The branch lookup itself runs in
	// Show — keeping it here would discard the entry's actions list.
	static ConversationEntry SelectEntry(ConversationData conv, ConversationContext ctx)
	{
		if (conv.entryBranches == null)
		{
			return null;
		}
		for (int i = 0; i < conv.entryBranches.Count; i++)
		{
			ConversationEntry entry = conv.entryBranches[i];
			if (entry == null)
			{
				continue;
			}
			if (entry.condition != null && !entry.condition.Evaluate(ctx))
			{
				continue;
			}
			return entry;
		}
		return null;
	}

	// Fires actions in array order. Null entries are skipped; the runtime
	// does not enforce ordering between actions and the branch text — an
	// action that closes the conversation suppresses the next branch.
	static void FireActions(Godot.Collections.Array<ConversationAction> actions, ConversationContext ctx)
	{
		if (actions == null)
		{
			return;
		}
		for (int i = 0; i < actions.Count; i++)
		{
			ConversationAction a = actions[i];
			if (a != null)
			{
				a.Execute(ctx);
			}
		}
	}

	static ConversationBranch FindBranch(ConversationData conv, StringName name)
	{
		if (conv.branches == null)
		{
			return null;
		}
		for (int i = 0; i < conv.branches.Count; i++)
		{
			ConversationBranch b = conv.branches[i];
			if (b != null && b.name == name)
			{
				return b;
			}
		}
		return null;
	}

	void ResolveAndScrambleLines(ConversationBranch branch, ConversationContext ctx)
	{
		_lines.Clear();
		if (branch.lineLocKeys == null)
		{
			return;
		}
		LanguageData lang = branch.language ?? ctx.speakerLanguage;
		ELanguageComponents missing = (ctx.player == null || lang == null)
			? ELanguageComponents.None
			: ELanguageComponents.All & ~ctx.player.GetLearnedComponents(lang);
		for (int i = 0; i < branch.lineLocKeys.Count; i++)
		{
			StringName key = branch.lineLocKeys[i];
			if (key == default || key == "")
			{
				continue;
			}
			string text = Loc.Get(key);
			_lines.Add(missing == ELanguageComponents.None
				? text
				: TextScrambler.Scramble(text, lang, missing));
		}
	}
}
