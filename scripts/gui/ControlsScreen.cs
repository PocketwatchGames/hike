using Godot;
using System;

// Read-only list of what each control does on keyboard/mouse and on gamepad.
// Both the main menu and the pause menu instance this same scene and call Open
// with what to restore once the player backs out.
//
// Both device columns are always shown — this is the screen you open to find
// out what the OTHER device does, so following InputDevice.Current the way
// ButtonHint does would hide half of it. The rows are the game's one
// ControlSetData — the same set [input:] tokens in authored text name — and
// it composes the glyphs from the live InputMap.
[GlobalClass]
public partial class ControlsScreen : Control
{
	[Export] PackedScene rowScene;
	[Export] Label _titleLabel;
	// Gets the column titles rather than a binding.
	[Export] ControlsRow _headerRow;
	// The authored rows are added here, under the header.
	[Export] Container _rowContainer;
	[Export] Button _backButton;
	[Export] ControlSetData controls;

	Action _onClose;

	public override void _Ready()
	{
		Loc.OnLanguageChanged += Rebuild;
		Rebuild();
	}

	public override void _ExitTree()
	{
		Loc.OnLanguageChanged -= Rebuild;
	}

	public void Open(Action onClose)
	{
		_onClose = onClose;
		Visible = true;
		// Deferred: the button has to be on screen before it can take focus, and
		// Open is called from the pressed handler of the button that opened us.
		_backButton?.CallDeferred(Control.MethodName.GrabFocus);
	}

	// Wired to the Back button, and to ui_cancel below.
	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	// _Input rather than _UnhandledInput: Escape is bound to TogglePause as well
	// as ui_cancel, and both the pause menu and GameClient watch for it further
	// down the chain. Consuming it here is what keeps backing out of this screen
	// from also un-pausing the game underneath it.
	public override void _Input(InputEvent e)
	{
		// IsVisibleInTree, not Visible: this screen hangs under the pause menu,
		// which is hidden wholesale on un-pause without clearing our own flag.
		if (!IsVisibleInTree())
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
	}

	void Rebuild()
	{
		if (_titleLabel != null)
		{
			_titleLabel.Text = Loc.Get(Loc.Keys.controls_title);
		}
		if (_backButton != null)
		{
			_backButton.Text = Loc.Get(Loc.Keys.controls_back);
		}
		_headerRow?.Fill(
			Loc.Get(Loc.Keys.controls_action),
			Loc.Get(Loc.Keys.controls_keyboard),
			Loc.Get(Loc.Keys.controls_gamepad));

		if (_rowContainer == null || rowScene == null || controls?.bindings == null)
		{
			return;
		}
		foreach (Node child in _rowContainer.GetChildren())
		{
			// Removed as well as freed: QueueFree lands at end of frame, so a
			// rebuild would otherwise render the old rows above the new ones.
			_rowContainer.RemoveChild(child);
			child.QueueFree();
		}
		foreach (ControlBindingData binding in controls.bindings)
		{
			if (binding == null)
			{
				continue;
			}
			ControlsRow row = rowScene.Instantiate<ControlsRow>();
			_rowContainer.AddChild(row);
			row.Fill(
				Loc.Get(binding.labelKey),
				Glyphs(binding, InputDevice.EDevice.KeyboardMouse),
				Glyphs(binding, InputDevice.EDevice.Gamepad));
		}
	}

	string Glyphs(ControlBindingData binding, InputDevice.EDevice device)
	{
		return controls.TryCompose(binding, device, out string glyphs) ? glyphs : Loc.Get(Loc.Keys.controls_unbound);
	}
}
