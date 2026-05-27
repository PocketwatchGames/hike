using Godot;
using System;

[Tool]
[GlobalClass]
public partial class ItemSlotPanel : PanelContainer
{
	[Export] private TextureButton _button;
	[Export] private TextureRect _itemTexture;
	[Export] private TextureRect _slotBackground;
	[Export] private Label _stackLabel;
	[Export] private Control _stackContainer;
	// Translucent ghost overlay used by select-mode UIs (InventoryScreen /
	// MerchantScreen). Shows a preview of the item that would land here on
	// commit. Authored hidden in the scene; null safely no-ops in SetGhost.
	[Export] private TextureRect _ghostOverlay;
	[Export] private Control _statusContainer;
	// Scene instantiated per armed effect on the item — typically
	// scenes/gui/status_effect_icon.tscn (or a sized-down variant for the
	// slot grid). Root must be a StatusEffectIcon; we drive it via
	// InitStatic so the persistent slot display skips the intro animation.
	[Export] private PackedScene _statusIconScene;

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
	// Raw down/up signals on top of `onPressed` (which is Godot's release-
	// driven Pressed signal). InventoryPanel uses these to distinguish tap
	// vs. hold on the slot's primary action — ButtonDown starts a hold
	// timer, ButtonUp ends it.
	public Action<ItemSlotPanel> onButtonDown;
	public Action<ItemSlotPanel> onButtonUp;

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
			_button.ButtonDown += OnButtonDown;
			_button.ButtonUp += OnButtonUp;
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
			_button.ButtonDown -= OnButtonDown;
			_button.ButtonUp -= OnButtonUp;
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
		if (_itemTexture != null)
		{
			_itemTexture.Texture = item?.data?.inventorySprite;
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
		RebuildStatusIcons(item);
	}

	// Refresh `_statusContainer` to one StatusEffectIcon per *armed* effect
	// on `item`. Buildup-only meters (e.g. a not-yet-soaked piece of armor
	// charging up in light rain) are intentionally skipped here — the slot
	// view is "what's actively affecting this item right now" and pre-arm
	// progress bars belong on the detailed info panel, not the grid.
	private void RebuildStatusIcons(ItemState item)
	{
		if (_statusContainer == null || _statusIconScene == null)
		{
			return;
		}
		foreach (Node child in _statusContainer.GetChildren())
		{
			child.QueueFree();
		}
		if (item == null)
		{
			return;
		}
		var effects = item.statusEffects.StatusEffects;
		for (int i = 0; i < effects.Count; i++)
		{
			StatusEffectData data = effects[i]?.data;
			if (data?.icon == null)
			{
				continue;
			}
			StatusEffectIcon icon = _statusIconScene.Instantiate<StatusEffectIcon>();
			_statusContainer.AddChild(icon);
			icon.InitStatic(data);
		}
	}

	// Show a translucent ghost overlay of `item` on top of this slot. Used by
	// select-mode UIs to preview the item that would land here on commit. Null
	// hides the overlay. No-op when the scene lacks the overlay node.
	public void SetGhost(ItemState item)
	{
		if (_ghostOverlay == null)
		{
			return;
		}
		Texture2D tex = item?.data?.inventorySprite;
		_ghostOverlay.Texture = tex;
		_ghostOverlay.Visible = tex != null;
	}

	// Dim this slot's icon to signal "the item here has been picked up by
	// select mode". The slot keeps its real Item — the dim is purely visual.
	// Cleared by passing false (back to full opacity).
	public void SetDimmed(bool dimmed)
	{
		if (_itemTexture == null)
		{
			return;
		}
		Color m = _itemTexture.Modulate;
		m.A = dimmed ? 0.3f : 1f;
		_itemTexture.Modulate = m;
	}

	public new void GrabFocus()
	{
		_button?.GrabFocus();
	}

	// True iff this slot's underlying button is the keyboard focus owner.
	// InventoryPanel / CookingPanel gate their input-action polling on this
	// so a stale `_focused` reference (last slot focused before focus moved
	// to a sibling panel) doesn't keep firing Drop / Remove ticks.
	public bool HasButtonFocus()
	{
		return _button != null && _button.HasFocus();
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

	void OnButtonDown()
	{
		onButtonDown?.Invoke(this);
	}

	void OnButtonUp()
	{
		onButtonUp?.Invoke(this);
	}

	void OnButtonMouseEntered()
	{
		_button?.GrabFocus();
	}
}
