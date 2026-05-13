// Names a player action. Selection layer (input mapping, charge tier, hold-menu)
// resolves the verb; the runner then runs the profile authored for that verb.
// Mostly diagnostic at runtime — by the time an action is in flight, the
// profile fully describes the behavior. Names appear in saved data and combat
// logs, so be specific (Light/Heavy/Use/Open/Break/Lockpick) rather than
// generic (Primary/Secondary).
public enum EActionVerb
{
	Use,
	// NPC conversation — the interactive's Complete spawns a chatter HUD
	// bubble anchored to the speaker. Authored as an InteractiveAction with
	// verb=Talk on a friendly mob's _interactiveActions array.
	Talk,
	// Open the cooking screen against this interactive. Authored on lit
	// campfires; Torch.Complete branches on this verb and asks GameClient
	// for the shared CookingScreen instead of toggling the flame.
	Cook,
	// Ignite an unlit fire / torch / campfire — the interactive's Complete
	// flips the active state on and runs the on-fx.
	Light,
	// Extinguish a lit fire / torch / campfire — the secondary action on a
	// lit campfire so the player can re-cool the station without cycling
	// through the cooking screen.
	Douse,
}
