using System.Collections.Generic;
using Godot;

// A generated sloped roof. The mesh is built in code rather than loaded from a
// .tscn because there is nothing to author: the shape comes from the footprint
// the editor dragged, so a scene per size/pitch combination is exactly what
// this exists to avoid. The surface still comes from an authored material.
// What a roof contributes over a given column.
//
// The distinction has to exist because a roof's cover is recorded once and read
// for two different questions. RoofSunStamper deliberately stamps the OVERSAIL
// into SunOpaque — an eave really does shade the ground under it, and really does
// hide someone standing beneath it from the camera. But the cutaway must not
// treat it as a ceiling, or standing under the eaves cuts the whole roof away
// while the player is still outside the house.
public enum ERoofCover
{
    None,
    // Under the eave or rake oversail: cover, but not a room.
    Oversail,
    // Inside the painted footprint — the space this roof is the ceiling of.
    Ceiling,
}

[GlobalClass]
public partial class Roof : Node3D, IWorldEntity, IClipCover
{
    // Half-extents of the PAINTED footprint, in local X/Z. The mesh oversails
    // these by the style's eave and rake overhangs; CoverAt uses them to tell
    // the room from the oversail.
    private float _halfFootprintX;
    private float _halfFootprintZ;
    // Half-extents INCLUDING that oversail — the same reach RoofSunStamper writes
    // into SunOpaque. Without it a consumer reading the stamp can tell that
    // something is overhead but not whether this roof is what put it there, so it
    // cannot distinguish "no roof involved" from "this roof's overhang".
    private float _halfStampX;
    private float _halfStampZ;

    // Matches CLIP_COLUMN_FROM_MASK in clip_dither.gdshaderinc: "no instance
    // value, resolve participation per fragment". What a roof reports whenever
    // the column cutaway isn't the live clip source, leaving the height-only
    // behaviour exactly as it was.
    private const float CLIP_PARTICIPATION_FROM_MASK = -1f;

    // Every pass that has to cut with the roof. The shadow proxy is deliberately
    // absent: it must keep casting after the roof cuts away, or the interior it
    // just revealed floods with sun.
    private readonly List<GeometryInstance3D> _clipInstances = new();
    private float _clipParticipation = CLIP_PARTICIPATION_FROM_MASK;

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

