using System;
using System.Collections.Generic;
using Godot;

// Procedural BRANCHING trunk for a tree prop. Lives on the tree's `Trunk`
// MeshInstance3D and grows a recursive woody skeleton from authored parameters
// instead of a static CylinderMesh, so the silhouette reads as a real branching
// tree rather than a smooth cone — and so every instance can vary.
//
// THERE IS NO SEPARATE "BRANCH" CONCEPT — it's all one branching trunk. The
// foliage clusters are attractors: starting from the base the trunk grows
// upward toward the clusters and, where the clusters it serves are spread out,
// SPLITS into child trunks that each carry the subset of clusters nearer to it.
// Children split again as they rise, so a stem can fork low and fork again
// higher up. Every cluster ends up at the thin tip of some branch. Authoring
// knobs control the character of the branching:
//   * SplitHeight       — how far up toward its clusters a fork happens (low =
//                          forks near the ground).
//   * BranchSpread      — how far a fork travels horizontally toward its clusters.
//   * VerticalTurn      — how sharply a branch turns back to vertical after
//                          spreading (0 = straight line to target, 1 = reach out
//                          then rise steeply, a candelabra arm).
//   * MaxBranchDepth /  — how many times it may fork, and how spread the clusters
//     SplitMinSpread       must be before a fork is worth it (MaxBranchDepth 0 =
//                          a single trunk leaning to the crown centroid).
//
// It also OWNS canopy placement and the twig cards:
//   * Per-instance HEIGHT VARIATION — a world-position hash picks an effective
//     height from TrunkHeight up to +HeightVariation (never shorter); the canopy
//     is STRETCHED vertically about its lowest cluster (the base stays over the
//     player's head while the crown rises), and the skeleton grows to the
//     stretched positions.
//   * TWIG cards at each branch tip (bridge the bark to the leaves through the
//     foliage cutaway). Moved out of FoliageMultiMesh, which is now leaf-only.
//
// Base-pivoted at the prop origin: Trunk-local == Foliage-local == prop-local,
// so all cluster / skeleton math is in one space and the node never moves.
//
// [Tool] so it previews in the editor. Per-instance variation is gated to
// runtime (Engine.IsEditorHint() == false): the editor previews nominal height
// and never mutates authored cluster / perch / collider transforms, so repeated
// Rebuild/save never drifts the scene.
[Tool]
[GlobalClass]
public partial class TreeTrunk : MeshInstance3D
{
    // -- Trunk shape -------------------------------------------------------

    // Nominal canopy-base height. Drives the per-instance height variation and
    // the root-bulge extent; the actual skeleton height follows the clusters.
    [Export] public float TrunkHeight = 5.0f;
    [Export] public float BottomRadius = 0.35f;
    // Radius at a branch tip (the thin end of every terminal branch).
    [Export] public float TopRadius = 0.06f;
    // Radial faceting (sides around each branch tube).
    [Export(PropertyHint.Range, "3,32,1")] public int RadialSegments = 8;
    [Export] public bool CapBottom = true;

    // -- Branching ---------------------------------------------------------

    // Maximum fork depth. 0 = a single trunk that leans to the crown centroid
    // (no forking); each increment allows another generation of forks. Needs to
    // be high enough to resolve the cluster count to single tips (binary, so
    // ~ceil(log2(clusterCount))) or the deepest clusters merge at a centroid.
    [Export(PropertyHint.Range, "0,8,1")] public int MaxBranchDepth = 5;
    // A node only forks if its clusters are spread (max 3D separation) beyond
    // this many meters — tight clusters share one tip instead of forking. 3D
    // (not just horizontal) so a vertically-stacked column (slender birch) keeps
    // forking up through its full height instead of stopping at the centroid.
    [Export] public float SplitMinSpread = 0.5f;
    // Where a fork happens, as a fraction from the branch start up toward the
    // lowest cluster it serves. Low => forks near the bottom and the children
    // travel up; high => a tall shared trunk that forks late.
    [Export(PropertyHint.Range, "0,1,0.01")] public float SplitHeight = 0.4f;
    // How far (fraction of the horizontal gap to its clusters) a fork point
    // travels sideways toward them — the horizontal "reach" of a branch.
    [Export(PropertyHint.Range, "0,1,0.01")] public float BranchSpread = 0.6f;
    // After a fork, how much of the horizontal distance to its target a branch
    // covers in an initial near-horizontal run before kinking to climb mostly
    // vertical (the leftover horizontal becomes a slight lean). 0 = head straight
    // at the target; ~0.8 = reach out low then turn up (birch-style elbow).
    [Export(PropertyHint.Range, "0,1,0.01")] public float ElbowReach = 0.45f;
    // Child radius / parent radius at each fork. < 1 thins the tree upward.
    [Export(PropertyHint.Range, "0.3,1,0.01")] public float RadiusFalloff = 0.75f;
    // How front-loaded a branch's taper is: 1 = even taper along its length;
    // higher shrinks it toward the tip radius quickly at the base then holds
    // roughly uniform, so branches don't read as long heavy cones.
    [Export(PropertyHint.Range, "1,5,0.1")] public float TaperBias = 3.0f;

