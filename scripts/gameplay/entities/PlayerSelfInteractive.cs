using Godot;
using Godot.Collections;

// A menu-only IInteractive fronting the player's always-available self-actions
// (Pray, and future rituals). NOT a world entity: it has no InteractiveBox, is
// never highlighted by proximity, and is never the default press action. It is
// surfaced only through the interact menu — appended to a highlighted world
// interactive's option list, or shown alone when the player opens the menu with
// nothing highlighted. Each self-action carries its own behavior via completion
// ItemEffects (e.g. PrayReturnHomeEffect), so Complete() is a no-op; this shell
// exists only so a self-action can run through the same ActionRunner /
// _curInteractive plumbing world interactions use. Owned by the Player.
public sealed class PlayerSelfInteractive : IInteractive
{
	readonly Player _player;

	public PlayerSelfInteractive(Player player)
	{
		_player = player;
	}

	// Anchor the (menu-only) HUD to the player themselves.
	public Vector3 hudPosition => _player.GlobalPosition;
	public bool CanInteract() => true;
	public bool CanActorInteract(Player player) => true;

	// Behavior lives on each action's completion ItemEffects, not here.
	public void Complete(int actionIndex) { }

	public Array<InteractiveAction> GetActions(Player player) => _player.SelfActions;
}
