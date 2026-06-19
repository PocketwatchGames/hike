using Godot;
using System;
using System.Collections.Generic;

// Modal camp hub, opened from a lit campfire (Forge → EActionVerb.Camp). Mirrors
// AlmanacScreen: it owns the input gate (GameClient.InputSuppressed), hides the
// in-game HUD, releases the mouse, and cycles Sleep / Cook / Stash tabs with
// TabLeft / TabRight. While open the player is concealed from mobs and plays the
// SitIdle pose (Player.EnterCamp / ExitCamp). ui_cancel closes the whole screen.
//
// Unlike AlmanacScreen's read-only sub-screens, the cooking and stash tabs are
// data-bound per open: Cook attaches to the campfire's Forge, Stash to the
// global player stash (WorldSimState.CampStash, reachable from any campfire).
// Each sub-screen is driven via its Open()/Close() — they no longer own any
// global gating of their own; this screen is the single owner.
[GlobalClass]
public partial class CampScreen : Control
{
	public enum ECampTab
	{
		Sleep,
		Cook,
		Stash,
	}

	[Export] SleepScreen _sleepScreen;
	[Export] CookingScreen _cookingScreen;
	[Export] StashScreen _stashScreen;
	[Export] ButtonHint _tabLeftButtonHint;
	[Export] ButtonHint _tabRightButtonHint;
	[Export] Control _sleepTab;
	[Export] Control _cookTab;
	[Export] Control _stashTab;

	GameClient _gameClient;
	Player _player;
	Forge _forge;
	ECampTab _curTab;
	bool _open;

	public override void _Ready()
	{
		_tabLeftButtonHint?.SetHint("TabLeft", string.Empty);
		_tabRightButtonHint?.SetHint("TabRight", string.Empty);
		Visible = false;
	}

	public void Open(Player player, Forge forge)
	{
		if (_open)
		{
			return;
		}
		_player = player;
		_forge = forge;
		_gameClient = GameClient.Current;
		if (_gameClient != null)
		{
			_gameClient.InputSuppressed = true;
			if (_gameClient.hud != null) { _gameClient.hud.Visible = false; }
			// A DoT can kill the player while camping (camping gates on danger, not
			// on damaging status) — tear down camp state if it does.
			_gameClient.onPlayerDied += OnPlayerDied;
		}
		Input.MouseMode = Input.MouseModeEnum.Visible;
		_player?.ClearInteractive();
		_player?.EnterCamp();
		MusicManager.Instance?.SetCamping(true);
		// Lower-pitch zoomed-in framing (with a transition blur) and hold the
		// day/night clock while resting.
		_gameClient?.camera?.SetCampMode(true);
		if (_player?.World != null) { _player.World.TimeOfDayFrozen = true; }
		_open = true;
		Visible = true;
		// Open the default tab even though _curTab already equals it — there is
		// no active sub-screen yet, so force the bind.
		_curTab = ECampTab.Sleep;
		OpenTab(ECampTab.Sleep);
	}

	public void Close()
	{
		if (!_open)
		{
			return;
		}
		CloseTab(_curTab);
		_open = false;
		Visible = false;
		_player?.ExitCamp();
		MusicManager.Instance?.SetCamping(false);
		_gameClient?.camera?.SetCampMode(false);
		if (_player?.World != null) { _player.World.TimeOfDayFrozen = false; }
		if (_gameClient != null)
		{
			_gameClient.onPlayerDied -= OnPlayerDied;
			_gameClient.InputSuppressed = false;
			if (_gameClient.hud != null) { _gameClient.hud.Visible = true; }
		}
		Input.MouseMode = Input.MouseModeEnum.Captured;
		_player = null;
		_forge = null;
	}

	// Switch to a tab: tear down the current sub-screen, bind the new one. The
	// previously-active tab's Close() runs its own cleanup (cooking returns idle
	// inputs to the bag; stash drops any pending selection).
	void ShowTab(ECampTab tab)
	{
		if (tab == _curTab)
		{
			return;
		}
		CloseTab(_curTab);
		OpenTab(tab);
	}

	void OpenTab(ECampTab tab)
	{
		_curTab = tab;
		UpdateTabHighlight();
		switch (tab)
		{
			case ECampTab.Sleep:
				_sleepScreen?.Open(_player, _forge?.HealFractionPerHour ?? 0f, RequestSleep);
				break;
			case ECampTab.Cook:
				_cookingScreen?.Open(_player, _forge);
				break;
			case ECampTab.Stash:
				_stashScreen?.Open(_player, GlobalStash());
				break;
		}
	}

	void CloseTab(ECampTab tab)
	{
		switch (tab)
		{
			case ECampTab.Sleep:
				_sleepScreen?.Close();
				break;
			case ECampTab.Cook:
				_cookingScreen?.Close();
				break;
			case ECampTab.Stash:
				_stashScreen?.Close();
				break;
		}
	}

	List<ItemState> GlobalStash()
	{
		return _player?.World?.WorldState?.SimState?.CampStash;
	}

	// SleepScreen callback: hide the camp UI but keep the player in camp state
	// (concealed, SitIdle, camp music, input gated) through the sleep fade + skip.
	// RestoreFromSleep re-shows the UI on a clean wake; OnPlayerDied tears it down
	// if a DoT kills the player mid-skip.
	void RequestSleep(double hours, double healFractionPerHour)
	{
		if (hours <= 0.0 || !_open)
		{
			return;
		}
		Visible = false;
		_gameClient?.BeginSleepFromCamp(hours, healFractionPerHour, RestoreFromSleep);
	}

	// Wake callback from GameClient.EndSleep: the input gate was handed back to us
	// rather than released, so the player is still camping. Re-show the UI and
	// re-open the active tab so its state refreshes (health/time changed during
	// the skip) and focus is restored.
	void RestoreFromSleep()
	{
		if (!_open)
		{
			return;
		}
		Visible = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		OpenTab(_curTab);
	}

	// A DoT killed the player while camping (open or mid-sleep). The death / respawn
	// flow now owns input and the HUD — drop camp state and the UI without touching
	// the input gate.
	void OnPlayerDied(Player player)
	{
		if (!_open)
		{
			return;
		}
		_open = false;
		Visible = false;
		_player?.ExitCamp();
		MusicManager.Instance?.SetCamping(false);
		_gameClient?.camera?.SetCampMode(false);
		if (_player?.World != null) { _player.World.TimeOfDayFrozen = false; }
		if (_gameClient != null)
		{
			_gameClient.onPlayerDied -= OnPlayerDied;
		}
		_player = null;
		_forge = null;
	}

	void UpdateTabHighlight()
	{
		SetTabActive(_sleepTab, _curTab == ECampTab.Sleep);
		SetTabActive(_cookTab, _curTab == ECampTab.Cook);
		SetTabActive(_stashTab, _curTab == ECampTab.Stash);
	}

	static void SetTabActive(Control tab, bool active)
	{
		if (tab != null)
		{
			tab.Modulate = active ? Colors.White : new Color(0.5f, 0.5f, 0.5f);
		}
	}

	void CycleTab(int direction)
	{
		int count = Enum.GetValues<ECampTab>().Length;
		int next = (((int)_curTab + direction) % count + count) % count;
		ShowTab((ECampTab)next);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		// Ignore input while hidden for a sleep skip (Visible false, _open true).
		if (!_open || !Visible)
		{
			return;
		}
		// Sub-screens (children) see _UnhandledInput first and consume ui_cancel
		// while they have an in-flight selection / count picker; a clean ui_cancel
		// falls through to here and closes the whole screen.
		if (e.IsActionPressed("ui_cancel"))
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
