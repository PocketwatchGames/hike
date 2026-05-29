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
    [Export] public Mesh CardMesh;
    [Export] public Texture2D LeafTexture;
    [Export] public Color LeafTintA = new Color(0.78f, 1.0f, 0.62f);
    [Export] public Color LeafTintB = new Color(1.05f, 1.0f, 0.45f);

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
    [Export] public float SwayAmplitude = 0.08f;
    [Export] public float WindBendStrength = 1.0f;

    // Procedural branches connecting trunk to each foliage cluster. Wire
    // BranchMesh to a unit-height CylinderMesh (branch_cylinder.tres) and
    // BranchMaterial to the same bark material used on the trunk so a
    // branch reads as a tapered extension of the trunk. One branch per
    // eligible cluster is generated at Rebuild time as a child of the
    // _Branches container under this MMI. Set BranchMesh = null to disable.
    [Export] public Mesh BranchMesh;
    [Export] public Material BranchMaterial;
    // How far below the cluster origin the branch attaches to the trunk
    // axis. Larger values give a more horizontal branch; smaller values a
    // more vertical one. Clamped so the attach point never sinks below
    // BranchMinPropLocalY.
    [Export] public float BranchAttachDrop = 1.5f;
    // Minimum height (in PropInstance-local Y, i.e. meters above the tree's
    // root) at which a branch may attach to the trunk. Keeps the lower
    // trunk clean — no twig-like branches sprouting from ground level.
    [Export] public float BranchMinPropLocalY = 3.0f;

    [ExportToolButton("Rebuild")]
    public Callable RebuildButton => Callable.From(Rebuild);

    private const string CardMaterialTemplatePath = "res://resources/materials/tree_cards_lit.tres";
    private const string BranchesContainerName = "_Branches";
    private const float GoldenAngleRad = 2.39996323f;
    private const float MaxAngleJitterDeg = 90f;
    // Minimum branch length to bother spawning. Anything shorter reads as a
    // bump on the trunk rather than a branch.
    private const float MinBranchLength = 0.3f;
    // Vertical clearance enforced between the branch attach point and the
    // cluster origin — if a cluster is within this distance of the minimum
    // attach height, the branch would be (near-)horizontal and stub-like, so
    // skip it.
    private const float MinAttachToClusterGap = 0.2f;

    public override void _Ready()
    {
        Rebuild();
    }

    private void Rebuild()
    {
        if (CardMesh == null)
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
            totalCards += Math.Max(0, c.CardCount);
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
        mm.Mesh = CardMesh;
        mm.InstanceCount = totalCards;

        int globalIdx = 0;
        int clusterIdx = 0;
        foreach (FoliageCluster cluster in clusters)
        {
            if (cluster.CardCount <= 0)
            {
                clusterIdx++;
                continue;
            }

            // Cluster-level RNG combines the authored PlacementSeed with the
            // cluster's child-index, so two clusters with the seed left at 0
            // still produce distinct placements.
            int seed = unchecked(cluster.PlacementSeed * 17 + clusterIdx * 6151 + 0x9E37);
            RandomNumberGenerator rng = new RandomNumberGenerator();
            rng.Seed = unchecked((ulong)seed);

            // Per-cluster tint mix value — independent RNG so reordering
            // clusters doesn't reshuffle which is light vs dark.
            RandomNumberGenerator tintRng = new RandomNumberGenerator();
            tintRng.Seed = unchecked((ulong)(cluster.PlacementSeed * 7919 + clusterIdx * 104729 + 0xABCDEF));
            float clusterMix = tintRng.Randf();
            Color clusterInstanceColor = new Color(clusterMix, 1f, 1f, 1f);

            // Cluster's transform within this MMI's local space — gets
            // composed onto each card so the cluster acts as a sub-origin.
            Transform3D clusterXform = cluster.Transform;

            for (int i = 0; i < cluster.CardCount; i++)
            {
                (Transform3D cardInClusterLocal, Vector3 blobRelOffsetCardLocal) = ComputeCardTransform(cluster, i, rng);
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
            Vector3 topLocal = cluster.Position + new Vector3(0f, cluster.EllipsoidRadii.Y, 0f);
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

        RebuildBranches(clusters);
    }

    // Build one cylinder branch per eligible cluster, attached to the trunk
    // axis (MMI-local X=0, Z=0) below the cluster and extending up-and-out
    // to the cluster origin. Branches share BranchMaterial with the trunk so
    // they read as continuous bark. Old branch children are wiped first; the
    // container is created on demand and excluded from the saved .tscn by
    // leaving Owner == null (so a Rebuild doesn't bloat the scene file).
    private void RebuildBranches(List<FoliageCluster> clusters)
    {
        Node3D container = GetOrCreateBranchesContainer();
        foreach (Node existing in container.GetChildren())
        {
            container.RemoveChild(existing);
            existing.QueueFree();
        }

        if (BranchMesh == null)
        {
            return;
        }

        // Y offset from PropInstance-local origin to this MMI's origin —
        // lets us evaluate BranchMinPropLocalY in MMI-local space without
        // round-tripping through ToGlobal/ToLocal per cluster.
        Node3D propNode = FindPropInstanceNode();
        if (propNode == null)
        {
            return;
        }
        float foliageYInProp = propNode.ToLocal(GlobalPosition).Y;
        float minAttachYMmi = BranchMinPropLocalY - foliageYInProp;

        foreach (FoliageCluster cluster in clusters)
        {
            float clusterYMmi = cluster.Position.Y;
            float clusterYProp = clusterYMmi + foliageYInProp;
            if (clusterYProp < BranchMinPropLocalY)
            {
                continue;
            }

            float attachYMmi = Mathf.Max(minAttachYMmi, clusterYMmi - BranchAttachDrop);
            if (attachYMmi >= clusterYMmi - MinAttachToClusterGap)
            {
                continue;
            }

            Vector3 attachMmi = new Vector3(0f, attachYMmi, 0f);
            Vector3 targetMmi = cluster.Position;
            Vector3 delta = targetMmi - attachMmi;
            float length = delta.Length();
            if (length < MinBranchLength)
            {
                continue;
            }
            Vector3 dir = delta / length;
            Vector3 midpoint = (attachMmi + targetMmi) * 0.5f;

            Basis basis = BasisAlignY(dir);
            Basis scaled = basis.Scaled(new Vector3(1f, length, 1f));
            Transform3D xform = new Transform3D(scaled, midpoint);

            MeshInstance3D branch = new MeshInstance3D
            {
                Mesh = BranchMesh,
                Transform = xform,
            };
            if (BranchMaterial != null)
            {
                branch.MaterialOverride = BranchMaterial;
            }
            container.AddChild(branch);
        }
    }

    private Node3D GetOrCreateBranchesContainer()
    {
        Node existing = GetNodeOrNull(BranchesContainerName);
        if (existing is Node3D existingNode3D)
        {
            return existingNode3D;
        }
        if (existing != null)
        {
            RemoveChild(existing);
            existing.QueueFree();
        }
        Node3D container = new Node3D { Name = BranchesContainerName };
        AddChild(container);
        return container;
    }

    private Node3D FindPropInstanceNode()
    {
        Node current = GetParent();
        while (current != null)
        {
            if (current is PropInstance prop)
            {
                return prop;
            }
            current = current.GetParent();
        }
        return null;
    }

    // Build a right-handed basis whose Y axis points along `dir`. X and Z
    // are arbitrary perpendiculars — the cylinder is radially symmetric so
    // their absolute orientation doesn't matter.
    private static Basis BasisAlignY(Vector3 dir)
    {
        Vector3 y = dir.Normalized();
        Vector3 helper = MathF.Abs(y.Y) > 0.9f ? Vector3.Forward : Vector3.Up;
        Vector3 x = helper.Cross(y);
        if (x.LengthSquared() < 1e-6f)
        {
            x = Vector3.Right;
        }
        x = x.Normalized();
        Vector3 z = x.Cross(y).Normalized();
        return new Basis(x, y, z);
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
        if (LeafTexture != null)
        {
            mat.SetShaderParameter("albedo_tex", LeafTexture);
        }
        mat.SetShaderParameter("leaf_tint_a", LeafTintA);
        mat.SetShaderParameter("leaf_tint_b", LeafTintB);
        mat.SetShaderParameter("sway_amplitude", SwayAmplitude);
        mat.SetShaderParameter("wind_bend_strength", WindBendStrength);
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
    private static (Transform3D Transform, Vector3 BlobRelOffsetCardLocal) ComputeCardTransform(FoliageCluster cluster, int i, RandomNumberGenerator rng)
    {
        Vector3 posLocal;
        Vector3 normal;
        Vector3 tipHint = Vector3.Up;
        float centerPivotFactor = 0f;

        switch (cluster.Placement)
        {
            case ECanopyPlacementMode.UprightStrand:
            {
                float angle = (i + 0.5f) / Math.Max(1, cluster.CardCount) * Mathf.Tau;
                float cx = MathF.Cos(angle);
                float cz = MathF.Sin(angle);
                posLocal = new Vector3(cx * cluster.EllipsoidRadii.X, 0f, cz * cluster.EllipsoidRadii.Z);
                normal = new Vector3(cx, 0f, cz).Normalized();
                break;
            }
            case ECanopyPlacementMode.Drooping:
            {
                FibonacciOnUpperHemisphere(i, cluster.CardCount, out float y, out float xr, out float zr);
                posLocal = new Vector3(xr * cluster.EllipsoidRadii.X, y * cluster.EllipsoidRadii.Y, zr * cluster.EllipsoidRadii.Z);
                normal = new Vector3(
                    SafeDiv(xr, cluster.EllipsoidRadii.X),
                    SafeDiv(y, cluster.EllipsoidRadii.Y),
                    SafeDiv(zr, cluster.EllipsoidRadii.Z)).Normalized();

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

                float perCardDroop = Mathf.Clamp(cluster.DroopAmount, 0f, 1f);
                tipHint = Vector3.Up.Rotated(swingAxis, perCardDroop * Mathf.Pi);
                centerPivotFactor = y * y * y;
                break;
            }
            case ECanopyPlacementMode.HemisphericalRadial:
            default:
            {
                FibonacciOnUpperHemisphere(i, cluster.CardCount, out float y, out float xr, out float zr);
                posLocal = new Vector3(xr * cluster.EllipsoidRadii.X, y * cluster.EllipsoidRadii.Y, zr * cluster.EllipsoidRadii.Z);
                normal = new Vector3(
                    SafeDiv(xr, cluster.EllipsoidRadii.X),
                    SafeDiv(y, cluster.EllipsoidRadii.Y),
                    SafeDiv(zr, cluster.EllipsoidRadii.Z)).Normalized();
                break;
            }
        }

        Basis baseBasis = BasisFromNormal(normal, tipHint);

        float roll = (rng.Randf() * Mathf.Tau) * cluster.RollJitter;
        Basis withRoll = baseBasis.Rotated(baseBasis.Z, roll);

        if (cluster.AngleJitter > 0f)
        {
            Vector3 axis = RandomUnitVector(rng);
            float angle = Mathf.DegToRad(MaxAngleJitterDeg) * rng.Randf() * cluster.AngleJitter;
            withRoll = withRoll.Rotated(axis, angle);
        }

        float scale = Mathf.Lerp(cluster.CardSizeMin, cluster.CardSizeMax, rng.Randf());
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
