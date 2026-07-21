using Godot;
using System.Collections.Generic;

// Modal camp hub, opened from a lit campfire (Campfire → EActionVerb.Camp). It
// owns the input gate (GameClient.InputSuppressed), hides the in-game HUD,
// releases the mouse, and while open conceals the player from mobs and plays the
// SitIdle pose (Player.EnterCamp / ExitCamp).
//
// Layout: a persistent status panel (top-right) always reads out the chosen
// character + their attuned spell, over a swappable body that is one of four
// views — the CampRoot button hub, or the Sleep / Select-Character / Select-Spell
// sub-screens. The hub buttons switch the body; ui_cancel backs a sub-screen out
// to the hub, or (from the hub) leaves camp.
//
// Leaving is gated only when the selection was reset and not yet re-made: after a
// night's rest and after a death there is no chosen character (the panel reads "No
// character selected", Leave Camp is disabled), so the player must pick before
// heading out. A plain campfire visit keeps the character + spell chosen last time,
// so the player can back out immediately without re-picking.
[GlobalClass]
public partial class CampScreen : Control
{
	// The swappable body view; the persistent chosen-character/spell panel sits
	// over whichever of these is showing.
	enum ECampView
	{
		Root,
		Sleep,
		Party,
		Spell,
	}

	[Export] SleepScreen _sleepScreen;
	[Export] PartyScreen _partyScreen;
	[Export] Control _campRoot;
	[Export] SpellSelectionScreen _spellSelectionScreen;
	// CampRoot hub buttons.
	[Export] Button _sleepButton;
	[Export] Button _characterButton;
	[Export] Button _spellButton;
	[Export] Button _leaveButton;
	// Persistent chosen-character readout.
	[Export] Label _chosenName;
	[Export] TextureRect _chosenPortrait;
	[Export] TextureRect _chosenArmor;
	[Export] TextureRect _chosenMelee;
	[Export] TextureRect _chosenRanged;
	[Export] Label _chosenStatus;
	// Persistent chosen-spell readout.
	[Export] Label _noSpellLabel;
	[Export] Control _noSpellPanel;
	[Export] ItemInfoPanel _chosenSpellPanel;
	// The two persistent readout blocks, hidden on the sub-screen that already
	// shows that info: the character block hides on Select-Character; the spell
	// block hides on Select-Character and Select-Spell.
	[Export] Control _playerChosenPanel;
	[Export] Control _spellChosenPanel;

	GameClient _gameClient;
	Player _player;
	Campfire _forge;
	// The campfire this camp is anchored to — used to re-gather the party when the
	// controlled member changes via the Select-Character screen.
	Vector3 _campfirePosition;
	ECampView _view;
	bool _open;
	// A character is committed for this camp — gates Leave Camp. Seeded from the sim's
	// per-day leader pick (SimParty.IsLeaderChosenToday): a plain visit inherits the
	// standing choice, but a death or a new day (the sim reset the pick) leaves it false
	// so the panel reads "No character selected" and Leave stays disabled until the
	// player chooses.
	bool _characterChosen;
	// Forced death Select-Character: the controlled character is a corpse, so the
	// pick transfers control immediately (not deferred to camp close) and backing
	// out of the party view is disallowed until a survivor is chosen.
	bool _deathSelect;
	// The last spell the player attuned this run — used to pre-highlight their
	// previous pick on the Select-Spell screen after a night's rest clears the
	// active attunement.
	SpellData _lastChosenSpell;

	// The sim-side party roster — source of the per-day leader pick (IsLeaderChosenToday,
	// reset by the day-roll in Sim.RequireLeaderChoice). The reset lives in sim so it
	// tracks the day-roll events (sleep / respawn / pray), not this UI.
	Party SimParty => _player?.Sim?.WorldState?.SimState?.Party;

