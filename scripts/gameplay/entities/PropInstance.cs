using Godot;

[GlobalClass]
public partial class PropInstance : Node3D, IWorldEntity
{
    public void OnSpawned(World world) { }

    public static PropInstance Create(World world, PropSimState data)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        instance.Position = data.WorldPosition;
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
