using Godot;
using Godot.Collections;

// Fronts "traverse this" as an ordinary interactive, so every self-traversal the
// player can start from where they stand — mantling a ledge up or down, taking
// hold of a climbable wall face, backing over a lip onto one — goes through the
// same path every chest and NPC uses: tap-to-run-the-default, hold-for-the-
// option-menu, a prompt drawn at the target, an authored icon and label.
//
// One interactive for all of them, not one each: only ever one is offered (the
// player resolves them in a fixed order — see TryFindTraversal), and two
// competing for the highlight slot would be two ways to answer a question that
// has one answer.
//
// Modelled on PlayerSelfInteractive — not a world entity, no InteractiveBox,
// never found by proximity. The Player offers it as the highlight when nothing
// real is in range, which is what ranks a chest above a ledge when the player
// stands at both.
//
// Not a Node3D, deliberately: there is nothing here to outline. GameClient casts
// the highlight to Node3D for the selection outline and gets null, which is the
// correct result for a ledge or a cliff face.
public sealed class TraversalInteractive : IInteractive
{
	private readonly Player _player;
	private bool _available;
	// Whether the traversal on offer goes DOWN, which is all the prompt needs
	// from it: the action carries the arrow and the label, and the player
	// re-resolves the traversal itself when the action completes.
	private bool _descending;

	// Where the prompt is drawn, smoothed. A traversal's landing point is a
	// voxel CENTRE, so anchoring to it directly makes the prompt jump a metre
	// sideways every time the player crosses a cell boundary while walking along
	// a ledge. The player supplies a target derived from their own continuous
	// position instead; this only has to absorb the remaining discontinuity,
	// which is the landing HEIGHT changing in whole voxels.
	private Vector3 _anchor;
	private bool _anchorValid;

	// One cached array per direction. GetActions is polled by the HUD every
	// frame, and a Godot Array is a native container — rebuilding one per call
	// would marshal on every frame for a prompt that never changes.
	private Array<InteractiveAction> _ascendActions;
	private Array<InteractiveAction> _descendActions;

	public TraversalInteractive(Player player)
	{
		_player = player;
	}

	// Refreshed once per tick by the player's highlight pass. anchorTarget is
	// where the prompt wants to be this tick; it is eased toward rather than
	// taken outright, except on the frame the prompt appears — easing in from
	// wherever the last ledge was would fly it across the world.
	public void SetTarget(bool available, bool descending, Vector3 anchorTarget, float dt)
	{
		_available = available;
		_descending = descending;
		if (!available)
		{
			_anchorValid = false;
			return;
		}
		if (!_anchorValid)
		{
			_anchor = anchorTarget;
			_anchorValid = true;
			return;
		}
		// Horizontal is taken outright: the caller derives it from the player's
		// own position and facing, so it is already continuous and easing it
		// would only add lag to something that never jumps. Height is the one
		// term that steps in whole voxels, so it is the only one eased.
		_anchor.X = anchorTarget.X;
		_anchor.Z = anchorTarget.Z;
		float tau = _player.data?.traversalPromptSmoothTime ?? 0f;
		float k = tau > 0f ? 1f - Mathf.Exp(-dt / tau) : 1f;
		_anchor.Y = Mathf.Lerp(_anchor.Y, anchorTarget.Y, k);
	}

	// Over what we would end up on — up top when climbing, down below when
	// dropping — so the prompt reads as "go there", not "you are here".
	public Vector3 hudPosition => _anchor;

	public bool CanInteract() => _available;
	public bool CanActorInteract(Player player) => _available && player == _player;

	// No X-ray silhouette: there is no mesh to show through a wall.
	public bool ShouldShowXray() => false;

	// The runner fires this from the action's OpenInteractive completion event.
	// Starting the traversal directly from here would race the runner's own
	// teardown, so the player defers it by a tick.
	public void Complete(int actionIndex)
	{
		_player.OnTraversalInteractComplete();
	}

	public Array<InteractiveAction> GetActions(Player player)
	{
		PlayerData data = _player.data;
		if (data == null)
		{
			return null;
		}
		if (_descending)
		{
			_descendActions ??= Wrap(data.traversalDownAction);
			return _descendActions;
		}
		_ascendActions ??= Wrap(data.traversalUpAction);
		return _ascendActions;
	}

	// A null action means the .tres was never assigned on PlayerData. Returning
	// an empty list makes the interactive offer nothing, so the press falls
	// through to the self-action menu rather than starting an action with no
	// timeline.
	private static Array<InteractiveAction> Wrap(InteractiveAction action)
	{
		var list = new Array<InteractiveAction>();
		if (action != null)
		{
			list.Add(action);
		}
		return list;
	}
}
