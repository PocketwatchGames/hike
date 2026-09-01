using Godot;
using Godot.Collections;

// Authored timeline for an item action. One profile per slot-driven verb on
// a weapon, consumable, or interactive. The runner consumes a profile +
// context and runs the timeline; the input/AI/UI layer chooses *which*
// profile to run.
[GlobalClass]
public partial class ItemActionProfile : Resource
{
	// Charge tiers, listed in the order they take over. Each tier holds for
	// its own `chargeTime` before the next same-comboIndex tier becomes
	// selected. A tap-fire weapon has a single entry with chargeTime=0 and
	// autoActivateAtMax=true. A charge-then-fire bow has a single entry with
	// chargeTime > 0 and autoActivateAtMax=false (release before chargeTime
	// fires early; full hold reaches the next tier or auto-fires).
	[Export] public Array<ItemAction> chargedActions = new();

	// Events fired while Charging. PlayAnim "wind up", spawn charge particle,
	// etc. Authored on a timeline measured from pressMs. Empty for tap-fire
	// weapons that have no perceivable charge phase.
	[Export] public Array<ItemEvent> chargeEvents = new();

	// One-shot events fired on any Charging→exit transition (activate, abort,
	// interrupt). Used to clean up persistent charging effects (stop emitter,
	// stop loop sound). No timeline — all fire at t=0 of the transition.
	[Export] public Array<ItemEvent> chargeEndEvents = new();

	// Events fired when charging aborts WITHOUT reaching even the lowest
	// tier (player released too early).
	[Export] public Array<ItemEvent> abortEvents = new();

	// One-shot Fx spawned on the actor when a press is REFUSED at t=0
	// because no tier in the current combo step can currently fire (every
	// tier fails its requirements / costs / ammo gate). The classic case is
	// "club swung while swimming": the press lands a small splash + thud
	// without ever entering Charging. Null = silent refusal.
	[Export] public PackedScene rejectEffect;

	// If true and the player holds past the cumulative chargeTime of every
	// tier on the current combo step, auto-fire the top tier (gated by its
	// requirements). If false, the player must release to commit.
	[Export] public bool autoActivateAtMax = true;

	// "Hold to completion": when true, releasing the input before the charge
	// fully fills does NOT activate — it aborts (fires the selected tier's
	// chargeCancelEffect / the profile's abortEvents). The only way to commit
	// is a full hold, which auto-fires via autoActivateAtMax. Use for channeled
	// actions where a half-hearted tap should accomplish nothing — digging with
	// the shovel, future channeled abilities. Contrast the bow, where an early
	// release is a deliberate weak shot. Requires autoActivateAtMax = true so a
	// filled charge still has a way to fire.
	[Export] public bool requireFullCharge = false;

	// Damage-during-charge interrupt policy. Active-phase interrupt is gated
	// by ItemAction.canInterrupt instead.
	[Export] public bool interruptOnDamage = true;

	// Runner-side queue only: TryStart while Active queues the press when it
	// lands within queueWindowSeconds of Active ending. NOTE the player's input
	// layer never reaches this (it banks can't-fire-yet presses itself, gated
	// by PlayerData.weaponQueueWindowSeconds — deliberately player-wide input
	// feel, not per-weapon data), so these fields only matter for direct
	// TryStart callers (mob AI).
	[Export] public bool queueable = false;
	[Export] public float queueWindowSeconds = 0.2f;

	// True when any tier's Active timeline drives the actor's body forward
	// (EItemEventType.ApplyMotion). Mob AI gates such an attack on being able to
	// WALK to its target: a dart is a committed displacement with no steering in
	// it, so an attack carrying one must not be thrown across ground the mob
	// could not have reached on foot.
	//
	// Folded once and cached. The walk is over Godot.Collections arrays — native
	// containers that marshal a Variant per element — and profiles are immutable
	// after load, so there is nothing to invalidate.
	private bool? _lunges;
	public bool Lunges
	{
		get
		{
			if (_lunges.HasValue)
			{
				return _lunges.Value;
			}
			bool found = false;
			int tierCount = chargedActions?.Count ?? 0;
			for (int i = 0; i < tierCount && !found; i++)
			{
				ItemAction tier = chargedActions[i];
				int eventCount = tier?.events?.Count ?? 0;
				for (int e = 0; e < eventCount; e++)
				{
					ItemEvent ev = tier.events[e];
					if (ev != null && (ev.type & EItemEventType.ApplyMotion) != 0)
					{
						found = true;
						break;
					}
				}
			}
			_lunges = found;
			return found;
		}
	}

	// Cumulative time-from-press at which the tier at `tierIndex` becomes
	// selectable: sum of `chargeTime` across all preceding tiers in
	// `chargedActions` that share `comboIndex`. Other-combo tiers don't
	// contribute, so each combo step has its own independent timeline.
	public static float GetTierStartTime(ItemActionProfile profile, int tierIndex, int comboIndex)
	{
		if (profile?.chargedActions == null || tierIndex <= 0) { return 0f; }
		int count = Mathf.Min(tierIndex, profile.chargedActions.Count);
		float t = 0f;
		for (int i = 0; i < count; i++)
		{
			ItemAction a = profile.chargedActions[i];
			if (a == null) { continue; }
			if (a.comboIndex != comboIndex) { continue; }
			t += a.chargeTime;
		}
		return t;
	}
}
