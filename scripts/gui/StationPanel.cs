using Godot;
using Godot.Collections;
using System.Collections.Generic;

// A station's experimentation input slots, its commit button (Cook / Craft), the
// list of its discovered recipes and the result banner. Like InventoryPanel this
// script owns only slot/focus/input plumbing — the backing store
// (the station's InputSlots) lives on the station, and the verb behavior
// (recipe match, reagent spending) lives in StationScreen. The screen hands us
// the slot array on Bind.
[GlobalClass]
public partial class StationPanel : MarginContainer
{
	// The experimentation slots every station has. Mirrored by station_panel.tscn's
	// _itemInputs — adding a slot here requires adding a matching ItemSlotPanel there.
	public const int SlotCount = 3;

	[Export] private Array<ItemSlotPanel> _itemInputs;
	[Export] private Button _commitButton;
	[Export] private Control _announcementContainer;
	[Export] private Label _announcementLabel;
	[Export] private TextureRect _announcementIcon;
	// How long the announcement stays on screen before auto-hiding. Drives
	// the timer in _Process; setting this to 0 keeps it visible until the
	// next ShowAnnouncement / HideAnnouncement / open.
	[Export] private float _announcementSeconds = 3f;
	[Export] private Control _recipeButtonContainer;
	[Export] private Label _noReceipesLabel;
	[Export] private PackedScene _recipeButtonScene;

	// Backing storage for slot contents — injected on Bind by the screen so
	// edits flow straight through to the persistent owner (the station). Length
	// must match _itemInputs.Count; null until Bind.
	ItemState[] _slots;

	// Fires whenever the focused slot's currently-displayed ItemState changes,
	// either because focus moved between input slots or because an input
	// slot's content was mutated. Screen subscribes to refresh the item info
	// panel and update Remove button-hint visibility.
	public System.Action<ItemSlotPanel, ItemState> onFocusedItemChanged;

	// Primary verb on a focused input slot (ui_accept). Tap returns one
	// unit to the inventory; hold opens the count picker. Both null = the
	// panel never tries to dispatch the verb.
	public System.Action<int, ItemSlotPanel, ItemState> onPrimaryTap;
	public System.Action<int, ItemSlotPanel, ItemState> onPrimaryHoldComplete;

	// Commit button pressed — an instant experimentation attempt.
	public System.Action onCommitPressed;

	// A recipe button in the list was clicked. Screen handles the reagent spend
	// and grants the item.
	public System.Action<Resource> onRecipeSelected;

	// A recipe button gained focus (gamepad navigation or mouse hover). Screen
	// previews the recipe's output.
	public System.Action<Resource> onRecipeFocused;

	public ButtonHint ButtonHintPrimary { get; set; }

	public Button CommitButton => _commitButton;
	public IReadOnlyList<ItemSlotPanel> Slots => _itemInputs;
	public IReadOnlyList<ItemState> Inputs => _slots;
	public ItemSlotPanel FocusedPanel => _focused;
	public int FocusedIndex => _focused != null && _itemInputs != null ? _itemInputs.IndexOf(_focused) : -1;
	public bool HoldLocked { get; set; }

	const float HoldSeconds = 0.5f;

	// Recipe button cache keyed by recipe. Diff-based rebuild in
	// RefreshRecipes reuses these so the focused button survives an
	// inventory mutation.
	readonly System.Collections.Generic.Dictionary<Resource, Button> _recipeButtons = new();

	ItemSlotPanel _focused;
	ItemState _lastFocusedItem;
	ItemSlotPanel _primaryPressed;
	float _primaryHold;
	bool _primaryHoldFired;
	bool _active;
	// Auto-hide countdown for the announcement banner. 0 = hidden / not
	// counting; positive = currently showing and ticking down each frame.
	float _announcementRemaining;

	public override void _Ready()
	{
		if (_itemInputs != null)
		{
			int inputCount = _itemInputs.Count;
			for (int i = 0; i < inputCount; i++)
			{
				ItemSlotPanel panel = _itemInputs[i];
				if (panel == null)
				{
					continue;
				}
				panel.onFocusEntered += OnPanelFocused;
				panel.onButtonDown += OnPanelButtonDown;
				panel.onButtonUp += OnPanelButtonUp;
				panel.SetItem(null);
			}
		}
		if (_commitButton != null)
		{
			_commitButton.Pressed += OnCommitButtonPressed;
		}
		HideAnnouncement();
	}

