using System;
using System.Collections.Generic;
using Godot;

// "Forge an item" modal. Opened by GameClient when the player uses a forge.
// Instances one selectable ForgeItemCard per offered item (each an ItemInfoPanel
// display) and reports the player's pick — or back-out — through the callbacks
// GameClient supplies. GameClient owns visibility and the input / HUD / mouse
// gating; this class is purely the view + selection plumbing. Mirrors
// UpgradeScreen, but over ItemStates instead of BoonData.
[GlobalClass]
public partial class ForgeScreen : Control
{
    [Export] private PackedScene itemCardScene;
    // Row the per-item cards are added into (MarginContainer/HBoxContainer).
    [Export] private Container _panelContainer;

    // Invoked with the chosen item; GameClient equips it and closes the screen.
    Action<ItemState> _onComplete;
    // Invoked instead when the player backs out without picking.
    Action _onCancel;

    public void Init(Action<ItemState> completeFunc, Action onCancel, List<ItemState> items)
    {
        _onComplete = completeFunc;
        _onCancel = onCancel;
        ClearPanels();
        ForgeItemCard first = null;
        if (items != null && _panelContainer != null && itemCardScene != null)
        {
            for (int i = 0; i < items.Count; i++)
            {
                ItemState item = items[i];
                if (item == null)
                {
                    continue;
                }
                ForgeItemCard card = itemCardScene.Instantiate<ForgeItemCard>();
                _panelContainer.AddChild(card);
                // Capture per-iteration so each button forges its own item.
                ItemState captured = item;
                card.Init(captured, () => Choose(captured));
                first ??= card;
            }
        }
        // Seed gamepad/keyboard focus on the first card. Deferred so the freshly
        // added button is in the tree and focusable first.
        if (first != null)
        {
            first.CallDeferred(ForgeItemCard.MethodName.GrabButtonFocus);
        }
    }

    void Choose(ItemState item)
    {
        Action<ItemState> cb = _onComplete;
        _onComplete = null;
        _onCancel = null;
        cb?.Invoke(item);
    }

    void Cancel()
    {
        Action cb = _onCancel;
        _onComplete = null;
        _onCancel = null;
        cb?.Invoke();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible)
        {
            return;
        }
        if (e.IsActionPressed("ui_cancel"))
        {
            Cancel();
            GetViewport().SetInputAsHandled();
        }
    }

    void ClearPanels()
    {
        if (_panelContainer == null)
        {
            return;
        }
        foreach (Node child in _panelContainer.GetChildren())
        {
            if (child is ForgeItemCard)
            {
                child.QueueFree();
            }
        }
    }
}
