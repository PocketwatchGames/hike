using System;
using System.Collections.Generic;
using Godot;

// Coordinator MultiMeshInstance3D that bakes every child FoliageCluster's
// cards into a single combined MultiMesh. Drop this under your tree's
// PropInstance root, parent your FoliageCluster nodes to it, and it
// builds one draw call's worth of foliage for the whole tree.
//
// Material is defined here (LeafTexture / LeafTintA / LeafTintB) and
// shared across every card on the tree, so the leaf species reads
// consistently. Per-cluster tint variation is driven by the shader's
// per-cluster hash on INSTANCE_COLOR.r (one mix value per cluster,
// scrambled by tree_origin for forest variation).
//
// tree_origin is set per-material by walking up to the nearest
// PropInstance ancestor and using its GlobalPosition. That means all
// FoliageMultiMesh instances under the same trunk share one tree-level
// hash baseline, and different trees in the forest get different hashes
// even when they share material.
[Tool]
[GlobalClass]
public partial class FoliageMultiMesh : MultiMeshInstance3D
{
    [Export] public Mesh cardMesh;
    [Export] public Texture2D leafTexture;
    [Export] public Color leafTintA = new Color(0.78f, 1.0f, 0.62f);
    [Export] public Color leafTintB = new Color(1.05f, 1.0f, 0.45f);

    // Sparse canopy detail species — flowers, buds, acorns, etc. Each entry is
    // one species (texture + tint + size + density + offsets, see
    // FoliageDetailData). Every cluster auto-scatters each species across its
    // surface; spread is driven by the species' Density (a multiplier on cluster
    // CardCount), with no per-cluster opt-in. Each used species bakes into its
    // own billboard MultiMesh under _Details (one extra draw call per species
    // per tree). Details inherit the canopy's lighting / wind / player-fade via
    // the tree_detail material.
    [Export] public FoliageDetailData[] details;

    // Per-tree wind tuning, stamped onto the per-MMI ShaderMaterial in
    // BuildCardMaterial. SwayAmplitude drives the per-card local rustle's
    // displacement in meters (object-space sin wave on the card's right
    // axis). Rustle FREQUENCY isn't per-tree — it comes from the shared
    // wind_phase global that SkyController integrates from palette base +
    // GustedWindSpeed, so all foliage shares one storm-responsive clock.
    // WindBendStrength scales the world-space directional bend that pushes
    // card tips along the global wind_dir; magnitude already tracks live
    // gusted wind through wind_amplitude, so this is a per-tree multiplier
    // on top of the weather signal. Drop to 0 to disable directional bend
    // on a variant (e.g. a stiff cactus or a dead tree).
    [Export] public float swayAmplitude = 0.08f;
    [Export] public float windBendStrength = 1.0f;

    [ExportToolButton("Rebuild")]
    public Callable RebuildButton => Callable.From(Rebuild);

    private const string CardMaterialTemplatePath = "res://resources/materials/tree_cards_lit.tres";
    private const string DetailMaterialTemplatePath = "res://resources/materials/tree_detail.tres";
    private const string DetailsContainerName = "_Details";
    private const float GoldenAngleRad = 2.39996323f;
    private const float MaxAngleJitterDeg = 90f;

    public override void _Ready()
    {
        // When parented to a TreeTrunk, the trunk owns the rebuild order: it
        // repositions the foliage clusters (canopy slide) and then drives our
        // Rebuild() with the slid positions. Skip the self-rebuild here so we
        // don't bake leaves at the un-slid authored positions first. Standalone
        // foliage (tall grass, bushes — no TreeTrunk parent) self-rebuilds.
        if (GetParent() is TreeTrunk)
        {
            return;
        }
        Rebuild();
    }

