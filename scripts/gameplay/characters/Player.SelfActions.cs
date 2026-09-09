using Godot;
using Godot.Collections;

// Always-available "self" interactions — actions the player performs on themselves
// with no world interactive present (Pray now; more rituals later). Authored on the
// Player as inline InteractiveAction sub-resources, exactly like a world entity's
// _actions, so they share the whole InteractiveAction pipeline: reagent gating +
// spend (InteractiveAction.reagents → the shared HasReagents/SpendReagents pool
// path), requirements, timelines, and completion ItemEffects. They differ from world
// interactions only in trigger surface: they are reached by HOLDING interact and by
// nothing else — over a highlighted interactive they are appended to its option
// list, and with nothing highlighted the hold raises them on their own. A TAP never
// reaches one, even where it has nothing else to do, so the button's meaning does
// not change with what happens to be in front of the player.
public partial class Player : CharacterBody3D
{
	[Export] private Array<InteractiveAction> _selfActions = new();

	// Held-model scene shown in the player's hand while the Dig self-action runs
	// (verb == Dig). A self-action carries no item, so the tool prop lives here on
	// the player rather than on an ItemState.heldModel. Typically the shovel's
	// held model (scenes/items/held/shovel_held.tscn).
	[Export] private PackedScene _digToolHeldScene;

	// The tool prop to show in-hand for the in-flight interactive action, or null.
	// Today only the Dig self-action carries one; read by UpdateHeldItemVisual.
	public PackedScene ActiveInteractionHeldModel =>
		(_runner != null && _runner.IsBusy && _runner.Current.interactiveAction?.verb == EActionVerb.Dig)
			? _digToolHeldScene
			: null;

	// Menu-only shell IInteractive (built in _Ready) that fronts _selfActions so a
	// self-action runs through the same ActionRunner / _curInteractive path a world
	// interaction uses.
	PlayerSelfInteractive _selfInteractive;

	// True from the moment interact is pressed with nothing highlighted until the
	// press resolves (menu opened, or released early). Drives
	// GameClient.UpdateInteractHUD to spawn the menu-only HUD, which is what draws
	// the hold bar; cleared when the hold is abandoned or the menu closes.
	bool _selfPromptActive;

	public Array<InteractiveAction> SelfActions => _selfActions;
	public IInteractive SelfInteractive => _selfInteractive;
	public bool SelfPromptActive => _selfPromptActive;

	// True when the runner is driving an interactive action flagged fadeToBlack —
	// GameClient reads it to fade the screen off the live interact progress.
	public bool CurrentInteractiveFadesToBlack =>
		_runner != null && _runner.IsBusy && (_runner.Current.interactiveAction?.fadeToBlack ?? false);

	void InitSelfActions()
	{
		_selfInteractive = new PlayerSelfInteractive(this);
	}

	// Raise the self-action prompt with no world interactive present (the player is
	// holding interact in open space). Spawns the menu-only HUD via the
	// interact-changed refresh; that HUD shows the hold bar and opens its options
	// modal when the hold completes. No-op when there are no self-actions to show.
	public void ShowSelfPrompt()
	{
		if (_selfActions == null || _selfActions.Count == 0 || _selfPromptActive)
		{
			return;
		}
		_selfPromptActive = true;
		onInteractChanged?.Invoke(_selfInteractive);
	}

	// Drop it again — the hold was abandoned, or the menu it raised has closed.
	// Refreshes so GameClient frees the menu-only HUD, unless a self-action just
	// started and _curInteractive is keeping it alive for its progress ring.
	public void HideSelfPrompt()
	{
		if (!_selfPromptActive)
		{
			return;
		}
		_selfPromptActive = false;
		onInteractChanged?.Invoke(_curInteractive);
	}

	// The interact modal's option list for `target`: the world interactive's actions
	// (if target is a world interactive) followed by the always-available self-actions.
	// When target IS the self-interactive, just the self-actions (no duplication).
	// Allocates fresh — called only on modal open / selection, not per frame.
	public Array<InteractiveAction> GetMenuActions(IInteractive target)
	{
		var result = new Array<InteractiveAction>();
		if (target != null && target != _selfInteractive)
		{
			Array<InteractiveAction> worldActions = target.GetActions(this);
			if (worldActions != null)
			{
				foreach (InteractiveAction a in worldActions)
				{
					result.Add(a);
				}
			}
		}
		if (_selfActions != null)
		{
			foreach (InteractiveAction a in _selfActions)
			{
				result.Add(a);
			}
		}
		return result;
	}

	// Start the merged-menu entry at `index` (see GetMenuActions): the first
	// worldCount entries route to the world interactive, the rest to a self-action.
	// Both go through TryStartInteractiveAction, so reagent gating, requirements, and
	// _curInteractive tracking are identical either way.
	public bool TryStartMenuAction(IInteractive target, int index)
	{
		int worldCount = (target != null && target != _selfInteractive)
			? target.GetActions(this)?.Count ?? 0
			: 0;
		if (index < worldCount)
		{
			return TryStartInteractiveAction(target, index);
		}
		return TryStartInteractiveAction(_selfInteractive, index - worldCount);
	}
}
