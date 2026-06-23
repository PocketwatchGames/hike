using Godot;
using Godot.Collections;

[GlobalClass]
public partial class MobData : Resource
{
    // Player-facing name shown in the Bestiary, announcement banners, and any
    // future "Goblin attacks!" UI. Matches the StringName pattern other
    // *Data resources use for human-readable identity (ItemData, RegionData).
    [Export] public StringName displayName;

    [ExportGroup("Team")]
    // Faction this mob belongs to. Drives targeting / aggro filters elsewhere
    // (a Friendly villager doesn't register as a threat, a Hostile creature
    // attacks the player, a Neutral animal only retaliates). Behaviors are
    // shared across teams — the brain decides what to do, the team decides
    // who counts as a target.
    [Export] public ETeam team = ETeam.Hostile;
    // Faction this mob scans as enemies for the second "threat perception"
    // channel (a companion watches the Hostile faction; a goblin watches the
    // Friendly faction to notice the player's pet). ETeam.None (the default)
    // means this mob does NOT scan for threats at all — ordinary mobs only
    // perceive the player and pay nothing. Set a real team to enable the
    // channel; it drives the companion brain's Wary / Attack tiers.
    [Export] public ETeam threatTeam = ETeam.None;

    [ExportGroup("Perception")]
    // How this mob's AI sees the player — sight cone reach and shape, the
    // accumulation curve that turns "in sight" into the triggered/alert
    // state in MobAI.UpdatePerception's mob-to-player block.
    [Export] public float VisionRange = 15f;
    [Export] public float VisionDotPower = 0.5f;
    [Export] public float VisionRangePower = 0.5f;
    // Lookout bonus while perched (flying mobs): VisionRange is scaled by this,
    // and the facing cone is dropped (vision goes omnidirectional) to model a
    // bird watching all around from an elevated vantage. 1 = no bonus. The
    // perched bird also ignores its own perch prop's collider for LOS so the
    // trunk it sits in doesn't blind it. Ignored by non-perching mobs.
    [Export] public float perchedVisionRangeMultiplier = 1.5f;
    [Export] public float PerceptionIncreaseSpeed = 0.5f;
    [Export] public float PerceptionRelaxationSpeed = 0.1f;
    [Export] public float MinPerceptionDelta = 0.05f;
    [Export] public float PerceptionThresholdAlert = 1f;
    // Lower awareness tier (below PerceptionThresholdAlert) at which the mob is
    // "wary" of a perceived target — aware enough to react cautiously (turn,
    // growl, bristle) but not yet fully triggered into combat. As perception
    // accumulates it crosses this first, then PerceptionThresholdAlert. Read by
    // graded-response behaviors: the companion brain enters BehaviorWary here
    // and BehaviorDogAttack at PerceptionThresholdAlert. Applies to both the
    // player perception slot and the threat-perception accumulation below.
    [Export(PropertyHint.Range, "0,1,0.01")] public float PerceptionThresholdWary = 0.5f;
    // Contact strength (summed vision+hearing+smell perceptionDelta) above which
    // an already-triggered mob refreshes its fix on the player's true position
    // from ANY sense — so it turns to face a player it can only hear/smell.
    // Higher than MinPerceptionDelta on purpose: faint contact is enough to
    // *sustain* the alert (it stays out of the decay branch) but not to *track*
    // facing, so a mob lingering at the edge of smell range stays agitated
    // without snapping to stare straight at the player. Direct line of sight
    // refreshes the fix regardless of this value (see UpdatePerception's canSee
    // block); this gate only governs the hearing/smell-only case.
    [Export] public float PerceptionThresholdTrack = 0.15f;
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
    // Eye dilation ("night eyes") — mirrors the player's (PlayerData). A 0..1
    // runtime state on MobSimState.EyeDilation, driven by the cached AmbientLight
    // where the mob stands and smoothed asymmetrically (dilate slow, constrict
    // fast). Relieves the darkness penalty on the mob seeing the player, so a mob
    // that's been sitting in the gloom spots a dimly-lit player a little better.
    // Defaults match PlayerData so mob and player dark-adapt identically.
    [Export(PropertyHint.Range, "0.1,15,0.1")] public float eyeDilationDilateSeconds = 3.0f;
    [Export(PropertyHint.Range, "0.05,5,0.05")] public float eyeDilationConstrictSeconds = 0.4f;
    // PARTIAL by design (0.35 = at full dilation darkness costs 65% of normal).
    // 0 = off (mob perception unaffected by dilation).
    [Export(PropertyHint.Range, "0,1,0.01")] public float eyeDilationVisionRelief = 0.35f;
    // Smell reach in meters. The mob walks the target's ScentEmitter.Crumbs
    // each perception tick; a crumb contributes when it's within smellRange
    // AND a physics raycast from the mob's nose to the crumb is unblocked.
    // Like hearing, smell can't trigger the alert state on its own — only
    // active visual contact crosses the perception threshold.
    [Export] public float smellRange = 8f;
    [Export] public float smellRangePower = 0.5f;
    // Whether THIS mob, when perceived by another threat-scanning mob, is
    // threatening enough to flip that mob's threat slot `triggered` from sight
    // alone. true (default) = a normal threat: a mob whose threatTeam matches
    // this one and which fully perceives it engages on sight. false = this mob
    // can still be perceived (the scanner becomes aware, can go Wary, tracks it)
    // but is never auto-attacked from perception alone — the scanner fights it
    // only after being struck (Mob.Hit latches the threat slot directly,
    // bypassing this gate). Set false on a tamed companion so wandering hostiles
    // notice the pet without picking a fight unless provoked. Read off the
    // *target* mob, so "harmlessness" travels with the creature. Only relevant
    // for mobs that some other species scans via threatTeam.
    [Export] public bool canTriggerMobs = true;
    // Aggro bleed-off rate (aggro points per second) for this mob's per-enemy
    // threat-priority meter (see AggroTracker / MobSimState.Aggro). Damage this
    // mob takes — or, for a companion, damage dealt to its master — adds aggro
    // toward the attacker (health damage * DamageData.aggroMultiplier); the mob
    // then targets whichever tracked enemy holds the most aggro. This value is
    // how fast that focus fades once an attacker stops dealing damage, letting
    // the mob fall back to picking its target by perception / proximity. Higher
    // = more fickle focus; 0 = aggro never decays (a grudge held until death).
    [Export] public float aggroReductionSpeed = 5f;

