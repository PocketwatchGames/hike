using System.Collections.Generic;
using Godot;

public enum EPlayerPerceptionState
{
    Hidden,
    Detected,
    Discovered,
    // Terminal state for a corpse the player has actually laid eyes on. A
    // motionless dead body, once seen, stays seen — perception toward it only
    // ever rose, so it never decays back to a memory silhouette or dithers
    // out. Latched in Mob.UpdatePerception the first tick a dead mob is
    // actively perceived; reset back to Discovered if the mob resurrects.
    CorpseDiscovered
}

public class MobSimState : EntitySimState
{
    // Per-item-type gift count cap. The third repeat saturates a mob's
    // interest; anything past that is rejected by Mob.WillAcceptGift. Lives
    // here (next to the GiftCounts field that enforces it) so the loyalty
    // system has a single tunable.
    public const int MaxGiftsPerItemType = 3;

    public readonly MobData MobData;

    // Mutable runtime sim state — this is the canonical source of truth for the
    // mob; the Mob node is just a view that reads from and writes back to it.
    // RotationY (and inherited WorldPosition) are kept current by Mob.SyncToSimState
    // before the node is freed on chunk unload.
    public float RotationY;
    // Authored spawn transform, used by Idle to send the mob home after it has
    // been pulled away (combat, wander) and to restore its original facing.
    // Captured at construction from the initial WorldPosition/RotationY, so it
    // reflects where the mob first appeared rather than where it is currently.
    // Subscene stamping translates SpawnPosition by the placement offset so a
    // mob authored at subscene-local (5, 1, 3) gets its spawn anchor moved
    // to the destination world coordinate, not left at the local one.
    public Vector3 SpawnPosition;
    public float SpawnRotationY;
    // Optional per-mob override for the behavior the mob starts in (and returns to
    // when a behavior returns Complete). Null means use the brain's idleBehavior.
    public StringName InitialBehavior;
    // Optional per-individual idle-loop clip override (NpcSpawnEntry.IdleAnimation):
    // a concrete clip name (e.g. "idle_happy", "idle_nervous") that replaces the
    // species' shared Idle binding when the mob is standing still, so NPCs built
    // from one MobData each have a distinct resting pose. Must name a clip baked
    // into the rig's animation library (polysplit/anims → human_anims.res); Mob
    // .UpdateAnimation falls back to the species idle if it's missing. default =
    // species default idle. Persisted via EntitySerializer so a hand-authored
    // idle survives chunk eviction and save/load.
    public StringName IdleAnimation;
    // Optional per-instance override for the mob's spoken language. When
    // non-null, Mob.SpeakDialogue uses this in place of MobData.language —
    // lets WorldGen / world files assign a language to a mob whose
    // MobData is shared across many spawns without mutating the resource.
    // Null falls through to MobData.language.
    public LanguageData Language;
    // Optional per-instance branching conversation. Mob.SpeakDialogue routes
    // a Talk action through this when set — lets WorldGen / world files pin
    // a unique conversation onto a mob whose MobData is shared across many
    // spawns. Null = no conversation (Talk does nothing).
    public ConversationData Conversation;
    // Companion command state: true = "stay" (hold position), false = "follow"
    // the player. Toggled by the player's companion command input via the live
    // Mob; lives on sim state so it survives chunk unload/reload. Only
    // meaningful when the mob is tamed (Tamed / Mob.IsCompanion).
    public bool StayCommanded;
    // Whether this mob has been tamed and is now the player's companion.
    // Tameable mobs are authored on a wild team (Prey) and only join the
    // player's side once Loyalty crosses MobData.tameLoyalty (see Mob.MaybeTame
    // / Tame) — Mob.ActorTeam then overrides the effective team to Friendly so
    // the player can't friendly-fire it (see Teams.AreAllied) and it stops
    // reading as stalkable prey. Set at spawn for the starter companion
    // (already tamed); MaybeTame flips it at runtime. Survives chunk reload via
    // the live MobSimState and is serialized to the .hike world file (alongside
    // StayCommanded) so a tamed pet stays tamed across a save/load — a fresh
    // WorldGen spawns the starter companion tamed, but a reloaded world has no
    // other way to know.
    public bool Tamed;
    // Cumulative loyalty earned by gifting items to this mob. Increases by
    // the player's CalculatePersonalValue total each accepted Gift action on
    // the merchant screen; consumed as gifts cross their requiredLoyalty
    // threshold. Never decreases — loyalty is a one-way relationship score.
    public float Loyalty;
    // Remaining loyalty rewards. Seeded by WorldGen (or the world-file
    // loader) from the worldgen-level placement entry; never sourced from
    // MobData, since a single shared species template can't reasonably
    // hand out the same rewards to every villager instance. Mob.ReceiveGift
    // removes entries as they're handed back to the player; when the list
    // is empty the mob has nothing left to give and WillAcceptGift starts
    // rejecting (one of the rejection cases the spec calls out).
    public List<LoyaltyGift> LoyaltyGifts = new();
    // Per-instance merchant inventory. Seeded by WorldGen from the
    // worldgen-level placement entry so each merchant can stock different
    // wares without forking the shared MobData. The MerchantScreen reads
    // from this when the player opens trade. Each entry carries its own
    // loyaltyCost / secret flags so individual items can be gated or hidden
    // without changing the shape of the inventory list. Empty list =
    // nothing to sell.
    public List<MobInventoryItem> Inventory = new();
    // Per-instance item-preference overrides, folded after the species' base
    // MobData.itemPreferences (multiplicative — see ItemTagPreference.Fold /
    // Mob.PerUnitValue). Seeded from NpcSpawnEntry so a specific merchant can
    // value wares differently without forking MobData. Persisted by path
    // (standalone ItemTagPreference .tres). Empty = species defaults.
    public List<ItemTagPreference> ItemPreferences = new();
    // Running count of items the player has gifted to this mob, keyed by
    // ItemData. Enforces the per-type cap (MaxGiftsPerItemType) so a player
    // can't spam apples for unlimited loyalty. Persists across screen opens
    // — the cap is a property of the relationship, not the session.
    public Dictionary<ItemData, int> GiftCounts = new();
    // Required circumstances for this mob's node to be created (see
    // ESpawnConditions). Authored at worldgen — e.g. Night for surface
    // goblins, Day | Clear for sparrows. The MobSimState persists in
    // WorldState either way; if the chunk loads while the conditions don't
    // hold no node spawns until the chunk reactivates while they do.
    public ESpawnConditions SpawnConditions;
    // Whether this mob is a merchant who accepts two-way trades. False (the
    // default) surfaces the GiveItem verb on Mob and opens the merchant
    // screen in gift-only mode (get panel hidden). True swaps the verb to
    // Trade and opens the screen with both staging panels visible — the
    // commit button label then flips between Gift and Trade based on what
    // the player has staged. Per-instance so a worldgen-authored shopkeeper
    // can trade while another mob sharing the same MobData stays gift-only.
    public bool WillTrade;
    // Authored party-member template for a recruitable NPC (NpcSpawnEntry
    // .recruitTemplate). When set, a RecruitToPartyAction in this mob's
    // conversation can add them to the party: the template is DEEP-cloned into a
    // new roster member who stands at the active campfire, and this mob despawns.
    // Null = not recruitable. A standalone PlayerState .tres (like the starting
    // party) so it round-trips through EntitySerializer by resource path — an
    // un-recruited NPC keeps its recruitability across chunk eviction / save.
    public PlayerState RecruitTemplate;
    // Elite mobs are a rarer, tougher variant — 25% larger (Mob applies the
    // scale), crowned, with the shared elite buff and crown-trophy loot. Authored
    // on the spawning MobDescriptor (a dedicated *_elite.tres), so it persists as
    // a plain flag; the signature effect + HUD badge ride StatusEffects / Badge
    // below.
    public bool Elite;
    // Difficulty tier, stamped at spawn (MobDescriptor.level plus the worldgen
    // level field — see WorldGen.ComputeMobLevel). Each level doubles the mob's
    // health, armor, and outgoing damage (2^Level, applied by Mob) and shows as
    // Level+1 pips on the HUD. 0 = base. Persisted via EntitySerializer so the
    // scaled vitals stay consistent across chunk eviction and save/load.
    public int Level;
    // The species variant this mob IS — the bestiary identity (discovery key,
    // see SimState.DiscoveredSpecies) and the source of
    // its recolor / loot / per-variant stat modifiers. Stamped from
    // MobDescriptor at spawn and persisted via EntitySerializer so the variant
    // identity survives chunk eviction and .hike load. The base MobData (this.
    // MobData) is reachable via Species.mob; they agree when Species is set.
    // Null only for legacy/editor-placed mobs built straight from a bare
    // MobData — those don't contribute to the bestiary.
    public SpeciesData Species;
    // Per-instance overrides stamped at spawn so one MobData/mobScene serves many
    // variants. Null = fall back to the species defaults. Persisted via
    // EntitySerializer so a reloaded variant keeps these rather than reverting.
    // Both come from the SpeciesData: Palette is the biome recolor, Weapons is the
    // loadout (a torch-bearing goblin vs a claw goblin — each its own species).
    // Read by Mob.Weapons.
    public MobPalette Palette;
    // Per-individual outfit override (NpcSpawnEntry.Outfit): the modular rig's
    // visible clothing/hair/hat mesh names, composed with the rig's always-on
    // base meshes at spawn (Mob → ModelAnimator.ApplyOutfit). Null/empty = the
    // scene's authored default outfit. Persisted via EntitySerializer so a
    // hand-dressed NPC keeps its look across chunk eviction and save/load.
    public string[] Outfit;
    public Godot.Collections.Array<WeaponData> Weapons;
    // Per-instance status effects authored on the descriptor, applied to every
    // mob it spawns regardless of Elite — the home for an elite's signature
    // effect (e.g. the Lightning weapon-mod on a *_elite.tres). Re-applied at
    // every node spawn (Mob.Initialize), since the status controller itself isn't
    // serialized — so these survive chunk eviction and .hike load.
    public Godot.Collections.Array<StatusEffectData> StatusEffects;
    // Loot ejected on death, stamped from SpeciesData.loot at spawn so a
    // zone variant drops its own spoils (loot is no longer a MobData field).
    // Null = no drops. Persisted via EntitySerializer (item path + count per
    // entry) so a reloaded mob still drops; descriptor mods on loot aren't
    // persisted (mob meat carries none — matches the chest-loot serialization).
    // Read by Mob.EjectLoot.
    public Godot.Collections.Array<ItemCount> Loot;
    // HUD badge icon (EliteMobDescriptor.badge, via the descriptor's elite
    // reference), read once by MobHUD. Null = no badge.
    public Texture2D Badge;
    // Per-elite crown scene override (EliteMobDescriptor.crownScene). Re-instanced
    // at every spawn by Mob.SpawnEliteCrown. Null = use the shared
    // SimData.EliteCrownScene. Only meaningful when Elite.
    public PackedScene EliteCrownScene;
    public bool Alive;
    // Burrow is a two-phase state machine: Burrowing is the descent window
    // after aiOutput.burrow first goes true, BurrowTimeMs is the absolute
    // GameTimeMs at which the descent completes, and Burrowed is the fully-
    // hidden state once the countdown elapses. All three clear the moment
    // aiOutput.burrow stops being set.
    public bool Burrowing;
    public ulong BurrowTimeMs;
    public bool Burrowed;
    // Flying mobs only: true while the mob is in the air (gravity off, flight
    // physics active). Driven each tick from AIOutput.airborne so the animation
    // and physics layers agree on whether the mob is aloft. Transient — not
    // serialized; a mob saved mid-flight loads grounded and takes off again.
    public bool Airborne;
    public float MaxHealth;
    public float Health;
    // Set by the deserializer so Mob.Initialize preserves the persisted vitals
    // instead of refilling to the freshly-composed max. Transient — a fresh
    // spawn leaves it false and gets its vitals finalized at spawn.
    public bool RestoredFromSave;
    // Latches true the first time the player deals damage to this mob (any
    // hit with hit.source == Player, regardless of whether armor absorbed
    // it). Decides whether the eventual death awards bestiary kill credit:
    // a mob that dies from a trap, status effect, or another mob with no
    // player intervention doesn't count. Never cleared once set — staying
    // alive after a player hit doesn't revoke credit on a later death.
    public bool DamagedByPlayer;
    // Per-hit flinch state. HitstunTime counts down each tick while > 0 and
    // the hitstun anim is held; KnockbackTime mirrors it for the knockback
    // lockout window so receivers that suspend their own movement during
    // knockback have a shared deadline. Independent of Dizzy — a hit can
    // hitstun without crossing the dizzy buildup threshold.
    public float HitstunTime;
    public float KnockbackTime;
    // Horizontal velocity (m/s) forced onto the body each physics tick
    // while KnockbackTime > 0. distance/time at hit time. Snapped back to
    // zero on the trailing edge so the mob doesn't coast past the authored
    // distance under residual physics damping. Y component unused.
    public Vector3 KnockbackVelocity;
    // ApplyMotion state — seeded by an ApplyMotion event firing through
    // Mob.ApplyMotion (e.g. a goblin claw's dart). While MotionTime > 0 the
    // body's horizontal velocity is forced to MotionVelocity each tick, the
    // path-driven impulse block is skipped, and (if MotionFreezeGravity)
    // gravity is suppressed. Snapped to zero on the trailing edge, same
    // pattern as KnockbackTime. Direction is captured at the moment of the
    // event so a later facing change doesn't curve the dart.
    public float MotionTime;
    public Vector3 MotionVelocity;
    public bool MotionFreezeGravity;
    public float Armor;
    // Game-time at which armor recharge can begin. Set on every armor-
    // absorbing hit; the longer recover window is what ArmorDepleted tracks
    // so the recharge-begin oneshot can pick the recover variant.
    public ulong ArmorRechargeStartMs;
    public bool ArmorRecharging;
    public bool ArmorDepleted;
    public float PlayerPerception;
    public ulong MemoryTimeMs;
    public ulong VisibleTimeMs;
    public EPlayerPerceptionState DiscoveryState;
    public InvestigateState? Investigation;
    public bool Yelled;
    public ulong SuspendAITimeMs;
    // One perception slot per potential target. Currently sized to 1 (the player);
    // the array shape is kept so multiplayer can add slots without reshuffling.
    public PerceptionState[] PerceptionTargets = new PerceptionState[1];