    public void Rebuild()
    {
        if (cardMesh == null)
        {
            Multimesh = null;
            return;
        }

        List<FoliageCluster> clusters = new List<FoliageCluster>();
        foreach (Node child in GetChildren())
        {
            if (child is FoliageCluster cluster)
            {
                clusters.Add(cluster);
            }
        }
        if (clusters.Count == 0)
        {
            Multimesh = null;
            return;
        }

        int totalCards = 0;
        foreach (FoliageCluster c in clusters)
        {
            totalCards += Math.Max(0, c.cardCount);
        }
        if (totalCards == 0)
        {
            Multimesh = null;
            return;
        }

        MultiMesh mm = new MultiMesh();
        mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        mm.UseCustomData = true;
        mm.UseColors = true;
        mm.Mesh = cardMesh;
        mm.InstanceCount = totalCards;

        int globalIdx = 0;
        int clusterIdx = 0;
        foreach (FoliageCluster cluster in clusters)
        {
            if (cluster.cardCount <= 0)
            {
                clusterIdx++;
                continue;
            }

            // Cluster-level RNG combines the authored PlacementSeed with the
            // cluster's child-index, so two clusters with the seed left at 0
            // still produce distinct placements.
            int seed = unchecked(cluster.placementSeed * 17 + clusterIdx * 6151 + 0x9E37);
            RandomNumberGenerator rng = new RandomNumberGenerator();
            rng.Seed = unchecked((ulong)seed);

            Color clusterInstanceColor = ClusterInstanceColor(cluster, clusterIdx);

            // Cluster's transform within this MMI's local space — gets
            // composed onto each card so the cluster acts as a sub-origin.
            Transform3D clusterXform = cluster.Transform;

            for (int i = 0; i < cluster.cardCount; i++)
            {
                (Transform3D cardInClusterLocal, Vector3 blobRelOffsetCardLocal) = ComputeCardTransform(cluster, i, cluster.cardCount, rng, cluster.cardSizeMin, cluster.cardSizeMax);
                // Compose cluster-local → MMI-local. The card's
                // blob-relative offset stays in CARD-local space (already
                // pre-rotated by the card's inverse basis) — when the
                // shader applies MODEL_MATRIX rotation it composes through
                // cluster's basis too, yielding the world-space outward
                // direction from cluster center to card. Uniform cluster
                // scale survives normalize; non-uniform scale on the
                // cluster node will distort sphere-normal direction.
                Transform3D xform = clusterXform * cardInClusterLocal;

                mm.SetInstanceTransform(globalIdx, xform);
                float phase = rng.Randf();
                mm.SetInstanceCustomData(globalIdx, new Color(phase, blobRelOffsetCardLocal.X, blobRelOffsetCardLocal.Y, blobRelOffsetCardLocal.Z));
                mm.SetInstanceColor(globalIdx, clusterInstanceColor);
                globalIdx++;
            }
            clusterIdx++;
        }

        Multimesh = mm;
        MaterialOverride = BuildCardMaterial();

        // tree_origin — walk up to the nearest PropInstance ancestor so
        // every FoliageMultiMesh under one trunk shares the same per-tree
        // hash baseline. Falls back to this node's own GlobalPosition if
        // no PropInstance is found (e.g. previewing in isolation).
        Vector3 treeOrigin = FindTreeOrigin();
        // Canopy height for the shader's directional bend — top-of-canopy
        // world Y minus tree_origin.y. Computed from cluster positions +
        // their Y radii so the bend's height term auto-scales to whatever
        // shape the author drops in (saplings vs full trees, tall conifers
        // vs squat bushes). Cluster Y radius is in cluster-local space; we
        // transform the cluster's top point through this MMI's global xform
        // to land in world space.
        float canopyTopWorldY = treeOrigin.Y;
        foreach (FoliageCluster cluster in clusters)
        {
            Vector3 topLocal = cluster.Position + new Vector3(0f, cluster.ellipsoidRadii.Y, 0f);
            float topWorldY = ToGlobal(topLocal).Y;
            if (topWorldY > canopyTopWorldY)
            {
                canopyTopWorldY = topWorldY;
            }
        }
        float canopyHeight = Mathf.Max(canopyTopWorldY - treeOrigin.Y, 0.5f);
        if (MaterialOverride is ShaderMaterial mat)
        {
            mat.SetShaderParameter("tree_origin", treeOrigin);
            mat.SetShaderParameter("canopy_height", canopyHeight);
        }

        RebuildDetails(clusters, treeOrigin, canopyHeight);
    }

