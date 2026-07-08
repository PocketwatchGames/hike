using Godot;
using System;
using System.Collections.Generic;

// Modal camp hub, opened from a lit campfire (Campfire → EActionVerb.Camp). Mirrors
// AlmanacScreen: it owns the input gate (GameClient.InputSuppressed), hides the
// in-game HUD, releases the mouse, and cycles Sleep / Cook / Stash tabs with
// TabLeft / TabRight. While open the player is concealed from mobs and plays the
// SitIdle pose (Player.EnterCamp / ExitCamp). ui_cancel closes the whole screen.
//
// Unlike AlmanacScreen's read-only sub-screens, the cooking and stash tabs are
// data-bound per open: Cook attaches to the campfire's Campfire, Stash to the
// party equipment stash (WorldSimState.PartyEquipmentStash, reachable from any
// campfire).
// Each sub-screen is driven via its Open()/Close() — they no longer own any
// global gating of their own; this screen is the single owner.
[GlobalClass]
public partial class CampScreen : Control
{
	public enum ECampTab
	{
		Sleep,
		Party,
		Cook,
		Stash,
		Craft
	}

	[Export] SleepScreen _sleepScreen;
	[Export] PartyScreen _partyScreen;
	[Export] CookingScreen _cookingScreen;
	[Export] StashScreen _stashScreen;
	[Export] CraftingScreen _craftingScreen;
	[Export] ButtonHint _tabLeftButtonHint;
	[Export] ButtonHint _tabRightButtonHint;
	[Export] Control _sleepTab;
	[Export] Control _cookTab;
	[Export] Control _partyTab;
	[Export] Control _stashTab;
	[Export] Control _craftTab;

	GameClient _gameClient;
	Player _player;
	Campfire _forge;
	ECampTab _curTab;
	bool _open;
	// Forced death party-select: only the party tab is available and tab cycling
	// is locked, so the player picks a surviving member to control.
	bool _partySelectMode;
	// The campfire this camp is anchored to — used to re-gather the party when the
	// controlled member changes via the Select-Character tab.
	Vector3 _campfirePosition;

	public override void _Ready()
	{
		_tabLeftButtonHint?.SetHint("TabLeft", string.Empty);
		_tabRightButtonHint?.SetHint("TabRight", string.Empty);
		Visible = false;
	}

	public void Open(Player player, Campfire forge)
	{
		// Camping anchors the party to this campfire (a later death gathers
		// survivors here). The arrival bank / material transfer is done up front by
		// GameClient.EnterCampWithFade before this screen opens.
		OpenInternal(player, forge, forge?.GlobalPosition ?? player?.GlobalPosition ?? Vector3.Zero,
			ECampTab.Sleep, partySelectMode: false);
	}

	// Forced Select-Character screen after a party member's death: opens at the
	// last campfire locked to the party tab (no sleep/cook/stash), so the player
	// picks a surviving member to control. Driven by GameClient.OpenDeathPartySelect.
	public void OpenPartySelect(Player controlledSurvivor, Vector3 campfirePosition)
	{
		OpenInternal(controlledSurvivor, null, campfirePosition,
			ECampTab.Party, partySelectMode: true);
	}

	void OpenInternal(Player player, Campfire forge, Vector3 campfirePosition, ECampTab startTab, bool partySelectMode)
	{
		if (_open)
		{
			return;
		}
		_player = player;
		_forge = forge;
		_partySelectMode = partySelectMode;
		_campfirePosition = campfirePosition;
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
		// Seat the living party around the fire (the death party-select gathers
		// itself once the player picks a survivor, so skip it there).
		if (!_partySelectMode) { _gameClient?.GatherPartyAt(campfirePosition); }
		// Lower-pitch zoomed-in framing focused on the campfire (with a transition
		// blur) and hold the day/night clock while resting.
		_gameClient?.camera?.SetCampMode(true, campfirePosition);
		if (_player?.World != null) { _player.World.TimeOfDayFrozen = true; }
		// Party-select mode shows only the party tab (the player must pick, not rest).
		UpdateTabVisibility();
		_open = true;
		Visible = true;
		// Open the start tab even though _curTab may already equal it — there is
		// no active sub-screen yet, so force the bind.
		_curTab = startTab;
		OpenTab(startTab);
	}