    // -- Jagged branch shape (zigzag, not smooth arcs) --------------------
    // Each branch run is straight; the branch zigzags at hard corners instead of
    // bending in a smooth arc. KinkLength is the average straight run between
    // corners (shorter = more zigzag); MaxBendAngle caps how hard each corner
    // turns. Verticality (below) damps the zigzag toward a straight line.
    [Export] public float KinkLength = 0.6f;
    [Export(PropertyHint.Range, "0,80,1")] public float MaxBendAngle = 40f;
    // Random radius swell at kink corners — a branch can bulge slightly thicker
    // at a turn (a knot). 0 = none; 0.3 = up to +30% at some kinks.
    [Export(PropertyHint.Range, "0,1,0.01")] public float KnotSwell = 0.2f;

    // -- Per-instance height variation ------------------------------------

    // Per-instance upward height variation: a tree grows by 0 .. HeightVariation
    // of TrunkHeight (never below TrunkHeight). 0.2 = up to +20% taller.
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightVariation = 0.2f;

    // When true, the per-instance world-position hash that drives the branch
    // structure is replaced by a fixed seed, so EVERY spawn of this scene grows
    // byte-identical branch geometry instead of a different tree per location.
    // Pair with HeightVariation = 0 for a fully size-and-shape "locked" tree
    // (the climbable tree, which must read the same everywhere). Defaults off so
    // normal forest trees keep their per-location variation. (Leaf/twig tint is
    // still position-hashed in the foliage shaders; only the geometry is pinned.)
    [Export] public bool LockSeed = false;

    // -- Gnarl / irregularity ---------------------------------------------

    // Straightness damper for the zigzag: 1 = straight runs (no zigzag), lower
    // lets each branch zigzag up to MaxBendAngle at its corners.
    [Export(PropertyHint.Range, "0,1,0.01")] public float Verticality = 0.9f;
    // Extra radius at the very base, flaring over the bottom RootBulgeHeight
    // (a fraction of TrunkHeight) of the root — the root swell.
    [Export(PropertyHint.Range, "0,3,0.01")] public float RootBulge = 0.6f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float RootBulgeHeight = 0.18f;
    // Per-region radius swell/pinch along each branch (low-frequency 1D noise).
    [Export(PropertyHint.Range, "0,1,0.01")] public float ThicknessVariation = 0.25f;
    [Export(PropertyHint.Range, "0.5,8,0.1")] public float ThicknessWaves = 3.0f;
    // Per-vertex radial jitter — the faceted, bark-knotty silhouette.
    [Export(PropertyHint.Range, "0,0.6,0.01")] public float Jaggedness = 0.12f;

    // -- Bark + twigs ------------------------------------------------------
    // Bark for the whole skeleton — applied to the generated trunk surface in
    // code (removing the static CylinderMesh sub-resource orphans any scene-
    // authored surface_material_override/0 at load). Wire it to the species' bark.
    [Export] public Material BranchMaterial;

    // Optional twig card at each branch tip — a quad textured with TwigsTexture
    // that bridges the bark and the leaf cluster and survives the foliage cutaway
    // (so a deep forest doesn't read as a row of bare tips). Null disables.
    [Export] public Mesh TwigsMesh;
    [Export] public Texture2D TwigsTexture;
    [Export] public float TwigsSize = 1.0f;
    // Fraction of the twigs quad height pulled back down the branch axis so the
    // texture's visible content meets the branch end.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float TwigsAttachInset = 0.1f;

    [ExportToolButton("Rebuild")]
    public Callable RebuildButton => Callable.From(Rebuild);

    private const string TwigsMaterialTemplatePath = "res://resources/materials/tree_twigs.tres";
    private const string WoodyContainerName = "_Woody";
    private const string FoliageChildName = "Foliage";
    // Segments shorter than this aren't worth baking (they'd be a bark wart).
    private const float MinSegmentLength = 0.15f;
    // A fork point always rises at least this far above its start, so a low
    // SplitHeight can't produce a zero-length stub.
    private const float MinForkRise = 0.4f;
    // How far a side branch's base is pushed back down the trunk axis (as a
    // multiple of the fork radius) so its capped base sits buried inside the
    // trunk volume and the junction shows no hole.
    private const float SideBranchBury = 2.0f;

    public override void _Ready()
    {
        Rebuild();
    }

