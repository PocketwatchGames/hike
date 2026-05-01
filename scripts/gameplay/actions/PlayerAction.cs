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

	// Combo index this action is locked to for the duration of the charge.
	// Chosen at press time from the driving weapon's chain state and held
	// fixed — tier selection during charging only considers chargedActions
	// whose comboIndex matches this value.
	public int targetComboIndex;

	public bool IsBusy => phase != EActionPhase.Ready;
}
