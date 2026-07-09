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
