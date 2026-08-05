using Godot;

// An authored POSITION with no body — the spawn-point half of the subscene
// variant system. Its Tag (on EntitySimState) names the pool it belongs to, and
// the SubscenePlacement that stamps the scene decides what, if anything, spawns
// there; see SubsceneVariant. WorldGen consumes markers at stamp time and never
// adds them to the stamped world, so a marker costs nothing at runtime.
//
// Unlike a POI, a marker carries its own Y — a spawn point on an upper floor or
// a cellar step lands where it was authored rather than on the terrain surface.
public class MarkerSimState : EntitySimState
{
    // `scene` is the editor's pin visual, and the only reason a marker has a
    // scene at all.
    public MarkerSimState(Vector3 worldPosition, string tag, PackedScene scene)
        : base(worldPosition, scene)
    {
        Tag = tag ?? "";
    }

    // Authoring-only: the pin exists to be seen and dragged in the world editor.
    // Worldgen strips markers, but a marker saved into a .hike (a WORLD document
    // rather than a scene) would otherwise leave a floating pin in the game.
    public override bool ShouldSpawn(Sim sim) => WorldEditor.Current != null;

    public override Node3D CreateEntity(Sim sim)
    {
        if (Scene == null)
        {
            return null;
        }
        var instance = Scene.Instantiate<Node3D>();
        SeatTransform(instance);
        sim.AddChild(instance);
        return instance;
    }
}
