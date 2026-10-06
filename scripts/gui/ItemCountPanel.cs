using Godot;
using System;

// "How many?" picker for splitting a stack — buy, sell, drop, stash, pick up.
// Self-contained modal: Open shows it and, until it closes, it takes every
// keyboard / gamepad press ahead of the GUI (stick or D-pad changes the count,
// accept confirms, cancel backs out), so nothing underneath — slot focus, a
// host's hold timers, a screen's own cancel — reacts while it is up. The host
// does nothing but Open it; focus goes back where it was when it closes. Mouse
// events still reach its < / > buttons.
[GlobalClass]
public partial class ItemCountPanel : MarginContainer
{
	[Export] public Label count;
	[Export] public Label label;
	[Export] public Button up;
	[Export] public Button down;
	[Export] public ButtonHint okHint;
	[Export] public ButtonHint cancelHint;
	// Holding a direction steps once, waits this long, then repeats.
	[Export(PropertyHint.Range, "0.05,2,0.05")] private float _repeatDelay = 0.4f;
	[Export(PropertyHint.Range, "0.02,1,0.01")] private float _repeatInterval = 0.08f;

	static readonly StringName[] IncreaseActions = { "MoveUp", "MoveRight", "ui_up", "ui_right" };
	static readonly StringName[] DecreaseActions = { "MoveDown", "MoveLeft", "ui_down", "ui_left" };

	private int _count;
	private int _maxCount;
	private Action<int> _onConfirm;
	private Action _onCancel;
	// The control that had focus at Open, given it back at close.
	private Control _returnFocus;
	// Direction held last frame (-1 / 0 / +1) and time until its next step.
	private int _heldStep;
	private float _repeatTimer;

	public bool IsOpen => Visible;

	public override void _Ready()
	{
		Visible = false;
		up.Pressed += () => SetCount(_count + 1);
		down.Pressed += () => SetCount(_count - 1);
		// The buttons are for the mouse. Taking focus would leave it here when the
		// picker hides, with nothing on the host screen focused.
		up.FocusMode = FocusModeEnum.None;
		down.FocusMode = FocusModeEnum.None;
		// Bind the visible OK / Cancel hints to the actions _Input listens for, so
		// the glyph follows whatever ui_accept / ui_cancel are rebound to.
		okHint?.SetHint("ui_accept", "OK");
		cancelHint?.SetHint("ui_cancel", "Cancel");
	}

	// Counts run 1..maxCount, starting at maxCount. `prompt` replaces the
	// "How many?" label.
	public void Open(int maxCount, Action<int> onConfirm, Action onCancel = null, string prompt = null)
	{
		_onConfirm = onConfirm;
		_onCancel = onCancel;
		_maxCount = Math.Max(1, maxCount);
		if (label != null && !string.IsNullOrEmpty(prompt))
		{
			label.Text = prompt;
		}
		SetCount(_maxCount);
		_returnFocus = GetViewport().GuiGetFocusOwner();
		// A direction already held (the stick mid-push) must be let go first.
		_heldStep = HeldStep();
		_repeatTimer = float.PositiveInfinity;
		Visible = true;
	}

	// _Input, not _UnhandledInput: the GUI would otherwise move slot focus on the
	// stick and activate the focused slot on accept before this saw either.
	public override void _Input(InputEvent e)
	{
		// IsVisibleInTree: input reaches a locally-visible panel under a closed
		// host screen, which would otherwise swallow input game-wide.
		// Releases pass: the hold that opened the picker is usually still down,
		// and the button it was held on must see it come up. A release never
		// triggers anything.
		if (!IsVisibleInTree() || e is InputEventMouse || !e.IsPressed())
		{
			return;
		}
		// Every press is eaten, including the stick: the count steps from
		// _Process, off the action state, because an analog stick sends a
		// "pressed" event for every small movement past the deadzone.
		GetViewport().SetInputAsHandled();
		if (e.IsActionPressed("ui_accept"))
		{
			Action<int> confirm = _onConfirm;
			Dismiss();
			confirm?.Invoke(_count);
		}
		else if (e.IsActionPressed("ui_cancel"))
		{
			Action cancel = _onCancel;
			Dismiss();
			cancel?.Invoke();
		}
	}

	public override void _Process(double delta)
	{
		if (!IsVisibleInTree())
		{
			return;
		}
		int step = HeldStep();
		if (step != _heldStep)
		{
			// A fresh push (or a change of direction) steps once at once.
			_heldStep = step;
			_repeatTimer = _repeatDelay;
			if (step != 0)
			{
				SetCount(_count + step);
			}
			return;
		}
		if (step == 0)
		{
			return;
		}
		_repeatTimer -= (float)delta;
		if (_repeatTimer <= 0f)
		{
			_repeatTimer = _repeatInterval;
			SetCount(_count + step);
		}
	}

	static int HeldStep()
	{
		bool increase = AnyPressed(IncreaseActions);
		bool decrease = AnyPressed(DecreaseActions);
		return increase == decrease ? 0 : (increase ? 1 : -1);
	}

	static bool AnyPressed(StringName[] actions)
	{
		foreach (StringName action in actions)
		{
			if (InputMap.HasAction(action) && Input.IsActionPressed(action))
			{
				return true;
			}
		}
		return false;
	}

	// Hide without calling back — a host closing under an open picker. Callbacks
	// are cleared before they run, so one may re-Open the panel.
	public void Dismiss()
	{
		Visible = false;
		_onConfirm = null;
		_onCancel = null;
		Control focus = _returnFocus;
		_returnFocus = null;
		if (focus != null && IsInstanceValid(focus) && focus.IsVisibleInTree())
		{
			focus.GrabFocus();
		}
	}

	private void SetCount(int value)
	{
		_count = Math.Clamp(value, 1, _maxCount);
		count.Text = _count.ToString();
		down.Disabled = _count <= 1;
		up.Disabled = _count >= _maxCount;
	}
}
