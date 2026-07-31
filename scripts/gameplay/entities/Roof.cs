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
        instance.Build(data, sim.SimData?.roofCapMaskMaterial, sim.SimData?.roofShadowCasterMaterial,
            sim.SimData?.roofInteriorMaterial);
        // Before AddChild, like every other entity — _Ready reads the transform.
        data.SeatTransform(instance);
        sim.AddChild(instance);
        return instance;
    }

    private void Build(RoofSimState data, Material capMaskMaterial, Material shadowCasterMaterial, Material interiorMaterial)
    {
        ArrayMesh mesh = RoofMeshBuilder.Build(
            data.Style, data.SizeX, data.SizeZ, data.SeamAxis, data.SlopeDegrees, data.Form);

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
        ApplyBroken(visual, data, innerShell: false);

        // Inner shell: the same mesh rendered back-faces-only, with slightly
        // SMALLER holes. Everywhere the roof is solid the outer surface is
        // nearer and depth-tests over it, so this is visible only through a
        // hole — as a ring of slab interior, which is the thickness you see
        // when you look down into one. Skipped entirely on an intact roof.
        if (interiorMaterial != null && data.Broken > 0f)
        {
            var inner = new MeshInstance3D();
            inner.Mesh = mesh;
            inner.MaterialOverride = interiorMaterial;
            inner.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            inner.Layers = GameCamera.MainSceneLayer;
            AddChild(inner);
            ApplyBroken(inner, data, innerShell: true);
        }

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
            // Same holes as the visible surface, or a gap you can see through
            // still casts a solid shadow and no light lands under it.
            ApplyBroken(shadowCaster, data, innerShell: false);
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

    // Instance uniforms rather than per-style materials, so one shared material
    // serves every roof and the visible mesh and its shadow proxy can be handed
    // identical values without duplicating anything.
    private static void ApplyBroken(GeometryInstance3D instance, RoofSimState data, bool innerShell)
    {
        RoofStyleData style = data.Style;
        // Remapped so the authored value means the fraction of surface gone —
        // see RoofBrokenNoise.ThresholdFor. Done here so the GPU never pays for it.
        instance.SetInstanceShaderParameter("broken", RoofBrokenNoise.ThresholdFor(data.Broken));
        instance.SetInstanceShaderParameter("broken_scale", style.brokenScale);
        instance.SetInstanceShaderParameter("broken_edge_scale", style.brokenScale * style.brokenEdgeRatio);
        instance.SetInstanceShaderParameter("broken_edge_jagged", style.brokenEdgeJagged);
        instance.SetInstanceShaderParameter("broken_bias", innerShell ? style.brokenInnerShrink : 1f);
        // The rim belongs on the outer surface; on the inner shell it would just
        // darken the ring that exists to be seen.
        instance.SetInstanceShaderParameter("broken_rim_darken", innerShell ? 0f : style.brokenRimDarken);
        instance.SetInstanceShaderParameter("broken_rim_width", style.brokenRimWidth);
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
