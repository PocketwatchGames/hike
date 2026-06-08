using Godot;

// Pairing of a DamageData template with its tick cadence. Authored inline
// on a DamageZone (`damageIntervals`) so a single hazard can stack several
// rates — e.g. a poison cloud that ticks status stacks once per second
// alongside a 0.3s burn drum. Each entry runs its own timer; a hit fired
// per entry is the standard discrete HurtBox.Hit path, so modifiers /
// status effects / hitstun / knockback all apply normally.
[GlobalClass]
public partial class IntervalDamageEntry : Resource
{
	// Damage payload applied each tick. Per-tick magnitude — not scaled by
	// the interval. With healthDamage = D and tickInterval = T, DPS = D / T.
	[Export] public DamageData damage;

	// Seconds between ticks while a body is inside the zone.
	[Export] public float tickInterval = 1f;

	// True = first tick fires the moment a HurtBox enters. False = wait
	// tickInterval before the first hit. Per-entry so a cloud can author an
	// immediate poison pulse on entry alongside a delayed slow-stack.
	[Export] public bool tickOnEnter = true;
}
