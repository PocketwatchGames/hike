using Godot;

// Authored tuning for a tripwire dart trap. A body-driven TriggerSource
// (the tripwire) pokes a DartDeployer, which — after a short warning beat —
// fires one dart from each of its muzzles. Unlike the spike trap (which hits
// everyone standing on the pad the instant it fires), the darts are real
// Projectiles: aimed at the intruder's torso at fire time, then flying
// straight, so stepping aside during the warning dodges them.
[GlobalClass]
public partial class DartTrapData : Resource
{
    // Damage carried by each dart. Author friendlyFire = true so the trap is a
    // true environmental hazard — it strikes the player AND any hostile mob
    // lured across the wire, not just the opposite team.
    [Export] public DamageData damageData;

    // The projectile scene each muzzle fires. Reuse the bow arrow, or author a
    // dedicated dart — anything whose root is a Projectile.
    [Export] public PackedScene dartScene;

    // Dart launch speed (m/s) and how long it flies before expiring. speed *
    // lifetime is the effective range; keep it long enough to cross the corridor
    // the tripwire spans.
    [Export] public float dartSpeed = 26f;
    [Export] public float dartLifetimeSeconds = 1.5f;

    // Height (meters) above the target's feet the darts aim for — torso level,
    // so a standing body is struck center-mass.
    [Export] public float aimHeight = 1.0f;

    // Seconds between the tripwire firing and the darts launching. The warning
    // fx plays at the start of this window, giving an alert player a beat to
    // step out of the line of fire.
    [Export] public float warningDelay = 0.35f;

    // Seconds after a volley before the trap re-arms. Inert during this window
    // so a body can't be shot twice in immediate succession.
    [Export] public float resetTime = 3f;

    // Discoverable.prominence while the trap is hidden and armed — how easily
    // the player spots the unsprung launcher. Applied to the host Discoverable
    // at spawn so the placement owns it, not the scene.
    [Export] public float armedProminence = 0.6f;

    // Prominence the trap jumps to the moment it fires: a sprung, obvious
    // launcher is far easier to notice than the hidden armed one. Applied
    // alongside the immediate ForceDiscover.
    [Export] public float firedProminence = 1.5f;

    // One-shot fx scenes. Wired in the .tscn; any may be null.
    // warningEffect plays on the deployer at the start of the warning beat;
    // fireEffect plays at each muzzle as its dart launches; disarmEffect plays
    // when the player disarms the trap.
    [Export] public PackedScene warningEffect;
    [Export] public PackedScene fireEffect;
    [Export] public PackedScene disarmEffect;

    // Projectile impact fx, mapped onto ProjectileImpact: hitEffect covers a
    // creature strike (health / armor / lethal), environmentEffect a wall clip.
    [Export] public PackedScene impactHitEffect;
    [Export] public PackedScene impactEnvironmentEffect;
}
