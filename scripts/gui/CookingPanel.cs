using Godot;
using Godot.Collections;
using System.Collections.Generic;

// The grid of cooking input slots, progress bar, and Cook / Cancel commit
// button. Like InventoryPanel this script owns only slot/focus/input
// plumbing — the persistent backing store (the campfire's TorchSimState
// CampfireSlots array) lives outside, and the verb behavior (recipe match,
// item routing, completion delivery) lives in CookingScreen. The screen
// hands us the slot array on Bind and drives the in-progress UI state via
// SetCookingProgress / SetCookingActive each frame.
[GlobalClass]
public partial class CookingPanel : MarginContainer
{
	[Export] private Array<ItemSlotPanel> _itemInputs;
	[Export] private Button _cookButton;
	[Export] private ProgressBar _cookingProgress;
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
	// edits flow straight through to the persistent owner (the campfire's
	// sim state). Length must match _itemInputs.Count; null until Bind.
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

	// Cook button pressed — fires onCookPressed in idle mode (start a cook
	// job) and onCancelPressed while a job is in flight (the button label
	// flips to "Cancel" via SetCookingActive).
	public System.Action onCookPressed;
	public System.Action onCancelPressed;

	// A recipe button in the right-hand list was clicked. Screen handles
	// the item routing (return slot contents to inventory, then load the
	// recipe's ingredients from inventory into slots).
	public System.Action<RecipeData> onRecipeSelected;

	public ButtonHint ButtonHintPrimary { get; set; }

	public Button CookButton => _cookButton;
	public IReadOnlyList<ItemSlotPanel> Slots => _itemInputs;
	public IReadOnlyList<ItemState> Inputs => _slots;
	public ItemSlotPanel FocusedPanel => _focused;
	public int FocusedIndex => _focused != null && _itemInputs != null ? _itemInputs.IndexOf(_focused) : -1;
	public bool HoldLocked { get; set; }
	public bool IsCooking => _cookingActive;

	const float HoldSeconds = 0.5f;

	// Recipe button cache keyed by recipe. Diff-based rebuild in
	// RefreshRecipes reuses these so the focused button survives an
	// inventory mutation.
	readonly System.Collections.Generic.Dictionary<RecipeData, Button> _recipeButtons = new();

	ItemSlotPanel _focused;
	ItemState _lastFocusedItem;
	ItemSlotPanel _primaryPressed;
	float _primaryHold;
	bool _primaryHoldFired;
	bool _active;
	// Mirrors the screen's "a cook is in flight" state — drives the button
	// label and routes the press to onCancelPressed vs onCookPressed.
	bool _cookingActive;
	// Label shown on the commit button while idle. The screen swaps this to
	// "Continue" when the slots are empty (a press then just leaves camp).
	string _idleLabel = "Cook!";
	// Auto-hide countdown for the announcement banner. 0 = hidden / not
	// counting; positive = currently showing and ticking down each frame.
	float _announcementRemaining;

	public override void _Ready()
	{
		if (_itemInputs != null)
		{
			for (int i = 0; i < _itemInputs.Count; i++)
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
		if (_cookButton != null)
		{
			_cookButton.Pressed += OnCookButtonPressed;
			_cookButton.Text = "Cook!";
		}
		if (_cookingProgress != null)
		{
			_cookingProgress.MinValue = 0;
			_cookingProgress.MaxValue = 1;
			_cookingProgress.Value = 0;
			_cookingProgress.Visible = false;
		}
		HideAnnouncement();
	}

	// Show the post-cook banner. `text` lands in _announcementLabel and
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
		if (_cookButton != null)
		{
			_cookButton.Pressed -= OnCookButtonPressed;
		}
	}

