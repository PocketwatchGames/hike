using Godot;

// Reacting to a dead body (see BehaviorInspectCorpse). One node covers all
// three cases — a death witnessed at the hands of an attacker, a death by trap,
// and a body come across later — because they are the same sequence with legs
// left out; the stimulus (CorpseSighting) says which.
[GlobalClass]
public partial class CorpseInspectBehaviorData : BehaviorData
{
    // Each leg's duration is a (min, max) second range, rolled per phase so a
    // group reacting to the same body does not move in lockstep.
    //
    // Seconds spent facing the body from wherever the mob was standing, before
    // walking over.
    [Export] public Vector2 glanceTimeRange = new Vector2(0.5f, 1.5f);
    // Seconds spent facing the body after arriving at it.
    [Export] public Vector2 studyTimeRange = new Vector2(0.5f, 1.5f);
    // Seconds spent facing where the killing blow came from, after the study.
    // Only reached by a mob that saw an attacker make the kill.
    [Export] public Vector2 damageLookTimeRange = new Vector2(2f, 4f);
    // Seconds spent staring when the mob is not going over at all — this species
    // never approaches, the species flies (the approach is ground pathing), or
    // the sighting itself said to keep away (a trap kill).
    [Export] public Vector2 glanceOnlyTimeRange = new Vector2(2f, 4f);
    // False for a species that reacts to a body without ever walking to it.
    // A flying species never approaches regardless: the walk over is driven by
    // the ground navigator.
    [Export] public bool approach = true;
    // Movement speed for the walk to the body (1 = full).
    [Export(PropertyHint.Range, "0,1,0.05")] public float approachSpeed = 1f;
    // How close to the body counts as having arrived. Deliberately a stride or
    // two back — a mob that walks right onto the corpse reads as clipping into
    // it rather than standing over it.
    [Export] public float arriveRange = 3.5f;
    // How jumpy the sight leaves the mob (Mob.RaiseSuspicion): perception grows
    // this much faster for the next MobData.suspicionDecaySeconds. Watching a
    // creature die is the bigger shock; walking up on a body someone else left
    // is the smaller one. 1 = no effect.
    [Export] public float witnessedDeathSuspicion = 2f;
    [Export] public float foundCorpseSuspicion = 1.5f;
    // Give up and move on if the body has not been reached in this long. A
    // corpse across a chasm or behind a shut door must not park the mob forever.
    [Export] public float approachTimeoutSeconds = 15f;

    // Neither engaged nor fleeing: a mob peering at a body is not danger for
    // the interactive gate.
    public CorpseInspectBehaviorData() { behaviorFlags = EBehaviorFlags.None; }

    public override BehaviorBase CreateRuntime() => new BehaviorInspectCorpse(this);
}