    [ExportGroup("Perceivability")]
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
    // Extra prominence multipliers while airborne / perched (flying mobs only).
    // A bird in flight reads against open sky and catches the eye, so it's the
    // most conspicuous; a perched bird up on a branch is still easier to spot
    // than the same critter hidden at ground level. 1 = no bonus.
    [Export] public float flyingProminenceMultiplier = 2f;
    [Export] public float perchedProminenceMultiplier = 1.5f;
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

    [ExportGroup("Personality")]
    // Loyalty threshold at which this mob becomes tamed and joins the player as
    // a companion/pet. Once a mob's per-instance MobSimState.Loyalty crosses
    // this value (mirrors LoyaltyGift.requiredLoyalty), Mob flips Tamed,
    // registers as the player's command target, and its effective team becomes
    // Friendly (see Mob.ActorTeam). 0 = NOT tameable — merchants/villagers leave
    // this at 0 so they accrue gift-loyalty without ever becoming pets. A
    // starter slice — naming and multiple companions build on this.
    [Export] public float tameLoyalty = 0f;
    // Native language spoken by this mob. Acts as the default
    // ConversationContext.speakerLanguage for any branch whose own
    // `language` field is null — the player's learned components against
    // this language decide what scrambles. MobSimState.Language overrides
    // this per-instance. Null = speaks the player's language unconditionally
    // (universal NPCs).
    [Export] public LanguageData language;
    // Per-species taste model. An ordered list of multiplier rules folded over
    // an item's base value (ItemData.value) to produce the subjective worth
    // this species places on it — see Mob.PerUnitValue / CalculatePersonalValue.
    // Empty = the species values everything at face value. A dog authors a
    // single whenMissing-Meat rule at multiplier 0 (anything that isn't meat is
    // worthless); a villager layers several likes/dislikes. Each entry is an
    // ItemTagPreference; rules compose multiplicatively in author order.
    [Export] public Godot.Collections.Array<ItemTagPreference> itemPreferences = new();
    [Export] public bool dangerous = false;

