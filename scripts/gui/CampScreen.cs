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
// party equipment stash (SimState.PartyEquipmentStash, reachable from any
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
	}

	[Export] SleepScreen _sleepScreen;
	[Export] PartyScreen _partyScreen;
	[Export] SpellSelectionScreen _spellSelectionScreen;
	[Export] ButtonHint _tabLeftButtonHint;
	[Export] ButtonHint _tabRightButtonHint;
	[Export] Control _sleepTab;
	[Export] Control _spellsTab;
	[Export] Control _partyTab;

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
	// Set when the in-flight sleep is a full rest to sunrise (not a 1-hour nap) so
	// the wake handler can advance to the Party tab.
	bool _pendingSleepToSunrise;
	// The player owes a character choice (just woke to a new day). Like the death
	// party-select it pins the screen to the Party tab — no cooking, no backing
	// out — until a member is picked, but on pick it proceeds to the Cook tab
	// rather than confirming a death respawn.
	bool _mustSelectCharacter;

	// Any state that forces the player to pick a controlled member before doing
	// anything else: the forced death select or a fresh day's wake.
	bool SelectionLocked => _partySelectMode || _mustSelectCharacter;

	// Any alchemy spell is known — the Spells tab is only offered when there's at
	// least one spell to attune (otherwise the tab is empty). Walks SimData.spells
	// against the active member's knowledge (SimState.IsSpellKnown).
	bool AnySpellKnown
	{
		get
		{
			SimData simData = _player?.Sim?.SimData;
			SimState worldSim = _player?.Sim?.WorldState?.SimState;
			if (simData?.spells == null || worldSim == null)
			{
				return false;
			}
			for (int i = 0; i < simData.spells.Count; i++)
			{
				if (worldSim.IsSpellKnown(simData.spells[i]))
				{
					return true;
				}
			}
			return false;
		}
	}

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
		_mustSelectCharacter = false;
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
		if (_player?.Sim != null) { _player.Sim.TimeOfDayFrozen = true; }
		// Party-select mode shows only the party tab (the player must pick, not rest).
		UpdateTabVisibility();
		_open = true;
		Visible = true;
		// Open the start tab even though _curTab may already equal it — there is
		// no active sub-screen yet, so force the bind.
		_curTab = startTab;
		OpenTab(startTab);
	}

	// Hide the non-party tab chips whenever the player owes a character choice
	// (forced death select or a fresh day's wake). The Cook chip is additionally
	// withheld once the active member has eaten their meal for the day.
	void UpdateTabVisibility()
	{
		bool full = !SelectionLocked;
		if (_sleepTab != null) { _sleepTab.Visible = full; }
		if (_spellsTab != null) { _spellsTab.Visible = full && AnySpellKnown; }
		if (_partyTab != null) { _partyTab.Visible = full; }
		if (_tabLeftButtonHint != null) { _tabLeftButtonHint.Visible = full; }
		if (_tabRightButtonHint != null) { _tabRightButtonHint.Visible = full; }
	}

	// Whether a tab can currently be opened. The Spells tab (ECampTab.Cook) is
	// offered only when at least one spell is known; the others are always available
	// (SelectionLocked is gated separately in CycleTab).
	bool IsTabAvailable(ECampTab tab)
	{
		if (tab == ECampTab.Cook)
		{
			return AnySpellKnown;
		}
		return true;
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
		if (_player?.Sim != null) { _player.Sim.TimeOfDayFrozen = false; }
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
		_mustSelectCharacter = false;
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
				// In the forced death select, choosing a member confirms + closes; in
				// normal camp, selecting marks the member active (the control switch
				// still defers to camp close) and advances to the Cook tab.
				_partyScreen?.Open(_gameClient, _partySelectMode ? OnPartyMemberConfirmed : OnPartyMemberSelected);
				break;
			case ECampTab.Cook:
				// A completed cook leaves camp (see OnDishCooked); so does pressing
				// the primary button with nothing loaded ("Continue" → Close).
				_spellSelectionScreen?.Open(_player, _forge, onCooked: OnDishCooked, onContinue: Close);
				break;
		}
	}

	// Forced death party-select: choosing a member commits the pick — control
	// transfers to the chosen survivor now (the controlled character is a corpse, so
	// unlike the normal-camp select this can't defer to camp close) and the party
	// re-gathers so they sit at the fire — then proceed like any post-select.
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
		ProceedAfterSelection();
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
				_spellSelectionScreen?.Close();
				break;
		}
	}

	List<ItemState> EquipmentStash()
	{
		return _player?.Sim?.WorldState?.SimState?.PartyEquipmentStash;
	}

	// SleepScreen callback: hide the camp UI but keep the player in camp state
	// (concealed, SitIdle, camp music, input gated) through the sleep fade + skip.
	// RestoreFromSleep re-shows the UI on a clean wake; OnPlayerDied tears it down
	// if a DoT kills the player mid-skip.
	void RequestSleep(double hours, double healFractionPerHour, bool toSunrise)
	{
		// Sleep-to-sunrise ignores `hours`; a nap needs a positive duration.
		if ((!toSunrise && hours <= 0.0) || !_open)
		{
			return;
		}
		Visible = false;
		_pendingSleepToSunrise = toSunrise;
		_gameClient?.BeginSleepFromCamp(hours, healFractionPerHour, RestoreFromSleep, toSunrise);
	}

	// Wake callback from GameClient.EndSleep: the input gate was handed back to us
	// rather than released, so the player is still camping. Re-show the UI and
	// re-open the active tab so its state refreshes (health/time changed during
	// the skip) and focus is restored. A full rest to sunrise advances to the Party
	// tab (a new day banks campfire knowledge / invites a roster review); a 1-hour
	// nap stays on Sleep.
	void RestoreFromSleep()
	{
		if (!_open)
		{
			return;
		}
		Visible = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		ECampTab wakeTab = _pendingSleepToSunrise ? ECampTab.Party : _curTab;
		if (_pendingSleepToSunrise)
		{
			// New day: pin to the Party tab until the player (re)picks who they
			// control, hiding the other tab chips like the death select does.
			_mustSelectCharacter = true;
			UpdateTabVisibility();
		}
		_pendingSleepToSunrise = false;
		// Tear down the outgoing tab before binding the new one (OpenTab alone
		// doesn't close the previous sub-screen); a same-tab wake re-binds in place.
		if (wakeTab != _curTab)
		{
			CloseTab(_curTab);
		}
		OpenTab(wakeTab);
	}

	// Normal-camp Select-Character callback: the pick marks the roster's active
	// member (the control switch still defers to camp close). No control-transfer
	// prologue is needed — the controlled character is alive.
	void OnPartyMemberSelected()
	{
		ProceedAfterSelection();
	}

	// Shared tail for both Select-Character callbacks: drop any selection lock,
	// refresh tab visibility, then move to the Cook tab so the active member can
	// cook their meal — or close straight out if they've already eaten today (there's
	// nothing left to do at camp), transferring control to the pick on the way out.
	void ProceedAfterSelection()
	{
		_partySelectMode = false;
		_mustSelectCharacter = false;
		UpdateTabVisibility();
		if (!AnySpellKnown)
		{
			Close();
			return;
		}
		ShowTab(ECampTab.Cook);
	}

	// CookingScreen callback: a dish finished cooking — leave camp.
	void OnDishCooked()
	{
		Close();
	}

	// Open the almanac (world map / inventory / bestiary / recipes) over the camp
	// screen, keeping the player camped. The almanac owns input gating while up;
	// hide the camp UI (but stay _open) so our _UnhandledInput steps aside and the
	// almanac handles Map / ui_cancel. Closing it invokes ReturnFromAlmanac.
	void OpenAlmanac()
	{
		if (!_open || _gameClient?.almanacScreen == null || _gameClient.almanacScreen.Visible)
		{
			return;
		}
		Visible = false;
		_gameClient.almanacScreen.Open(AlmanacScreen.EAlmanacTab.WorldMap, _gameClient, onClose: ReturnFromAlmanac);
	}

	// Almanac closed (via its own ui_cancel/Map) — it released the input gate and
	// re-showed the HUD on the way out, so re-establish the camp gate, re-show the
	// camp UI, and re-bind the active tab to restore focus.
	void ReturnFromAlmanac()
	{
		if (!_open)
		{
			return;
		}
		if (_gameClient != null)
		{
			_gameClient.InputSuppressed = true;
			if (_gameClient.hud != null) { _gameClient.hud.Visible = false; }
		}
		Input.MouseMode = Input.MouseModeEnum.Visible;
		Visible = true;
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
		if (_player?.Sim != null) { _player.Sim.TimeOfDayFrozen = false; }
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
		SetTabActive(_spellsTab, _curTab == ECampTab.Cook);
		SetTabActive(_partyTab, _curTab == ECampTab.Party);
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
		// Pinned to the party tab while the player owes a character choice.
		if (SelectionLocked)
		{
			return;
		}
		// Step in `direction` past any unavailable tab (Cook once the active member
		// has eaten), landing on the next openable one.
		int count = Enum.GetValues<ECampTab>().Length;
		int next = (int)_curTab;
		for (int step = 0; step < count; step++)
		{
			next = ((next + direction) % count + count) % count;
			if (IsTabAvailable((ECampTab)next))
			{
				ShowTab((ECampTab)next);
				return;
			}
		}
	}

	// The Map action (back/Tab) opens the almanac. Handled in _Input rather than
	// _UnhandledInput because its Tab keybind is also Godot's ui_focus_next: the
	// GUI focus system consumes Tab during the GUI phase, before unhandled input
	// runs, so it would move control focus instead of reaching us. _Input runs
	// ahead of that. Only fires while the camp screen is the foreground modal
	// (Visible is false while the almanac is layered over it).
	public override void _Input(InputEvent e)
	{
		if (!_open || !Visible)
		{
			return;
		}
		if (e.IsActionPressed("Map"))
		{
			OpenAlmanac();
			GetViewport().SetInputAsHandled();
		}
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
			// While a character choice is owed (death select or a fresh day) there's
			// no committed active member — the player must pick, so backing out is
			// disallowed.
			if (!SelectionLocked)
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