    // Cross-faction threat awareness — the accumulating perception toward the
    // nearest mob on the opposite side of the player divide, built with the same
    // vision model as the player slot above. Only updated for mobs that scan
    // (dangerous hostiles / companions — see MobAI.AccumulateThreatPerception);
    // drives the Wary / Attack tiers in the companion brain. `target` holds the
    // Mob currently being tracked (null when none in range).
    public PerceptionState ThreatPerception;

    // Damage-driven threat priority, one decaying aggro value per tracked enemy.
    // A separate mechanic from perception above (awareness) — perception decides
    // whether an enemy is engaged at all, aggro decides which engaged enemy to
    // hit. Fed by Mob.Damage (attacker hurts this mob) and the player→companion
    // relay (attacker hurts this mob's master); consumed by target selection.
    // Transient combat state, intentionally not serialized.
    public readonly AggroTracker Aggro = new();

    // UpdatePerception is throttled to PerceptionTickInterval seconds. Each frame
    // accumulates delta into PerceptionTickAccumulator; when it overflows the
    // interval, UpdatePerception runs with the accumulated delta and the
    // accumulator is reset. The accumulator is seeded with a random offset at
    // construction so different mobs raycast on different frames (jitter).
    public const float PerceptionTickInterval = 0.1f;
    public float PerceptionTickAccumulator;