    // Per-cluster instance color stamped into every card's COLOR. Shared by
    // the leaf bake and the detail bake so a cluster's tint mix (COLOR.r) and
    // player-occlusion fade-eligibility bit (COLOR.g) match across both.
    //   COLOR.r = cluster tint mix — an independent RNG (seeded apart from
    //             placement) so reordering clusters doesn't reshuffle which
    //             is light vs dark; tree_cards_lit reads it for the per-
    //             cluster tint variation axis.
    //   COLOR.g = static fade-eligibility flag (1 = participates in the
    //             camera→player cutaway, 0 = never fades). The actual fade is
    //             per-pixel in the shader; this only gates IN vs OUT per
    //             cluster, matching the FadesWhenOccludingPlayer toggle.
    private static Color ClusterInstanceColor(FoliageCluster cluster, int clusterIdx)
    {
        RandomNumberGenerator tintRng = new RandomNumberGenerator();
        tintRng.Seed = unchecked((ulong)(cluster.placementSeed * 7919 + clusterIdx * 104729 + 0xABCDEF));
        float clusterMix = tintRng.Randf();
        float fadeEligible = cluster.fadesWhenOccludingPlayer ? 1f : 0f;
        return new Color(clusterMix, fadeEligible, 1f, 1f);
    }

    // Bake each FoliageDetailData species into its own child MultiMesh under
    // _Details. Every cluster scatters round(CardCount * Density) cards of the
    // species — no per-cluster opt-in, so authoring is just "drop the species
    // on the tree, set its Density". Detail cards reuse the cluster's full
    // placement pipeline (same ComputeCardTransform, so Drooping et al. apply)
    // with a distinct RNG stream so they don't land exactly on leaf cards.
    // The _Details container is rebuilt each time and excluded from the saved
    // .tscn by leaving Owner == null.
    private void RebuildDetails(List<FoliageCluster> clusters, Vector3 treeOrigin, float canopyHeight)
    {
        Node3D container = GetOrCreateContainer(DetailsContainerName);
        foreach (Node existing in container.GetChildren())
        {
            container.RemoveChild(existing);
            existing.QueueFree();
        }

        if (cardMesh == null || details == null || details.Length == 0)
        {
            return;
        }

        for (int typeIdx = 0; typeIdx < details.Length; typeIdx++)
        {
            FoliageDetailData detail = details[typeIdx];
            if (detail == null || detail.texture == null || detail.density <= 0f)
            {
                continue;
            }

            int totalCards = 0;
            foreach (FoliageCluster c in clusters)
            {
                totalCards += DetailCardCount(c, detail);
            }
            if (totalCards == 0)
            {
                continue;
            }

            // Width follows the texture's aspect ratio; the authored size is
            // the billboard HEIGHT (see FoliageDetailData.SizeMin/Max).
            int texH = Math.Max(1, detail.texture.GetHeight());
            float detailAspect = detail.texture.GetWidth() / (float)texH;

            MultiMesh mm = new MultiMesh();
            mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
            mm.UseCustomData = true;
            mm.UseColors = true;
            mm.Mesh = cardMesh;
            mm.InstanceCount = totalCards;

            int globalIdx = 0;
            int clusterIdx = 0;
            foreach (FoliageCluster cluster in clusters)
            {
                int detailCount = DetailCardCount(cluster, detail);
                if (detailCount <= 0)
                {
                    clusterIdx++;
                    continue;
                }

                // Distinct seed (mixes the species index) so each detail
                // species samples different surface points than the leaves and
                // than each other, while staying deterministic across rebuilds.
                int seed = unchecked(cluster.placementSeed * 17 + clusterIdx * 6151 + typeIdx * 2749 + 0x5EED);
                RandomNumberGenerator rng = new RandomNumberGenerator();
                rng.Seed = unchecked((ulong)seed);

                Color clusterInstanceColor = ClusterInstanceColor(cluster, clusterIdx);
                Transform3D clusterXform = cluster.Transform;

                // Two orthogonal offsets carry a detail off the raw ellipsoid
                // surface point: OutwardOffset HORIZONTALLY toward the silhouette
                // rim (so it clears the sideways-splaying leaf cards) and
                // VerticalOffset up/down. The push is deliberately horizontal,
                // not along the surface normal — these clusters are oblate, so
                // their normals point nearly straight UP across the whole top
                // and a normal push would just lift details into the air without
                // reaching the outer edge the iso camera sees occluded.
                Vector3 clusterCenterMMI = clusterXform.Origin;

                for (int i = 0; i < detailCount; i++)
                {
                    // Reuse the leaf placement only for its anchor POINT on the
                    // canopy shell — pass size 0 so no card-scale droop pivot is
                    // applied; the billboard's own size/orientation comes from
                    // the scaled basis below, not from the placement transform.
                    (Transform3D placement, Vector3 _) = ComputeCardTransform(cluster, i, detailCount, rng, 0f, 0f);
                    Vector3 anchorBase = (clusterXform * placement).Origin;

                    // Horizontal direction from the cluster's vertical axis out
                    // to this point. Near the very top center it degenerates —
                    // those details are already on top, so leave them unpushed.
                    Vector3 horiz = anchorBase - clusterCenterMMI;
                    horiz.Y = 0f;
                    Vector3 outward = horiz.LengthSquared() > 1e-4f ? horiz.Normalized() : Vector3.Zero;

                    Vector3 anchorLocal = anchorBase
                        + outward * detail.outwardOffset
                        + Vector3.Up * detail.verticalOffset;

                    float height = Mathf.Lerp(detail.sizeMin, detail.sizeMax, rng.Randf());
                    float width = height * detailAspect;
                    // Billboard reads width/height from the basis column lengths
                    // (MODEL_MATRIX[0]/[1]); orientation is irrelevant since the
                    // shader rebuilds a camera-facing basis. Translation is the
                    // anchor in MMI-local space, same frame as the leaf cards.
                    Basis basis = Basis.Identity.Scaled(new Vector3(width, height, 1f));
                    Transform3D xform = new Transform3D(basis, anchorLocal);

                    float phase = rng.Randf();
                    mm.SetInstanceTransform(globalIdx, xform);
                    mm.SetInstanceCustomData(globalIdx, new Color(phase, 0f, 0f, 0f));
                    mm.SetInstanceColor(globalIdx, clusterInstanceColor);
                    globalIdx++;
                }
                clusterIdx++;
            }

            MultiMeshInstance3D mmi = new MultiMeshInstance3D
            {
                Name = $"Detail{typeIdx}",
                Multimesh = mm,
                MaterialOverride = BuildDetailMaterial(detail, treeOrigin, canopyHeight),
            };
            container.AddChild(mmi);
        }
    }

