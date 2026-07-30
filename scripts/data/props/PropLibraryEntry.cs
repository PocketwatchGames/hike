using Godot;

// How a prop groups in authoring UI. Purely presentational — it decides which
// sections of the editor palette an entry lands in, nothing about behavior.
// PropType is the behavioral axis and is authored separately, because they
// genuinely differ: a boulder is a Rock to an author and a PropType.Tree to the
// sim (solid, blocks pathing).
//
// Flags, not a plain enum: a prop can legitimately belong under more than one
// heading (a mossy stump is both Tree and Foliage), and the palette shows an
// entry under every section it ticks.
[System.Flags]
public enum EPropCategory
{
    Tree = 1 << 0,
    Rock = 1 << 1,
    Foliage = 1 << 2,
    Furniture = 1 << 3,
    Other = 1 << 4,
}

// One placeable prop: a scene plus how it should be presented and how it
// behaves once placed.
[GlobalClass]
public partial class PropLibraryEntry : Resource
{
    [Export] public string displayName = "";
    [Export] public PackedScene scene;
    [Export] public EPropCategory category = EPropCategory.Other;

    // The behavior the placed PropSimState gets. Tree takes the solid path
    // (PropInstance + path-blocker rasterization from its collider); Foliage
    // takes the billboard-sprite path and never blocks navigation. See
    // PropSimState.CreateEntity / GetPathBlockerCells.
    [Export] public PropType propType = PropType.Tree;

    // Optional palette-button art. Null falls back to the name label.
    [Export] public Texture2D icon;
}
