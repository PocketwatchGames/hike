using Godot;

// Batched renderer for grounding-shadow blobs — the player's plus every
// shadow-casting mob's. All blobs are one MultiMesh on the ground-stain
// projector layer (layer 5); GroundStainProjector composites them into the lit
// ground AND grass shaders. One draw call for the player and any number of mobs.
//
// Structural sibling of FootprintScatter, but simpler: blobs track live, moving
// bodies rather than being stamped-and-faded, so there's no per-instance
// lifetime, no discovery gate, and no per-texture bucket — one shared material
// (SimData.groundShadowMaterial, vertex_color_use_as_albedo so each blob's alpha
// rides INSTANCE_COLOR.a). The whole instance set is rebuilt from the live
// caster list each _Process, mirroring how WorldPropScatter / FootprintScatter
// defer all GPU writes to _Process.
//
// Daylight fade: each blob's alpha is scaled by
//   1 - daylightFade * (DirectionalShadowStrength * skyExposure(pos))
// so a blob fades out only where the sun/moon already throws a crisp shadow AND
// the caster stands under open sky — it substitutes for a real contact shadow,
// never doubles one. Both inputs are needed: under cover (skyExposure→0) or in
// flat light (DirectionalShadowStrength→0) the blob stays full.
[GlobalClass]
public partial class GroundShadowScatter : Node3D
{
    // Mobs farther than this (XZ) from the player contribute nothing — their
    // blob would land outside the GroundStainProjector frustum and never be
    // sampled. A little above the projector's radiusWorld (40) so a blob
    // straddling the edge still renders. Pure cull, not a visible tuning. (The
    // player is always at the projector center, so it's never culled.)
    private const float CullRadius = 48f;

    // Lift the quad just off the ground so it sits inside the projector frustum
    // without z-fighting the terrain (matches the footprint quads).
    private const float QuadHeightOffset = 0.05f;

    // Upload ceiling for one frame. Far above any plausible count of
    // shadow-casting mobs inside CullRadius; a backstop against a runaway world,
    // not a budget. Excess casters simply go un-shadowed that frame.
    private const int Capacity = 512;

    // Shared unit plane (XZ, faces +Y, centered) — per-instance size comes from
    // the instance transform. Created in code like the other scatters' quads.
    private static PlaneMesh _sharedQuad;
    private static PlaneMesh GetQuad()
    {
        _sharedQuad ??= new PlaneMesh { Size = Vector2.One };
        return _sharedQuad;
    }

    private MultiMesh _mm;
    private MultiMeshInstance3D _mmi;
    // Set once material init fails (SimData has no material) so we don't retry
    // and re-log every frame.
    private bool _initFailed;

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("GroundShadowScatter.Process");
        World world = World.Current;
        Player player = world?.player;
        if (_initFailed || player == null)
        {
            return;
        }

        // Master off (the stain projector itself stops rendering, so emitting is
        // wasted): park the MultiMesh empty and skip the rebuild.
        if (!CVars.groundStain.Value)
        {
            if (_mm != null && _mm.VisibleInstanceCount != 0)
            {
                _mm.VisibleInstanceCount = 0;
            }
            return;
        }

        if (_mmi == null && !TryInit())
        {
            return;
        }

        SimData sim = world.SimData;
        WorldState ws = world.WorldState;
        Vector3 playerPos = player.GlobalPosition;
        float cullSq = CullRadius * CullRadius;

        // Global daylight-fade inputs (per-entity sky exposure is folded in below).
        float dirShadow = SkyController.Current?.DirectionalShadowStrength ?? 0f;
        float daylightFade = sim?.groundShadowDaylightFade ?? 1f;
        float mobMaster = sim?.mobShadowAlpha ?? 0f;
        bool mobShadows = CVars.mobShadows.Value;

        // InstanceCount is allocated once at Capacity (in TryInit); the live count
        // varies almost every frame as casters enter/leave range and fade, so we
        // cap the drawn set with VisibleInstanceCount (a cheap draw-range limit)
        // rather than resizing InstanceCount (which reallocates the GPU buffer).
        int count = 0;