    // How many cards of one detail species a cluster scatters: its CardCount
    // scaled by the species Density, rounded to the nearest whole card.
    private static int DetailCardCount(FoliageCluster cluster, FoliageDetailData detail)
    {
        if (cluster.cardCount <= 0 || detail.density <= 0f)
        {
            return 0;
        }
        return Mathf.RoundToInt(cluster.cardCount * detail.density);
    }

    // Detail material — built from the billboard tree_detail template (so
    // details face the camera, layer in front via depth bias, and stay crisp)
    // with the species' own texture, tint, and depth bias. Lighting / cloud /
    // block-light / player-fade globals match the leaves, so a detail shades
    // consistently with the canopy it sits on.
    private ShaderMaterial BuildDetailMaterial(FoliageDetailData detail, Vector3 treeOrigin, float canopyHeight)
    {
        ShaderMaterial template = GD.Load<ShaderMaterial>(DetailMaterialTemplatePath);
        if (template == null)
        {
            GD.PushError($"FoliageMultiMesh: missing detail material template at {DetailMaterialTemplatePath}");
            return null;
        }
        ShaderMaterial mat = (ShaderMaterial)template.Duplicate();
        mat.SetShaderParameter("albedo_tex", detail.texture);
        mat.SetShaderParameter("leaf_tint_a", detail.tintA);
        mat.SetShaderParameter("leaf_tint_b", detail.tintB);
        mat.SetShaderParameter("depth_bias", detail.depthBias);
        mat.SetShaderParameter("sway_amplitude", swayAmplitude);
        mat.SetShaderParameter("wind_bend_strength", windBendStrength);
        mat.SetShaderParameter("tree_origin", treeOrigin);
        mat.SetShaderParameter("canopy_height", canopyHeight);
        return mat;
    }

    private Node3D GetOrCreateContainer(string name)
    {
        Node existing = GetNodeOrNull(name);
        if (existing is Node3D existingNode3D)
        {
            return existingNode3D;
        }
        if (existing != null)
        {
            RemoveChild(existing);
            existing.QueueFree();
        }
        Node3D container = new Node3D { Name = name };
        AddChild(container);
        return container;
    }

