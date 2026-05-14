using Godot;
using System;
using System.Collections.Generic;

// HUD panel that types out a list of dialogue strings one character at a
// time. ui_accept while a line is mid-type fills the rest of that line
// immediately; ui_accept on a fully-revealed line advances to the next
// line, and on the last line closes the panel. Typing rate is driven by
// CVars.dialogueTypingSpeed (characters per second).
//
// While open, gameClient.InputSuppressed flips on so the same press that
// reveals / advances the line doesn't also fall through to Jump / Interact
// in Player.ProcessInput.
[GlobalClass]
public partial class DialogueController : Control
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

	public void Show(IReadOnlyList<string> lines, Action onClose = null)
	{
		if (lines == null || lines.Count == 0)
		{
			return;
		}
		_lines.Clear();
		for (int i = 0; i < lines.Count; i++)
		{
			_lines.Add(lines[i] ?? string.Empty);
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
}
