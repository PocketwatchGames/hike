using Godot;

// Tuning for the Boat vehicle — water-locked momentum. See Boat._PhysicsProcess
// for how each field feeds the step. Authored as boat.tres and wired onto the
// boat scene's `_data` slot.
[GlobalClass]
public partial class BoatData : RideableData
{
    // Top horizontal speed the hull approaches under full paddle input (m/s).
    [Export] public float maxSpeed = 5f;

    // How quickly velocity climbs toward the steering target (m/s²). Higher =
    // snappier paddle response.
    [Export] public float acceleration = 6f;

    // Passive water resistance that bleeds off speed when the rider eases off
    // the stick (m/s²). Also the rate the hull settles onto the local current
    // (or onto a stop in still water) when released.
    [Export] public float drag = 4f;

    // How strongly the local water current carries the boat while ridden and
    // afloat. 1 = the hull drifts at the full water speed (SampleWaterCurrent,
    // matching the visible surface flow); 0 = current is ignored. Paddle thrust
    // adds on top of this, so the rider can still push across or upstream.
    [Export(PropertyHint.Range, "0,2,0.05")] public float currentStrength = 1f;

    // Maximum rate the hull heading swings toward the steering direction
    // (degrees/second). The boat pivots toward where the rider points rather
    // than snapping instantly, giving it weight.
    [Export(PropertyHint.Range, "0,720,1")] public float turnRateDegrees = 120f;

    // How fast the hull's Y settles onto the water surface (per-second lerp
    // rate). Higher = stiffer floating, lower = a softer rise/fall.
    [Export] public float buoyancyLerp = 8f;

    // Gentle vertical bob layered on top of the water surface so a parked boat
    // isn't dead still. Amplitude in meters, period in seconds.
    [Export] public float bobAmplitude = 0.08f;
    [Export] public float bobPeriodSeconds = 2.5f;

    // Stick deflection (0..1) above which the boat counts as "propelling" for
    // the rider's paddle animation.
    [Export(PropertyHint.Range, "0,1,0.01")] public float propellingInputThreshold = 0.15f;

    // Downward settle speed applied when the hull is beached (no water column
    // beneath it) so it rests on the ground instead of hanging in the air
    // (m/s²). Propulsion is refused while beached.
    [Export] public float beachedGravity = 12f;
}
