using Godot;

public enum EPlayerPerceptionState
{
    Hidden,
    Detected,
    Discovered
}

public class MobSimState : EntitySimState
{
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
    // When true, the mob's node is only created if the chunk activates during
    // nighttime. Authored at worldgen for surface goblins so they only show up
    // after dark. The MobSimState persists in WorldState either way; if the
    // chunk loads in daylight no node spawns until the chunk is unloaded and
    // reactivated after sunset.
    public bool SpawnAtNight;
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
