using Godot;

[GlobalClass]
public partial class MobData : Resource
{
    [ExportGroup("Mob Perceives Player")]
    // How this mob's AI sees the player — sight cone reach and shape, the
    // accumulation curve that turns "in sight" into the triggered/alert
    // state in MobAI.UpdatePerception's mob-to-player block.
    [Export] public float VisionRange = 15f;
    [Export] public float VisionDotPower = 0.5f;
    [Export] public float VisionDistancePower = 2f;
    [Export] public float PerceptionIncreaseSpeed = 0.5f;
    [Export] public float PerceptionRelaxationSpeed = 0.1f;
    [Export] public float MinPerceptionDelta = 0.05f;
    [Export] public float PerceptionThresholdAlert = 1f;

    [ExportGroup("Player Perceives Mob")]
    // How the player sees this mob — fed into PlayerPerception.Tick. Movement
    // gates the per-frame visibility (a still mob is harder to spot), which
    // is folded into prominence at the call site along with tall-grass
    // camouflage. The thresholds and prominence are the per-target tuning
    // the player-side helper consumes directly.
    [Export] public float visibilityMovementMin = 0.5f;
    [Export] public float visibilityMovementPower = 2;
    [Export] public float maxVisibilitySpeed = 5f;
    // Free scalar on the player's perception distance — large mobs pass
    // >1 to be spotted from farther; small / sneaky mobs <1.
    [Export] public float prominence = 1f;
    // Per-mob thresholds for player-perceives-mob state transitions. Same
    // semantics as Discoverable: set detectedThreshold == discoveredThreshold
    // for a mob that should pop straight from Hidden to Discovered with no
    // suspicious phase (e.g. a boss that simply isn't sneakable).
    [Export(PropertyHint.Range, "0,1,0.01")] public float detectedThreshold = 0.1f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float discoveredThreshold = 1f;
    // How long a Discovered mob stays Discovered after the player loses
    // sight. Stationary mobs are remembered longer; mobs that have moved
    // out of their last-known position decay faster.
    [Export] public float MemoryStationaryTime = 60f;
    [Export] public float MemoryMovingTime = 3f;

    [ExportGroup("")]
    // Faction this mob belongs to. Drives targeting / aggro filters elsewhere
    // (a Friendly villager doesn't register as a threat, a Hostile creature
    // attacks the player, a Neutral animal only retaliates). Behaviors are
    // shared across teams — the brain decides what to do, the team decides
    // who counts as a target.
    [Export] public ETeam team = ETeam.Hostile;
    // Localization key for the line spoken when an interact action with verb
    // Talk completes. Empty string = no chatter line. Resolved through
    // Loc.Get at speak time so language switches mid-game pick up the new
    // string on the next interaction.
    [Export] public StringName chatterLocKey = "";
    // Seconds the chatter bubble stays on screen before fading. Speech timing
    // is per-mob because a long-winded NPC and a one-word grunt belong on
    // different schedules.
    [Export] public float chatterDurationSeconds = 3f;
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
    // Scene instantiated for this mob type. Single source of truth — every
    // place that previously paired a (PackedScene, MobData) reference
    // (ZoneGenData goblin/kun_kun, MobSpawnEntry) now references MobData
    // alone and reads MobScene from it.
    [Export] public PackedScene MobScene;
    // MovingLight scene this mob spawns when it lights its torch (dark
    // ambient + discovered). Instantiated on demand in Mob and freed when
    // the conditions clear — same instantiate/free pattern and field name
    // as TorchData.movingLightScene. Null on torch-less species.
    [Export] public PackedScene movingLightScene;

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
