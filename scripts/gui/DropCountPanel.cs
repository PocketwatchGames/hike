using Godot;
using System;

public partial class DropCountPanel : MarginContainer
{
	[Export] public Label count;
	[Export] public Label label;
	[Export] public Button up;
	[Export] public Button down;
	[Export] public ButtonHint okHint;
	[Export] public ButtonHint cancelHint;
	private int _count;
	private int _maxCount;
	public Action<int> onConfirm;
	public Action onCancel;


	public void Init(int maxCount, Action<int> onConfirm, Action onCancel, string prompt = null)
	{
		this.onConfirm = onConfirm;
		this.onCancel = onCancel;
		_count = maxCount;
		_maxCount = maxCount;
		count.Text = _count.ToString();
		down.Disabled = _count <= 0;
		up.Disabled = _count >= _maxCount;
		// Per-context prompt — "Drop how many?" by default, but the cooking
		// screen passes "Cook how many?" / "Remove how many?" so the same
		// panel reads correctly under both modal hosts.
		if (label != null && !string.IsNullOrEmpty(prompt))
		{
			label.Text = prompt;
		}
		// Pull focus onto the up button so a sub-sequent ui_left/right doesn't
		// land on whatever was focused before (an inventory slot). Combined
		// with SetSlotsFocusable(false) on the inventory side, this fully
		// contains navigation to the count picker.
		up.GrabFocus();
	}
	public override void _Ready()
	{
		up.Pressed += () => SetCount(_count + 1);
		down.Pressed += () => SetCount(_count - 1);
		// Bind the visible OK / Cancel button hints to the actual input
		// actions _UnhandledInput listens for, so the glyph stays in sync with
		// whatever the player has rebound ui_accept / ui_cancel to.
		okHint?.SetHint("ui_accept", "OK");
		cancelHint?.SetHint("ui_cancel", "Cancel");
	}

	public override void _UnhandledInput(InputEvent e)
	{
		// IsVisibleInTree, not Visible: unhandled input is delivered regardless of
		// ancestor visibility, so a locally-visible panel under a closed host screen
		// would otherwise keep eating ui_cancel / ui_accept game-wide.
		if (!IsVisibleInTree())
		{
			return;
		}
		// Stick / D-pad / WASD adjust the count. IsActionPressed on an analog
		// axis fires once per stick deflection past the deadzone, so a single
		// tap = single increment — matches the up/down button behavior.
		if (e.IsActionPressed("MoveUp") || e.IsActionPressed("MoveRight"))
		{
			SetCount(_count + 1);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e.IsActionPressed("MoveDown") || e.IsActionPressed("MoveLeft"))
		{
			SetCount(_count - 1);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e.IsActionPressed("ui_accept"))
		{
			// Snapshot the callback before nulling so a re-Init in onConfirm
			// (unlikely but possible) can install a fresh pair without us
			// stomping it back to null afterwards.
			Action<int> cb = onConfirm;
			onConfirm = null;
			onCancel = null;
			cb?.Invoke(_count);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			Action cb = onCancel;
			onConfirm = null;
			onCancel = null;
			cb?.Invoke();
			GetViewport().SetInputAsHandled();
			return;
		}
	}

	private void SetCount(int value)
	{
		_count = Math.Max(0, Math.Min(_maxCount, value));
		count.Text = _count.ToString();
		down.Disabled = _count <= 0;
		up.Disabled = _count >= _maxCount;
	}
}