    [ExportGroup("Combat")]
    // NOTE: a mob's weapon loadout is NOT a base-template trait — it lives on the
    // per-variant SpeciesData.weapons (so a claw goblin and a torch-bearer are
    // distinct species sharing this MobData). MobDescriptor.CreateState stamps it
    // onto MobSimState.Weapons; Mob.Weapons reads it from there. See SpeciesData /
    // BehaviorAttack.
    [Export] public float maxHealth = 10f;
    [Export] public float maxArmor = 0f;
    // Health a tamed companion is restored to when the player revives its
    // corpse (the Revive interactive — see Mob.Revive). An absolute value, not
    // a fraction: a companion comes back wounded at the authored amount and is
    // clamped to [1, maxHealth] so a revive never lands dead or over-full.
    // Only consulted for revivable mobs (tameLoyalty > 0).
    [Export] public float reviveHealth = 1f;
    // Player health spent to revive this companion's corpse (the Revive
    // interactive). The revive is refused at press when the player can't afford
    // it (ReviveBloodRequirement) and the cost is drained as blood — drained
    // health that regenerates over time — on completion. 0 = free revive. Only
    // consulted for revivable mobs (tameLoyalty > 0).
    [Export] public float reviveHealthCost = 0f;
    [Export] public float armorRechargeDelay = 6f;
    [Export] public float armorRechargeSpeed = 1f;
    [Export] public float armorRecoverTime = 30f;
    // Inherent stat modifiers. Composed with active StatusEffectData.
    // modifiers when the actor queries any stat. Damage / armor penetration / blunt /
    // knockback / buildup scaling all key on hit tags via this list;
    // vulnerabilities author multiplier > 1. Kun-kun's Dizzy vulnerability
    // is { Dizzy, 3 } here — any buildup feeding a Dizzy-tagged effect
    // lands triple.
    [Export] public Godot.Collections.Array<StatModifier> modifiers;
    // Per-species Dizzy resistance — a base trait every mob tunes, like
    // maxHealth / maxArmor (which are likewise direct fields with EStat
    // counterparts for situational deltas). The Dizzy buildup meter fills to
    // 1.0 to land the effect; this is the buildup multiple required to get
    // there — 1 is stock, 2 means "needs twice the buildup" (resistant), 0.5
    // means "half the buildup" (easily dizzied). Folded into ComposeMaskMul as
    // an inverse contribution scalar, so it composes with any situational
    // { Dizzy, x } StatModifier (kun-kun's { Dizzy, 3 } vulnerability still
    // stacks on top). Leave at 1 for no per-species adjustment.
    [Export(PropertyHint.Range, "0.1,10,0.1,or_greater")] public float dizzyResistance = 1f;
    // Status effect applied to this mob the moment it spawns and never removed
    // by the spawn path — the home for an intrinsic, lifelong effect. A summoned
    // minion authors its self-expiry here: a StatusEffectData with
    // maxHealthDrainPerSecond and duration=0, so its MAX health withers ~1/sec
    // (no floating damage number) until it reaches 0 and the minion dies. Null
    // on ordinary mobs.
    [Export] public StatusEffectData spawnStatusEffect;

    [ExportGroup("Burrowing")]
    [Export] public bool canBurrow = false;
    // Seconds from the moment a mob starts burrowing to when it's fully
    // underground and uninteractable. During this window the mesh is sinking
    // but the mob is still hittable.
    [Export] public float burrowTime = 1.5f;
    [Export] public float hideRange = 20f;
    // Status effect applied to this mob when the player digs it up (shovel) —
    // a brief stun so the player gets a beat before it attacks. Per-species so
    // a burrowing critter reads dizzy while a dug-up boss can be authored to
    // resist (shorter effect) or skip the stun entirely (leave null). The
    // effect's own duration is the stun length.
    [Export] public StatusEffectData dugUpStun;

