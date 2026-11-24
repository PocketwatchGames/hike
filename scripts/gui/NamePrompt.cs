using System;
using Godot;

// A one-line text entry over the HUD: the player types a name and confirms it.
// Keyboard only for now (a pad would need Steam's virtual keyboard). Confirm
// refuses a blank name, and there is no cancel - whatever opened the prompt is
// waiting on an answer (NamePlayerAction holds its conversation).
[GlobalClass]
public partial class NamePrompt : Control
{
	[Export] public GameClient gameClient;
	[Export] Label _title;
	[Export] LineEdit _field;
	[Export] ButtonHint _confirmHint;

	Action<string> _onConfirm;
	// Whether this prompt raised InputSuppressed, so it only lowers what it
	// raised: inside a conversation the conversation owns the gate.
	bool _suppressedInput;

	public bool IsOpen => Visible;

	public override void _Ready()
	{
		Visible = false;
		if (_field != null)
		{
			_field.TextSubmitted += OnTextSubmitted;
		}
		Loc.OnLanguageChanged += ApplyText;
		ApplyText();
	}

	public override void _ExitTree()
	{
		Loc.OnLanguageChanged -= ApplyText;
		if (_field != null)
		{
			_field.TextSubmitted -= OnTextSubmitted;
		}
	}

	void ApplyText()
	{
		if (_title != null)
		{
			_title.Text = Loc.Get(Loc.Keys.name_prompt_title);
		}
		if (_confirmHint != null)
		{
			_confirmHint.ActionName = Loc.Get(Loc.Keys.name_prompt_confirm);
		}
	}

	// Opens on `initial`, all selected so typing replaces it. A second Open
	// while one is up replaces it; the first caller never hears back.
	public void Open(string initial, Action<string> onConfirm)
	{
		_onConfirm = onConfirm;
		if (gameClient != null && !_suppressedInput && !gameClient.InputSuppressed)
		{
			gameClient.InputSuppressed = true;
			_suppressedInput = true;
		}
		Visible = true;
		if (_field != null)
		{
			_field.Text = initial ?? string.Empty;
			// Deferred: the press that fired the opening action is still being
			// dispatched, and focus must land after the field is visible.
			Callable.From(FocusField).CallDeferred();
		}
	}

	void FocusField()
	{
		if (!Visible || _field == null)
		{
			return;
		}
		_field.GrabFocus();
		_field.Edit();
		_field.SelectAll();
	}

	void OnTextSubmitted(string text)
	{
		Confirm();
	}

	void Confirm()
	{
		string name = _field?.Text.StripEdges() ?? string.Empty;
		if (name.Length == 0)
		{
			FocusField();
			return;
		}
		Visible = false;
		_field?.Unedit();
		if (_suppressedInput && gameClient != null)
		{
			gameClient.InputSuppressed = false;
		}
		_suppressedInput = false;
		Action<string> cb = _onConfirm;
		_onConfirm = null;
		cb?.Invoke(name);
	}

	// Reached only when the field has lost focus (it consumes its own keys).
	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_accept"))
		{
			GetViewport().SetInputAsHandled();
			Confirm();
			return;
		}
		// No cancel, and Escape must not reach the pause menu or the
		// conversation underneath.
		if (e.IsActionPressed("ui_cancel"))
		{
			GetViewport().SetInputAsHandled();
			FocusField();
		}
	}
}
