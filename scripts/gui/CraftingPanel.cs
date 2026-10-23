using System.Collections.Generic;
using Godot;
using Godot.Collections;

// Left column of the CraftingScreen: the bench's job queue over the list of
// learned recipes, with a result banner. Owns only the widgets and their focus
// signals — the screen decides what a press does and feeds the rows in.
[GlobalClass]
public partial class CraftingPanel : MarginContainer
{
	// One per CraftQueue.Capacity, in queue order; the first is the job in progress.
	[Export] private Array<ItemSlotPanel> _queueSlots;
	// The head job's progress, drawn over the first queue slot.
	[Export] private ProgressBar _headProgress;
	// Shown while the finished head job waits for room in the stash.
	[Export] private Label _blockedLabel;
	[Export] private Control _announcementContainer;
	[Export] private Label _announcementLabel;
	[Export] private TextureRect _announcementIcon;
	// How long the banner stays up; 0 keeps it until the next one.
	[Export(PropertyHint.Range, "0,10,0.1")] private float _announcementSeconds = 3f;
	[Export] private Control _recipeButtonContainer;
	[Export] private Label _noRecipesLabel;
	[Export] private PackedScene _recipeButtonScene;

	public System.Action<ItemData> onRecipeSelected;
	public System.Action<ItemData> onRecipeFocused;
	// A queue slot gained focus, or was clicked; the index is the job's.
	public System.Action<int, ItemSlotPanel> onQueueFocused;
	public System.Action<int> onQueuePressed;

	// Diff-rebuilt so the focused recipe button survives a refresh.
	readonly System.Collections.Generic.Dictionary<ItemData, Button> _recipeButtons = new();
	float _announcementRemaining;

	public override void _Ready()
	{
		if (_queueSlots != null)
		{
			int count = _queueSlots.Count;
			for (int i = 0; i < count; i++)
			{
				ItemSlotPanel panel = _queueSlots[i];
				if (panel == null)
				{
					continue;
				}
				int index = i;
				panel.onFocusEntered += p => onQueueFocused?.Invoke(index, p);
				panel.onButtonUp += _ => onQueuePressed?.Invoke(index);
				panel.SetItem(null);
			}
		}
		if (_blockedLabel != null)
		{
			_blockedLabel.Text = Loc.Get(Loc.Keys.craft_queue_blocked);
		}
		HideAnnouncement();
	}

	public ItemSlotPanel QueueSlot(int index)
	{
		return _queueSlots != null && index >= 0 && index < _queueSlots.Count ? _queueSlots[index] : null;
	}

	// Paint the queue: each job's output in its slot, the head's progress at
	// `clock`.
	public void RefreshQueue(CraftQueue queue, double clock)
	{
		if (_queueSlots != null)
		{
			int count = _queueSlots.Count;
			for (int i = 0; i < count; i++)
			{
				CraftJob job = queue != null && i < queue.Count ? queue.Jobs[i] : null;
				ItemSlotPanel panel = _queueSlots[i];
				if (panel == null)
				{
					continue;
				}
				// A non-stacking item made several at a time still shows how many.
				int countBadge = job != null && !job.IsRepair && job.made != null && !job.made.IsStackable && job.count > 1 ? job.count : -1;
				panel.SetItem(job?.Preview(), countBadge);
			}
		}
		RefreshProgress(queue, clock);
	}

	// Per-frame: just the bar and the blocked note.
	public void RefreshProgress(CraftQueue queue, double clock)
	{
		bool running = queue != null && !queue.IsEmpty;
		bool blocked = running && queue.HeadBlocked;
		if (_headProgress != null)
		{
			_headProgress.Visible = running;
			_headProgress.Value = running ? queue.HeadProgress(clock) : 0.0;
		}
		if (_blockedLabel != null)
		{
			_blockedLabel.Visible = blocked;
		}
	}

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

	// One row of the recipe list. An unavailable row (can't be paid, or the queue
	// is full) is disabled but still previews on focus.
	public readonly struct RecipeRow
	{
		public readonly ItemData item;
		public readonly string name;
		public readonly bool available;

		public RecipeRow(ItemData item, string name, bool available)
		{
			this.item = item;
			this.name = name;
			this.available = available;
		}
	}

	public void RefreshRecipes(IReadOnlyList<RecipeRow> rows)
	{
		if (_recipeButtonContainer == null)
		{
			return;
		}
		var desired = new HashSet<ItemData>();
		for (int i = 0; i < rows.Count; i++)
		{
			desired.Add(rows[i].item);
		}
		var stale = new List<ItemData>();
		foreach (ItemData key in _recipeButtons.Keys)
		{
			if (!desired.Contains(key))
			{
				stale.Add(key);
			}
		}
		for (int i = 0; i < stale.Count; i++)
		{
			_recipeButtons[stale[i]]?.QueueFree();
			_recipeButtons.Remove(stale[i]);
		}
		for (int i = 0; i < rows.Count; i++)
		{
			RecipeRow row = rows[i];
			if (!_recipeButtons.TryGetValue(row.item, out Button button))
			{
				button = CreateRecipeButton(row);
				if (button != null)
				{
					_recipeButtons[row.item] = button;
				}
			}
			if (button != null)
			{
				button.Disabled = !row.available;
			}
		}
		if (_noRecipesLabel != null)
		{
			_noRecipesLabel.Visible = _recipeButtons.Count == 0;
		}
	}

	Button CreateRecipeButton(RecipeRow row)
	{
		if (_recipeButtonScene == null || _recipeButtonContainer == null)
		{
			return null;
		}
		Button button = _recipeButtonScene.Instantiate<Button>();
		button.Text = row.name;
		button.Icon = row.item.inventorySprite;
		ItemData captured = row.item;
		button.Pressed += () => onRecipeSelected?.Invoke(captured);
		button.FocusEntered += () => onRecipeFocused?.Invoke(captured);
		// Hover grabs focus, matching ItemSlotPanel, so mouse and gamepad preview
		// the same way.
		button.MouseEntered += button.GrabFocus;
		_recipeButtonContainer.AddChild(button);
		return button;
	}

	// Focus the first recipe that can be queued now. False when there is none.
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

	public override void _Process(double delta)
	{
		if (_announcementRemaining > 0f)
		{
			_announcementRemaining -= (float)delta;
			if (_announcementRemaining <= 0f)
			{
				HideAnnouncement();
			}
		}
	}
}