    [ExportGroup("Audio")]
    // Time-of-day window (normalized [0,1): 0 = midnight, 0.25 = sunrise,
    // 0.5 = noon, 0.75 = sunset) during which this mob plays its idle anim-
    // audio loop (the _idleLoopFx chirp/hum). Outside the window the idle
    // loop is suppressed — a sparrow set to 0.15..0.65 chirps from before
    // dawn to early afternoon and falls silent the rest of the day. When
    // Start == End the window is the whole day (always active, the default).
    // Start > End wraps past midnight (e.g. a nocturnal mob at 0.75..0.25).
    // Only the idle loop is gated; the idle animation itself still plays.
    [Export(PropertyHint.Range, "0,1,0.001")] public float IdleLoopStartTimeOfDay = 0f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float IdleLoopEndTimeOfDay = 0f;
    // Blended rainAmount (0..1) above which the idle anim-audio loop falls
    // silent — a skittish critter clams up once the weather turns from a
    // drizzle into real rain. 1 = never suppressed by rain (the default; the
    // loop plays in any weather). 0.2 ≈ "quiet in anything more than a
    // drizzle". Same rainAmount signal the spawn gate reads
    // (World.CurrentRainAmount).
    [Export(PropertyHint.Range, "0,1,0.01")] public float IdleLoopMaxRain = 1f;
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
    // Continuous movement noise this mob emits. Mapped from current speed:
    // 0 at rest, sneakDecibels at half maxSpeed, runDecibels at maxSpeed.
    // Listeners (player + other mobs) check `decibels * hearingRange >
    // distance` to hear, and add a hearing contribution to their perception
    // delta when they do.
    [Export] public float sneakDecibels = 1f;
    [Export] public float runDecibels = 4f;

    [ExportGroup("AI")]
    [Export] public StringName defaultBehavior = "Idle";
    [Export] public BrainData brain;

    [ExportGroup("Bestiary")]
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
    // Exp awarded to each of the killing player's equipped weapons and armor
    // when this mob dies. Granted in Mob.Damage on the lethal hit; status-
    // effect kills (poison without an attributable source) do not award exp.
    [Export] public int exp = 0;

    [ExportGroup("Visuals")]
    // Per-EAnimation binding from logical slot to a concrete animation clip name
    // plus retiming policy. Empty slots resolve to default-StringName and the
    // animator silently skips them — author the dictionary in each mob .tres
    // to wire each slot to its concrete clip. See AnimationData.
    [Export] public Godot.Collections.Dictionary<EAnimation, AnimationData> animations = new();
    // Scale multiplier applied to the worldspace MobHUD once this species
    // has been discovered or triggered. Smaller creatures use values <1
    // so their callout doesn't dwarf them; bosses go >1. The pre-discovery
    // perception meter always renders at a fixed small scale regardless.
    [Export] public float hudScale = 1f;
    // Scene instantiated for this mob type. Single source of truth — every
    // place that previously paired a (PackedScene, MobData) reference
    // (ZoneGenData goblin/kun_kun, MobSpawnEntry) now references MobData
    // alone and reads MobScene from it.
    [Export] public PackedScene MobScene;
    // Per-instance recolor applied at spawn so one MobScene/FBX can serve many
    // biome variants (e.g. swamp vs desert goblin) without a unique model each.
    // Null = use the authored textures as-is. See MobPalette / ModelAnimator.
    [Export] public MobPalette palette;

    [ExportGroup("Loot & Death")]
    // NOTE: the loot drop list is NOT a base-species trait — it's a per-variant
    // concern that lives on SpeciesData.loot (so a forest vs desert kun-kun
    // drops different meat). CreateState stamps it onto MobSimState.Loot;
    // Mob.EjectLoot reads it from there. See SpeciesData / MobDescriptor.

