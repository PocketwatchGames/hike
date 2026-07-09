using Godot;
using Godot.Collections;

// A fallen party member's Player node stays where it died as a revivable corpse.
// While its hosted member is dead, the Player surfaces the shared party-revive
// verb (SimData.partyReviveAction) through the interactive system: a live party
// member walks up and interacts to revive it. Reviving respawns the member at
// the campfire (GameClient.RevivePartyMember); the action's completion event fx
// is the visual cue. An InteractiveBox child — monitorable only while dead —
// makes the body targetable without the live player ever detecting itself.
public partial class Player : IInteractive, ILiveMapMarker
{
	[Export] private InteractiveBox _corpseInteractiveBox;
	private Array<InteractiveAction> _reviveActions;

	// ILiveMapMarker: a fallen party member marks a grave where it lies, always
	// visible on the maps until revived. A live member shows nothing (its own
	// position isn't charted). Icon + tint are authored centrally on SimData.
	public bool ShouldShowMapMarker => Member is { IsDead: true };
	public Vector3 MapMarkerWorldPosition => GlobalPosition;
	public Texture2D MapMarkerIcon => _world?.SimData?.partyGraveMapMarkerIcon;
	public Color MapMarkerModulate => _world?.SimData?.liveMapMarkerColor ?? Colors.Yellow;

	public override void _ExitTree()
	{
		_world?.UnregisterLiveMapMarker(this);
		base._ExitTree();
	}

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
