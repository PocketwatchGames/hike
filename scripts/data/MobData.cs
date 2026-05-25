using Godot;
using Godot.Collections;

[GlobalClass]
public partial class MobData : Resource
{
    // Per-EAnimation binding from logical slot to SpriteFrames clip name plus
    // retiming policy. Empty slots resolve to default-StringName and the
    // animator silently skips them — author the dictionary in each mob .tres
    // to wire each slot to its concrete clip. See AnimationData.
    [Export] public Godot.Collections.Dictionary<EAnimation, AnimationData> animations = new();

    // Look up the SpriteFrames clip name for an EAnimation slot. Returns
    // default StringName when the slot is unbound — callers route this
    // through LitSpriteAnimator.Play / HasAnimation, both of which no-op
    // on unknown names, so an unbound slot is a silent skip rather than a
    // hard error.
    public StringName GetAnimationName(EAnimation anim)
    {
        return animations.TryGetValue(anim, out AnimationData d) && d != null ? d.name : default;
    }

    // Returns whether the slot is authored to track statusAnimMul. Returns
    // false for unbound slots — playing nothing at status-retimed speed is
    // the same as playing nothing at authored speed.
    public bool IsAnimationSpeedAffected(EAnimation anim)
    {
        return animations.TryGetValue(anim, out AnimationData d) && d != null && d.affectedBySpeedMultiplier;
    }

    // Player-facing name shown in the Bestiary, announcement banners, and any
    // future "Goblin attacks!" UI. Matches the StringName pattern other
    // *Data resources use for human-readable identity (ItemData, RegionData).
    [Export] public StringName displayName;

    // Whether this species shows up in the bestiary and fires a discovery
    // announcement the first time a player sees one. False for "common
    // knowledge" species the player wouldn't catalogue — villagers,
    // livestock, future named NPCs. Distinct from the per-instance
    // EPlayerPerceptionState.Discovered, which still progresses normally
    // on these mobs for AI / HUD purposes; this flag just controls the
    // species-level bestiary entry.
    [Export] public bool appearsInBestiary = true;

    // Cumulative kill thresholds for the bestiary entry's level. Entry i
    // is the total kills required to reach level (i+1); the bestiary
    // shows current level + progress to the next threshold. Empty list
    // means the entry doesn't level (stays at level 0). At max level the
    // bar fills and shows total kills instead of a target.
    [Export] public Array<int> killsPerLevel = new();

    // Portrait shown on the right-hand bestiary detail panel for this
    // species. Authored at higher resolution than the in-world sprite —
    // the bestiary's TextureRect controls final size. Null leaves the
    // portrait slot empty (hidden).
    [Export] public Texture2D bestiaryPortrait;

    // Scale multiplier applied to the worldspace MobHUD once this species
    // has been discovered or triggered. Smaller creatures use values <1
    // so their callout doesn't dwarf them; bosses go >1. The pre-discovery
    // perception meter always renders at a fixed small scale regardless.
    [Export] public float hudScale = 1f;

    [ExportGroup("Mob Perceives Player")]
    // How this mob's AI sees the player — sight cone reach and shape, the
    // accumulation curve that turns "in sight" into the triggered/alert
    // state in MobAI.UpdatePerception's mob-to-player block.
    [Export] public float VisionRange = 15f;
    [Export] public float VisionDotPower = 0.5f;
    [Export] public float VisionRangePower = 0.5f;
    [Export] public float PerceptionIncreaseSpeed = 0.5f;
    [Export] public float PerceptionRelaxationSpeed = 0.1f;
    [Export] public float MinPerceptionDelta = 0.05f;
    [Export] public float PerceptionThresholdAlert = 1f;
    // Per-sense multipliers applied to the vision / hearing / smell perception
    // delta before they're summed and accumulated. Setting any of these to 0
    // turns off that sense for this mob (blind / deaf / anosmic) while
    // leaving the others intact.
    [Export] public float VisionStrength = 1f;
    [Export] public float HearingStrength = 1f;
    [Export] public float SmellStrength = 0f;
    // Hearing reach scalar. A sound of `decibels` is heard if
    // `decibels * hearingRange > distance` — i.e. the audible distance is
    // `decibels * hearingRange`. State transitions (triggered / discovered)
    // are gated on active visual contact, so a hearing-only spike raises
    // perception but won't cross the threshold without sight.
    [Export] public float hearingRange = 5f;
    [Export] public float hearingRangePower = 0.5f;
    // Smell reach in meters. The mob walks the target's ScentEmitter.Crumbs
    // each perception tick; a crumb contributes when it's within smellRange
    // AND a physics raycast from the mob's nose to the crumb is unblocked.
    // Like hearing, smell can't trigger the alert state on its own — only
    // active visual contact crosses the perception threshold.
    [Export] public float smellRange = 8f;
    [Export] public float smellRangePower = 0.5f;

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
    // Native language spoken by this mob. Acts as the default
    // ConversationContext.speakerLanguage for any branch whose own
    // `language` field is null — the player's learned components against
    // this language decide what scrambles. MobSimState.Language overrides
    // this per-instance. Null = speaks the player's language unconditionally
    // (universal NPCs).
    [Export] public LanguageData language;
    [Export] public bool canBurrow = false;
    // Seconds from the moment a mob starts burrowing to when it's fully
    // underground and uninteractable. During this window the mesh is sinking
    // but the mob is still hittable.
    [Export] public float burrowTime = 1.5f;
    [Export] public float hideRange = 20f;
    [Export] public float maxHealth = 10f;
    [Export] public float maxArmor = 0f;

