using Godot;

// A generated sloped roof. The mesh is built in code rather than loaded from a
// .tscn because there is nothing to author: the shape comes from the footprint
// the editor dragged, so a scene per size/pitch combination is exactly what
// this exists to avoid. The surface still comes from an authored material.
[GlobalClass]
public partial class Roof : Node3D, IWorldEntity
{
    public void OnSpawned(Sim sim) { }

    public static Roof Create(Sim sim, RoofSimState data)
    {
        var instance = new Roof();
        // A style only overrides the shadow proxy when it punches holes; see
        // RoofStyleData.shadowCasterMaterial.
        Material shadowCaster = data.Style.shadowCasterMaterial ?? sim.SimData?.roofShadowCasterMaterial;
        instance.Build(data, sim.SimData?.roofCapMaskMaterial, shadowCaster);
        // Before AddChild, like every other entity — _Ready reads the transform.
        data.SeatTransform(instance);
        sim.AddChild(instance);
        return instance;
    }

    private void Build(RoofSimState data, Material capMaskMaterial, Material shadowCasterMaterial)
    {
        ArrayMesh mesh = RoofMeshBuilder.Build(
            data.Style, data.SizeX, data.SizeZ, data.SeamAxis, data.SlopeDegrees);

        // Everything sits at the node origin, which is the roof's BASE: the mesh
        // is built with Y = 0 at the eave's underside. model_lit resolves the
        // ceiling cutaway once per mesh from MODEL_MATRIX[3], so that origin is
        // the single elevation the whole roof clips at. The flat soffit is what
        // makes clipping at the base correct rather than merely requested — see
        // RoofMeshBuilder.
        var visual = new MeshInstance3D();
        visual.Mesh = mesh;
        visual.Layers = GameCamera.MainSceneLayer;
        // Shadows come from the proxy below instead, for the same reason terrain
        // splits them out.
        visual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        if (data.Style.material != null)
        {
            visual.MaterialOverride = data.Style.material;
        }
        AddChild(visual);

        // Non-clipping shadow proxy. The visible material discards above the
        // cutaway and Godot runs fragment() in the shadow pass too, so without
        // this a roof stops casting the instant it cuts away and floods the
        // interior it just revealed with sun. Same fix voxel terrain uses.
        if (shadowCasterMaterial != null)
        {
            var shadowCaster = new MeshInstance3D();
            shadowCaster.Mesh = mesh;
            shadowCaster.MaterialOverride = shadowCasterMaterial;
            shadowCaster.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
            AddChild(shadowCaster);
        }

        // Second copy of the same mesh into the off-screen cap mask, so the roof
        // both survives against empty sky (it writes "don't cap" where it draws)
        // and reads as a cut ceiling once clipped (it stops writing, and the
        // white clear becomes the cap). Same geometry, so the two can't drift.
        if (capMaskMaterial != null)
        {
            var capMask = new MeshInstance3D();
            capMask.Mesh = mesh;
            capMask.MaterialOverride = capMaskMaterial;
            capMask.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            capMask.Layers = GameCamera.CapMaskLayer;
            AddChild(capMask);
        }

        // A plain StaticBody3D on Environment, not a PorousBody: a roof is a
        // building surface, so flight and grounded sight should both stop at it.
        // Being on the Solid mask is also what lets GameCamera's upward clip ray
        // find the roof — which is the whole reason the soffit is flat.
        var body = new StaticBody3D();
        body.CollisionLayer = (uint)ECollisionLayer.Environment;
        var collision = new CollisionShape3D();
        collision.Shape = mesh.CreateTrimeshShape();
        body.AddChild(collision);
        AddChild(body);
    }

    // Bisection toggle, mirroring PropInstance so roofs drop out of the frame
    // alongside the rest of the prop pass. Subscription lifetime tracks the node.
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