    private void Rebuild()
    {
        Vector3 treeOrigin = FindTreeOrigin();

        // Per-instance variation seeded from world position: a stand varies but
        // each tree is stable across reloads. In the editor the tree sits at the
        // origin so the hash is deterministic and the preview never drifts.
        // LockSeed pins this hash to a fixed point so a "locked" tree grows the
        // same geometry at every spawn (world-Y math below still uses the real
        // treeOrigin — only the variation seed is pinned).
        Vector3 seedOrigin = LockSeed ? Vector3.Zero : treeOrigin;
        float heightHash = Hash13(seedOrigin);
        float baseSeed = Hash13(seedOrigin + new Vector3(31.4f, 2.7f, 11.9f)) * 100f;

        // Height variation is runtime-only so the editor canopy (NOT stretched in
        // the editor) stays consistent with the previewed skeleton. Variation only
        // ADDS height (hash is [0,1)), so TrunkHeight is the authored minimum — a
        // tree never shrinks below its authored size, only grows up to
        // TrunkHeight * (1 + HeightVariation).
        float effHeight = TrunkHeight;
        if (!Engine.IsEditorHint())
        {
            effHeight = TrunkHeight * (1f + heightHash * HeightVariation);
        }
        effHeight = Mathf.Max(effHeight, 0.5f);

        FoliageMultiMesh foliage = GetNodeOrNull<FoliageMultiMesh>(FoliageChildName);
        List<FoliageCluster> clusters = CollectClusters(foliage);

        // Runtime: stretch the canopy vertically about its lowest cluster to the
        // new height, resize the collider, and lift the perch / wind emitter.
        // Done BEFORE growing the skeleton so it targets the final (stretched)
        // cluster positions. cluster.Position is already trunk-local == prop-local
        // (Foliage authored at the trunk origin), and so are the prop-space Y
        // values of the collider / perch / wind emitter, so one pivot serves all.
        if (!Engine.IsEditorHint() && clusters.Count > 0)
        {
            float scale = effHeight / TrunkHeight;
            float pivotY = MinClusterY(clusters);
            float topY = pivotY;
            foreach (FoliageCluster c in clusters)
            {
                Vector3 p = c.Position;
                p.Y = pivotY + (p.Y - pivotY) * scale;
                c.Position = p;
                topY = Mathf.Max(topY, p.Y);
            }
            ResizeCollider(topY);
            ScaleNodeY(FindSiblingOrChild<Node3D>("WindEmitterSource"), pivotY, scale);
        }

        // Grow the branching skeleton toward the clusters, bake it into one mesh.
        List<Tip> tips = new List<Tip>();
        List<Strand> strands = BuildSkeleton(clusters, effHeight, baseSeed, tips);
        Mesh = BakeSkeleton(strands);

        // Runtime: snap the perch to the highest branch tip the skeleton actually
        // grew (the topmost twig), so a bird sits at the true crown rather than
        // the authored nominal point. Editor-gated so the authored transform
        // never drifts on save (the editor previews the nominal skeleton anyway).
        if (!Engine.IsEditorHint())
        {
            PerchAtHighestTip(tips);
        }
        // Apply bark in code — the scene-authored surface_material_override/0 is
        // orphaned once the static trunk mesh sub-resource is removed.
        if (BranchMaterial != null)
        {
            SetSurfaceOverrideMaterial(0, BranchMaterial);
        }

        float canopyHeight = ComputeCanopyHeight(clusters, treeOrigin);
        ShaderMaterial twigsRuntimeMat = BuildTwigsMaterial(treeOrigin, canopyHeight);
        RebuildTwigs(tips, twigsRuntimeMat);

        // Rebuild leaves AFTER the clusters are repositioned. FoliageMultiMesh
        // skips its own _Ready rebuild when parented to a TreeTrunk.
        foliage?.Rebuild();
    }

    // -- Canopy stretch / collider ----------------------------------------

    // Move the perch to the highest branch tip. Tips are in trunk-local space,
    // which equals prop-local (the trunk is identity at the prop origin), so the
    // tip position drops straight onto the sibling perch's local transform.
    private void PerchAtHighestTip(List<Tip> tips)
    {
        if (tips.Count == 0)
        {
            return;
        }
        Node3D perch = FindSiblingOrChild<Node3D>("Perch");
        if (perch == null)
        {
            return;
        }
        Vector3 highest = tips[0].Position;
        foreach (Tip t in tips)
        {
            if (t.Position.Y > highest.Y)
            {
                highest = t.Position;
            }
        }
        perch.Position = highest;
    }

    // Scale a node's prop-local Y about `pivotY` (the lowest cluster), matching
    // the canopy stretch so the wind emitter rides with the crown.
    private static void ScaleNodeY(Node3D node, float pivotY, float scale)
    {
        if (node == null)
        {
            return;
        }
        Vector3 p = node.Position;
        p.Y = pivotY + (p.Y - pivotY) * scale;
        node.Position = p;
    }

    // Stretch the trunk collider so it spans the ground up to the stretched
    // trunk top (highest cluster). The cylinder shape is duplicated per instance
    // first — it's a shared species sub-resource, so resizing it in place would
    // resize every other tree of the same kind.
    private void ResizeCollider(float trunkTop)
    {
        StaticBody3D body = FindSiblingOrChild<StaticBody3D>("Body");
        if (body == null)
        {
            return;
        }
        foreach (Node child in body.GetChildren())
        {
            if (child is CollisionShape3D cs && cs.Shape is CylinderShape3D cyl)
            {
                CylinderShape3D ownShape = (CylinderShape3D)cyl.Duplicate();
                ownShape.Height = trunkTop;
                cs.Shape = ownShape;
                Vector3 p = cs.Position;
                p.Y = trunkTop * 0.5f;
                cs.Position = p;
            }
        }
    }

    // -- Branching skeleton ------------------------------------------------

    // One woody segment of the skeleton (a tapered, optionally curved tube from
    // A to B). The trunk and all forks are just segments.
    private struct Segment
    {
        public Vector3 A;
        public Vector3 B;
        public float RadiusA;
        public float RadiusB;
        public bool IsRoot;
        public float Seed;
    }

    // A terminal branch tip that reaches a cluster — gets a twig card.
    private struct Tip
    {
        public Vector3 Position;
        public Vector3 Dir;
    }

    // A maximal chain of structural segments baked as ONE continuous, welded
    // tube (shared rings at the structural joints, one parallel-transport frame
    // — no gaps, no twist discontinuity). The root strand runs ground-to-tip
    // through the dominant subgroup at every fork; each non-dominant subgroup
    // spawns a side-branch strand whose base is buried inside the trunk.
    private class Strand
    {
        public List<Segment> Segs = new List<Segment>();
        // Cap the base ring: the root (sits on the ground) or a side branch
        // (base buried in the trunk, cap hidden — closes the otherwise-open end).
        public bool CapBase;
    }

