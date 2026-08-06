using Godot;
using System.Collections.Generic;

public class DoorSimState : EntitySimState
{
    public bool Active = true;

    // Bottom cell of the doorway column this door makes opaque while closed.
    // Runtime-only and deliberately NOT serialized: it is derived from the seat
    // position against world voxels, and resolving it once (see
    // Door.ResolveOccluderBase) is what keeps the load-time stamp and the
    // runtime toggle writing the same cells. Null until first resolved, which
    // also marks a door the load-time stamp has never seen.
    public Vector3I? OccluderBase;

    public DoorSimState(Vector3 worldPosition, float rotationY, PackedScene scene)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Door.Create(sim, this);
    }
}

// Player-operated (and optionally lever-linked) trapdoor. Persists whether the
// leaf is currently open and the link tag a Lever targets it by.
public class TrapdoorSimState : EntitySimState
{
    // True == leaf swung open. Persisted so a reloaded world snaps back to how
    // the player left it.
    public bool Open;

    // Shared key a Lever pulls this trapdoor by. Empty = player-operated only.
    // Distinct from EntitySimState.Tag, which is the subscene variant pool —
    // reusing that would make the trapdoor stop spawning unconditionally.
    public string LinkTag = "";

    public TrapdoorSimState(Vector3 worldPosition, float rotationY, PackedScene scene)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Trapdoor.Create(sim, this);
    }
}

// A pull-lever that remote-triggers trapdoors sharing its target link tag.
public class LeverSimState : EntitySimState
{
    // LinkTag of the trapdoor(s) this lever throws. Empty = wired to nothing.
    public string TargetLinkTag = "";

    // Current handle position (persisted so a reloaded lever keeps its throw).
    public bool On;

    public LeverSimState(Vector3 worldPosition, float rotationY, PackedScene scene)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Lever.Create(sim, this);
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

    public override Node3D CreateEntity(Sim sim)
    {
        return Torch.Create(sim, this);
    }
}

// Campfires / cooking stations. Standalone from TorchSimState — the
// persistent cooking inputs and cook-job timer are forge-specific.
public class CampfireSimState : EntitySimState
{
    // Default hazard danger-zone radius (meters) — see EntitySimState.HazardRadius.
    // Single source for the spawn entry's [Export] default and the .hike
    // deserialization fallback so the two never diverge.
    public const float DefaultHazardRadius = 1.25f;

    // Number of cooking slots a forge exposes. Mirrored by the
    // CookingPanel.tscn layout — adding a slot here requires adding a
    // matching ItemSlotPanel reference there.
    public const int CampfireSlotCount = 3;

    // A campfire spawns unlit unless authored otherwise (the party's spawn
    // campfire). Lighting one douses all others so only one is ever Active.
    public bool Active = false;

    // Persistent experimentation inputs. Campfire reads/writes through this
    // array so contents survive CookingScreen open/close; closing the screen
    // returns them to the party material stash.
    public ItemState[] CampfireSlots = new ItemState[CampfireSlotCount];

    public CampfireSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Campfire.Create(sim, this);
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
    // Subclass-specific ItemState fields (WeaponState.ammo,
    // LanternState.isActive) are NOT preserved — items round-trip through
    // ItemData.CreateState(), resetting to authored defaults. Lift this when
    // player Inventory persistence lands.
    public readonly List<ItemState> Contents = new();

