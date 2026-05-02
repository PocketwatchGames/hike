using Godot;

[GlobalClass]
public partial class MobData : Resource
{
    [Export] public float VisionRange = 15f;
    [Export] public float VisionDotPower = 0.5f;
    [Export] public float VisionDistancePower = 2f;
    [Export] public float PerceptionIncreaseSpeed = 0.5f;
    [Export] public float PerceptionRelaxationSpeed = 0.1f;
    [Export] public float MinPerceptionDelta = 0.05f;
    [Export] public float PerceptionThresholdAlert = 1f;
    [Export] public float visibilityLightMax = 0.75f;
    [Export] public float visibilityMovementMin = 0.5f;
    [Export] public float visibilityMovementPower = 2;
    [Export] public float maxVisibilitySpeed = 5f;
    [Export] public float MemoryStationaryTime = 60f;
    [Export] public float MemoryMovingTime = 3f;
    [Export] public bool canBurrow = false;
    // Seconds from the moment a mob starts burrowing to when it's fully
    // underground and uninteractable. During this window the mesh is sinking
    // but the mob is still hittable.
    [Export] public float burrowTime = 1.5f;
    [Export] public float hideRange = 20f;
    [Export] public float maxHealth = 10f;
    [Export] public float maxArmor = 0f;
    [Export] public float armorRechargeDelay = 6f;
    [Export] public float armorRechargeSpeed = 1f;
    [Export] public float armorRecoverTime = 30f;
    [Export] public float yellVolume = 15;
    [Export] public float maxSpeed = 4f;
    [Export] public StringName defaultBehavior = "Idle";
    [Export] public bool dangerous = false;
    [Export] public BrainData brain;
    [Export] public PackedScene torch;

    // ---- Traversal profile ----
    // Read by the navigation system to decide which voxels this mob can walk
    // through, climb, or swim in. A mob with default values is a plain ground
    // walker that steps over 1-voxel curbs and avoids water.

    // Vertical voxels of step-up the mob can enter without "climbing" — 1 lets
    // a mob walk up a single-voxel ledge, 0 means it stops at any rise. Higher
    // values are for goat/spider-like climbers. Used by the walkability grid
    // to decide which neighbour cells are reachable from the current cell.
    [Export] public int maxStepHeight = 1;

    // Vertical voxels of drop the mob is willing to take when the pathfinder
    // is invoked with allowFalling=true (chase, follow). 0 = "never drop"
    // (skittish mobs that refuse to leave their ledge). Wander always passes
    // allowFalling=false regardless of this value, so even mobs with a high
    // maxFallHeight don't accidentally wander themselves off a cliff.
    [Export] public int maxFallHeight = 4;

    // True if the mob can climb arbitrary vertical surfaces (spider). Skips
    // the maxStepHeight check entirely and lets the pathfinder treat any
    // adjacent solid as walkable.
    [Export] public bool canClimb = false;

    // True if the mob can enter Water voxels at all. False = water is a hard
    // wall (e.g. small flightless creatures); true = water is enterable but
    // costs `waterCost` per cell. Amphibious mobs set canSwim=true,
    // waterCost=1; mobs that hate water set canSwim=true, waterCost=5.
    [Export] public bool canSwim = true;

    // Pathfinder cost multiplier for water cells. 1 = neutral. Higher values
    // mean the mob will detour around water if there's a dry path within
    // cost*distance — so 5 means "swim only if dry path is 5x longer".
    [Export] public float waterCost = 5f;

    // True if the mob ignores ground entirely. Pathfinder runs in 3D for
    // these and steering applies a hover force toward terrain+hoverHeight.
    [Export] public bool canFly = false;

    // For fliers: preferred altitude above the terrain surface in voxels.
    // Steering layer pulls the mob toward this height when no goal demands
    // otherwise.
    [Export] public float hoverHeight = 4f;

    // Mob's half-width for clearance checks. Used to validate that a path
    // cell has enough horizontal room and to size the separation kernel.
    [Export] public float clearanceRadius = 0.4f;
}