	// Hide the non-party tab chips while in the forced death party-select.
	void UpdateTabVisibility()
	{
		bool full = !_partySelectMode;
		if (_sleepTab != null) { _sleepTab.Visible = full; }
		if (_cookTab != null) { _cookTab.Visible = full; }
		if (_stashTab != null) { _stashTab.Visible = full; }
		if (_tabLeftButtonHint != null) { _tabLeftButtonHint.Visible = full; }
		if (_tabRightButtonHint != null) { _tabRightButtonHint.Visible = full; }
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
		// Apply any Select-Character choice made this camp: control transfers to
		// the member the roster now marks active (no-op if unchanged). Runs after
		// camp teardown so it repoints the follow camera / HUD to the new member.
		// A deliberate switch carries the consumable belt to the new character; the
		// death-respawn switch (OnDeathBlackout / OnPartyMemberConfirmed) does not.
		_gameClient?.SyncControlToActive(transferBelt: true);
		Input.MouseMode = Input.MouseModeEnum.Captured;
		_player = null;
		_forge = null;
		// Restore the full tab set for the next (normal) camp.
		_partySelectMode = false;
		UpdateTabVisibility();
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
			case ECampTab.Party:
				// In the forced death select, choosing a member confirms + closes;
				// in normal camp there's no callback and the switch defers to close.
				_partyScreen?.Open(_gameClient, _partySelectMode ? OnPartyMemberConfirmed : null);
				break;
			case ECampTab.Cook:
				_cookingScreen?.Open(_player, _forge);
				break;
			case ECampTab.Stash:
				_stashScreen?.Open(_player, EquipmentStash());
				break;
			case ECampTab.Craft:
				_craftingScreen?.Open(_player, _forge);
				break;
		}
	}

	// Forced death party-select: choosing a member commits the pick — control
	// transfers to the chosen survivor now and the party re-gathers so they sit at
	// the fire — then the forced-select lock drops and the screen proceeds to the
	// stash tab so the player can outfit their new character before leaving.
	void OnPartyMemberConfirmed()
	{
		if (_gameClient != null)
		{
			_player?.ExitCamp();
			_gameClient.SyncControlToActive();
			_player = _gameClient.Player;
			_player?.EnterCamp();
			_gameClient.GatherPartyAt(_campfirePosition);
			_gameClient.camera?.SetCampMode(true, _campfirePosition);
		}
		_partySelectMode = false;
		UpdateTabVisibility();
		ShowTab(ECampTab.Stash);
	}

	void CloseTab(ECampTab tab)
	{
		switch (tab)
		{
			case ECampTab.Sleep:
				_sleepScreen?.Close();
				break;
			case ECampTab.Party:
				_partyScreen?.Close();
				break;
			case ECampTab.Cook:
				_cookingScreen?.Close();
				break;
			case ECampTab.Stash:
				_stashScreen?.Close();
				break;
			case ECampTab.Craft:
				_craftingScreen?.Close();
				break;
		}
	}

	List<ItemState> EquipmentStash()
	{
		return _player?.World?.WorldState?.SimState?.PartyEquipmentStash;
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
		SetTabActive(_partyTab, _curTab == ECampTab.Party);
		SetTabActive(_stashTab, _curTab == ECampTab.Stash);
		SetTabActive(_craftTab, _curTab == ECampTab.Craft);
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
		// Locked to the party tab during the forced death select.
		if (_partySelectMode)
		{
			return;
		}
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
			// The forced death select has no active character yet — the player
			// must pick a survivor, so backing out is disallowed.
			if (!_partySelectMode)
			{
				Close();
			}
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