    public ChestSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override bool ShouldSpawn(Sim sim)
    {
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
        return Chest.Create(sim, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

public class BerryTreeSimState : RegrowSimState
{
    // Number of berries the tree drops when picked. Per-instance so worldgen can
    // vary the payload between bushes; serialized so a stocked bush keeps its
    // count across save/load.
    public int BerryCount;

    // Inherited RegrowDay is the harvest deadline: bare (picked) while the world
    // day is below it, ripe again once reached. A half-harvested forest stays
    // half-harvested across save/load.
    public BerryTreeSimState(Vector3 worldPosition, PackedScene scene, int berryCount)
        : base(worldPosition, scene)
    {
        BerryCount = berryCount;
    }

    public override bool IsRoadObstacle => true;

    public override Node3D CreateEntity(Sim sim)
    {
        return BerryTree.Create(sim, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

public class TrapSimState : EntitySimState
{
    // Default hazard danger-zone radius (meters) — see EntitySimState.HazardRadius.
    // Larger than the fire traps: the spike field is a ~3x3m square.
    public const float DefaultHazardRadius = 2.5f;

    public bool Disarmed;

    public TrapSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Trap.Create(sim, this);
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

    public override Node3D CreateEntity(Sim sim)
    {
        return Signpost.Create(sim, this);
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

    public override Node3D CreateEntity(Sim sim)
    {
        return KnowledgeStone.Create(sim, this);
    }
}

public class WellSimState : EntitySimState
{
    public WellSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Well.Create(sim, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

// Rest tent. No persistent per-instance state — interacting runs a one-shot
// time-skip on the GameClient (see Tent), nothing on the tent changes.
public class TentSimState : EntitySimState
{
    public TentSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Tent.Create(sim, this);
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

    public override Node3D CreateEntity(Sim sim)
    {
        return ClimbableTree.Create(sim, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

public class FireTrapSimState : EntitySimState
{
    // Default hazard danger-zone radius (meters) — see EntitySimState.HazardRadius.
    public const float DefaultHazardRadius = 1f;

    // Random per-instance offset (seconds) added to the trap's first Idle
    // window so neighbouring traps don't fire in lockstep. Rolled once at
    // creation and persisted through save/load — preserving the rhythm
    // matters for replayability of authored swamp encounters.
    public float PhaseOffsetSeconds;

    public FireTrapSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return FireTrap.Create(sim, this);
    }
}

// Smithing forge (weapon/armor granting station). Distinct from the Campfire
// cooking station — no lit state, no cook jobs. Inherited RegrowDay is the daily
// cooldown deadline (stamped to DayNumber + 1 on use).
public class ForgeSimState : RegrowSimState
{
    // Power tier stamped onto every item the forge mints (see ItemState.level).
    public int Level;

    // Concrete upgrade slot this forge grants into — resolved at bake time from the
    // spawn entry (authored, or position-derived). Fixed for the forge's lifetime;
    // decides which upgrades it offers and which model / marker icon it shows.
    public EUpgradeSlot Slot;

    public ForgeSimState(Vector3 worldPosition, PackedScene scene, int level, EUpgradeSlot slot)
        : base(worldPosition, scene)
    {
        Level = level;
        Slot = slot;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Forge.Create(sim, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

// Fountain (daily refill station — health or lantern fuel; the variant is
// carried by the scene, see Fountain.EFountainKind). Like the Forge it re-arms
// once per in-world day; no level or minted items — just the inherited RegrowDay
// deadline (stamped to DayNumber + 1 on use).
public class FountainSimState : RegrowSimState
{
    public FountainSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return Fountain.Create(sim, this);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}

// Forageable resource node (mushroom patch, herb clump). A fixed, persistent
// anchor that presents a pickup while ripe and re-grows it after RegrowDays. The
// node owns nothing the player picks up directly — it spawns a transient Loot
// (the mushroom) and re-arms via the inherited RegrowDay when that Loot is
// collected, so Loot itself stays a dumb ephemeral pickup. Item + RegrowDays are
// carried here (from ForageSpawnEntry) so one spawner scene serves every
// forageable variant.
public class ForageSpawnerSimState : RegrowSimState
{
    // The item the presented pickup carries (e.g. a mushroom).
    public ItemData Item;

    // In-world days from harvest until the pickup regrows.
    public int RegrowDays;

    public ForageSpawnerSimState(Vector3 worldPosition, PackedScene scene, ItemData item, int regrowDays)
        : base(worldPosition, scene)
    {
        Item = item;
        RegrowDays = regrowDays;
    }

    public override Node3D CreateEntity(Sim sim)
    {
        return ForageSpawner.Create(sim, this);
    }
}