    private List<Strand> BuildSkeleton(List<FoliageCluster> clusters, float effHeight, float baseSeed, List<Tip> tips)
    {
        List<Strand> strands = new List<Strand>();
        Strand root = new Strand { CapBase = CapBottom };
        strands.Add(root);

        if (clusters.Count == 0)
        {
            // No clusters — a lone straight trunk.
            root.Segs.Add(new Segment
            {
                A = Vector3.Zero,
                B = new Vector3(0f, effHeight, 0f),
                RadiusA = BottomRadius,
                RadiusB = TopRadius,
                IsRoot = true,
                Seed = baseSeed,
            });
            return strands;
        }
        Grow(Vector3.Zero, new List<FoliageCluster>(clusters), BottomRadius, 0, baseSeed, root, strands, tips);
        return strands;
    }

    // Recursively grow from `start`, carrying `group`, appending structural
    // segments to `strand`. If the group is a single cluster (or tight / at the
    // depth cap) grow one terminal segment to it. Otherwise grow a stub to a
    // fork point, then CONTINUE this strand into the dominant sub-group (the one
    // most aligned with the incoming direction, so the trunk reads as one
    // continuous tube) and spawn a NEW strand for the other sub-group whose base
    // is buried back inside the trunk.
    private void Grow(Vector3 start, List<FoliageCluster> group, float radius, int depth, float seed, Strand strand, List<Strand> strands, List<Tip> tips)
    {
        if (group.Count == 0)
        {
            return;
        }

        bool terminal = group.Count == 1 || depth >= MaxBranchDepth || MaxClusterSpread(group) < SplitMinSpread;
        if (terminal)
        {
            Vector3 goal = group.Count == 1 ? group[0].Position : Centroid(group);
            strand.Segs.Add(new Segment { A = start, B = goal, RadiusA = radius, RadiusB = TopRadius, IsRoot = depth == 0, Seed = seed });
            Vector3 dir = (goal - start);
            dir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : Vector3.Up;
            tips.Add(new Tip { Position = goal, Dir = dir });
            return;
        }

        Bisect(group, out List<FoliageCluster> groupA, out List<FoliageCluster> groupB);

        // Fork point: up toward the lowest cluster by a randomized fraction of
        // SplitHeight (so fork heights vary a lot, not a regular ladder), nudged
        // horizontally toward the group's centroid by BranchSpread.
        float groupMinY = MinClusterY(group);
        float splitFrac = Mathf.Clamp(SplitHeight * (0.3f + 1.6f * Hash2(seed, 4.6f)), 0.05f, 0.95f);
        float forkY = Mathf.Lerp(start.Y, groupMinY, splitFrac);
        forkY = Mathf.Max(forkY, start.Y + MinForkRise);
        Vector3 cen = Centroid(group);
        Vector3 forkXz = new Vector3(start.X, 0f, start.Z) + new Vector3(cen.X - start.X, 0f, cen.Z - start.Z) * (BranchSpread * splitFrac);
        Vector3 fork = new Vector3(forkXz.X, forkY, forkXz.Z);

        float forkRadius = radius * 0.92f;
        strand.Segs.Add(new Segment { A = start, B = fork, RadiusA = radius, RadiusB = forkRadius, IsRoot = depth == 0, Seed = seed });

        // The dominant sub-group continues THIS strand straight on (welded at the
        // fork radius, no step); the other spawns a side branch.
        Vector3 incoming = fork - start;
        incoming = incoming.LengthSquared() > 1e-6f ? incoming.Normalized() : Vector3.Up;
        bool aDominant = AlignScore(incoming, fork, groupA) >= AlignScore(incoming, fork, groupB);
        List<FoliageCluster> domGroup = aDominant ? groupA : groupB;
        List<FoliageCluster> sideGroup = aDominant ? groupB : groupA;
        float domSeed = aDominant ? seed * 1.7f + 1.3f : seed * 1.7f + 2.9f;
        float sideSeed = aDominant ? seed * 1.7f + 2.9f : seed * 1.7f + 1.3f;

        Grow(fork, domGroup, forkRadius, depth + 1, domSeed, strand, strands, tips);

        // Side branch: a new strand thinned by RadiusFalloff, its base pushed
        // back down the trunk axis so the capped base is buried (no junction
        // hole) and the limb emerges through the trunk surface.
        Strand side = new Strand { CapBase = true };
        strands.Add(side);
        Vector3 sideStart = fork - incoming * (forkRadius * SideBranchBury);
        Grow(sideStart, sideGroup, radius * RadiusFalloff, depth + 1, sideSeed, side, strands, tips);
    }

    // How well a sub-group continues the incoming direction (dot of the incoming
    // unit vector with the direction from the fork to the sub-group centroid).
    // Higher = straighter continuation, so it becomes the dominant trunk.
    private static float AlignScore(Vector3 incoming, Vector3 fork, List<FoliageCluster> group)
    {
        Vector3 d = Centroid(group) - fork;
        return d.LengthSquared() > 1e-6f ? incoming.Dot(d.Normalized()) : -1f;
    }

