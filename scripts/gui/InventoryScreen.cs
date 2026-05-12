using Godot;
using System;

// Modal wrapper around InventoryPanel. Handles open/close lifecycle, the
// gameClient.InputSuppressed gate so gameplay input stops while the screen is
// up, and the ui_cancel close. All slot / verb logic lives in InventoryPanel —
// this script just shows/hides the CanvasLayer and binds the panel to the
// active player.
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] public GameClient gameClient;
	[Export] private InventoryPanel _panel;
	[Export] private PlayerStatsPanel _statsPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	[Export] private DropCountPanel _dropCountPanel;

	Action _onClose;
	Player _player;

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned += OnPlayerSpawned;
		}
		// Mirror the inventory panel's focused-item changes into the side
		// info panel. The panel fires on focus moves, on Bind, and after
		// Refresh — that covers every state transition the info panel cares
		// about, including "screen just became visible" via Bind.
		if (_panel != null)
		{
			_panel.onFocusedItemChanged += OnFocusedItemChanged;
			_panel.onDropHoldComplete += OnDropHoldComplete;
		}
		_itemInfoPanel?.SetItem(null);
		// Drop count panel starts hidden — only the hold path makes it visible.
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
		}
		if (_panel != null)
		{
			_panel.onFocusedItemChanged -= OnFocusedItemChanged;
			_panel.onDropHoldComplete -= OnDropHoldComplete;
		}
	}

	void OnFocusedItemChanged(ItemState item)
	{
		_itemInfoPanel?.SetItem(item);
	}

	// Hold-drop on the inventory panel — pop the count picker so the player
	// can pick how many to drop from a stack. Init wires both confirm and
	// cancel back to handlers that hide the panel and release the panel's
	// DropLocked gate so the inventory's drop key processes again.
	void OnDropHoldComplete(ItemState item)
	{
		if (item == null || _dropCountPanel == null || _panel == null)
		{
			return;
		}
		Inventory inventory = _player?.Inventory;
		if (inventory == null)
		{
			return;
		}
		// Lock the inventory slots out of focus traversal so the analog stick
		// can't walk the highlight off the picker while it's up.
		_panel.SetSlotsFocusable(false);
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count => OnDropCountConfirmed(inventory, item, count),
			onCancel: OnDropCountCancelled);
	}

	void OnDropCountConfirmed(Inventory inventory, ItemState item, int count)
	{
		if (count > 0)
		{
			inventory.Drop(item, count);
		}
		CloseDropCountPanel();
	}

	void OnDropCountCancelled()
	{
		CloseDropCountPanel();
	}

	void CloseDropCountPanel()
	{
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
		if (_panel != null)
		{
			_panel.DropLocked = false;
			// Re-enable focus on inventory slots and put it back on the slot
			// that was highlighted before the picker stole it.
			_panel.SetSlotsFocusable(true);
			_panel.RestoreFocus();
		}
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
		// If the screen happens to already be visible when the player spawns
		// (load-order edge case), forward the bind immediately so the slots
		// populate without waiting for an Open() / Close() cycle.
		if (Visible)
		{
			_panel?.Bind(_player);
		}
	}

	public void Open(Action onClose = null)
	{
		_onClose = onClose;
		if (gameClient != null)
		{
			gameClient.InputSuppressed = true;
			if (gameClient.hud != null)
			{
				gameClient.hud.Visible = false;
			}
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
		if (gameClient != null)
		{
			gameClient.InputSuppressed = false;
			if (gameClient.hud != null)
			{
				gameClient.hud.Visible = true;
			}
		}
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	void OnVisibilityChanged()
	{
		Input.MouseMode = Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
		if (Visible)
		{
			_panel?.Bind(_player);
		}
		else
		{
			_panel?.Unbind();
			// Closing the inventory drops any in-flight count-picker state so
			// the next open starts clean.
			CloseDropCountPanel();
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
	}
}