	// Bind the panel to its backing slot array (the campfire's CampfireSlots).
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
		// Clear the visuals so a stale icon from the previous campfire
		// doesn't flash on the next open.
		if (_itemInputs != null)
		{
			foreach (ItemSlotPanel panel in _itemInputs)
			{
				panel?.SetItem(null);
			}
		}
		SetCookingActive(false, 0f);
		_lastFocusedItem = null;
	}

	// Add `amount` units of `donor.data` to the cooking inputs. Stacks into
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
			existing.stackCount += delta;
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
			fresh.stackCount = delta;
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
		ItemState extracted = slot.data.CreateState();
		extracted.stackCount = taken;
		slot.stackCount -= taken;
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
		for (int i = 0; i < _itemInputs.Count; i++)
		{
			ItemState state = (_slots != null && i < _slots.Length) ? _slots[i] : null;
			_itemInputs[i]?.SetItem(state);
		}
		EmitFocusedItem();
	}

	// Rebuild the right-hand recipe list. Walks every recipe in SimData,
	// filters to those that (a) match this forge's type and (b) the player
	// has discovered, and reconciles one Button per discovered recipe.
	// Diff-based — existing buttons stay alive across refreshes so focus
	// held on a recipe button survives an inventory mutation. Buttons whose
	// ingredient demand can't be met by inventory + currently-loaded slots
	// come back disabled; the click handler swaps slot contents in-place
	// against the recipe target rather than draining and re-adding, so
	// items already in the right slots don't visibly flash.
	public void RefreshRecipes(Array<RecipeData> allRecipes, WorldSimState worldSim, System.Collections.Generic.IEnumerable<ItemState> available, ECampfireType forgeType)
	{
		if (_recipeButtonContainer == null)
		{
			return;
		}

		var combined = new System.Collections.Generic.Dictionary<ItemData, int>();
		if (available != null)
		{
			foreach (ItemState s in available)
			{
				AccumulateInto(combined, s);
			}
		}
		if (_slots != null)
		{
			for (int i = 0; i < _slots.Length; i++)
			{
				AccumulateInto(combined, _slots[i]);
			}
		}

		var desired = new System.Collections.Generic.HashSet<RecipeData>();
		if (worldSim != null && allRecipes != null)
		{
			for (int i = 0; i < allRecipes.Count; i++)
			{
				RecipeData recipe = allRecipes[i];
				if (recipe == null || recipe.forgeType != forgeType || recipe.inputs == null)
				{
					continue;
				}
				if (!worldSim.IsRecipeDiscovered(recipe))
				{
					continue;
				}
				if (recipe.outputItem != null)
				{
					desired.Add(recipe);
				}
			}
		}

		// Drop buttons whose recipe no longer belongs in the list (only
		// happens on rebind to a different forge; discovered recipes don't
		// un-discover within a session).
		var stale = new System.Collections.Generic.List<RecipeData>();
		foreach (var key in _recipeButtons.Keys)
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

		// Create missing buttons; refresh the Disabled flag AND the Text on
		// every entry — Text needs to re-evaluate because the output may
		// have just been identified (placeholder → real name), which fires
		// via Inventory.onChanged → RefreshRecipeList → here while the
		// cooking screen is still open.
		foreach (RecipeData recipe in desired)
		{
			if (!_recipeButtons.TryGetValue(recipe, out Button button))
			{
				button = CreateRecipeButton(recipe);
				if (button != null)
				{
					_recipeButtons[recipe] = button;
				}
			}
			if (button != null)
			{
				button.Disabled = !HasIngredients(recipe, combined);
				ItemData output = recipe.outputItem;
				if (output != null)
				{
					button.Text = worldSim != null
						? worldSim.GetItemDisplayName(output)
						: output.displayName.ToString();
				}
			}
		}

		if (_noReceipesLabel != null)
		{
			_noReceipesLabel.Visible = _recipeButtons.Count == 0;
		}
	}

	static void AccumulateInto(System.Collections.Generic.Dictionary<ItemData, int> totals, ItemState s)
	{
		if (s?.data == null || s.stackCount <= 0)
		{
			return;
		}
		totals.TryGetValue(s.data, out int existing);
		totals[s.data] = existing + s.stackCount;
	}

	Button CreateRecipeButton(RecipeData recipe)
	{
		if (_recipeButtonScene == null || _recipeButtonContainer == null)
		{
			return null;
		}
		ItemData output = recipe.outputItem;
		if (output == null)
		{
			return null;
		}
		Button button = _recipeButtonScene.Instantiate<Button>();
		if (button == null)
		{
			return null;
		}
		// Text is set by the caller (RefreshRecipes) on every refresh so the
		// label re-evaluates against the current identification state.
		button.Icon = output.inventorySprite;
		RecipeData captured = recipe;
		button.Pressed += () => onRecipeSelected?.Invoke(captured);
		_recipeButtonContainer.AddChild(button);
		return button;
	}

	// Loads the recipe's exact authored count for each ingredient — the
	// matcher already accepts anything in [count - range, count + range],
	// so the target count is always a valid match. Optional ingredients
	// (count <= 0) don't need to be present, so they don't count toward
	// "have enough".
	static bool HasIngredients(RecipeData recipe, System.Collections.Generic.Dictionary<ItemData, int> combined)
	{
		if (recipe?.inputs == null)
		{
			return false;
		}
		for (int i = 0; i < recipe.inputs.Count; i++)
		{
			RecipeInput input = recipe.inputs[i];
			if (input?.item == null)
			{
				return false;
			}
			int needed = input.count;
			if (needed <= 0)
			{
				continue;
			}
			combined.TryGetValue(input.item, out int available);
			if (available < needed)
			{
				return false;
			}
		}
		return true;
	}

	// Drives the commit button label + progress bar from the screen each
	// frame: idle = "Cook!" + progress hidden, cooking = "Cancel" + bar
	// visible at `progress` (0..1).
	public void SetCookingActive(bool active, float progress)
	{
		_cookingActive = active;
		if (_cookButton != null)
		{
			_cookButton.Text = active ? "Cancel" : _idleLabel;
		}
		if (_cookingProgress != null)
		{
			_cookingProgress.Visible = active;
			_cookingProgress.Value = Mathf.Clamp(progress, 0f, 1f);
		}
	}

	// Set the commit button's idle label (applied only while no cook is in
	// flight — a running job keeps the "Cancel" label). The screen uses this to
	// flip between "Cook!" (ingredients loaded) and "Continue" (slots empty).
	public void SetIdleLabel(string label)
	{
		_idleLabel = string.IsNullOrEmpty(label) ? "Cook!" : label;
		if (!_cookingActive && _cookButton != null)
		{
			_cookButton.Text = _idleLabel;
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
		// Mid-cook the inputs are frozen — ignore button presses on slots.
		if (_cookingActive)
		{
			return;
		}
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
		if (fired || HoldLocked || !_active || _cookingActive)
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

	void OnCookButtonPressed()
	{
		if (!_active || HoldLocked)
		{
			return;
		}
		if (_cookingActive)
		{
			onCancelPressed?.Invoke();
		}
		else
		{
			onCookPressed?.Invoke();
		}
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		// Announcement auto-hide ticks independently of _active / cooking
		// state so the banner can finish its fade even after a screen
		// close-and-reopen, but Unbind() / Open() reset it explicitly so
		// stale text doesn't survive into a new session.
		if (_announcementRemaining > 0f)
		{
			_announcementRemaining -= dt;
			if (_announcementRemaining <= 0f)
			{
				HideAnnouncement();
			}
		}
		if (!_active || _cookingActive)
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
	// AND the Cook button. Used by the screen to keep the stick from
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
		if (_cookButton != null)
		{
			_cookButton.FocusMode = focusable ? Control.FocusModeEnum.All : Control.FocusModeEnum.None;
		}
	}

	public void RestoreFocus()
	{
		ItemSlotPanel target = _focused ?? FindFirstFocusable();
		target?.GrabFocus();
	}

	// Focus the commit button so gamepad / keyboard can drive the primary
	// (Cook / Continue) action the moment the tab opens, without navigating.
	// Deferred by the caller — GrabFocus needs the node visible-in-tree.
	public void GrabCookButtonFocus()
	{
		_cookButton?.GrabFocus();
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
