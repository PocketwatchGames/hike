using Godot;

// One hand-placed entity: which spawn entry, where, and which way it faces.
//
// The entry is referenced DIRECTLY rather than by an index into the document's
// palette, so reordering the palette cannot silently turn every chest in the
// world into a goblin.
//
// A `SpawnEntryData` rather than a prop scene, because that is what the bake
// already knows how to place: the same entries the scatter layers use, spawned
// through the same `TrySpawn`. It also means one palette covers props, mobs,
// chests, loot and NPCs instead of one list per kind.
[GlobalClass]
public partial class EntityPlacement : Resource
{
    [Export] public SpawnEntryData entry;
    [Export] public Vector2I anchorXZ;

    // Quarter turns, sharing SubscenePlacement's enum. Entities can hold any
    // yaw, but the painter is a map: 90 degrees is what a author can aim at a
    // metre grid, and it is the same R/F the scene tool uses.
    [Export] public ESubsceneRotation rotation;

    // "Sit on whatever ground is under me" — the value every entity placed on the
    // surface keeps, and the value a document written before floors existed loads
    // with, since a field absent from a .tres keeps its C# initializer.
    public const int OnTheGround = int.MinValue;

    // The floor this entity was placed on, when that is NOT the top of the
    // height field — inside a tunnel, or on a built deck. Absolute rather than an
    // offset from the ground: a passage is carved at a fixed Y and does not move
    // when the hill above it is repainted, so an entity standing in one must not
    // either. Surface entities keep OnTheGround and are seated from the terrain
    // at bake, exactly as they always were, so they still follow ground that
    // moves under them.
    [Export] public int floorY = OnTheGround;

    // Was this placement made from that palette entry? Its own FORK counts: a
    // chest whose text has been edited is still a chest, and dropping out of the
    // palette's highlight the moment it is customized is backwards — a
    // customized one is the one most worth finding.
    //
    // The comparison is on FAMILY, which is what makes selecting a family entry
    // highlight every member of it: one npc palette file, so every NPC forked
    // from it answers true whatever rig, outfit or conversation it was then
    // given. Comparing DISPLAY names would not — that string now carries the
    // variant, so an NPC would stop matching the entry it came from the moment
    // its appearance was picked.
    public bool IsFrom(SpawnEntryData paletteEntry)
    {
        if (entry == null || paletteEntry == null)
        {
            return false;
        }
        if (entry == paletteEntry)
        {
            return true;
        }
        string family = SpawnEntryData.FamilyName(entry);
        return !string.IsNullOrEmpty(family)
            && family == SpawnEntryData.FamilyName(paletteEntry);
    }

    // This placement's entry, ready to be EDITED — the properties of a
    // hand-placed entity ARE its entry's, so there is no second place to put a
    // signpost's text or a chest's spawn conditions.
    //
    // Copy-on-write: a placement starts out pointing straight at the palette's
    // shared .tres, so a chest nobody has customized keeps tracking whatever that
    // entry is retuned to. The first edit forks it, and the fork — having no
    // resource path — saves INTO placements.tres as a sub-resource belonging to
    // this placement alone. Shallow on purpose: a forked signpost still
    // references the same LanguageData rather than getting a private copy of it.
    public SpawnEntryData EditableEntry()
    {
        if (entry == null || SpawnEntryData.IsOwnedCopy(entry))
        {
            return entry;
        }
        if (entry.Duplicate(false) is not SpawnEntryData copy)
        {
            GD.PushError($"EntityPlacement: could not fork entry '{entry.ResourcePath}' for editing");
            return entry;
        }
        // The palette file it came from, kept as the resource NAME: a fork has no
        // path, and this is then the only thing left that says what it is. Engine
        // bookkeeping rather than a script property, so PlacementsAspect leaves it
        // alone and it still round-trips through the .tres.
        copy.ResourceName = entry.ResourcePath.GetFile().GetBaseName();
        // Cleared explicitly. A duplicate that kept its path would save as an
        // ext_resource pointing back at the palette file, which silently throws
        // the fork away on the next load.
        copy.ResourcePath = "";
        entry = copy;
        return entry;
    }
}