    // Split a group into two spatially-coherent sub-groups: seed with the two
    // clusters farthest apart (in XZ), assign each remaining cluster to its
    // nearer seed.
    private static void Bisect(List<FoliageCluster> group, out List<FoliageCluster> a, out List<FoliageCluster> b)
    {
        a = new List<FoliageCluster>();
        b = new List<FoliageCluster>();

        int s0 = 0;
        int s1 = 1;
        float best = -1f;
        for (int i = 0; i < group.Count; i++)
        {
            for (int j = i + 1; j < group.Count; j++)
            {
                float d = group[i].Position.DistanceTo(group[j].Position);
                if (d > best)
                {
                    best = d;
                    s0 = i;
                    s1 = j;
                }
            }
        }
        Vector3 seed0 = group[s0].Position;
        Vector3 seed1 = group[s1].Position;
        foreach (FoliageCluster c in group)
        {
            if (c.Position.DistanceTo(seed0) <= c.Position.DistanceTo(seed1))
            {
                a.Add(c);
            }
            else
            {
                b.Add(c);
            }
        }
        // Degenerate guard (all coincident): force a non-empty split.
        if (a.Count == 0 || b.Count == 0)
        {
            a.Clear();
            b.Clear();
            for (int i = 0; i < group.Count; i++)
            {
                (i % 2 == 0 ? a : b).Add(group[i]);
            }
        }
    }

    // Max pairwise 3D distance within the group — 3D so a vertically-stacked
    // column (slender birch) counts as spread and keeps forking up through its
    // full height instead of terminating at the centroid (which would strand the
    // top clusters above a too-short trunk).
    private static float MaxClusterSpread(List<FoliageCluster> group)
    {
        float best = 0f;
        for (int i = 0; i < group.Count; i++)
        {
            for (int j = i + 1; j < group.Count; j++)
            {
                best = Mathf.Max(best, group[i].Position.DistanceTo(group[j].Position));
            }
        }
        return best;
    }

    private static Vector3 Centroid(List<FoliageCluster> group)
    {
        Vector3 sum = Vector3.Zero;
        foreach (FoliageCluster c in group)
        {
            sum += c.Position;
        }
        return sum / group.Count;
    }

    private static float MinClusterY(List<FoliageCluster> group)
    {
        float min = float.MaxValue;
        foreach (FoliageCluster c in group)
        {
            min = Mathf.Min(min, c.Position.Y);
        }
        return min;
    }

    // -- Mesh baking -------------------------------------------------------

    private Mesh BakeSkeleton(List<Strand> strands)
    {
        SurfaceTool st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float rootBulgeMeters = RootBulgeHeight * TrunkHeight;
        foreach (Strand strand in strands)
        {
            EmitStrand(st, strand, rootBulgeMeters);
        }
        // Smooth normals read the gnarled bark softly. No tangents — the bark
        // material has no normal map (and they'd spam warnings on thin tips).
        st.GenerateNormals();
        // Weld coincident ring vertices (continuous tubes share them) into an
        // indexed surface — fewer unique verts, better GPU vertex-cache reuse.
        st.Index();
        return st.Commit();
    }

    // Bake one strand as a single continuous JAGGED tube. Each structural
    // segment contributes a detailed node list (elbow + irregular kink runs)
    // and per-node radii; consecutive segments are concatenated, dropping the
    // duplicate joint node so the rings WELD into one un-broken tube walked by a
    // single parallel-transport frame (no gap, no twist seam at the forks).
    private void EmitStrand(SurfaceTool st, Strand strand, float rootBulgeMeters)
    {
        List<Vector3> nodes = new List<Vector3>();
        List<float> radii = new List<float>();
        float strandSeed = strand.Segs.Count > 0 ? strand.Segs[0].Seed : 0f;

        foreach (Segment seg in strand.Segs)
        {
            if ((seg.B - seg.A).Length() < MinSegmentLength)
            {
                // Too short to detail — carry just its endpoint(s) forward so the
                // polyline stays connected and the next segment still welds on.
                if (nodes.Count == 0)
                {
                    nodes.Add(seg.A);
                    radii.Add(Mathf.Max(TaperRadius(seg, 0f), 0.01f));
                }
                nodes.Add(seg.B);
                radii.Add(Mathf.Max(TaperRadius(seg, 1f), 0.01f));
                continue;
            }

            BuildSegmentNodes(seg, out List<Vector3> segNodes, out List<float> segTs);
            float[] segRadii = ComputeSegmentRadii(seg, segNodes, segTs, rootBulgeMeters);
            // Skip segNodes[0] after the first segment — it coincides with the
            // previous segment's last node (the welded joint).
            int startIdx = nodes.Count == 0 ? 0 : 1;
            for (int k = startIdx; k < segNodes.Count; k++)
            {
                nodes.Add(segNodes[k]);
                radii.Add(segRadii[k]);
            }
        }

        if (nodes.Count < 2)
        {
            return;
        }
        EmitTube(st, nodes, radii.ToArray(), strand.CapBase, strandSeed);
    }