    // Named damage profiles fired by this mob's attack actions. Mirrors
    // WeaponData.damageProfiles — ItemEvent.damageProfileKey resolves
    // against this dict when the attacker is a Mob instead of a weapon-
    // carrying Player. Convention: "primary" is the default key for the
    // mob's main attack; multi-attack species add additional keys
    // (e.g. "claw" / "bite"). Empty dict = no damage on Melee/Hitscan
    // events sourced from this mob.
    [Export] public Dictionary<StringName, DamageData> damageProfiles = new();

    public DamageData GetDamage(StringName key)
    {
        if (damageProfiles == null)
        {
            return null;
        }
        return damageProfiles.TryGetValue(key, out DamageData d) ? d : null;
    }

    [Export] public float stunThreshold = 5f;
    [Export] public float stunRechargeDelay = 6f;
    [Export] public float stunRechargeSpeed = 1f;
    [Export] public float stunRecoverTime = 5f;
    [Export] public float armorRechargeDelay = 6f;
    [Export] public float armorRechargeSpeed = 1f;
    [Export] public float armorRecoverTime = 30f;
    [Export] public float yellVolume = 15;
    // How this mob responds when it hears another mob's yell. yellVolume
    // is yeller-side (who hears me); these three are receiver-side (how
    // do I investigate what I heard). Range is the Euclidean tolerance
    // around the yelled-about point at which the receiver considers itself
    // "arrived and inspecting"; cancelTime caps how long it pursues the
    // rumour before giving up; pauseTime is how long it lingers at the
    // point once it arrives. Authored receiver-side so a skittish prey
    // mob can investigate cautiously while a guard dog charges in.
    [Export] public float yellInvestigateRange = 8f;
    [Export] public float yellInvestigateCancelTime = 30f;
    [Export] public float yellInvestigatePauseTime = 3f;
    [Export] public float maxSpeed = 4f;
    // Maximum yaw rate (radians/sec) the body can rotate per physics tick.
    // Drives the yaw lerp in Mob._PhysicsProcess — agile creatures snap to
    // their facing target, lumbering mobs commit to a heading.
    [Export] public float turnSpeed = 6f;
    // Sustained-fall thresholds for the fall loop animation. Vertical speed
    // below -fallEnterSpeed counts as "falling fast"; the fall anim only
    // engages after the body has been falling fast for fallGraceTime
    // seconds, so short hops over ledges or shoves from neighbours don't
    // flicker into it.
    [Export] public float fallEnterSpeed = 1f;
    [Export] public float fallGraceTime = 0.4f;
    // Continuous movement noise this mob emits. Mapped from current speed:
    // 0 at rest, sneakDecibels at half maxSpeed, runDecibels at maxSpeed.
    // Listeners (player + other mobs) check `decibels * hearingRange >
    // distance` to hear, and add a hearing contribution to their perception
    // delta when they do.
    [Export] public float sneakDecibels = 1f;
    [Export] public float runDecibels = 4f;
    [Export] public StringName defaultBehavior = "Idle";
    [Export] public bool dangerous = false;
    // Exp awarded to each of the killing player's equipped weapons and armor
    // when this mob dies. Granted in Mob.Damage on the lethal hit; status-
    // effect kills (poison without an attributable source) do not award exp.
    [Export] public int exp = 0;
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

    // Loot ejected from the mob's body when it dies. Each entry spawns
    // `count` Loot instances of `item`, fired outward from the mob's
    // position with the same upward-arc impulse pattern chests use. Empty
    // (or null entries) on a mob means no drops.
    [Export] public Array<ItemCount> loot = new();

    // Outward arc speed (m/s) applied to each piece of ejected loot when
    // the mob dies — both authored drops in EjectLoot and any stuck arrows
    // scattered with the corpse. Launched on a 45° upward arc; larger
    // values scatter wider.
    [Export] public float lootEjectSpeed = 5f;

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
    // Used for wading cells (water column shallower than swimDepthThreshold);
    // deeper columns price through swimCost instead.
    [Export] public float waterCost = 5f;

    // Pathfinder cost multiplier for swim cells — water columns at least
    // swimDepthThreshold voxels deep, where the mob has to swim rather than
    // wade. Higher than waterCost so a mob picks a wading detour over a
    // swim leg when both routes are otherwise equivalent.
    [Export] public float swimCost = 15f;

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

    // ---- Water / swim profile ----
    // Per-mob swim physics. Defaults match PlayerData so a stock mob feels
    // the same in water as the player; override per-species to make a
    // bobbing leaf-fish very different from a heavy bear.

    // Minimum contiguous water-column depth (in voxels) under this mob's
    // feet that triggers swimming — buoyancy, current drag, and the
    // swimSpeed cap kick in once the column reaches this depth; otherwise
    // the mob wades on the seafloor with ground physics. 2 matches the
    // player (swims in 2+ deep water, wades through 1-voxel puddles); a
    // frog uses 1 (swims in any water), a moose uses 3+ (chest-deep before
    // it has to swim). Shared by Mob.UpdateWaterState and the pathfinder's
    // wade/swim cost split so both layers agree on what's a swim cell.
    [Export] public float swimDepthThreshold = 2f;
    [Export] public float swimSpeed = 3.5f;
    [Export] public float buoyancyAcceleration = 15f;
    [Export] public float waterDrag = 5f;
    [Export] public float waterSinkSpeed = 2f;
    [Export] public float waterSurfaceOffset = 1f;
    // Rate (per second) at which the swimming mob's horizontal velocity is
    // dragged toward the local water current. High = the river carries
    // the mob; low = it mostly swims under its own power.
    [Export] public float waterCurrentDrag = 2f;
}
