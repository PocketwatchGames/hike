using System.Collections.Generic;
using Godot;

public enum EPlayerPerceptionState
{
    Hidden,
    Detected,
    Discovered
}

public class MobSimState : EntitySimState
{
    // Per-item-type gift count cap. The third repeat saturates a mob's
    // interest; anything past that is rejected by Mob.WillAcceptGift. Lives
    // here (next to the GiftCounts field that enforces it) so the loyalty
    // system has a single tunable. Per-mob overrides can live on MobData
    // later if it turns out species need their own caps.
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
    public readonly Vector3 SpawnPosition;
    public readonly float SpawnRotationY;
    // Optional per-mob override for the behavior the mob starts in (and returns to
    // when a behavior returns Complete). Null means use the brain's idleBehavior.
    public StringName InitialBehavior;
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
    // Running count of items the player has gifted to this mob, keyed by
    // ItemData. Enforces the per-type cap (MaxGiftsPerItemType) so a player
    // can't spam apples for unlimited loyalty. Persists across screen opens
    // — the cap is a property of the relationship, not the session.
    public Dictionary<ItemData, int> GiftCounts = new();
    // When true, the mob's node is only created if the chunk activates during
    // nighttime. Authored at worldgen for surface goblins so they only show up
    // after dark. The MobSimState persists in WorldState either way; if the
    // chunk loads in daylight no node spawns until the chunk is unloaded and
    // reactivated after sunset.
    public bool SpawnAtNight;
    // Whether this mob is a merchant who accepts two-way trades. False (the
    // default) surfaces the GiveItem verb on Mob and opens the merchant
    // screen in gift-only mode (get panel hidden). True swaps the verb to
    // Trade and opens the screen with both staging panels visible — the
    // commit button label then flips between Gift and Trade based on what
    // the player has staged. Per-instance so a worldgen-authored shopkeeper
    // can trade while another mob sharing the same MobData stays gift-only.
    public bool WillTrade;
    public bool Alive;
    // Burrow is a two-phase state machine: Burrowing is the descent window
    // after aiOutput.burrow first goes true, BurrowTimeMs is the absolute
    // GameTimeMs at which the descent completes, and Burrowed is the fully-
    // hidden state once the countdown elapses. All three clear the moment
    // aiOutput.burrow stops being set.
    public bool Burrowing;
    public ulong BurrowTimeMs;
    public bool Burrowed;
    public float MaxHealth;
    public float Health;
    // Stun has two distinct deadlines, so each gets its own field rather than
    // one variable doing double duty. Stunned is the explicit binary state
    // (mirrors Armor's ArmorDepleted/ArmorRecharging pattern); StunRecoverMs
    // is the wake-up deadline (only meaningful while Stunned); StunRechargeStartMs
    // is the post-hit delay before the sub-threshold meter starts draining
    // (only meaningful while !Stunned && Stun > 0). One is always 0/unused.
    // Latches true the first time the player deals damage to this mob (any
    // hit with hit.source == Player, regardless of whether armor absorbed
    // it). Decides whether the eventual death awards bestiary kill credit:
    // a mob that dies from a trap, status effect, or another mob with no
    // player intervention doesn't count. Never cleared once set — staying
    // alive after a player hit doesn't revoke credit on a later death.
    public bool DamagedByPlayer;
    public float Stun;
    public bool Stunned;
    public ulong StunRecoverMs;
    public ulong StunRechargeStartMs;
    // Per-hit flinch state. HitstunTime counts down each tick while > 0 and
    // the hitstun anim is held; KnockbackTime mirrors it for the knockback
    // lockout window so receivers that suspend their own movement during
    // knockback have a shared deadline. Independent of Stunned — a hit can
    // hitstun without crossing the stun threshold.
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

    // Torch light/douse thresholds live on SimData (MobTorchLightThreshold /
    // MobTorchDouseThreshold) so they're tunable per-world and can carry a
    // hysteresis gap between them — the gap kills the per-tick on/off
    // flicker that happens when ambient hovers near a single threshold.

    public MobSimState(Vector3 worldPosition, float rotationY, PackedScene scene, MobData mobData)
        : this(worldPosition, rotationY, worldPosition, rotationY, scene, mobData)
    {
    }

    // Full constructor used by the deserializer so a mob restored from disk
    // keeps its authored spawn transform even if its current position has drifted.
    public MobSimState(Vector3 worldPosition, float rotationY, Vector3 spawnPosition, float spawnRotationY, PackedScene scene, MobData mobData)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
        SpawnPosition = spawnPosition;
        SpawnRotationY = spawnRotationY;
        MobData = mobData;
        Alive = true;
        MaxHealth = mobData.maxHealth;
        Health = mobData.maxHealth;
        Armor = mobData.maxArmor;
        PlayerPerception = 0f;
        DiscoveryState = EPlayerPerceptionState.Hidden;
        MemoryTimeMs = 0;
        PerceptionTickAccumulator = (float)GD.RandRange(0.0, PerceptionTickInterval);
        LightSampleAccumulator = (float)GD.RandRange(0.0, LightSampleInterval);
    }

    public override bool ShouldSpawn(World world)
    {
        if (!Alive)
        {
            return false;
        }
        if (SpawnAtNight && !WorldState.IsNight(world.WorldState.TimeOfDay01))
        {
            return false;
        }
        return true;
    }

    public override Node3D CreateEntity(World world)
    {
        if (!ShouldSpawn(world))
        {
            return null;
        }
        return Mob.Create(world, this);
    }
}