    // Cached environment-light readings, refreshed every LightSampleInterval.
    // SkyBrightness is the time-of-day / storm-scaled primary intensity (the
    // sun "is dim because it's stormy or nighttime" signal). SunExposure is
    // the [0,1] sunlight-BFS value at the mob's voxel (the "is dim because
    // I'm in a cave / under a roof" signal). AmbientLight is their product —
    // the single number behaviors compare against a "light my torch" threshold.
    public const float LightSampleInterval = 0.75f;
    public float LightSampleAccumulator;
    public float SkyBrightness;
    public float SunExposure;
    public float AmbientLight;

    // Dark-adaptation ("night eyes") state in [0,1]: 0 = light-adapted, 1 = fully
    // dilated. Mirrors Player.EyeDilation — smoothed each frame in Mob from the
    // cached AmbientLight, and read by UpdatePerception to relieve the darkness
    // penalty on seeing the player. Not serialized; re-converges in ~seconds.
    public float EyeDilation;

    public MobSimState(Vector3 worldPosition, float rotationY, PackedScene scene, MobData mobData, int level = 0)
        : this(worldPosition, rotationY, worldPosition, rotationY, scene, mobData, level)
    {
    }

    // Full constructor used by the deserializer so a mob restored from disk
    // keeps its authored spawn transform even if its current position has drifted.
    // `level` is the mob's difficulty tier, known at spawn — the deserializer
    // passes 0 and overwrites Level/Health/Armor with the persisted values after.
    public MobSimState(Vector3 worldPosition, float rotationY, Vector3 spawnPosition, float spawnRotationY, PackedScene scene, MobData mobData, int level = 0)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
        SpawnPosition = spawnPosition;
        SpawnRotationY = spawnRotationY;
        MobData = mobData;
        Alive = true;
        // Only combat threats carry a difficulty tier; prey / villagers /
        // companions are always level 0. Enforced here — the single point every
        // fresh spawn's level flows through — so no caller can leave a
        // non-dangerous mob leveled (which would scale its vitals and light HUD pips).
        Level = mobData.dangerous ? Mathf.Max(0, level) : 0;
        // Vitals at their level-scaled max. MaxHealth stays the unscaled drainable
        // base (the maxHealth/maxArmor properties re-apply the 2^Level pool
        // multiplier on read); Health/Armor are stored already scaled so they
        // agree with those properties even for a mob baked into a .hike straight
        // from worldgen and reloaded as RestoredFromSave (which skips
        // Mob.Initialize's vitals finalize). Inherent MobData.modifiers and elite
        // status effects fold in later, in that finalize, for a fresh live spawn.
        float mult = Mob.PoolLevelMultiplier(Level);
        MaxHealth = mobData.maxHealth;
        Health = mobData.maxHealth * mult;
        Armor = mobData.maxArmor * mult;
        PlayerPerception = 0f;
        DiscoveryState = EPlayerPerceptionState.Hidden;
        MemoryTimeMs = 0;
        PerceptionTickAccumulator = (float)GD.RandRange(0.0, PerceptionTickInterval);
        LightSampleAccumulator = (float)GD.RandRange(0.0, LightSampleInterval);
    }

    public override bool ShouldSpawn(Sim sim)
    {
        if (!Alive)
        {
            return false;
        }
        if (!sim.SpawnConditionsMet(SpawnConditions))
        {
            return false;
        }
        return true;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        if (!ShouldSpawn(sim))
        {
            return null;
        }
        return Mob.Create(sim, this);
    }

    // Returns this mob to its authored spawn condition so a fresh node
    // re-materializes at the spawn post, full health, unaware — as if the chunk
    // had just loaded. Driven by Sim.ResetSpawns when time passes (sleep / death).
    // Restores the transform, revives, and clears all combat/awareness runtime
    // state; vitals refill on the next Mob.Initialize because RestoredFromSave is
    // cleared here (so a killed or half-dead mob comes back at its full level/
    // elite-scaled max). Deliberately preserves identity and progression the reset
    // shouldn't undo — taming, loyalty, gifts, inventory, level, elite, species.
    public void ResetToSpawn()
    {
        WorldPosition = SpawnPosition;
        RotationY = SpawnRotationY;
        Alive = true;
        // Force the next spawn down the fresh-spawn path so it refills vitals to
        // the composed (level/elite-scaled) max rather than keeping the persisted
        // wounded pool.
        RestoredFromSave = false;

        // Awareness / perception back to unseen-and-unaware.
        PlayerPerception = 0f;
        MemoryTimeMs = 0;
        VisibleTimeMs = 0;
        DiscoveryState = EPlayerPerceptionState.Hidden;
        Investigation = null;
        Yelled = false;
        SuspendAITimeMs = 0;
        ThreatPerception = default;
        for (int i = 0; i < PerceptionTargets.Length; i++)
        {
            PerceptionTargets[i] = default;
        }
        Aggro.Clear();
        DamagedByPlayer = false;

        // Flinch / knockback / applied-motion transients.
        HitstunTime = 0f;
        KnockbackTime = 0f;
        KnockbackVelocity = Vector3.Zero;
        MotionTime = 0f;
        MotionVelocity = Vector3.Zero;
        MotionFreezeGravity = false;

        // Burrow / flight.
        Burrowing = false;
        Burrowed = false;
        BurrowTimeMs = 0;
        Airborne = false;

        // Armor recharge bookkeeping (the armor pool itself refills at spawn).
        ArmorRecharging = false;
        ArmorDepleted = false;
        ArmorRechargeStartMs = 0;
    }
}
