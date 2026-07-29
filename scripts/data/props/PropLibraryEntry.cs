using Godot;

// How a prop groups in authoring UI. Purely presentational — it decides which
// section of the editor palette an entry lands in, nothing about behavior.
// PropType is the behavioral axis and is authored separately, because they
// genuinely differ: a boulder is a Rock to an author and a PropType.Tree to the
// sim (solid, blocks pathing).
public enum EPropCategory
{
    Tree,
    Rock,
    Foliage,
    Other,
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