        // Re-derived rather than passed back out of the builder, the same way the
        // editor's drag preview does it — the clamps that keep a footprint from
        // collapsing live in RoofDimensions, so reading the raw sizes here would
        // disagree with the mesh on a degenerate drag.
        var size = new RoofDimensions(
            data.Style, data.SizeX, data.SizeZ, data.SeamAxis, data.SlopeDegrees, data.Form);
        bool alongX = data.SeamAxis == ERoofSeamAxis.AlongX;
        _halfFootprintX = alongX ? size.HalfSeamBody : size.HalfAcrossBody;
        _halfFootprintZ = alongX ? size.HalfAcrossBody : size.HalfSeamBody;
        // HalfSeam / HalfAcross, not the Body pair — the full reach with overhangs,
        // which is exactly what RoofSunStamper rasterizes.
        _halfStampX = alongX ? size.HalfSeam : size.HalfAcross;
        _halfStampZ = alongX ? size.HalfAcross : size.HalfSeam;

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
        _clipInstances.Add(visual);

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
            _clipInstances.Add(inner);
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
            _clipInstances.Add(capMask);
        }

        // Seed every pass before the first frame draws — an instance uniform that
        // has never been set reads as 0 ("exempt"), which would hold the roof on
        // for the frame or two before the first update reaches it.
        ApplyClipParticipation(CLIP_PARTICIPATION_FROM_MASK);

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

    // A roof is a ceiling only over the footprint it was dragged over. Past that
    // line the mesh is eave / rake oversail hanging over open ground, and the
    // upward cutaway ray hits its underside just the same — so without this,
    // standing under the eaves cut the whole roof away from outside the house.
    // The node origin is the footprint centre, so this is a local-space rectangle
    // test; ToLocal rather than a world-space compare because the seat transform
    // owns the placement.
    public bool IsCeilingAt(Vector3 worldPos)
    {
        return CoverAt(worldPos) == ERoofCover.Ceiling;
    }

    // The three-way form, for consumers reading the SunOpaque stamp rather than
    // hitting the mesh with a ray. A ray already knows a roof was involved because
    // it hit one; a stamp reader does not, so it needs Oversail and None told
    // apart — unrecognised cover has to stay a ceiling (it belongs to a roof
    // further off, or to something that is not a roof at all), while cover this
    // roof positively identifies as its own overhang does not.
    public ERoofCover CoverAt(Vector3 worldPos)
    {
        Vector3 local = ToLocal(worldPos);
        float x = Mathf.Abs(local.X);
        float z = Mathf.Abs(local.Z);
        if (x <= _halfFootprintX && z <= _halfFootprintZ)
        {
            return ERoofCover.Ceiling;
        }
        if (x <= _halfStampX && z <= _halfStampZ)
        {
            return ERoofCover.Oversail;
        }
        return ERoofCover.None;
    }

    // How much of the column cutaway this roof takes, decided for the whole mesh
    // and pushed to every pass that cuts with it.
    //
    // How much of the cut this roof takes, decided for the whole mesh from HOW
    // MUCH OF ITS FOOTPRINT sits over the player's own air region.
    //
    // Not a sampled point. Every choice of point is arbitrary for a mesh spanning
    // tens of metres, and both previous attempts failed in play: the origin read
    // whatever happened to be under the middle of the building, and the nearest
    // footprint point reads the rect's boundary, which is not the wall line — a
    // footprint painted slightly larger than its building clamps onto open
    // street, so a roof cut while the player merely walked past and stopped a
    // step later. Coverage has no point to be wrong about.
    //
    // Measured against the AIR REGION specifically: a roof follows the space
    // beneath it, and the house's own walls are carried by the flood as bounds of
    // whatever region the player is in.
    public void UpdateClipParticipation(IClipMask mask, Vector3 playerPosition)
    {
        float participation = CLIP_PARTICIPATION_FROM_MASK;
        if (mask != null)
        {
            FootprintExtents(out Vector2 min, out Vector2 max);
            participation = mask.RegionCoverage(min, max);
        }
        if (Mathf.IsEqualApprox(participation, _clipParticipation))
        {
            return;
        }
        _clipParticipation = participation;
        ApplyClipParticipation(participation);
    }

    private void ApplyClipParticipation(float participation)
    {
        for (int i = 0; i < _clipInstances.Count; i++)
        {
            _clipInstances[i].SetInstanceShaderParameter("clip_participation", participation);
        }
    }

    // World-space XZ bounds of the painted footprint. Built from the four corners
    // rather than the half-extents directly, so a roof seated at a 90° yaw (the
    // only rotations the editor produces) reports the rect it actually occupies.
    // Inset by one cell: the outermost ring straddles the wall line, and a
    // footprint painted a shade larger than its building would otherwise count
    // open ground outside the wall as part of the space the roof covers.
    private const float FOOTPRINT_INSET = 1f;

    private void FootprintExtents(out Vector2 min, out Vector2 max)
    {
        float hx = Mathf.Max(_halfFootprintX - FOOTPRINT_INSET, 0.5f);
        float hz = Mathf.Max(_halfFootprintZ - FOOTPRINT_INSET, 0.5f);
        Vector3 a = ToGlobal(new Vector3(-hx, 0f, -hz));
        Vector3 b = ToGlobal(new Vector3(hx, 0f, -hz));
        Vector3 c = ToGlobal(new Vector3(-hx, 0f, hz));
        Vector3 d = ToGlobal(new Vector3(hx, 0f, hz));
        min = new Vector2(
            Mathf.Min(Mathf.Min(a.X, b.X), Mathf.Min(c.X, d.X)),
            Mathf.Min(Mathf.Min(a.Z, b.Z), Mathf.Min(c.Z, d.Z)));
        max = new Vector2(
            Mathf.Max(Mathf.Max(a.X, b.X), Mathf.Max(c.X, d.X)),
            Mathf.Max(Mathf.Max(a.Z, b.Z), Mathf.Max(c.Z, d.Z)));
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