    // Outward arc speed (m/s) applied to each piece of ejected loot when
    // the mob dies — both authored drops in EjectLoot and any stuck arrows
    // scattered with the corpse. Launched on a 45° upward arc; larger
    // values scatter wider.
    [Export] public float lootEjectSpeed = 5f;
    // When true the mob leaves no corpse: once it dies (loot ejected, death
    // fx fired) the body fades out in place over deathDespawnSeconds and is
    // removed permanently (node + sim state). For ethereal creatures like the
    // fairy, whose "body" is a glowing orb that should wink out rather than
    // litter the ground. Reuses the escape-vanish path with zero ascent.
    [Export] public bool despawnOnDeath = false;
    [Export] public float deathDespawnSeconds = 0.5f;

    [ExportGroup("Navigation")]
    [ExportSubgroup("Traversal & Movement")]
    // Read by the navigation system to decide which voxels this mob can walk
    // through, climb, or swim in. A mob with default values is a plain ground
    // walker that steps over 1-voxel curbs and avoids water.

    // Vertical voxels of step-up the mob can enter without "climbing" — 1 lets
    // a mob walk up a single-voxel ledge, 0 means it stops at any rise. Higher
    // values are for goat/spider-like climbers. Used by the walkability grid
    // to decide which neighbour cells are reachable from the current cell.
    [Export] public int maxStepHeight = 1;
    // Vertical speed (m/s) the body is driven upward at when the step-up assist
    // clears a voxel riser directly ahead of its movement. The mob's locomotion
    // is a purely-horizontal impulse, so without this the capsule wedges against
    // an upward step and stalls; this lift lets gravity + forward motion carry
    // it onto the ledge. High enough that the rise beats gravity for the tick;
    // the lift self-terminates once the capsule rises above the step. 0 disables
    // step-up entirely (a mob that should stall at any rise). See Mob.TryStepUp.
    [Export] public float stepClimbSpeed = 4f;
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
    // True if the mob is heavy/large enough to set off body-driven traps
    // (pressure-plate spike traps, etc). False = the trap's TriggerSource
    // ignores it entirely, so it neither springs the trap nor is caught by
    // one another body sprung. Small critters (dog, sparrow, kun-kun) set
    // this false.
    [Export] public bool triggersTraps = true;
    // Mob's half-width for clearance checks. Used to validate that a path
    // cell has enough horizontal room and to size the separation kernel.
    [Export] public float clearanceRadius = 0.4f;
    // Vertical voxels of headroom the mob needs above a surface to stand on
    // it. The default 2 matches a roughly player-height creature. A short mob
    // (dog, rat, chicken) sets 1 so the pathfinder lets it duck into 1-voxel
    // slots — low cave mouths, gaps under overhangs — that a 2-tall mob can't
    // fit through; a future tall mob (moose, bear rearing) sets 3+. Read only
    // by the walkability grid's headroom check, so it gates which cells the
    // nav system treats as standable — it does NOT resize the physics capsule.
    // Keep it truthful to the body's actual height. The shared walkability
    // cache keys on it, so distinct heights don't share standability samples.
    [Export] public int verticalClearance = 2;
    [Export] public float maxSpeed = 4f;
    // How strongly foliage (bushes, tall grass) slows this mob. The foliage's
    // own speed multiplier is applied at full strength at 1, ignored entirely
    // at 0, and partially at intermediate values (e.g. dogs at 0.5 are only
    // half-slowed). Light/flying creatures (kunkuns, sparrows) push through
    // unhindered at 0.
    [Export(PropertyHint.Range, "0,1,0.01")] public float foliageSpeedModifier = 1f;
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
    // Wind pickup factor in [0, 1] while airborne — parallels PlayerData.
    // windDragXZ. The drift target a falling mob is nudged toward each tick is
    // (sampled wind velocity) × windDragXZ, so a mob in 15 m/s wind drifts
    // toward 15 × windDragXZ m/s. Zero leaves falls perfectly vertical.
    // Doubles as the per-(m/s) head/tailwind coefficient on flier cruise speed
    // (see windFlySpeedCap).
    [Export(PropertyHint.Range, "0,1,0.005")] public float windDragXZ = 0.075f;

