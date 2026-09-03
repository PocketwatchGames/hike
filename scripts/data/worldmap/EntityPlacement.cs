using Godot;

// Eighth turns about +Y. The member's integer value IS the eighth-turn count,
// and the sense matches an entity's RotationY: at Deg0 it faces +Z, at Deg90 it
// faces +X.
//
// Eighths where a subscene takes quarters (ESubsceneRotation), because the two
// are constrained by different things: a stamp is a raster footprint and can
// only be turned in quarter turns, while an entity is a point that can hold any
// yaw at all. 45 degrees is simply the finest an author can aim at with a cursor
// on a metre grid, so that is where the painter locks.
//
// APPEND new members only. Godot renumbers on insert, and a running editor with
// a stale assembly silently drops .tres lines that end up equal to the default.
public enum EEntityFacing
{
    Deg0,
    Deg45,
    Deg90,
    Deg135,
    Deg180,
    Deg225,
    Deg270,
    Deg315,
}

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
    // The palette entry this placement was made from. NEVER replaced — a fork
    // goes beside it, in `custom`, so the link to the palette survives being
    // customized. Reconstructing that link afterwards was tried and cannot be
    // made to work: with the reference overwritten, the only thing left saying
    // where a fork came from was a name (its `ResourceName`, or an exported
    // family string), and a name drifts. Three forked knowledge stones in the
    // default world carried "knowledge_stone_vyeshal" against a palette file
    // named knowledge_stone_vyeshal_vocab1 and silently stopped matching it.
    [Export] public SpawnEntryData source;

    // This placement's private copy of `source`, once something on it has been
    // edited. Null until then, which is what makes an uncustomized placement
    // track whatever the palette entry is retuned to.
    [Export] public SpawnEntryData custom;

    [Export] public Vector2I anchorXZ;

    // Which way this entity is aimed. Only an entry whose spawn reads
    // SpawnContext.FacingY does anything with it (SpawnEntryData.UsesFacing);
    // the painter neither draws nor sets a facing on the ones that do not.
    [Export] public EEntityFacing facing;

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

    // What this placement actually spawns: its own copy once it has one, else
    // the shared palette entry. Every reader wants this; only the palette
    // MEMBERSHIP questions read `source`.
    public SpawnEntryData Entry => custom ?? source;

    // Has this placement been edited away from its palette entry? A customized
    // one still belongs to the palette entry it came from — it is the one most
    // worth finding — so this marks it rather than un-matching it.
    public bool IsCustomized => custom != null;

    // Was this placement made from that palette entry? Reference equality, so a
    // fork answers exactly as its original does and a renamed palette file
    // changes nothing.
    public bool IsFrom(SpawnEntryData paletteEntry)
        => source != null && paletteEntry != null && source == paletteEntry;

    // What to call this placement in the authoring UI: the palette entry it came
    // from, plus the variant this individual is, plus a mark when it carries its
    // own copy. One answer, because the tool row, the hover readout and the
    // property panel all name the same thing and a name that differs between
    // them reads as two different entries.
    public string DisplayName()
    {
        string name = SpawnEntryData.PaletteName(source);
        string variant = Entry?.VariantName();
        if (!string.IsNullOrEmpty(variant))
        {
            name = $"{name}: {variant}";
        }
        return IsCustomized ? $"{name} *" : name;
    }

    // Radians about +Y — what the bake hands the spawn as SpawnContext.FacingY.
    public float FacingRadians => Radians(facing);

    public static float Radians(EEntityFacing facing) => (int)facing * Mathf.Pi * 0.25f;

    // Unit direction in world XZ, for drawing the facing on the map. +Z at Deg0,
    // matching the yaw sense above.
    public static Vector2 Direction(EEntityFacing facing)
    {
        float a = Radians(facing);
        return new Vector2(Mathf.Sin(a), Mathf.Cos(a));
    }

    // The eighth turn nearest a direction in world XZ (x, z) — how the painter
    // turns "the cursor is over there" into a facing. A zero direction answers
    // Deg0 rather than whatever Atan2 makes of it.
    public static EEntityFacing Nearest(Vector2 dirXZ)
    {
        if (dirXZ.LengthSquared() <= 0f)
        {
            return EEntityFacing.Deg0;
        }
        int step = Mathf.RoundToInt(Mathf.Atan2(dirXZ.X, dirXZ.Y) / (Mathf.Pi * 0.25f));
        return (EEntityFacing)((step % 8 + 8) % 8);
    }

    // This placement's entry, ready to be EDITED — the properties of a
    // hand-placed entity ARE its entry's, so there is no second place to put a
    // signpost's text or a chest's spawn conditions.
    //
    // Copy-on-write: the first edit forks `source` into `custom`, which — having
    // no resource path — saves INTO placements.tres as a sub-resource belonging
    // to this placement alone. Shallow on purpose: a forked signpost still
    // references the same LanguageData rather than getting a private copy of it.
    public SpawnEntryData EditableEntry()
    {
        if (custom != null || source == null)
        {
            return Entry;
        }
        if (source.Duplicate(false) is not SpawnEntryData copy)
        {
            GD.PushError($"EntityPlacement: could not fork entry '{source.ResourcePath}' for editing");
            return source;
        }
        // Cleared explicitly. A duplicate that kept its path would save as an
        // ext_resource pointing back at the palette file, which silently throws
        // the fork away on the next load.
        copy.ResourcePath = "";
        custom = copy;
        return custom;
    }
}
