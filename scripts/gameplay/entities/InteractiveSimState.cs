using Godot;
using System.Collections.Generic;

public class DoorSimState : EntitySimState
{
    public bool Active = true;
    public readonly float RotationY;

    public DoorSimState(Vector3 worldPosition, float rotationY, PackedScene scene)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
    }

    public override Node3D CreateEntity(World world)
    {
        return Door.Create(world, this);
    }
}

public class TorchSimState : EntitySimState
{
    public bool Active = true;
    // When true, Torch.Create overrides Active based on world time-of-day at
    // chunk activation: lit at night, unlit during the day. Authored on
    // worldgen-spawned campfires so they "come alive" after dark without the
    // player having to light each one. Player toggles still apply for the
    // duration the chunk is loaded; the next chunk activation re-evaluates.
    public bool AutoLightAtNight;

    public TorchSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return Torch.Create(world, this);
    }
}

// Campfires / cooking stations. Standalone from TorchSimState — the
// persistent cooking inputs and cook-job timer are forge-specific.
public class ForgeSimState : EntitySimState
{
    // Number of cooking slots a forge exposes. Mirrored by the
    // CookingPanel.tscn layout — adding a slot here requires adding a
    // matching ItemSlotPanel reference there.
    public const int ForgeSlotCount = 3;

    public bool Active = true;
    // When true, Forge.Create overrides Active based on world time-of-day
    // at chunk activation: lit at night, unlit during the day. Authored on
    // worldgen-spawned campfires so they "come alive" after dark without
    // the player having to light each one.
    public bool AutoLightAtNight;

    // Persistent cooking inputs. Forge reads/writes through this array so
    // contents survive CookingScreen open/close; idle-close returns them
    // to the player's inventory, mid-cook close leaves them for the active
    // job to consume.
    public ItemState[] ForgeSlots = new ItemState[ForgeSlotCount];

    // Non-null while a cook job is in flight. Forge._PhysicsProcess ticks
    // remainingSeconds; on completion the slots are drained and the output
    // is delivered through the bound CookingScreen (if any) or spawned as
    // Loot at the forge's position.
    public ForgeJob ActiveForgeJob;

    public ForgeSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return Forge.Create(world, this);
    }
}

// Active cook job — recipe + timer + output preview. Owned by
// ForgeSimState; the forge's runtime entity ticks the timer. Discovery
// flags aren't tracked here — Forge.CompleteForgeJob computes them against
// the live WorldSimState at the moment the cook actually finishes, so a
// cancelled cook doesn't leak credit and an offscreen completion still
// records correctly.
public class ForgeJob
{
    public RecipeData recipe;
    public ItemData outputItem;
    public float remainingSeconds;
    public float totalSeconds;

    public float Progress01
    {
        get
        {
            if (totalSeconds <= 0f)
            {
                return 0f;
            }
            float elapsed = totalSeconds - remainingSeconds;
            return Godot.Mathf.Clamp(elapsed / totalSeconds, 0f, 1f);
        }
    }
}

public class ChestSimState : EntitySimState
{
    public bool Active = true;
    // Required circumstances for this chest's node to be created (see
    // ESpawnConditions). Authored at worldgen for chests anchored to gated
    // encounters (e.g. Night for campfire encampments). Mirrors
    // MobSimState.SpawnConditions.
    public ESpawnConditions SpawnConditions;
    // Contents the chest ejects on open. Authored on whatever places the
    // chest (ChestSpawnEntry for procedural spawns, WorldGenData for test
    // fixtures, future editor placements) — the chest scene itself carries
    // no loot, so a single generic chest.tscn handles every variant by
    // having a different LootItems list pushed onto its SimState. Each
    // ItemCount ejects as one stacked Loot (single pickup with stackCount
    // = count), so "5 mushrooms" is one pile rather than five separate
    // pickups.
    public ItemCount[] LootItems;