    // Build one structural segment's detailed centreline as a JAGGED polyline.
    // The base path is an elbow (reach out, then climb) when the target is off
    // to the side; it is then walked in IRREGULAR straight runs (random length
    // around KinkLength) that kink at hard corners. The kink amount scales with
    // THINNESS — the thick base stays nearly straight, thin branch tips zigzag
    // the most — and is damped by Verticality and capped near MaxBendAngle.
    private void BuildSegmentNodes(Segment seg, out List<Vector3> nodes, out List<float> ts)
    {
        List<Vector3> basePath = BuildBasePath(seg.A, seg.B, seg.Seed);
        float total = 0f;
        for (int i = 0; i < basePath.Count - 1; i++)
        {
            total += (basePath[i + 1] - basePath[i]).Length();
        }
        total = Mathf.Max(total, 1e-3f);

        float bendK = Mathf.Tan(Mathf.DegToRad(MaxBendAngle) * 0.5f) * (1f - Mathf.Clamp(Verticality, 0f, 1f));
        float radSpan = Mathf.Max(BottomRadius - TopRadius, 1e-4f);

        nodes = new List<Vector3> { basePath[0] };
        ts = new List<float> { 0f };

        float cum = 0f;
        for (int i = 0; i < basePath.Count - 1; i++)
        {
            Vector3 p0 = basePath[i];
            Vector3 p1 = basePath[i + 1];
            float runLen = (p1 - p0).Length();
            Vector3 u = (p1 - p0) / Mathf.Max(runLen, 1e-4f);
            // Seeded zigzag plane for this run.
            Vector3 refUp = MathF.Abs(u.Y) > 0.9f ? Vector3.Right : Vector3.Up;
            Vector3 px = u.Cross(refUp).Normalized();
            Vector3 pz = u.Cross(px).Normalized();
            float planeAng = Hash2(seg.Seed + i * 7.1f, 5.3f) * Mathf.Tau;
            Vector3 perp = px * Mathf.Cos(planeAng) + pz * Mathf.Sin(planeAng);

            // Walk the run in irregular steps; kink at each interior node.
            float pos = 0f;
            int kIdx = 0;
            while (true)
            {
                float jitter = 0.45f + 1.2f * Hash2(seg.Seed + i * 31.7f + kIdx, 9.4f);   // ~0.45..1.65
                pos += KinkLength * jitter;
                if (pos > runLen - 0.4f * KinkLength)
                {
                    break;
                }
                float gt = (cum + pos) / total;
                // Thinness 0 (thick base) .. 1 (thin tip): stiffer wood kinks less.
                float thin = Mathf.Clamp((BottomRadius - TaperRadius(seg, gt)) / radSpan, 0f, 1f);
                float kinkScale = Mathf.Lerp(0.1f, 1f, thin);
                float prevStep = KinkLength * jitter;
                float mag = prevStep * bendK * kinkScale * (0.6f + 0.6f * Hash2(seg.Seed + kIdx * 5.2f, 3.1f));
                float sign = (kIdx % 2 == 0) ? 1f : -1f;
                nodes.Add(p0 + u * pos + perp * (mag * sign));
                ts.Add(gt);
                kIdx++;
            }
            cum += runLen;
            nodes.Add(p1);
            ts.Add(cum / total);
        }
    }

    // Per-node radius along a structural segment: FRONT-LOADED taper (shrinks
    // fast at the base, then ~uniform — no long cones), with root bulge (root
    // only), per-region thickness noise, and a random knot swell at interior
    // kink corners.
    private float[] ComputeSegmentRadii(Segment seg, List<Vector3> nodes, List<float> ts, float rootBulgeMeters)
    {
        int n = nodes.Count - 1;
        float[] radii = new float[nodes.Count];
        for (int k = 0; k <= n; k++)
        {
            float t = ts[k];
            float radius = TaperRadius(seg, t);
            if (seg.IsRoot && rootBulgeMeters > 1e-4f && nodes[k].Y < rootBulgeMeters)
            {
                float b = 1f - nodes[k].Y / rootBulgeMeters;
                radius *= 1f + RootBulge * b * b;
            }
            float tn = ValueNoise1(t * ThicknessWaves + seg.Seed);
            radius *= 1f + ThicknessVariation * (tn * 2f - 1f);
            // Knot: random swell at interior kink corners (incl. the elbow).
            if (KnotSwell > 0f && k > 0 && k < n)
            {
                radius *= 1f + KnotSwell * Hash2(seg.Seed + k * 2.1f, 7.7f);
            }
            radii[k] = Mathf.Max(radius, 0.01f);
        }
        return radii;
    }

    // Front-loaded taper radius at fraction t along a segment: drops toward the
    // tip radius quickly at the base then holds roughly uniform (no long cones).
    private float TaperRadius(Segment seg, float t)
    {
        return seg.RadiusB + (seg.RadiusA - seg.RadiusB) * Mathf.Pow(1f - t, Mathf.Max(TaperBias, 1f));
    }

    // Elbow base path: when the target is off to the side, insert a hard corner
    // so the branch reaches out (mostly horizontal, slight rise) for `reach` of
    // the horizontal distance, then climbs (mostly vertical, slight lean) to the
    // target. `reach` is randomized PER BRANCH around ElbowReach, so some stems
    // rise almost straight off the trunk (reach ~0) while others jag well out
    // before elbowing up. A near-vertical segment gets no elbow.
    private List<Vector3> BuildBasePath(Vector3 a, Vector3 b, float seed)
    {
        float hLen = new Vector3(b.X - a.X, 0f, b.Z - a.Z).Length();
        float reach = Mathf.Clamp(ElbowReach * 1.3f * Hash2(seed, 2.2f), 0f, 1f);
        if (reach > 0.05f && hLen > 0.3f)
        {
            const float elbowRise = 0.12f;   // slight upward angle on the reach
            Vector3 e = new Vector3(
                Mathf.Lerp(a.X, b.X, reach),
                a.Y + (b.Y - a.Y) * elbowRise,
                Mathf.Lerp(a.Z, b.Z, reach));
            return new List<Vector3> { a, e, b };
        }
        return new List<Vector3> { a, b };
    }