	// Show the result banner. `text` lands in _announcementLabel and
	// `icon` (may be null — e.g. for failed cooks) lands in
	// _announcementIcon. Auto-hides after _announcementSeconds via the
	// _Process tick below.
	public void ShowAnnouncement(string text, Texture2D icon)
	{
		if (_announcementContainer == null)
		{
			return;
		}
		if (_announcementLabel != null)
		{
			_announcementLabel.Text = text ?? string.Empty;
		}
		if (_announcementIcon != null)
		{
			_announcementIcon.Texture = icon;
			_announcementIcon.Visible = icon != null;
		}
		_announcementContainer.Visible = true;
		_announcementRemaining = Mathf.Max(0f, _announcementSeconds);
	}

	public void HideAnnouncement()
	{
		_announcementRemaining = 0f;
		if (_announcementContainer != null)
		{
			_announcementContainer.Visible = false;
		}
	}

	public override void _ExitTree()
	{
		if (_commitButton != null)
		{
			_commitButton.Pressed -= OnCommitButtonPressed;
		}
	}

	// Bind the panel to its backing slot array (the station's InputSlots).
	// All TryAdd / TryRemove / DrainInputs operations mutate this array
	// directly so the persistent owner sees the changes immediately.
	public void Bind(ItemState[] slots)
	{
		_slots = slots;
		_active = true;
		Refresh();
	}

	public void Unbind()
	{
		_active = false;
		CancelHeld();
		_slots = null;
		// Clear the visuals so a stale icon from the previous station
		// doesn't flash on the next open.
		if (_itemInputs != null)
		{
			foreach (ItemSlotPanel panel in _itemInputs)
			{
				panel?.SetItem(null);
			}
		}
		_lastFocusedItem = null;
	}

	// Add `amount` units of `donor.data` to the inputs. Stacks into
	// an existing matching input first, then falls back to the first empty
	// slot, then refuses the remainder. Returns the number of units actually
	// placed (caller leaves any unplaced count in the inventory).
	public int TryAdd(ItemState donor, int amount)
	{
		if (donor?.data == null || amount <= 0 || _slots == null)
		{
			return 0;
		}
		int placed = 0;
		// Pass 1: stack onto an existing slot of the same item kind.
		for (int i = 0; i < _slots.Length && placed < amount; i++)
		{
			ItemState existing = _slots[i];
			if (existing == null || !existing.IsSameKind(donor))
			{
				continue;
			}
			int room = existing.RemainingStackSpace();
			if (room <= 0)
			{
				continue;
			}
			int delta = Mathf.Min(room, amount - placed);
			// Input slots are spoil-agnostic staging (recipes match by kind, not
			// deadline); the real stash decrement happens oldest-first at the caller.
			existing.AddUnits(delta, 0);
			placed += delta;
		}
		// Pass 2: drop into the first empty slot.
		for (int i = 0; i < _slots.Length && placed < amount; i++)
		{
			if (_slots[i] != null)
			{
				continue;
			}
			ItemState fresh = donor.data.CreateState();
			int delta = Mathf.Min(donor.data.maxStack, amount - placed);
			fresh.SetCount(delta);
			_slots[i] = fresh;
			placed += delta;
		}
		if (placed > 0)
		{
			Refresh();
		}
		return placed;
	}

	// Remove up to `amount` units from input slot `index`. Returns the
	// removed ItemState (a fresh stack with the actually-removed count) or
	// null if no removal happened.
	public ItemState TryRemove(int index, int amount)
	{
		if (_slots == null || index < 0 || index >= _slots.Length || amount <= 0)
		{
			return null;
		}
		ItemState slot = _slots[index];
		if (slot == null)
		{
			return null;
		}
		int taken = Mathf.Min(amount, slot.stackCount);
		if (taken <= 0)
		{
			return null;
		}
		ItemState extracted = slot.SplitOff(taken);
		if (slot.stackCount <= 0)
		{
			_slots[index] = null;
		}
		Refresh();
		return extracted;
	}

	// Empty every input slot. Returns the previous contents so the screen
	// can re-home them in the inventory (or drop them on the ground) when
	// the player leaves without committing.
	public List<ItemState> DrainInputs()
	{
		var drained = new List<ItemState>();
		if (_slots == null)
		{
			return drained;
		}
		for (int i = 0; i < _slots.Length; i++)
		{
			ItemState s = _slots[i];
			if (s != null)
			{
				drained.Add(s);
				_slots[i] = null;
			}
		}
		Refresh();
		return drained;
	}