    // Persistent slot contents — the inventory the chest actually holds
    // between visits. Distinct from LootItems (which is the worldgen-rolled
    // ejection recipe consumed when the chest is opened): Contents holds
    // live ItemState instances with stack counts and cooldowns, and rides
    // the wire format so a stash-style chest keeps whatever the player
    // deposited across save/load and chunk eviction. Default empty.
    // Mutators (stash UI, future chest UIs) must write to this list
    // directly — the runtime Chest node holds a reference to this SimState,
    // so direct mutation persists without any sync-back hook.
    // Subclass-specific ItemState fields (WeaponState.ammo/level,
    // ConsumableState.isActive, ArmorState.exp/level) are NOT preserved
    // — items round-trip through ItemData.CreateState(), resetting to
    // authored defaults. Lift this when player Inventory persistence lands.
    public readonly List<ItemState> Contents = new();

    public ChestSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override bool ShouldSpawn(World world)
    {
        if (!world.SpawnConditionsMet(SpawnConditions))
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
        return Chest.Create(world, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

public class BerryTreeSimState : EntitySimState
{
    // Once true the tree is bare and stays that way (no regrowth). Hurtbox
    // disables, interactive blocks. Per-instance so worldgen can vary the
    // payload between bushes; serialized so a half-harvested forest stays
    // half-harvested across save/load.
    public bool Picked;
    public int BerryCount;

    public BerryTreeSimState(Vector3 worldPosition, PackedScene scene, int berryCount)
        : base(worldPosition, scene)
    {
        BerryCount = berryCount;
    }

    public override Node3D CreateEntity(World world)
    {
        return BerryTree.Create(world, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

public class TrapSimState : EntitySimState
{
    public bool Disarmed;

    public TrapSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return Trap.Create(world, this);
    }
}

public class SignpostSimState : EntitySimState
{
    // Text shown in the HUD panel when the player interacts. Stored on the
    // sim state so each placed signpost in a world file can carry its own
    // message — the .tscn is shared.
    public string Text;
    public LanguageData Language;

    public SignpostSimState(Vector3 worldPosition, PackedScene scene, string text, LanguageData language)
        : base(worldPosition, scene)
    {
        Text = text ?? string.Empty;
        Language = language;
    }

    public override Node3D CreateEntity(World world)
    {
        return Signpost.Create(world, this);
    }
}

public class KnowledgeStoneSimState : EntitySimState
{
    // Inscription shown to the player when read. Stored per-instance so a
    // single .tscn can carry many different stones across the world file.
    public string Text;
    // Language the inscription is written in (drives the scramble gating on
    // display). Decoupled from what the stone teaches — see KnowledgeStone
    // for the split rationale.
    public LanguageData InscriptionLanguage;
    // Concepts this specific stone grants on read. Empty/null falls back to
    // whatever the scene authored on its `_concepts` field. Polymorphic
    // resource refs — LanguageTeachable, RecipeTeachable, RegionTeachable.
    // EntitySerializer's legacy KnowledgeStone wire format (Language +
    // Components int) is converted into a single-entry LanguageTeachable
    // here on read so old .hike files keep working.
    public Godot.Collections.Array<TeachableConcept> Concepts;

    public KnowledgeStoneSimState(Vector3 worldPosition, PackedScene scene, string text, LanguageData inscriptionLanguage, Godot.Collections.Array<TeachableConcept> concepts)
        : base(worldPosition, scene)
    {
        Text = text ?? string.Empty;
        InscriptionLanguage = inscriptionLanguage;
        Concepts = concepts;
    }

    public override Node3D CreateEntity(World world)
    {
        return KnowledgeStone.Create(world, this);
    }
}

public class WellSimState : EntitySimState
{
    public WellSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return Well.Create(world, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

// A tree the player can climb to perch in the canopy. Climbing hides the
// player from mobs and lifts the camera into bird's-eye (see Player.
// EnterClimbableTree). No persistent per-instance state — the tree is always
// climbable and the "am I up there" state lives on the Player, not the tree
// (so it survives the tree's chunk streaming out underneath the player).
public class ClimbableTreeSimState : EntitySimState
{
    public ClimbableTreeSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return ClimbableTree.Create(world, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

public class FireTrapSimState : EntitySimState
{
    // Random per-instance offset (seconds) added to the trap's first Idle
    // window so neighbouring traps don't fire in lockstep. Rolled once at
    // creation and persisted through save/load — preserving the rhythm
    // matters for replayability of authored swamp encounters.
    public float PhaseOffsetSeconds;

    public FireTrapSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return FireTrap.Create(world, this);
    }
}