    // Build a tapered tube through `nodes` (one ring per node) with a parallel-
    // transported radial frame (no twist) and OUTWARD-facing winding. Hard
    // corners at the nodes read as kinks. Jaggedness perturbs each ring radially.
    private void EmitTube(SurfaceTool st, List<Vector3> nodes, float[] radii, bool capBase, float seed)
    {
        int n = nodes.Count - 1;
        if (n < 1)
        {
            return;
        }
        int radial = Math.Max(3, RadialSegments);

        // Per-node tangent = miter of the adjacent run directions.
        Vector3[] tan = new Vector3[n + 1];
        for (int k = 0; k <= n; k++)
        {
            Vector3 inDir = k > 0 ? (nodes[k] - nodes[k - 1]).Normalized() : Vector3.Zero;
            Vector3 outDir = k < n ? (nodes[k + 1] - nodes[k]).Normalized() : Vector3.Zero;
            Vector3 t = inDir + outDir;
            tan[k] = t.LengthSquared() > 1e-8f ? t.Normalized() : (k < n ? outDir : inDir);
        }

        // Parallel-transport a radial normal so the tube doesn't twist.
        Vector3[] nrm = new Vector3[n + 1];
        Vector3[] bin = new Vector3[n + 1];
        Vector3 refUp0 = MathF.Abs(tan[0].Y) > 0.9f ? Vector3.Right : Vector3.Up;
        Vector3 nCur = tan[0].Cross(refUp0).Cross(tan[0]).Normalized();
        for (int k = 0; k <= n; k++)
        {
            if (k > 0)
            {
                Vector3 axis = tan[k - 1].Cross(tan[k]);
                float sinA = axis.Length();
                if (sinA > 1e-5f)
                {
                    float ang = Mathf.Atan2(sinA, tan[k - 1].Dot(tan[k]));
                    nCur = nCur.Rotated(axis / sinA, ang);
                }
            }
            nCur = (nCur - tan[k] * nCur.Dot(tan[k])).Normalized();
            nrm[k] = nCur;
            bin[k] = nrm[k].Cross(tan[k]);   // (N, B) match the old (worldX, worldZ) for a vertical tube
        }

        float cumLen = 0f;
        for (int k = 0; k < n; k++)
        {
            float vA = cumLen;
            cumLen += (nodes[k + 1] - nodes[k]).Length();
            float vB = cumLen;
            for (int s = 0; s < radial; s++)
            {
                float a0 = (float)s / radial * Mathf.Tau;
                float a1 = (float)(s + 1) / radial * Mathf.Tau;

                Vector3 v00 = TubeVertex(nodes[k], nrm[k], bin[k], radii[k], a0, k, s, seed);
                Vector3 v10 = TubeVertex(nodes[k], nrm[k], bin[k], radii[k], a1, k, s + 1, seed);
                Vector3 v01 = TubeVertex(nodes[k + 1], nrm[k + 1], bin[k + 1], radii[k + 1], a0, k + 1, s, seed);
                Vector3 v11 = TubeVertex(nodes[k + 1], nrm[k + 1], bin[k + 1], radii[k + 1], a1, k + 1, s + 1, seed);

                float u0 = (float)s / radial;
                float u1 = (float)(s + 1) / radial;
                // Outward-facing winding (front faces point out of the trunk).
                AddTri(st, v00, new Vector2(u0, vA), v11, new Vector2(u1, vB), v01, new Vector2(u0, vB));
                AddTri(st, v00, new Vector2(u0, vA), v10, new Vector2(u1, vA), v11, new Vector2(u1, vB));
            }
        }

        if (capBase)
        {
            Vector2 cuv = new Vector2(0.5f, 0.5f);
            for (int s = 0; s < radial; s++)
            {
                float a0 = (float)s / radial * Mathf.Tau;
                float a1 = (float)(s + 1) / radial * Mathf.Tau;
                Vector3 e0 = TubeVertex(nodes[0], nrm[0], bin[0], radii[0], a0, 0, s, seed);
                Vector3 e1 = TubeVertex(nodes[0], nrm[0], bin[0], radii[0], a1, 0, s + 1, seed);
                Vector2 uv0 = new Vector2(0.5f + 0.5f * Mathf.Cos(a0), 0.5f + 0.5f * Mathf.Sin(a0));
                Vector2 uv1 = new Vector2(0.5f + 0.5f * Mathf.Cos(a1), 0.5f + 0.5f * Mathf.Sin(a1));
                AddTri(st, nodes[0], cuv, e0, uv0, e1, uv1);
            }
        }
    }

    private Vector3 TubeVertex(Vector3 center, Vector3 nrm, Vector3 bin, float radius, float angle, int ring, int seg, float seed)
    {
        float rr = radius;
        if (Jaggedness > 0f)
        {
            float h = Hash2(ring * 0.731f + seed, seg * 1.137f);
            rr *= 1f + Jaggedness * (h * 2f - 1f);
        }
        return center + (nrm * Mathf.Cos(angle) + bin * Mathf.Sin(angle)) * rr;
    }