    private ShaderMaterial BuildCardMaterial()
    {
        ShaderMaterial template = GD.Load<ShaderMaterial>(CardMaterialTemplatePath);
        if (template == null)
        {
            GD.PushError($"FoliageMultiMesh: missing card material template at {CardMaterialTemplatePath}");
            return null;
        }
        ShaderMaterial mat = (ShaderMaterial)template.Duplicate();
        if (leafTexture != null)
        {
            mat.SetShaderParameter("albedo_tex", leafTexture);
        }
        mat.SetShaderParameter("leaf_tint_a", leafTintA);
        mat.SetShaderParameter("leaf_tint_b", leafTintB);
        mat.SetShaderParameter("sway_amplitude", swayAmplitude);
        mat.SetShaderParameter("wind_bend_strength", windBendStrength);
        return mat;
    }

    private Vector3 FindTreeOrigin()
    {
        Node current = GetParent();
        while (current != null)
        {
            if (current is PropInstance prop)
            {
                return prop.GlobalPosition;
            }
            current = current.GetParent();
        }
        return GlobalPosition;
    }

    // -- Placement math (cluster-local space) --------------------------------

    // Returns the card's CLUSTER-LOCAL transform and the cluster-center-to-
    // card vector pre-rotated by the card's inverse basis so the shader can
    // recover the world-space direction with a single MODEL_MATRIX rotation
    // (see tree_cards_lit.gdshader for the math).
    private static (Transform3D Transform, Vector3 BlobRelOffsetCardLocal) ComputeCardTransform(FoliageCluster cluster, int i, int count, RandomNumberGenerator rng, float sizeMin, float sizeMax)
    {
        Vector3 posLocal;
        Vector3 normal;
        Vector3 tipHint = Vector3.Up;
        float centerPivotFactor = 0f;

        switch (cluster.placement)
        {
            case ECanopyPlacementMode.UprightStrand:
            {
                float angle = (i + 0.5f) / Math.Max(1, count) * Mathf.Tau;
                float cx = MathF.Cos(angle);
                float cz = MathF.Sin(angle);
                posLocal = new Vector3(cx * cluster.ellipsoidRadii.X, 0f, cz * cluster.ellipsoidRadii.Z);
                normal = new Vector3(cx, 0f, cz).Normalized();
                break;
            }
            case ECanopyPlacementMode.Drooping:
            {
                FibonacciOnUpperHemisphere(i, count, out float y, out float xr, out float zr);
                posLocal = new Vector3(xr * cluster.ellipsoidRadii.X, y * cluster.ellipsoidRadii.Y, zr * cluster.ellipsoidRadii.Z);
                normal = new Vector3(
                    SafeDiv(xr, cluster.ellipsoidRadii.X),
                    SafeDiv(y, cluster.ellipsoidRadii.Y),
                    SafeDiv(zr, cluster.ellipsoidRadii.Z)).Normalized();

                Vector3 outwardHorizontal = new Vector3(posLocal.X, 0f, posLocal.Z);
                if (outwardHorizontal.LengthSquared() < 1e-6f)
                {
                    outwardHorizontal = Vector3.Right;
                }
                outwardHorizontal = outwardHorizontal.Normalized();
                Vector3 swingAxis = outwardHorizontal.Cross(Vector3.Up);
                if (swingAxis.LengthSquared() < 1e-6f)
                {
                    swingAxis = Vector3.Right;
                }
                swingAxis = swingAxis.Normalized();

                float perCardDroop = Mathf.Clamp(cluster.droopAmount, 0f, 1f);
                tipHint = Vector3.Up.Rotated(swingAxis, perCardDroop * Mathf.Pi);
                centerPivotFactor = y * y * y;
                break;
            }
            case ECanopyPlacementMode.SweptBough:
            {
                FibonacciOnUpperHemisphere(i, count, out float y, out float xr, out float zr);
                posLocal = new Vector3(xr * cluster.ellipsoidRadii.X, y * cluster.ellipsoidRadii.Y, zr * cluster.ellipsoidRadii.Z);

                Vector3 outwardHorizontal = new Vector3(posLocal.X, 0f, posLocal.Z);
                if (outwardHorizontal.LengthSquared() < 1e-6f)
                {
                    outwardHorizontal = Vector3.Right;
                }
                outwardHorizontal = outwardHorizontal.Normalized();

                // SweptBough rotates the card's TIP (its long axis — the
                // direction a bough points) about the horizontal tangent
                // around the trunk, driven by DroopAmount: 0 -> points
                // straight up, 0.5 -> points straight out (horizontal),
                // 1 -> points straight down. The face normal rides the same
                // rotation as the perpendicular partner, so the broad face
                // stays square to the bough. Pine boughs read best in the
                // upper half of the range (~0.6-0.8), where the tip points
                // outward and down. Contrast Drooping, which keeps the card
                // facing along the ellipsoid gradient and only tilts the
                // tip — a swing that projects away on the side cards.
                Vector3 swingAxis = Vector3.Up.Cross(outwardHorizontal);
                if (swingAxis.LengthSquared() < 1e-6f)
                {
                    swingAxis = Vector3.Right;
                }
                swingAxis = swingAxis.Normalized();

                float sweep = Mathf.Clamp(cluster.droopAmount, 0f, 1f) * Mathf.Pi;
                tipHint = Vector3.Up.Rotated(swingAxis, sweep);
                normal = outwardHorizontal.Rotated(swingAxis, sweep);
                centerPivotFactor = y * y * y;
                break;
            }
            case ECanopyPlacementMode.HemisphericalRadial:
            default:
            {
                FibonacciOnUpperHemisphere(i, count, out float y, out float xr, out float zr);
                posLocal = new Vector3(xr * cluster.ellipsoidRadii.X, y * cluster.ellipsoidRadii.Y, zr * cluster.ellipsoidRadii.Z);
                normal = new Vector3(
                    SafeDiv(xr, cluster.ellipsoidRadii.X),
                    SafeDiv(y, cluster.ellipsoidRadii.Y),
                    SafeDiv(zr, cluster.ellipsoidRadii.Z)).Normalized();
                break;
            }
        }

        Basis baseBasis = BasisFromNormal(normal, tipHint);

        float roll = (rng.Randf() * Mathf.Tau) * cluster.rollJitter;
        Basis withRoll = baseBasis.Rotated(baseBasis.Z, roll);

        if (cluster.angleJitter > 0f)
        {
            Vector3 axis = RandomUnitVector(rng);
            float angle = Mathf.DegToRad(MaxAngleJitterDeg) * rng.Randf() * cluster.angleJitter;
            withRoll = withRoll.Rotated(axis, angle);
        }

        float scale = Mathf.Lerp(sizeMin, sizeMax, rng.Randf());
        Basis finalBasis = withRoll.Scaled(new Vector3(scale, scale, scale));

        if (centerPivotFactor > 0f)
        {
            posLocal -= withRoll.Y * (scale * 0.5f * centerPivotFactor);
        }

        // Cluster center is (0,0,0) in cluster-local; the offset to the
        // card IS posLocal. Pre-rotate by the inverse card basis so the
        // shader's MODEL_MATRIX rotation recovers the world-space direction.
        Vector3 blobRelOffsetCardLocal = withRoll.Transposed() * posLocal;

        return (new Transform3D(finalBasis, posLocal), blobRelOffsetCardLocal);
    }