        // Writes one blob instance at the next free slot, folding in the daylight
        // fade by sampling sky exposure at the blob's ground position. Captures
        // `count` (a local; local functions may mutate captured locals).
        void Emit(Vector3 pos, float radius, float baseAlpha)
        {
            if (count >= Capacity || radius <= 0f || baseAlpha <= 0f)
            {
                return;
            }
            float skyExp = ws?.GetSkyExposure01(pos) ?? 1f;
            float envFactor = 1f - daylightFade * Mathf.Clamp(dirShadow * skyExp, 0f, 1f);
            float alpha = baseAlpha * envFactor;
            if (alpha <= 0f)
            {
                return;
            }

            // Unit quad scaled to the blob's diameter (size 1 → spans 1 unit).
            float diameter = radius * 2f;
            Basis basis = Basis.Identity;
            basis.X *= diameter;
            basis.Z *= diameter;
            var xform = new Transform3D(basis, new Vector3(pos.X, pos.Y + QuadHeightOffset, pos.Z));

            _mm.SetInstanceTransform(count, xform);
            // rgb stays white: the material's gradient carries the black shadow
            // color, and white * black = black. Alpha is the per-blob coverage.
            // Instance colors aren't sRGB-converted, but white/alpha need no
            // conversion (see FootprintScatter's tint note).
            _mm.SetInstanceColor(count, new Color(1f, 1f, 1f, alpha < 1f ? alpha : 1f));
            count++;
        }

        // The player always casts a blob (no discovery gate); it's the projector
        // center, so never distance-culled.
        Emit(playerPos, sim?.playerShadowRadius ?? 0f, 1f);

        if (mobShadows && mobMaster > 0f)
        {
            foreach (Mob mob in world.GetEntities<Mob>())
            {
                if (count >= Capacity)
                {
                    break;
                }
                MobData data = mob.mobData;
                if (data == null || data.groundShadowRadius <= 0f)
                {
                    continue;
                }
                float baseAlpha = mob.GroundShadowAlpha * mobMaster;
                if (baseAlpha <= 0f)
                {
                    continue;
                }
                // RememberedPosition, not the live body: a discovered-but-out-of-LOS
                // mob is drawn as a frozen memory silhouette pinned at its last-seen
                // spot while it keeps simulating elsewhere. The blob must sit under
                // the silhouette the player sees, not drift off with the invisible body.
                Vector3 pos = mob.RememberedPosition;
                float dx = pos.X - playerPos.X;
                float dz = pos.Z - playerPos.Z;
                if (dx * dx + dz * dz > cullSq)
                {
                    continue;
                }
                Emit(pos, data.groundShadowRadius, baseAlpha);
            }
        }

        if (_mm.VisibleInstanceCount != count)
        {
            _mm.VisibleInstanceCount = count;
        }
        // Pin culling bounds to the projector's working area around the player so
        // the moving instances are never wrongly frustum-culled (and never drag a
        // huge auto-AABB). Local == world: the MMI sits untransformed under World.
        _mmi.CustomAabb = new Aabb(
            new Vector3(playerPos.X - CullRadius, playerPos.Y - CullRadius, playerPos.Z - CullRadius),
            new Vector3(CullRadius * 2f, CullRadius * 2f, CullRadius * 2f));
    }

    private bool TryInit()
    {
        Material material = World.Current?.SimData?.groundShadowMaterial;
        if (material == null)
        {
            GD.PushError("GroundShadowScatter: SimData.groundShadowMaterial is not set — grounding shadows disabled.");
            _initFailed = true;
            return false;
        }

        // Allocate the full buffer once; per-frame visibility is driven by
        // VisibleInstanceCount (see _Process) so the buffer is never reallocated.
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = GetQuad(),
            InstanceCount = Capacity,
            VisibleInstanceCount = 0,
        };
        _mmi = new MultiMeshInstance3D
        {
            Multimesh = _mm,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = GroundStainProjector.STAIN_PROXY_LAYER_MASK,
            Name = "GroundShadowBlobs",
        };
        AddChild(_mmi);
        return true;
    }
}
