using Godot;
using System;

// Modal wrapper that owns the Inventory / World Map / Bestiary / Recipe
// tabs. While open it gates gameplay input (GameClient.InputSuppressed),
// hides the in-game HUD, and releases the mouse. TabLeft / TabRight cycle
// the active tab in enum order (with wrap-around); ui_cancel closes the
// whole thing and invokes the onClose callback passed in by the caller.
[GlobalClass]
public partial class AlmanacScreen : Control
{
	public enum EAlmanacTab
	{
		Inventory,
		WorldMap,
		Bestiary,
		Recipe
	}
	[Export] InventoryScreen _inventoryScreen;
	[Export] WorldMapScreen _worldMapScreen;
	[Export] BestiaryScreen _bestiaryScreen;
	[Export] RecipeScreen _recipeScreen;
	[Export] ButtonHint _tabLeftButtonHint;
	[Export] ButtonHint _tabRightButtonHint;
	[Export] Control _inventoryTab;
	[Export] Control _worldMapTab;
	[Export] Control _bestiaryTab;
	[Export] Control _recipeTab;

	GameClient _gameClient;
	Action _onClose;
	EAlmanacTab _curTab;

	public override void _Ready()
	{
		UpdateTab(_inventoryScreen, _inventoryTab, false);
		UpdateTab(_worldMapScreen, _worldMapTab, false);
		UpdateTab(_bestiaryScreen, _bestiaryTab, false);
		UpdateTab(_recipeScreen, _recipeTab, false);

		_tabLeftButtonHint?.SetHint("TabLeft", string.Empty);
		_tabRightButtonHint?.SetHint("TabRight", string.Empty);

		Visible = false;
	}

	public void Open(EAlmanacTab tab, GameClient gameClient, MobData focusMob = null, Action onClose = null)
	{
		_gameClient = gameClient;
		_onClose = onClose;
		// Idempotent — sub-screens just stash the reference. Cheaper than
		// tracking an initialized flag, and lets the screens be swapped at
		// runtime (e.g. a future inspector-driven debug GameClient).
		_inventoryScreen?.Initialize(gameClient);
		_worldMapScreen?.Initialize(gameClient);
		_bestiaryScreen?.Initialize(gameClient);
		_recipeScreen?.Initialize(gameClient);
		// Per-open focus hint, consumed by the target sub-screen's next
		// Rebuild. Only the Bestiary tab uses it today; other tabs can
		// add their own typed focus params here if needed without changing
		// the Open() surface that callers use.
		_bestiaryScreen?.SetPendingFocus(focusMob);
		if (_gameClient != null)
		{
			_gameClient.InputSuppressed = true;
			if (_gameClient.hud != null) { _gameClient.hud.Visible = false; }
		}
		Input.MouseMode = Input.MouseModeEnum.Visible;
		Visible = true;
		ShowTab(tab);
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		// Hide every sub-screen so the next Open() always re-triggers
		// VisibilityChanged on the chosen tab (which is what binds the
		// player into the inventory panel).
		UpdateTab(_inventoryScreen, _inventoryTab, false);
		UpdateTab(_worldMapScreen, _worldMapTab, false);
		UpdateTab(_bestiaryScreen, _bestiaryTab, false);
		UpdateTab(_recipeScreen, _recipeTab, false);
		if (_gameClient != null)
		{
			_gameClient.InputSuppressed = false;
			if (_gameClient.hud != null) { _gameClient.hud.Visible = true; }
		}
		Input.MouseMode = Input.MouseModeEnum.Captured;
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	public void ShowTab(EAlmanacTab tab)
	{
		_curTab = tab;
		UpdateTab(_inventoryScreen, _inventoryTab, tab == EAlmanacTab.Inventory);
		UpdateTab(_worldMapScreen, _worldMapTab, tab == EAlmanacTab.WorldMap);
		UpdateTab(_bestiaryScreen, _bestiaryTab, tab == EAlmanacTab.Bestiary);
		UpdateTab(_recipeScreen, _recipeTab, tab == EAlmanacTab.Recipe);
	}

	static void UpdateTab(Control screen, Control tab, bool active)
	{
		if (screen != null) { screen.Visible = active; }
		if (tab != null) { tab.Modulate = active ? Colors.White : new Color(0.5f, 0.5f, 0.5f); }
	}

	void CycleTab(int direction)
	{
		int count = Enum.GetValues<EAlmanacTab>().Length;
		int next = ((int)_curTab + direction + count) % count;
		ShowTab((EAlmanacTab)next);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel") || e.IsActionPressed("Map") || e.IsActionPressed("Inventory"))
		{
			Close();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e.IsActionPressed("TabLeft"))
		{
			CycleTab(-1);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e.IsActionPressed("TabRight"))
		{
			CycleTab(1);
			GetViewport().SetInputAsHandled();
		}
	}
}
