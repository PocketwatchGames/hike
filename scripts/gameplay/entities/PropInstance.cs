using Godot;

[GlobalClass]
public partial class PropInstance : Node3D, IWorldEntity
{
    // Porousness is owned per-collider by node type: a prop's movement collider
    // is a PorousBody (blocks movement / grounded sight, lets smell, sound,
    // perched vision, and flight pass through), while a genuinely solid prop
    // would use a plain StaticBody3D on Environment. No per-prop toggle.

    // How many voxel cells of wall this prop carves out, upward from the cell it
    // stands in. 0 (every prop but window frames) touches no voxels. A window
    // frame IS a hole in a wall, so the hole belongs to the SCENE and follows it
    // wherever the frame is placed — editor, subscene stamp, worldgen — instead
    // of being painted to match by hand each time. See PropSimState.ResolveStamp.
    [Export(PropertyHint.Range, "0,8,1")] public int apertureHeight = 0;

    // Must match the [Export] above in both name and default: a scene sitting on
    // the default stores no value for the load pass to read.
    private const int DEFAULT_APERTURE_HEIGHT = 0;
    private static readonly ScenePropertyCache _apertureHeights =
        new ScenePropertyCache("apertureHeight", DEFAULT_APERTURE_HEIGHT);

    public static int GetApertureHeight(PackedScene scene)
    {
        return _apertureHeights.Get(scene);
    }

    public void OnSpawned(Sim sim) { }

    public static PropInstance Create(Sim sim, PropSimState data)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        data.SeatTransform(instance);
        sim.AddChild(instance);
        return instance;
    }

    // Bisection toggle: every PropInstance hides itself when CVars.propsVisible
    // goes false. Combined with mob_visible / mob_hud / mob_shadows this lets
    // you attribute the render_draw_calls table to mobs vs props vs everything
    // else (terrain, hud, decals). Subscription lifetime tracks the node.
    public override void _Ready()
    {
        Visible = CVars.propsVisible.Value;
        CVars.propsVisible.OnChanged += OnPropsVisibleChanged;
        TreeExiting += () => CVars.propsVisible.OnChanged -= OnPropsVisibleChanged;
    }

    private void OnPropsVisibleChanged(CVar cvar)
    {
        Visible = ((CVarBool)cvar).Value;
    }
}