    private static void FibonacciOnUpperHemisphere(int i, int n, out float y, out float xr, out float zr)
    {
        int count = Math.Max(1, n);
        y = (i + 0.5f) / count;
        float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
        float phi = i * GoldenAngleRad;
        xr = MathF.Cos(phi) * r;
        zr = MathF.Sin(phi) * r;
    }

    private static Basis BasisFromNormal(Vector3 normal, Vector3 tipHint)
    {
        Vector3 z = normal.Normalized();
        if (z.LengthSquared() < 1e-6f)
        {
            z = new Vector3(0f, 0f, 1f);
        }
        Vector3 y = tipHint - tipHint.Dot(z) * z;
        if (y.LengthSquared() < 1e-6f)
        {
            Vector3 alt = MathF.Abs(z.Y) > 0.9f ? Vector3.Right : Vector3.Up;
            y = alt - alt.Dot(z) * z;
        }
        y = y.Normalized();
        Vector3 x = y.Cross(z).Normalized();
        return new Basis(x, y, z);
    }

    private static Vector3 RandomUnitVector(RandomNumberGenerator rng)
    {
        float z = rng.RandfRange(-1f, 1f);
        float phi = rng.Randf() * Mathf.Tau;
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(phi), r * MathF.Sin(phi), z);
    }

    private static float SafeDiv(float a, float b)
    {
        return MathF.Abs(b) < 1e-6f ? 0f : a / b;
    }
}
