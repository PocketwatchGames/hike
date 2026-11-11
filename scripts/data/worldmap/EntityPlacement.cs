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
// chests, loot and NPCs instead of one list per kind. The source / fork pair is
// AuthoredPlacement's, shared with the entities a scene is built from.
[GlobalClass]
public partial class EntityPlacement : AuthoredPlacement
{
    [Export] public Vector2I anchorXZ;

    // Which way this entity is aimed. Every entry honours one — it is seated
    // onto the EntitySimState's RotationY, which every entity carries — so there
    // is no placement the painter declines to aim.
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

    // What the author calls this one, or empty. A name makes the placement a
    // place the rest of the game can refer to: the bake registers it as a point
    // of interest (`tp <name>`), and hands it to the spawn, where a buried spot
    // becomes the treasure a map of that name points at. On the PLACEMENT, not
    // the entry, so naming a chest does not fork it off its palette file.
    [Export] public string name = "";

    // The name as the bake uses it — trimmed, or null when there is none.
    public string Name => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    // What to call this placement in the authoring UI: its own name if it has
    // one, the palette entry it came from, plus the variant this individual is,
    // plus a mark when it carries its own copy. One answer, because the tool row,
    // the hover readout and the property panel all name the same thing and a
    // name that differs between them reads as two different entries.
    public override string DisplayName()
    {
        return Name != null ? $"{Name} ({Label()})" : Label();
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
}
