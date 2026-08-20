using Godot;

// An authored ROAD ENDPOINT inside a `.hikescene` — the front door of a house,
// the gap in a town square's wall — so a stamped scene says where a path is
// meant to touch it instead of leaving worldgen to guess a side.
//
// Sibling of MarkerSimState and authored the same way (a tagged pin, one editor
// brush per tag), but consumed by a different pass: a marker is a candidate
// SPAWN POSITION a variant may fill, a path hint is a POINT OF INTEREST. Each
// one a stamp carries registers as "<placement>.<tag>" in
// WorldState.PointsOfInterest (see WorldGen.RegisterSubscenePathHints), which is
// what makes it addressable by a RoadConnection like any other named place.
//
// Its Tag is the hint's name within the scene ("door", "north_gate"), and also
// selects the tread the auto-linked spur is carved with — see
// WorldGenData.pathHintProfiles.
public class PathHintSimState : EntitySimState
{
    // `scene` is the editor's pin visual, and the only reason a hint has a
    // scene at all.
    public PathHintSimState(Vector3 worldPosition, string tag, PackedScene scene)
        : base(worldPosition, scene)
    {
        Tag = tag ?? "";
    }

    // Authoring-only, like a marker: worldgen turns hints into POIs and never
    // stamps them, but a hint saved into a .hike (a WORLD document rather than a
    // scene) would otherwise leave a floating pin in the game.
    public override bool ShouldSpawn(Sim sim) => WorldEditor.Current != null;

    public override Node3D CreateEntity(Sim sim)
    {
        // The chunk-streaming spawn queue calls CreateEntity directly, so the
        // gate has to be re-checked here or the pin materializes in a game.
        if (Scene == null || !ShouldSpawn(sim))
        {
            return null;
        }
        var instance = Scene.Instantiate<Node3D>();
        SeatTransform(instance);
        sim.AddChild(instance);
        return instance;
    }
}
