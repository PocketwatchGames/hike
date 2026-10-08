using Godot;
using System.Collections.Generic;

// Modal camp hub, opened from a lit campfire (Campfire → EActionVerb.Camp). It
// owns the input gate (GameClient.InputSuppressed), hides the in-game HUD,
// releases the mouse, and while open conceals the player from mobs and plays the
// SitIdle pose (Player.EnterCamp / ExitCamp).
//
// Layout: a swappable body that is the CampRoot button hub or one of the Sleep /
// Select-Character / Stash / Cook sub-screens. The hub buttons switch the body;
// ui_cancel backs a sub-screen out to the hub, or (from the hub) leaves camp.
//
// The controlled character carries over between camps and rests — it changes only
// when the player picks another on the Select-Character screen.
[GlobalClass]
public partial class CampScreen : Control
{
	enum ECampView
	{
		Root,
		Sleep,
		Party,
		Stash,
		Cook,
	}

	[Export] SleepScreen _sleepScreen;
	[Export] PartyScreen _partyScreen;
	[Export] Control _campRoot;
	[Export] StashScreen _stashScreen;
	[Export] CookingScreen _cookingScreen;
	// CampRoot hub buttons.
	[Export] Button _sleepButton;
	[Export] Button _characterButton;
	[Export] Button _stashButton;
	[Export] Button _cookButton;
	[Export] Button _leaveButton;
	// Persistent per-character meal readout (lower right): the active food status
	// effect the chosen character ate today. The whole block hides when they have
	// no active meal (see RefreshCookpotPanel).
	[Export] Control _cookpotChosenPanel;
	[Export] StatusEffectInfoPanel _cookpotInfoPanel;

	GameClient _gameClient;
	Player _player;
	// The camp is always at the world's single lit fire, so resolve it LIVE from the sim
	// rather than caching a node: a death/return-home respawn opens before the fire's chunk has
	// streamed its entities back in, and this lets cooking enable itself the moment it does
	// (a cached snapshot would be null forever). Null when no fire is lit / not yet resident.
	Campfire Forge => _gameClient?.LitCampfireNode;
	ECampView _view;
	bool _open;

	// Cooking needs a lit fire resident. Resolved live, so it flips true on its own once a
	// respawn/return-home fire streams in (UpdateHubButtons re-runs from _Process while on the hub).
	bool CanCook => Forge != null;

	public override void _Ready()
	{
		Visible = false;
		if (_sleepButton != null) { _sleepButton.Pressed += OnSleepButton; }
		if (_characterButton != null) { _characterButton.Pressed += OnCharacterButton; }
		if (_stashButton != null) { _stashButton.Pressed += OnStashButton; }
		if (_cookButton != null) { _cookButton.Pressed += OnCookButton; }
		if (_leaveButton != null) { _leaveButton.Pressed += OnLeaveButton; }
	}

	public override void _ExitTree()
	{
		if (_sleepButton != null) { _sleepButton.Pressed -= OnSleepButton; }
		if (_characterButton != null) { _characterButton.Pressed -= OnCharacterButton; }
		if (_stashButton != null) { _stashButton.Pressed -= OnStashButton; }
		if (_cookButton != null) { _cookButton.Pressed -= OnCookButton; }
		if (_leaveButton != null) { _leaveButton.Pressed -= OnLeaveButton; }
	}

	// The arrival bank / material transfer is done up front by
	// GameClient.EnterCampWithFade before this screen opens. The lit fire (if any)
	// is read live, so cooking enables itself once a wake's fire streams back in.
	public void Open(Player player, Vector3 campfirePosition)
	{
		if (_open)
		{
			return;
		}
		_player = player;
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
		_gameClient?.GatherPartyAt(campfirePosition);
		// Lower-pitch zoomed-in framing focused on the campfire (with a transition
		// blur) and hold the day/night clock while resting.
		_gameClient?.camera?.SetCampMode(true, campfirePosition);
		if (_player?.Sim != null) { _player.Sim.TimeOfDayFrozen = true; }
		_open = true;
		Visible = true;
		// Land on the hub with Leave Camp focused so the player can back out
		// immediately.
		_view = ECampView.Root;
		ShowHubFocusingLeave();
	}