	// Sync the slot views to the backing array and pulse focus state so the
	// screen can refresh side panels.
	public void Refresh()
	{
		if (_itemInputs == null)
		{
			return;
		}
		int inputCount = _itemInputs.Count;
		for (int i = 0; i < inputCount; i++)
		{
			ItemState state = (_slots != null && i < _slots.Length) ? _slots[i] : null;
			_itemInputs[i]?.SetItem(state);
		}
		EmitFocusedItem();
	}

	// One row of the recipe list: what the recipe makes (the key handed back on
	// select / focus), how it reads, and whether the maker can pay for it now.
	public readonly struct RecipeRow
	{
		public readonly Resource key;
		public readonly string name;
		public readonly Texture2D icon;
		public readonly bool affordable;

		public RecipeRow(Resource key, string name, Texture2D icon, bool affordable)
		{
			this.key = key;
			this.name = name;
			this.icon = icon;
			this.affordable = affordable;
		}
	}

	// Reconcile the recipe list to `rows` — the station's discovered recipes.
	// Diff-based — existing buttons stay alive across refreshes so focus held on
	// a recipe button survives an inventory mutation. An unaffordable row is
	// disabled (selecting a recipe spends on the spot, so the slots'
	// experimental contents don't count).
	public void RefreshRecipes(System.Collections.Generic.IReadOnlyList<RecipeRow> rows)
	{
		if (_recipeButtonContainer == null)
		{
			return;
		}

		var desired = new System.Collections.Generic.HashSet<Resource>();
		for (int i = 0; i < rows.Count; i++)
		{
			desired.Add(rows[i].key);
		}

		// Drop buttons whose recipe no longer belongs in the list (only
		// happens on rebind to a different station; discovered recipes don't
		// un-discover within a session).
		var stale = new System.Collections.Generic.List<Resource>();
		foreach (Resource key in _recipeButtons.Keys)
		{
			if (!desired.Contains(key))
			{
				stale.Add(key);
			}
		}
		for (int i = 0; i < stale.Count; i++)
		{
			Button toFree = _recipeButtons[stale[i]];
			_recipeButtons.Remove(stale[i]);
			toFree?.QueueFree();
		}

		// Create missing buttons; refresh the Disabled flag on every entry.
		for (int i = 0; i < rows.Count; i++)
		{
			RecipeRow row = rows[i];
			if (!_recipeButtons.TryGetValue(row.key, out Button button))
			{
				button = CreateRecipeButton(row);
				if (button != null)
				{
					_recipeButtons[row.key] = button;
				}
			}
			if (button != null)
			{
				button.Disabled = !row.affordable;
			}
		}

		if (_noReceipesLabel != null)
		{
			_noReceipesLabel.Visible = _recipeButtons.Count == 0;
		}
	}

	Button CreateRecipeButton(RecipeRow row)
	{
		if (_recipeButtonScene == null || _recipeButtonContainer == null)
		{
			return null;
		}
		Button button = _recipeButtonScene.Instantiate<Button>();
		if (button == null)
		{
			return null;
		}
		button.Text = row.name;
		button.Icon = row.icon;
		Resource captured = row.key;
		button.Pressed += () => onRecipeSelected?.Invoke(captured);
		button.FocusEntered += () => onRecipeFocused?.Invoke(captured);
		// Hover grabs focus, matching ItemSlotPanel, so mouse and gamepad preview
		// the same way. A disabled (unaffordable) recipe still previews.
		button.MouseEntered += button.GrabFocus;
		_recipeButtonContainer.AddChild(button);
		return button;
	}

	public void SetCommitText(string text)
	{
		if (_commitButton != null)
		{
			_commitButton.Text = text;
		}
	}

	// The screen disables the commit button while the slots are empty — there's
	// nothing to make, and the button never leaves camp.
	public void SetCommitEnabled(bool enabled)
	{
		if (_commitButton != null)
		{
			_commitButton.Disabled = !enabled;
		}
	}

	void OnPanelFocused(ItemSlotPanel panel)
	{
		_focused = panel;
		CancelHeld();
		EmitFocusedItem();
	}

	void EmitFocusedItem(bool force = false)
	{
		ItemState current = _focused?.Item;
		if (!force && current == _lastFocusedItem)
		{
			return;
		}
		_lastFocusedItem = current;
		onFocusedItemChanged?.Invoke(_focused, current);
	}

