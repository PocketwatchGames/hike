using Godot;

// Shared authored tuning for rideable vehicles (see IRideable / RideableVehicle).
// Subclassed per vehicle (BoatData now, HorseData later) with the
// locomotion-specific numbers; this base carries only what every rideable
// needs — the seated animation slots. Lives in a .tres wired onto the vehicle
// scene so designers tune feel without touching code.
[GlobalClass]
public partial class RideableData : Resource
{
    // EAnimation slot the rider loops while seated and stationary. Resolved
    // per-actor through PlayerData.animations like every other slot, so the
    // rider's own art supplies the pose (a boat gets a paddle-rest, a horse a
    // saddle-idle).
    [Export] public EAnimation idleAnim = EAnimation.Idle;

    // EAnimation slot the rider loops while the vehicle is being propelled.
    [Export] public EAnimation moveAnim = EAnimation.Run;
}
