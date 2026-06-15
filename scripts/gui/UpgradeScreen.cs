using Godot;
using System;
using System.Collections.Generic;

// "Pick a boon" modal view. Opened by GameClient when a consumable (the fairy
// corpse) offers the player a choice of permanent status effects. Instances
// one UpgradePanel per offered upgrade, fills each with its effect data, and
// reports the player's pick (or back-out) through the callbacks GameClient
// supplies. GameClient owns this screen's visibility and the input / HUD /
// mouse gating; this class is purely the view + selection plumbing.
[GlobalClass]
public partial class UpgradeScreen : Control
{
	[Export] private PackedScene upgradePanelScene;
	// Row the per-upgrade cards are added into (MarginContainer/HBoxContainer).
	[Export] private Container _panelContainer;

	// Completion callback from the consumable (via GameClient) — invoked with
	// the chosen boon so the consumable owns "what selecting does".
	Action<BoonData> _onComplete;
	// Invoked instead when the player backs out without picking.
	Action _onCancel;

	// Build the cards for `upgrades` and stash the callbacks. GameClient marks
	// the screen visible; each card's button applies its own boon through
	// Choose, and ui_cancel backs out through Cancel.
	public void Init(Action<BoonData> completeFunc, Action onCancel, List<BoonData> upgrades)
	{
		_onComplete = completeFunc;
		_onCancel = onCancel;
		ClearPanels();
		UpgradePanel first = null;
		if (upgrades != null && _panelContainer != null && upgradePanelScene != null)
		{
			for (int i = 0; i < upgrades.Count; i++)
			{
				BoonData upgrade = upgrades[i];
				if (upgrade == null)
				{
					continue;
				}
				UpgradePanel panel = upgradePanelScene.Instantiate<UpgradePanel>();
				_panelContainer.AddChild(panel);
				// Capture per-iteration so each button applies its own boon.
				BoonData captured = upgrade;
				panel.Init(captured, () => Choose(captured));
				first ??= panel;
			}
		}
		// Seed gamepad/keyboard focus on the first card so the screen is
		// navigable without a mouse. Deferred so the freshly added button is
		// in the tree and focusable first.
		if (first != null)
		{
			first.CallDeferred(UpgradePanel.MethodName.GrabButtonFocus);
		}
	}

	void Choose(BoonData upgrade)
	{
		Action<BoonData> cb = _onComplete;
		_onComplete = null;
		_onCancel = null;
		cb?.Invoke(upgrade);
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
			if (child is UpgradePanel)
			{
				child.QueueFree();
			}
		}
	}
}
