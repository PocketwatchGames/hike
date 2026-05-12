using Godot;
using System;

[Tool]
[GlobalClass]
public partial class ItemSlotPanel : PanelContainer
{
	[Export] private TextureButton _button;
	[Export] private TextureRect _slotBackground;
	[Export] private Label _stackLabel;
	[Export] private Control _stackContainer;

	// Per-instance empty-slot sprite (head silhouette, body silhouette, etc.).
	// Wrapped in a property setter so inspector edits apply to _slotBackground
	// immediately — combined with [Tool] above, each panel instance previews
	// its silhouette while you author the scene, without "Editable Children".
	// Null hides _slotBackground outright so the slot reads as a plain box.
	Texture2D _backgroundTexture;
	[Export]
	public Texture2D BackgroundTexture
	{
		get => _backgroundTexture;
		set
		{
			_backgroundTexture = value;
			ApplyBackground();
		}
	}

	public Action<ItemSlotPanel> onFocusEntered;
	public Action<ItemSlotPanel> onPressed;

	public ItemState Item { get; private set; }

	public override void _Ready()
	{
		// Setter may have fired before _slotBackground was wired (export
		// ordering during scene load); re-apply now that the rect is resolved.
		ApplyBackground();

		if (Engine.IsEditorHint())
		{
			return;
		}

		if (_button != null)
		{
			_button.FocusEntered += OnButtonFocusEntered;
			_button.Pressed += OnButtonPressed;
			// Mouse hover grabs focus so the focused-panel state (and the
			// Use / Drop hints that key off it) tracks the cursor the same
			// way D-pad navigation does on gamepad.
			_button.MouseEntered += OnButtonMouseEntered;
		}
	}

	public override void _ExitTree()
	{
		if (Engine.IsEditorHint())
		{
			return;
		}
		if (_button != null)
		{
			_button.FocusEntered -= OnButtonFocusEntered;
			_button.Pressed -= OnButtonPressed;
			_button.MouseEntered -= OnButtonMouseEntered;
		}
	}

	void ApplyBackground()
	{
		if (_slotBackground == null)
		{
			return;
		}
		if (_backgroundTexture != null)
		{
			_slotBackground.Texture = _backgroundTexture;
			_slotBackground.Visible = true;
		}
		else
		{
			_slotBackground.Visible = false;
		}
	}

	public void SetItem(ItemState item)
	{
		Item = item;
		if (_button != null)
		{
			_button.TextureNormal = item?.data?.inventorySprite;
		}
		// Only toggle when there's actually a backing texture — a null
		// BackgroundTexture leaves _slotBackground hidden permanently
		// (ApplyBackground above) so we don't unhide an empty rect.
		if (_slotBackground != null && _backgroundTexture != null)
		{
			_slotBackground.Visible = item == null;
		}
		if (_stackLabel != null)
		{
			bool showStack = item != null && item.data != null && item.data.IsStackable && item.stackCount > 1;
			_stackContainer.Visible = showStack;
			if (showStack)
			{
				_stackLabel.Text = item.stackCount.ToString();
			}
		}
	}

	public new void GrabFocus()
	{
		_button?.GrabFocus();
	}

	// Toggle whether the slot button accepts keyboard / gamepad focus. Set to
	// false while a sub-modal (e.g. DropCountPanel) is up so ui_left/right
	// from the analog stick can't traverse focus onto the slots.
	public void SetFocusable(bool focusable)
	{
		if (_button != null)
		{
			_button.FocusMode = focusable ? FocusModeEnum.All : FocusModeEnum.None;
		}
	}

	void OnButtonFocusEntered()
	{
		onFocusEntered?.Invoke(this);
	}

	void OnButtonPressed()
	{
		onPressed?.Invoke(this);
	}

	void OnButtonMouseEntered()
	{
		_button?.GrabFocus();
	}
}