    [ExportSubgroup("Flight")]
    // True if the mob ignores ground entirely. Pathfinder runs in 3D for
    // these and steering applies a hover force toward terrain+hoverHeight.
    [Export] public bool canFly = false;
    // For fliers: preferred altitude above the terrain surface in voxels.
    // Steering layer pulls the mob toward this height when no goal demands
    // otherwise. A behavior may override per-trip via AIOutput.flyAltitude
    // (future low/medium/high cruise tiers).
    [Export] public float hoverHeight = 4f;
    // Horizontal cruise speed while airborne, m/s. Replaces maxSpeed for the
    // flight steering cap — birds travel faster than they hop on the ground.
    [Export] public float flySpeed = 9f;
    // Max climb/descent rate while seeking the target altitude, m/s. Caps the
    // vertical hover correction so a bird eases onto its cruise height rather
    // than snapping to it.
    [Export] public float verticalSpeed = 5f;
    // Altitude spring stiffness (per second). Higher = the bird corrects to its
    // target height more aggressively; lower = lazier, more floaty bobbing.
    [Export] public float hoverStiffness = 3f;
    // How strongly baked air currents displace flight, as a fraction of the
    // local wind velocity blended into the bird's desired velocity. 0 = wind
    // ignored, 1 = fully carried by the wind, >1 = exaggerated (kite-like).
    [Export(PropertyHint.Range, "0,2,0.05")] public float windInfluence = 0.5f;
    // Head/tailwind modulation of cruise speed, layered on top of windInfluence.
    // The component of wind ALONG the flight heading (dot of desired direction
    // and the local wind) scales flySpeed by windDragXZ per m/s: a tailwind
    // speeds the bird up, a headwind slows it down. windFlySpeedCap clamps that
    // fractional contribution symmetrically, so at the default 0.5 a bird tops
    // out at +50% with the wind and is floored at 50% slower straight into it.
    [Export(PropertyHint.Range, "0,1,0.05")] public float windFlySpeedCap = 0.5f;
    // Voxels of terrain look-ahead along the flight direction: the target
    // altitude is lifted to clear the highest surface within this distance so
    // the bird rises over hills ahead instead of skimming into them.
    [Export] public float flightLookAhead = 6f;

    [ExportSubgroup("Water / Swim")]
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

    // Look up the animation clip name for an EAnimation slot. Returns
    // default StringName when the slot is unbound — callers route this
    // through the animator's Play / HasAnimation, both of which no-op
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

    // Whether the idle anim-audio loop should be playing at the given
    // normalized time of day. Handles the wrap-around window (Start > End)
    // and treats Start == End as "always on".
    public bool IsIdleLoopActiveAt(double timeOfDay01)
    {
        if (Mathf.IsEqualApprox(IdleLoopStartTimeOfDay, IdleLoopEndTimeOfDay))
        {
            return true;
        }
        if (IdleLoopStartTimeOfDay < IdleLoopEndTimeOfDay)
        {
            return timeOfDay01 >= IdleLoopStartTimeOfDay && timeOfDay01 < IdleLoopEndTimeOfDay;
        }
        // Wrap-around window spanning midnight.
        return timeOfDay01 >= IdleLoopStartTimeOfDay || timeOfDay01 < IdleLoopEndTimeOfDay;
    }

    // Folds itemPreferences over an item's base value, multiplying by every
    // rule whose tag condition the item satisfies. Returns baseValue unchanged
    // when the list is empty (the species has no opinions).
    public float ApplyItemPreferences(float baseValue, EItemType itemTags)
    {
        if (itemPreferences == null)
        {
            return baseValue;
        }
        float v = baseValue;
        foreach (ItemTagPreference pref in itemPreferences)
        {
            if (pref != null && pref.Matches(itemTags))
            {
                v *= pref.multiplier;
            }
        }
        return v;
    }
}
