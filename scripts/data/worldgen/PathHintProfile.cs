using Godot;

// The tread an AUTO-LINKED path gets, keyed by the hint tag that produced it —
// so a "door" hint spurs a narrow dirt footpath while a "gate" hint spurs a
// wide cobbled one, with the scene author choosing the character by picking a
// brush rather than by editing worldgen.
//
// Only consulted for the spurs WorldGen carves itself
// (SubscenePlacement.connectPathHints). A road authored explicitly against a
// hint's POI name is an ordinary RoadConnection and carries its own width and
// texture.
[GlobalClass]
public partial class PathHintProfile : Resource
{
    // PathHintSimState.Tag this applies to. An EMPTY tag is the fallback entry:
    // it matches every hint no named profile claims, so one wildcard covers a
    // world that doesn't care to distinguish them.
    [Export] public string hintTag = "";

    // Tread width range in voxels, same meaning as RoadConnection's: the path
    // holds one rolled width for a stride, then re-rolls. Keep MinWidth >= 2 so
    // the mesher's neighborhood overlay vote reads the path texture rather than
    // averaging it away.
    [Export(PropertyHint.Range, "1,16,1")] public int minWidth = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int maxWidth = 3;

    // Overlay block stamped along the tread. Null falls back to
    // WorldGenData.roadDefaultTexture.
    [Export] public BlockSurfaceData texture;
}
