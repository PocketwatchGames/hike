using Godot;

[GlobalClass]
public partial class PropInstance : Node3D, IWorldEntity, IPorous
{
    // When true (the default), this prop's colliders authored on the default
    // Environment layer (1) are remapped to Porous at spawn (by World, via
    // PorousColliders.Apply), so the prop blocks movement/grounded-sight but
    // lets smell, sound, perched vision, and flight pass through. Only layer-1
    // colliders are touched — anything deliberately authored on another layer
    // (a tallgrass-style rustle area, a future bespoke collider) keeps its
    // layer, so mixed-collider props work. Set false for genuinely solid props
    // (boulders, buildings) whose colliders should stay solid like a wall.
    [Export] public bool Porous { get; set; } = true;

    public void OnSpawned(World world) { }

    public static PropInstance Create(World world, PropSimState data)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        instance.Position = data.WorldPosition;
        instance.Rotation = new Vector3(0f, data.RotationY, 0f);
        world.AddChild(instance);
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
