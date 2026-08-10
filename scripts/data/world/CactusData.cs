using Godot;

// Authored tuning for a Cactus — a stationary desert hazard that erupts a ring
// of spines when a creature touches it or it's struck. The spines carry hazard
// damage (damageData.hazardProfile), so one authored cactus threatens a
// weakling and a boss proportionally wherever it's placed.
[GlobalClass]
public partial class CactusData : Resource
{
    // Spine projectile fired outward and the hit payload it carries. damageData
    // authors a HazardProfileData so the bite scales to whoever it catches —
    // leave healthDamage 0 and let the hazard profile drive it.
    [Export] public PackedScene projectileScene;
    [Export] public DamageData damageData;

    // Number of spines per burst, spread evenly around the full circle.
    [Export(PropertyHint.Range, "1,64,1")] public int spineCount = 12;

    // Launch speed (m/s) and flight time (s) of each spine.
    [Export] public float spineSpeed = 12f;
    [Export] public float spineLifetimeSeconds = 1.2f;

    // Upward tilt of the fired spines above the horizontal plane, in degrees — a
    // small lift so spines rise toward a creature's hurtbox instead of skimming
    // the ground past its feet.
    [Export(PropertyHint.Range, "0,80,1")] public float spineElevationDegrees = 12f;

    // Height above the cactus base the ring launches from (meters) — the body's
    // mid-height, so spines erupt from the plant rather than its feet.
    [Export] public float launchHeight = 0.6f;

    // Minimum seconds between bursts, shared by the touch and struck triggers so
    // a creature standing in it (or a flurry of hits) can't chain-fire it every
    // frame. Gated on the sim clock, so it slows uniformly under slow-mo.
    [Export] public float cooldownSeconds = 1.5f;

    // Telegraph: seconds between the trigger and the spines actually launching.
    // warningEffect plays at the start of it, so a creature that notices the
    // bristle has a beat to back out. On the sim clock like the cooldown.
    [Export] public float warningSeconds = 0.45f;

    // One-shot fx spawned at the cactus, both at launchHeight: the bristle
    // rattle when it arms, and the spine puff when the ring actually launches.
    [Export] public PackedScene warningEffect;
    [Export] public PackedScene burstEffect;
}
