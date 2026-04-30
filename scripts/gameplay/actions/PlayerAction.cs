using Godot;

// Runtime state of one in-flight action. Owned by ActionRunner; passed by
// reference to event handlers so they can read pressMs / activateMs / etc.
// `lastEventIndex` was previously per-weapon (WeaponState.lastWeaponEventIndex)
// — it's now per-action because two activations of the same weapon need
// independent timeline cursors.
public struct PlayerAction
{
	public EActionPhase phase;
	public ItemActionProfile profile;
	public ChargedAction selectedTier;
	public int selectedTierIndex;
	public ActionContext context;

	// Timeline cursors. pressMs is when input went down (start of Charging).
	// activateMs is when Active began. endMs is when Active will end.
	public ulong pressMs;
	public ulong activateMs;
	public ulong endMs;

	// Last fired index in the currently-walked event list (chargeEvents
	// during Charging, selectedTier.events during Active). Reset on phase
	// transitions.
	public int lastEventIndex;

	// Continuous charge fraction in [0, 1] sampled at activation, stashed for
	// per-action curves (bow accuracy / range scaling — phase 3).
	public float chargeT;

	public bool IsBusy => phase != EActionPhase.Ready;
}
