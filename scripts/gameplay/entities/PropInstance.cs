using Godot;

[GlobalClass]
public partial class PropInstance : Node3D, IWorldEntity
{
    // Porousness is owned per-collider by node type: a prop's movement collider
    // is a PorousBody (blocks movement / grounded sight, lets smell, sound,
    // perched vision, and flight pass through), while a genuinely solid prop
    // would use a plain StaticBody3D on Environment. No per-prop toggle.

    public void OnSpawned(Sim sim) { }

    public static PropInstance Create(Sim sim, PropSimState data)
    {
        var instance = data.Scene.Instantiate<PropInstance>();
        instance.Position = data.WorldPosition;
        instance.Rotation = new Vector3(0f, data.RotationY, 0f);
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