	// Leave Camp's focus is queued after OpenView(Root)'s default focus so it wins.
	void ShowHubFocusingLeave()
	{
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
		// follow camera / HUD to the new member.
		_gameClient?.SyncControlToActive();
		Input.MouseMode = Input.MouseModeEnum.Captured;
		_player = null;
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
			case ECampView.Stash:
				_stashScreen?.Close();
				break;
			case ECampView.Cook:
				_cookingScreen?.Close();
				break;
		}
	}

	void OpenView(ECampView view)
	{
		RefreshCookpotPanel();
		switch (view)
		{
			case ECampView.Root:
				if (_campRoot != null) { _campRoot.Visible = true; }
				UpdateHubButtons();
				_sleepButton?.CallDeferred(Control.MethodName.GrabFocus);
				break;
			case ECampView.Sleep:
				_sleepScreen?.Open(_player, Forge?.HealFractionPerHour ?? 0f, RequestSleep);
				break;
			case ECampView.Party:
				// Selecting marks the roster's active member (control transfers on camp
				// close) and returns to the hub.
				_partyScreen?.Open(_gameClient, OnCharacterChosen);
				break;
			case ECampView.Stash:
				_stashScreen?.Open(ChosenPlayer(), _player?.Sim?.WorldState?.SimState?.PartyStash);
				break;
			case ECampView.Cook:
				// Cooking grants the meal into the chosen character's backpack and stays
				// on this tab; ui_cancel backs out to the hub.
				_cookingScreen?.Open(ChosenPlayer(), Forge);
				break;
		}
	}

	void OnSleepButton() { ShowView(ECampView.Sleep); }
	void OnCharacterButton() { ShowView(ECampView.Party); }
	void OnStashButton() { ShowView(ECampView.Stash); }
	void OnCookButton() { if (CanCook) { ShowView(ECampView.Cook); } }
	void OnLeaveButton() { Close(); }

	public override void _Process(double delta)
	{
		// A respawn/return-home fire can stream in a few frames after the hub opens; re-sync the
		// hub buttons so the cook button enables itself the moment its fire becomes resident.
		if (_open && _view == ECampView.Root)
		{
			UpdateHubButtons();
		}
	}

	void UpdateHubButtons()
	{
		if (_cookButton != null) { _cookButton.Disabled = !CanCook; }
	}

	// PartyScreen pick callback: the roster's active member is set (control transfers
	// on camp close). Selecting a member AT the campfire commits their provisional field
	// knowledge to the shared party pool — the same bank a camp visit does — so what one
	// character learned is available to whoever the player switches to next. (Banks
	// Party.Active, which the pick just set.)
	void OnCharacterChosen()
	{
		_player?.Sim?.WorldState?.SimState?.BankActiveKnowledge();
		ShowHubFocusingLeave();
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
	// rather than released, so the player is still camping. Re-bind the sleep view to
	// refresh its health / time readout.
	void RestoreFromSleep()
	{
		if (!_open)
		{
			return;
		}
		Visible = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;
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

	// The persistent meal readout shows the chosen character's active food status
	// effect (the recipe they last ate today, EEffectCategory.Meal). A character who
	// hasn't eaten today (the effect expired at sunrise, or they never ate) hides the
	// whole block, as do the Select-Character and Stash views.
	void RefreshCookpotPanel()
	{
		StatusEffectData meal = ChosenPlayer()?.ActiveMealEffect;
		bool show = meal != null && _view != ECampView.Party && _view != ECampView.Stash;
		if (_cookpotChosenPanel != null)
		{
			_cookpotChosenPanel.Visible = show;
		}
		// StatusEffectInfoPanel.SetStatusEffect is a no-op on null (keeps its last
		// content), so only push when there's a meal — the block is hidden otherwise.
		if (show && _cookpotInfoPanel != null)
		{
			_cookpotInfoPanel.SetStatusEffect(meal);
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
				ShowView(ECampView.Root);
			}
			else
			{
				Close();
			}
			GetViewport().SetInputAsHandled();
		}
	}
}
