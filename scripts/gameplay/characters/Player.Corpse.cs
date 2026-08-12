using Godot;
using Godot.Collections;

// A fallen party member's Player node stays where it died as a revivable corpse.
// While its hosted member is dead, the Player surfaces the shared party-revive
// verb (SimData.partyReviveAction) through the interactive system: a live party
// member walks up and interacts to revive it. Reviving respawns the member at
// the campfire (GameClient.RevivePartyMember); the action's completion event fx
// is the visual cue. An InteractiveBox child — monitorable only while dead —
// makes the body targetable without the live player ever detecting itself.
public partial class Player : IInteractive
{
	[Export] private InteractiveBox _corpseInteractiveBox;
	// Live map marker child (the grave). Authored into player.tscn with its icon
	// + tint; shown only while this party member is dead (see Initialize).
	[Export] private LiveMapMarker _liveMapMarker;
	private Array<InteractiveAction> _reviveActions;

	// Enable/disable the corpse's interactive detection. GameClient calls this on
	// death (true) and on revive (false). While alive the body must not be
	// interactable — it's the controlled character or a standing party member.
	public void SetCorpseInteractable(bool interactable)
	{
		if (_corpseInteractiveBox != null)
		{
			_corpseInteractiveBox.Monitorable = interactable;
		}
	}

	private bool IsRevivableCorpse =>
		Member is { IsDead: true } && _world?.SimData?.partyReviveAction != null;

	// Latched in HandleDeath: this member died airborne or in water, so its body
	// has to be relocated before it settles. Read at the death blackout rather
	// than live — by then a body that died mid-air may already have landed
	// somewhere unreachable, and the water state may have changed as it sank.
	private bool _diedOffGround;

	// Return a body that died off solid ground to the last spot its owner stood
	// on. A corpse left where it fell in deep water or off a cliff sinks, floats,
	// or lands somewhere a surviving member can't walk to — and the revive
	// interactive is the only way to get that member back. Called from the death
	// blackout (GameClient.OnDeathBlackout) so the move happens behind a black
	// screen. No-op for a body that died with its feet on the ground.
	public void ReturnBodyToLastGroundedPosition()
	{
		if (!_diedOffGround)
		{
			return;
		}
		_diedOffGround = false;
		// Newest history entry — the closest grounded spot to where they died.
		// (The stuck recovery deliberately reaches back to the OLDEST entry
		// instead; it needs distance from the edge tile that wedged the player.)
		int newest = (_safeGroundedHistoryWriteIdx + SafeGroundedHistorySize - 1) % SafeGroundedHistorySize;
		TeleportTo(_safeGroundedHistory[newest]);
		// Count the body as settled so TickInactive freezes it here instead of
		// re-running gravity from a position we already know is standable.
		_grounded = true;
	}

	public Vector3 hudPosition => hudAnchor != null ? hudAnchor.GlobalPosition : GlobalPosition;

	public bool CanInteract() => IsRevivableCorpse;

	public bool CanActorInteract(Player player) => IsRevivableCorpse;

	public Array<InteractiveAction> GetActions(Player player)
	{
		if (!IsRevivableCorpse)
		{
			return null;
		}
		_reviveActions ??= new Array<InteractiveAction> { _world.SimData.partyReviveAction };
		return _reviveActions;
	}

	public void Complete(int actionIndex)
	{
		if (IsRevivableCorpse)
		{
			GameClient.Current?.RevivePartyMember(this);
		}
	}
}