	void OnPanelButtonDown(ItemSlotPanel panel)
	{
		_primaryPressed = panel;
		_primaryHold = 0f;
		_primaryHoldFired = false;
		ButtonHintPrimary?.SetProgress(0f);
	}

	void OnPanelButtonUp(ItemSlotPanel panel)
	{
		ItemSlotPanel pressed = _primaryPressed;
		_primaryPressed = null;
		ButtonHintPrimary?.SetProgress(0f);
		bool fired = _primaryHoldFired;
		_primaryHold = 0f;
		_primaryHoldFired = false;
		if (fired || HoldLocked || !_active)
		{
			return;
		}
		if (pressed != null && pressed != panel)
		{
			return;
		}
		int index = _itemInputs != null ? _itemInputs.IndexOf(panel) : -1;
		if (index < 0)
		{
			return;
		}
		onPrimaryTap?.Invoke(index, panel, panel?.Item);
	}

	void OnCommitButtonPressed()
	{
		if (!_active || HoldLocked)
		{
			return;
		}
		onCommitPressed?.Invoke();
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		// Announcement auto-hide ticks independently of _active so the banner
		// can finish its fade even after a screen close-and-reopen, but
		// Unbind() / Open() reset it explicitly so stale text doesn't survive
		// into a new session.
		if (_announcementRemaining > 0f)
		{
			_announcementRemaining -= dt;
			if (_announcementRemaining <= 0f)
			{
				HideAnnouncement();
			}
		}
		if (!_active)
		{
			return;
		}
		if (_primaryPressed == null || onPrimaryHoldComplete == null || HoldLocked || _primaryHoldFired)
		{
			return;
		}
		_primaryHold += dt;
		float progress = Mathf.Clamp(_primaryHold / HoldSeconds, 0f, 1f);
		ButtonHintPrimary?.SetProgress(progress);
		if (_primaryHold >= HoldSeconds)
		{
			ItemSlotPanel pressed = _primaryPressed;
			int index = _itemInputs != null ? _itemInputs.IndexOf(pressed) : -1;
			_primaryHoldFired = true;
			_primaryHold = 0f;
			ButtonHintPrimary?.SetProgress(0f);
			HoldLocked = true;
			if (index >= 0)
			{
				onPrimaryHoldComplete.Invoke(index, pressed, pressed?.Item);
			}
		}
	}

	// Flip focusability on every focus target in the panel — input slots
	// AND the commit button. Used by the screen to keep the stick from
	// walking focus off the count picker while it's up.
	public void SetSlotsFocusable(bool focusable)
	{
		if (_itemInputs != null)
		{
			foreach (ItemSlotPanel panel in _itemInputs)
			{
				panel?.SetFocusable(focusable);
			}
		}
		if (_commitButton != null)
		{
			_commitButton.FocusMode = focusable ? Control.FocusModeEnum.All : Control.FocusModeEnum.None;
		}
	}

	public void RestoreFocus()
	{
		ItemSlotPanel target = _focused ?? FindFirstFocusable();
		target?.GrabFocus();
	}

	// Focus the commit button so gamepad / keyboard can drive the primary
	// action the moment the tab opens, without navigating.
	// Deferred by the caller — GrabFocus needs the node visible-in-tree.
	public void GrabCommitButtonFocus()
	{
		_commitButton?.GrabFocus();
	}

	// Focus the first recipe button the party can currently cook (enabled ==
	// HasIngredients). Walks the container in visual order. Returns false when no
	// makeable recipe is listed so the screen can fall back to another target.
	public bool GrabFirstAvailableRecipeFocus()
	{
		if (_recipeButtonContainer == null)
		{
			return false;
		}
		foreach (Node child in _recipeButtonContainer.GetChildren())
		{
			if (child is Button button && button.Visible && !button.Disabled)
			{
				button.GrabFocus();
				return true;
			}
		}
		return false;
	}

	ItemSlotPanel FindFirstFocusable()
	{
		if (_itemInputs == null)
		{
			return null;
		}
		foreach (ItemSlotPanel panel in _itemInputs)
		{
			if (panel != null) { return panel; }
		}
		return null;
	}

	void CancelHeld()
	{
		_primaryHold = 0f;
		_primaryHoldFired = false;
		_primaryPressed = null;
		ButtonHintPrimary?.SetProgress(0f);
	}
}
