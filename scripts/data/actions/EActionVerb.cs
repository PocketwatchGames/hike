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
	// Open the merchant screen in trade mode — present on every friendly
	// NPC (any mob authored with Talk) as a second interactive verb. The
	// trade mode shows the merchant-inventory and get-side panels so the
	// player can trade or gift items.
	GiveItem,
	// Open the merchant screen in two-way trade mode — replaces GiveItem
	// on mobs whose MobSimState.WillTrade is true. The merchant-inventory
	// and get-side panels stay visible so the player can stage a swap; the
	// commit button still falls back to a gift when nothing is requested
	// from the merchant's side.
	Trade,
	// Climb a ClimbableTree — the interactive's Complete lifts the player into
	// the bird's-eye overlook, hides the model, and conceals them from mobs
	// until they take damage or end the overlook.
	Climb,
	// Board a rideable vehicle (boat, future mounts) — the interactive's
	// Complete parents the player onto the vehicle and suspends on-foot
	// locomotion (see RideableVehicle / Player.Mount).
	Mount,
	// Revive a dead companion — surfaced only on a tamed mob's corpse (see
	// Mob.CanRevive). The interactive's Complete restores the mob to life
	// (alive flag, live collision layers, health), undoing Mob.Die().
	Revive,
}