    private static void AddTri(SurfaceTool st, Vector3 a, Vector2 ua, Vector3 b, Vector2 ub, Vector3 c, Vector2 uc)
    {
        st.SetUV(ua);
        st.AddVertex(a);
        st.SetUV(ub);
        st.AddVertex(b);
        st.SetUV(uc);
        st.AddVertex(c);
    }

    // -- Twigs -------------------------------------------------------------

    private void RebuildTwigs(List<Tip> tips, ShaderMaterial twigsRuntimeMat)
    {
        Node3D container = GetOrCreateWoodyContainer();
        foreach (Node existing in container.GetChildren())
        {
            container.RemoveChild(existing);
            existing.QueueFree();
        }

        if (TwigsMesh == null || twigsRuntimeMat == null)
        {
            return;
        }

        foreach (Tip tip in tips)
        {
            Basis basis = BasisAlignY(tip.Dir);
            // Random roll about the branch axis so adjacent tips don't align;
            // seed from the tip position for stability across rebuilds.
            int twigSeed = unchecked((int)(tip.Position.X * 73856093f) ^ (int)(tip.Position.Y * 19349663f) ^ (int)(tip.Position.Z * 83492791f));
            RandomNumberGenerator twigRng = new RandomNumberGenerator { Seed = unchecked((ulong)twigSeed) };
            float roll = twigRng.Randf() * Mathf.Tau;
            Basis twigsBasis = basis.Rotated(tip.Dir, roll).Scaled(new Vector3(TwigsSize, TwigsSize, TwigsSize));
            // Pull the quad pivot back along the branch axis so the texture's
            // visible content meets the branch end.
            Vector3 origin = tip.Position - tip.Dir * (TwigsAttachInset * TwigsSize);
            MeshInstance3D twigs = new MeshInstance3D
            {
                Mesh = TwigsMesh,
                Transform = new Transform3D(twigsBasis, origin),
                MaterialOverride = twigsRuntimeMat,
            };
            container.AddChild(twigs);
        }
    }

    private Node3D GetOrCreateWoodyContainer()
    {
        Node existing = GetNodeOrNull(WoodyContainerName);
        if (existing is Node3D existingNode3D)
        {
            return existingNode3D;
        }
        if (existing != null)
        {
            RemoveChild(existing);
            existing.QueueFree();
        }
        // Owner left null so the generated twigs are excluded from the saved
        // .tscn (a Rebuild doesn't bloat the scene file).
        Node3D container = new Node3D { Name = WoodyContainerName };
        AddChild(container);
        return container;
    }

    private ShaderMaterial BuildTwigsMaterial(Vector3 treeOrigin, float canopyHeight)
    {
        if (TwigsMesh == null || TwigsTexture == null)
        {
            return null;
        }
        ShaderMaterial template = GD.Load<ShaderMaterial>(TwigsMaterialTemplatePath);
        if (template == null)
        {
            GD.PushError($"TreeTrunk: missing twigs material template at {TwigsMaterialTemplatePath}");
            return null;
        }
        ShaderMaterial mat = (ShaderMaterial)template.Duplicate();
        mat.SetShaderParameter("albedo_tex", TwigsTexture);
        mat.SetShaderParameter("tree_origin", treeOrigin);
        mat.SetShaderParameter("canopy_height", canopyHeight);
        return mat;
    }

    // -- Helpers -----------------------------------------------------------

    private List<FoliageCluster> CollectClusters(FoliageMultiMesh foliage)
    {
        List<FoliageCluster> clusters = new List<FoliageCluster>();
        if (foliage == null)
        {
            return clusters;
        }
        foreach (Node child in foliage.GetChildren())
        {
            if (child is FoliageCluster cluster)
            {
                clusters.Add(cluster);
            }
        }
        return clusters;
    }

    private float ComputeCanopyHeight(List<FoliageCluster> clusters, Vector3 treeOrigin)
    {
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
        return Mathf.Max(canopyTopWorldY - treeOrigin.Y, 0.5f);
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

    private T FindSiblingOrChild<T>(string name) where T : Node
    {
        if (GetNodeOrNull(name) is T child)
        {
            return child;
        }
        Node current = GetParent();
        while (current != null)
        {
            if (current is PropInstance)
            {
                return current.GetNodeOrNull<T>(name);
            }
            current = current.GetParent();
        }
        return null;
    }

    // Right-handed basis whose Y axis points along `dir`.
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

    // 3D hash -> [0,1). Same construction as the shaders' hash13.
    private static float Hash13(Vector3 p)
    {
        p = Fract(p * new Vector3(0.1031f, 0.1030f, 0.0973f));
        float d = p.Dot(new Vector3(p.Y, p.Z, p.X) + new Vector3(33.33f, 33.33f, 33.33f));
        p += new Vector3(d, d, d);
        return Frac((p.X + p.Y) * p.Z);
    }

    private static float Hash2(float x, float y)
    {
        float h = Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f;
        return h - Mathf.Floor(h);
    }

    private static float ValueNoise1(float x)
    {
        float i = Mathf.Floor(x);
        float f = x - i;
        f = f * f * (3f - 2f * f);
        float a = Hash2(i, 0.5f);
        float b = Hash2(i + 1f, 0.5f);
        return Mathf.Lerp(a, b, f);
    }

    private static Vector3 Fract(Vector3 v)
    {
        return new Vector3(Frac(v.X), Frac(v.Y), Frac(v.Z));
    }

    private static float Frac(float v)
    {
        return v - Mathf.Floor(v);
    }
}