	// Any alchemy spell is known — the Select-Spell button is only enabled when
	// there's at least one spell to attune. Walks SimData.spells against the active
	// member's knowledge (SimState.IsSpellKnown).
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
		Visible = false;
		if (_sleepButton != null) { _sleepButton.Pressed += OnSleepButton; }
		if (_characterButton != null) { _characterButton.Pressed += OnCharacterButton; }
		if (_spellButton != null) { _spellButton.Pressed += OnSpellButton; }
		if (_leaveButton != null) { _leaveButton.Pressed += OnLeaveButton; }
	}

	public override void _ExitTree()
	{
		if (_sleepButton != null) { _sleepButton.Pressed -= OnSleepButton; }
		if (_characterButton != null) { _characterButton.Pressed -= OnCharacterButton; }
		if (_spellButton != null) { _spellButton.Pressed -= OnSpellButton; }
		if (_leaveButton != null) { _leaveButton.Pressed -= OnLeaveButton; }
	}

	public void Open(Player player, Campfire forge)
	{
		// Camping anchors the party to this campfire (a later death gathers
		// survivors here). The arrival bank / material transfer is done up front by
		// GameClient.EnterCampWithFade before this screen opens.
		OpenInternal(player, forge, forge?.GlobalPosition ?? player?.GlobalPosition ?? Vector3.Zero,
			deathSelect: false);
	}

	// Forced Select-Character screen after a party member's death: opens at the last
	// campfire straight into the party view, locked there until the player picks a
	// surviving member to control. Driven by GameClient.OpenDeathPartySelect.
	public void OpenPartySelect(Player controlledSurvivor, Vector3 campfirePosition)
	{
		OpenInternal(controlledSurvivor, null, campfirePosition, deathSelect: true);
	}

	void OpenInternal(Player player, Campfire forge, Vector3 campfirePosition, bool deathSelect)
	{
		if (_open)
		{
			return;
		}
		_player = player;
		_forge = forge;
		_campfirePosition = campfirePosition;
		_deathSelect = deathSelect;
		// A plain campfire visit keeps whoever is controlled + their attuned spell; only
		// a new day (Sim.RequireLeaderChoice) or a death resets the pick. The death
		// select always forces a survivor pick.
		_characterChosen = !deathSelect && (SimParty?.IsLeaderChosenToday ?? true);
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
		// Seat the living party around the fire (the death select gathers itself once
		// the player picks a survivor, so skip it there).
		if (!deathSelect) { _gameClient?.GatherPartyAt(campfirePosition); }
		// Lower-pitch zoomed-in framing focused on the campfire (with a transition
		// blur) and hold the day/night clock while resting.
		_gameClient?.camera?.SetCampMode(true, campfirePosition);
		if (_player?.Sim != null) { _player.Sim.TimeOfDayFrozen = true; }
		_open = true;
		Visible = true;
		_view = ECampView.Root;
		if (_characterChosen)
		{
			// Plain visit, selection intact: land on the hub with Leave Camp focused so
			// the player can back out immediately.
			ShowView(ECampView.Root);
			_leaveButton?.CallDeferred(Control.MethodName.GrabFocus);
		}
		else
		{
			// New day or death reset the pick: run the guided flow to force it.
			AdvanceGuidedFlow();
		}
	}

	// Guided arrival flow: force the required picks in order — choose a character,
	// then (if none is attuned) choose a spell — before landing on the hub with Leave
	// Camp focused. Re-run after each pick and on a new day's wake; manual ui_cancel /
	// almanac return go straight to the hub without re-triggering it.
	void AdvanceGuidedFlow()
	{
		RefreshChosenPanel();
		if (!_characterChosen)
		{
			ShowView(ECampView.Party);
			return;
		}
		if (AnySpellKnown && ChosenPlayer()?.Inventory?.AttunedSpell == null)
		{
			ShowView(ECampView.Spell);
			return;
		}
		// Everything chosen — land on the hub and focus Leave Camp (queued after
		// OpenView(Root)'s default focus so it wins) so a confirm press heads out.
		ShowView(ECampView.Root);
		_leaveButton?.CallDeferred(Control.MethodName.GrabFocus);
	}

	public void Close()
	{
		if (!_open)
		{
			return;
		}
		HideView(_view);
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
		// Apply the Select-Character choice: control transfers to the roster's active
		// member (no-op if unchanged). Runs after camp teardown so it repoints the
		// follow camera / HUD to the new member. A deliberate switch carries the
		// attuned spell to the new character; the death-respawn switch (handled in
		// OnCharacterChosen) already transferred control.
		_gameClient?.SyncControlToActive(transferBelt: true);
		Input.MouseMode = Input.MouseModeEnum.Captured;
		_player = null;
		_forge = null;
		_deathSelect = false;
		_characterChosen = false;
	}

	// Switch the body view: tear down the current sub-screen (its Close() runs its
	// own cleanup), bind the new one.
	void ShowView(ECampView view)
	{
		HideView(_view);
		_view = view;
		OpenView(view);
	}

	void HideView(ECampView view)
	{
		switch (view)
		{
			case ECampView.Root:
				if (_campRoot != null) { _campRoot.Visible = false; }
				break;
			case ECampView.Sleep:
				_sleepScreen?.Close();
				break;
			case ECampView.Party:
				_partyScreen?.Close();
				break;
			case ECampView.Spell:
				_spellSelectionScreen?.Close();
				break;
		}
	}

	void OpenView(ECampView view)
	{
		UpdateChosenPanelVisibility();
		switch (view)
		{
			case ECampView.Root:
				if (_campRoot != null) { _campRoot.Visible = true; }
				RefreshChosenPanel();
				UpdateHubButtons();
				_sleepButton?.CallDeferred(Control.MethodName.GrabFocus);
				break;
			case ECampView.Sleep:
				_sleepScreen?.Open(_player, _forge?.HealFractionPerHour ?? 0f, RequestSleep);
				break;
			case ECampView.Party:
				// Selecting marks the roster's active member (control transfers on camp
				// close, or immediately in the death select) and returns to the hub.
				_partyScreen?.Open(_gameClient, OnCharacterChosen);
				break;
			case ECampView.Spell:
				// Attunes onto the chosen character so the panel and the attunement
				// agree even before the on-close control transfer.
				_spellSelectionScreen?.Open(ChosenPlayer(), _forge, OnSpellChosen, PreferredSpell());
				break;
		}
	}

	void OnSleepButton() { ShowView(ECampView.Sleep); }
	void OnCharacterButton() { ShowView(ECampView.Party); }
	void OnSpellButton() { if (AnySpellKnown) { ShowView(ECampView.Spell); } }
	void OnLeaveButton() { TryLeave(); }

	// Leave camp — gated on a chosen character (the Leave button is disabled and
	// ui_cancel from the hub is a no-op until then).
	void TryLeave()
	{
		if (_characterChosen)
		{
			Close();
		}
	}

	void UpdateHubButtons()
	{
		if (_spellButton != null) { _spellButton.Disabled = !AnySpellKnown; }
		if (_leaveButton != null) { _leaveButton.Disabled = !_characterChosen; }
	}

	// PartyScreen pick callback. Marks the character chosen for this camp; if the
	// controlled character was a corpse (death select) control transfers to the pick
	// now (the corpse can't stay controlled), otherwise it defers to camp close.
	// Returns to the hub.
	void OnCharacterChosen()
	{
		// A pick made during the forced flow (nobody committed yet) continues on to the
		// spell step; a manual mid-camp switch just returns to the hub.
		bool wasGuided = !_characterChosen;
		if (_deathSelect && _gameClient != null)
		{
			_player?.ExitCamp();
			_gameClient.SyncControlToActive();
			_player = _gameClient.Player;
			_player?.EnterCamp();
			_gameClient.GatherPartyAt(_campfirePosition);
			_gameClient.camera?.SetCampMode(true, _campfirePosition);
		}
		_deathSelect = false;
		_characterChosen = true;
		SimParty?.MarkLeaderChosen();
		if (wasGuided)
		{
			// Forced flow continues — if no spell is attuned yet, straight to the spell screen.
			AdvanceGuidedFlow();
		}
		else
		{
			// Manual switch during a plain camp — back to the hub, don't force a spell pick.
			ShowView(ECampView.Root);
			_leaveButton?.CallDeferred(Control.MethodName.GrabFocus);
		}
	}

	// SpellSelectionScreen pick callback: the spell was attuned on that screen.
	// Remember it as the previous pick and advance the flow (which now lands on the
	// hub with Leave Camp focused).
	void OnSpellChosen()
	{
		SpellData attuned = ChosenPlayer()?.Inventory?.AttunedSpell;
		if (attuned != null) { _lastChosenSpell = attuned; }
		AdvanceGuidedFlow();
	}

	// Spell to pre-highlight on the Select-Spell screen: the live attunement if any,
	// else the previous pick (which a night's rest clears from the live slot).
	SpellData PreferredSpell()
	{
		return ChosenPlayer()?.Inventory?.AttunedSpell ?? _lastChosenSpell;
	}

	// SleepScreen callback: hide the camp UI but keep the player in camp state
	// (concealed, SitIdle, camp music, input gated) through the sleep fade + skip.
	// RestoreFromSleep re-shows the UI on a clean wake; OnPlayerDied tears it down if
	// a DoT kills the player mid-skip.
	void RequestSleep(double hours, double healFractionPerHour, bool toSunrise)
	{
		// Sleep-to-sunrise ignores `hours`; a nap needs a positive duration.
		if ((!toSunrise && hours <= 0.0) || !_open)
		{
			return;
		}
		Visible = false;
		_gameClient?.BeginSleepFromCamp(hours, healFractionPerHour, RestoreFromSleep, toSunrise);
	}

	// Wake callback from GameClient.EndSleep: the input gate was handed back to us
	// rather than released, so the player is still camping. A rest to sunrise rolled the
	// day, so the sim reset the leader + spell pick (Sim.RequireLeaderChoice + the
	// client's OnNewDay attunement clear) — the player must re-pick (guided flow, Leave
	// disabled). A 1-hour nap rolls nothing, so the choice stands and we just re-bind the
	// sleep view to refresh its health / time readout.
	void RestoreFromSleep()
	{
		if (!_open)
		{
			return;
		}
		Visible = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		_characterChosen = SimParty?.IsLeaderChosenToday ?? _characterChosen;
		if (!_characterChosen)
		{
			AdvanceGuidedFlow();
			return;
		}
		ShowView(_view);
	}

	// The character the panel represents — the roster's active member's Player
	// (index-aligned with PartyPlayers). Falls back to the controlled player.
	Player ChosenPlayer()
	{
		if (_gameClient == null)
		{
			return _player;
		}
		IReadOnlyList<Player> players = _gameClient.PartyPlayers;
		int idx = _gameClient.ActivePartyIndex;
		if (players != null && idx >= 0 && idx < players.Count)
		{
			return players[idx];
		}
		return _player;
	}

	// Hide the persistent readouts on the sub-screen that already presents that
	// info: the character block on Select-Character, the spell block on
	// Select-Character and Select-Spell. Visible on the hub and Sleep views.
	void UpdateChosenPanelVisibility()
	{
		if (_playerChosenPanel != null)
		{
			_playerChosenPanel.Visible = _view != ECampView.Party;
		}
		if (_spellChosenPanel != null)
		{
			_spellChosenPanel.Visible = _view != ECampView.Party && _view != ECampView.Spell;
		}
	}

	void RefreshChosenPanel()
	{
		Player chosen = _characterChosen ? ChosenPlayer() : null;
		if (_chosenName != null)
		{
			_chosenName.Text = chosen != null ? chosen.PlayerName : "No character selected";
		}
		Inventory inv = chosen?.Inventory;
		SetIcon(_chosenArmor, inv?.GetEquipped(EInventorySlot.Armor)?.data?.inventorySprite);
		SetIcon(_chosenMelee, inv?.GetEquipped(EInventorySlot.WeaponMelee)?.data?.inventorySprite);
		SetIcon(_chosenRanged, inv?.GetEquipped(EInventorySlot.WeaponRanged)?.data?.inventorySprite);
		// No portrait art for player characters yet — keep the slot blank.
		if (_chosenPortrait != null) { _chosenPortrait.Visible = false; }
		if (_chosenStatus != null)
		{
			_chosenStatus.Text = (chosen?.Member?.IsWellRested ?? false) ? "Well Rested" : string.Empty;
		}
		RefreshChosenSpell(chosen);
	}

	void RefreshChosenSpell(Player chosen)
	{
		SpellData spell = chosen?.Inventory?.AttunedSpell;
		if (_chosenSpellPanel != null)
		{
			if (spell != null)
			{
				ItemState state = spell.CreateState();
				state.stackCount = 1;
				_chosenSpellPanel.SetItem(state);
			}
			else
			{
				_chosenSpellPanel.SetItem(null);
			}
		}
		// The "no spell selected" placeholder — hidden once a spell is chosen, since
		// the panel above already shows its name and details.
		if (_noSpellPanel != null)
		{
			_noSpellPanel.Visible = spell == null;
			_noSpellLabel.Text = "no spell selected";
		}
	}

	static void SetIcon(TextureRect rect, Texture2D texture)
	{
		if (rect != null)
		{
			rect.Texture = texture;
			rect.Visible = texture != null;
		}
	}

	// Open the almanac (world map / inventory / bestiary / recipes) over the camp
	// screen, keeping the player camped. The almanac owns input gating while up; hide
	// the camp UI (but stay _open) so our _UnhandledInput steps aside and the almanac
	// handles Map / ui_cancel. Closing it invokes ReturnFromAlmanac.
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
	// camp UI, and re-bind the active view to restore focus.
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
		OpenView(_view);
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
		HideView(_view);
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

	// The Map action (back/Tab) opens the almanac. Handled in _Input rather than
	// _UnhandledInput because its Tab keybind is also Godot's ui_focus_next: the GUI
	// focus system consumes Tab during the GUI phase, before unhandled input runs, so
	// it would move control focus instead of reaching us. _Input runs ahead of that.
	// Only fires while the camp screen is the foreground modal (Visible is false
	// while the almanac is layered over it).
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
		// Sub-screens (children) see _UnhandledInput first and consume ui_cancel while
		// they have an in-flight selection; a clean ui_cancel falls through to here.
		if (e.IsActionPressed("ui_cancel"))
		{
			if (_view != ECampView.Root)
			{
				// Back out of a sub-screen to the hub — unless a death select still
				// owes a survivor pick (there's no committed character to leave with).
				if (!(_view == ECampView.Party && _deathSelect))
				{
					ShowView(ECampView.Root);
				}
			}
			else
			{
				// From the hub, ui_cancel leaves camp — gated on a chosen character.
				TryLeave();
			}
			GetViewport().SetInputAsHandled();
		}
	}
}
