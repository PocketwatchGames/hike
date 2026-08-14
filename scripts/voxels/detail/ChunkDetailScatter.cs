using System;
using System.Collections.Generic;
using Godot;

// Builds MultiMeshInstance3D children for one chunk's painted detail sprites.
//
// One MultiMesh is emitted per (chunk, DetailEntry) — i.e. one draw call per
// sprite type per chunk, regardless of instance count. Hundreds of grass
// blades in a chunk cost the same draw-call budget as one. Per-instance:
//   - position    : voxel center + sub-voxel jitter, sitting on top of the
//                   solid voxel that owns the DetailGroup paint
//   - basis       : non-uniform scale (tex_width_world, tex_height_world, 1)
//                   * per-instance random ScaleMin..ScaleMax multiplier. The
//                   shader reads scale_x / scale_y separately so texture
//                   aspect drives the sprite shape automatically.
//   - custom data : (normal.xyz, porosity) — world-space surface normal
//                   estimated from the painted voxel's neighbour heights. The
//                   shader projects this onto the screen plane and uses it as
//                   the sprite's up axis so blades on a slope lean with the
//                   slope when viewed across the slope, and read as upright
//                   when viewed along the slope. The .w channel carries the
//                   ground tile's wetness porosity (BlockSurfaceData.Porosity) so the
//                   sprite's wet darken scales by the same fraction as the
//                   ground beneath it.
//   - color       : (r,g,b,1) — ground color of the solid voxel under the
//                   sprite (a terrain's load-computed flat-tile average, or
//                   the block's own top surface). The
//                   shader pulls sprite pixels toward this where the entry's
//                   tint map paints its G channel, so blades read as rooted in
//                   the ground instead of floating.
//
// All scatter inputs (placement, weighted entry pick, scale) hash from
// (chunkCoord, x, y, z, slot) so the same chunk re-scattered produces the
// same layout — chunk reload doesn't shuffle the field.
public static class ChunkDetailScatter
{
    // Per-painted-voxel candidate slots are bounded by DetailGroupData.
    // InstancesPerVoxel; each slot rolls a hash against DetailStrength/255.

    // World-to-texture-pixel ratio shared by every detail sprite. 16 matches
    // the voxel tile grid (gen_voxel_tiles.py TILE = 16) so sprites sit at
    // the same pixel density as the terrain they scatter over. Changing this
    // resizes every scatter uniformly.
    public const float PIXELS_PER_UNIT = 16f;

    // ±range scanned per neighbour column when estimating surface height for
    // the normal. Covers single-voxel ledges; larger steps are treated as
    // cliffs and the column's height is left as-is (returns wy unchanged),
    // which is the right behaviour for grass at a cliff edge — we don't want
    // it leaning hard down the cliff face.
    private const int NORMAL_SCAN_RANGE = 2;

    // Compute the per-DetailEntry instance contributions for one chunk. Pure
    // function — no scene graph mutation. Caller submits the result to
    // WorldDetailScatter, which composes contributions from all chunks into
    // one MultiMesh per entry. Returns null if the chunk paints no detail.
    public static Dictionary<DetailEntry, List<InstanceData>> Compute(
        ChunkState data,
        System.Func<int, int, int, int> getVoxel,
        DetailGroupData[] groups)
    {
        if (groups == null || groups.Length == 0)
        {
            return null;
        }

        // Bucket per DetailEntry so each (mesh, material) becomes one MultiMesh
        // entry contribution. WorldDetailScatter aggregates buckets across
        // chunks into one MultiMesh per entry.
        var buckets = new Dictionary<DetailEntry, List<InstanceData>>();

        // Weighted-pick palette reused per voxel (refilled from each voxel's
        // group) so the scatter doesn't allocate per voxel. Clear() keeps the
        // backing buffer between voxels.
        var palette = new WeightedList<DetailEntry>();

        Vector3I chunkCoord = data.ChunkCoord;
        int chunkWx = chunkCoord.X * ChunkState.SIZE;
        int chunkWy = chunkCoord.Y * ChunkState.SIZE;
        int chunkWz = chunkCoord.Z * ChunkState.SIZE;

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    int groupId1Based = data.DetailGroup[x, y, z];
                    if (groupId1Based == 0)
                    {
                        continue;
                    }
                    int groupIdx = groupId1Based - 1;
                    if (groupIdx < 0 || groupIdx >= groups.Length)
                    {
                        continue;
                    }
                    DetailGroupData group = groups[groupIdx];
                    if (group == null || group.entries == null || group.entries.Count == 0)
                    {
                        continue;
                    }

                    int strength = data.DetailStrength[x, y, z];
                    if (strength == 0)
                    {
                        continue;
                    }

                    // Hold sprites back from cliff lips and off narrow shelves.
                    // Runs before the normal / AO / tint work below so a
                    // rejected voxel skips all of it.
                    float edgeFactor = ComputeEdgeFactor(chunkWx + x, chunkWy + y, chunkWz + z, group, getVoxel);
                    strength = (int)(strength * edgeFactor);
                    if (strength <= 0)
                    {
                        continue;
                    }

                    // Estimate the surface normal from neighbour heights once
                    // per painted voxel; all instances scattered on this voxel
                    // share the same normal. The shader uses this to roll
                    // sprites in their billboard plane so they lean with the
                    // slope when viewed across it.
                    Vector3 normal = ComputeSurfaceNormal(chunkWx + x, chunkWy + y, chunkWz + z, getVoxel);

                    // Baked hemisphere AO for the painted voxel, mirroring the
                    // mesher's per-vertex bake (ChunkMesherDC.ComputeAo) so a
                    // blade shelter-darkens in lockstep with the ground it sits
                    // on. Shared by all instances on this voxel; carried per
                    // instance in the MultiMesh color alpha and applied by
                    // detail_sprite.gdshader's ao_factor. 0 = open (no change).
                    float ao = ComputeAo(chunkWx + x, chunkWy + y, chunkWz + z, normal, getVoxel);

                    // Ground tint for rooting the sprite's base visually. All
                    // instances on this voxel share it — the mean colour of the
                    // block's own top surface, so a blade always matches the
                    // ground it grows out of.
                    int voxelType = getVoxel(chunkWx + x, chunkWy + y, chunkWz + z);
                    Color groundTint = ChunkMesh.GroundTintFor(voxelType);

                    // The visible ground under the sprite is the OVERLAY tile
                    // (dirt path, clover, moss, etc.) wherever one is painted —
                    // not the terrain's flat tile. OverlayId is a direct
                    // tile_array layer index (0 = none), so pull the sprite root
                    // toward that tile's average instead. This makes grass roots
                    // match the actual ground they sit on (paths/overlays), and
                    // is per-voxel so it's correct even where detail of one biome
                    // scatters over a voxel of another.
                    int overlayId = data.GetOverlayId(x, y, z);
                    if (overlayId != 0 && ChunkMesh.TryGetLayerAverageLinear(overlayId, out Color overlayTint))
                    {
                        groundTint = overlayTint;
                    }

                    // Wetness porosity of the ground beneath the sprite — the
                    // SAME BlockSurfaceData.Porosity the terrain shader folds into its
                    // wet darken (wet_dark = saturation * porosity). Resolved in
                    // lockstep with groundTint above (overlay > terrain FlatTile >
                    // authored voxel tile). The detail shader multiplies its
                    // wet_factor by this so a wet blade darkens by the same
                    // fraction as the ground it's rooted in; without it sprites
                    // darken at full strength (porosity 1) and read too dark over
                    // the same wet terrain (which only darkens by its porosity).
                    float groundPorosity = ResolveGroundPorosity(
                        voxelType, chunkWx + x, chunkWy + y, chunkWz + z, overlayId);

                    palette.Clear();
                    for (int e = 0; e < group.entries.Count; e++)
                    {
                        DetailEntry de = group.entries[e];
                        if (de != null)
                        {
                            palette.Add(de, de.weight);
                        }
                    }
                    if (palette.TotalWeight <= 0f)
                    {
                        continue;
                    }

                    int slots = group.instancesPerVoxel;
                    for (int slot = 0; slot < slots; slot++)
                    {
                        // 4 independent sub-rolls per slot. Hash inputs include
                        // a role byte so each roll has its own bit pattern;
                        // sharing one hash across all rolls produces visible
                        // correlations (e.g. tall sprites always near +X edge).
                        uint keepRoll = Hash(chunkCoord, x, y, z, slot, 0) & 0xFF;
                        if (keepRoll >= (uint)strength)
                        {
                            continue;
                        }

                        uint entryRoll = Hash(chunkCoord, x, y, z, slot, 1);
                        float roll = ((entryRoll & 0xFFFFFF) / (float)0xFFFFFF) * palette.TotalWeight;
                        DetailEntry entry = palette.Choose(roll);
                        if (entry == null || entry.texture == null)
                        {
                            continue;
                        }

                        uint posRoll = Hash(chunkCoord, x, y, z, slot, 2);
                        uint shapeRoll = Hash(chunkCoord, x, y, z, slot, 3);

                        // Jitter within the voxel footprint, with a small inset
                        // so sprites don't pile up exactly on chunk seams where
                        // a neighbour chunk's scatter would visually clash.
                        const float INSET = 0.1f;
                        const float SPAN = 1f - 2f * INSET;
                        float jx = INSET + ((posRoll & 0xFFFF) / 65535f) * SPAN;
                        float jz = INSET + (((posRoll >> 16) & 0xFFFF) / 65535f) * SPAN;

                        float scaleT = (shapeRoll & 0xFFFF) / 65535f;
                        float scaleMult = Mathf.Lerp(entry.scaleMin, entry.scaleMax, scaleT);

                        // Per-instance world size comes from the texture's
                        // pixel dimensions divided by PIXELS_PER_UNIT, so
                        // trimming a PNG shrinks the sprite automatically.
                        // ScaleMult is the per-instance ScaleMin..ScaleMax
                        // roll — layered on as a uniform multiplier.
                        float worldW = entry.texture.GetWidth() / PIXELS_PER_UNIT * scaleMult;
                        float worldH = entry.texture.GetHeight() / PIXELS_PER_UNIT * scaleMult;

                        // Sit on top of the solid voxel. y+1 is the air-voxel
                        // floor. Transforms are emitted in WORLD space — the
                        // global WorldDetailScatter manager places its
                        // MultiMeshInstance3Ds at world origin, so chunks
                        // can't rely on a parent's world offset.
                        var worldPos = new Vector3(chunkWx + x + jx, chunkWy + y + 1f, chunkWz + z + jz);
                        var basis = Basis.Identity.Scaled(new Vector3(worldW, worldH, 1f));
                        var transform = new Transform3D(basis, worldPos);

                        if (!buckets.TryGetValue(entry, out List<InstanceData> list))
                        {
                            list = new List<InstanceData>();
                            buckets[entry] = list;
                        }
                        list.Add(new InstanceData { Transform = transform, Normal = normal, GroundTint = groundTint, Porosity = groundPorosity, Ao = ao });
                    }
                }
            }
        }

        return buckets.Count > 0 ? buckets : null;
    }

    // Underlying ground porosity for one painted voxel, mirroring
    // GroundTypeResolver's BlockSurfaceData resolution order (overlay > terrain
    // FlatTile > authored voxel tile) so the value matches the porosity the
    // terrain shader reads for the same surface. Defaults to BlockSurfaceData's 0.5
    // when no block resolves.
    private static float ResolveGroundPorosity(
        int voxelType, int wx, int wy, int wz,
        int overlayId)
    {
        const float DEFAULT_POROSITY = 0.5f;
        if (overlayId != 0)
        {
            BlockSurfaceData overlay = BlockCatalog.Active.GetSurfaceByLayer(overlayId);
            if (overlay != null)
            {
                return overlay.porosity;
            }
        }

        BlockSurfaceData top = BlockCatalog.Active.GetById(voxelType)?.SurfaceFor(EBlockFace.Top);
        return top != null ? top.porosity : DEFAULT_POROSITY;
    }

    // FNV-1a 32-bit over the input bytes. Cheap and produces visually
    // uncorrelated outputs across adjacent inputs, which is all we need —
    // not a cryptographic hash.
    private static uint Hash(Vector3I chunkCoord, int x, int y, int z, int slot, int role)
    {
        uint h = 2166136261u;
        h = Mix(h, (uint)chunkCoord.X);
        h = Mix(h, (uint)chunkCoord.Y);
        h = Mix(h, (uint)chunkCoord.Z);
        h = Mix(h, (uint)x);
        h = Mix(h, (uint)y);
        h = Mix(h, (uint)z);
        h = Mix(h, (uint)slot);
        h = Mix(h, (uint)role);
        return h;
    }

    private static uint Mix(uint h, uint v)
    {
        h ^= v & 0xFFu;
        h *= 16777619u;
        h ^= (v >> 8) & 0xFFu;
        h *= 16777619u;
        h ^= (v >> 16) & 0xFFu;
        h *= 16777619u;
        h ^= (v >> 24) & 0xFFu;
        h *= 16777619u;
        return h;
    }

    // Hemisphere occlusion at a painted voxel, the scatter-side analog of
    // ChunkMesherDC.ComputeAo (which bakes terrain AO into COLOR.a). Marches the
    // outward-normal hemisphere from the air voxel above the surface and stops
    // at the first solid within AO_STEPS, cosine-weighted by facing. Returns
    // [0,1]: 0 = open flat ground (no occluder above → grass unaffected, matches
    // the terrain), higher under canopy / overhangs / against tall neighbours.
    // Uses int solidity (not the mesher's binary density field), which is
    // the same surface the scatter already queries — close enough since AO is
    // low-frequency and only needs to track the ground beneath the blade.
    private const int AO_STEPS = 2;
    private const float AO_MIN_FACING = 0.1f;
    private static readonly Vector3[] AoDirs =
    {
        new Vector3( 1, 0, 0), new Vector3(-1, 0, 0),
        new Vector3( 0, 1, 0), new Vector3( 0,-1, 0),
        new Vector3( 0, 0, 1), new Vector3( 0, 0,-1),
        new Vector3( 0.57735026f,  0.57735026f,  0.57735026f), new Vector3(-0.57735026f,  0.57735026f,  0.57735026f),
        new Vector3( 0.57735026f, -0.57735026f,  0.57735026f), new Vector3(-0.57735026f, -0.57735026f,  0.57735026f),
        new Vector3( 0.57735026f,  0.57735026f, -0.57735026f), new Vector3(-0.57735026f,  0.57735026f, -0.57735026f),
        new Vector3( 0.57735026f, -0.57735026f, -0.57735026f), new Vector3(-0.57735026f, -0.57735026f, -0.57735026f),
    };

    private static float ComputeAo(int wx, int wy, int wz, Vector3 n, System.Func<int, int, int, int> getVoxel)
    {
        // Sample from the air voxel that hosts the sprite (one above the solid
        // surface), so the march never immediately hits the voxel the blade
        // grows out of.
        var p = new Vector3(wx, wy + 1, wz);
        float occ = 0f;
        float totalW = 0f;
        for (int i = 0; i < AoDirs.Length; i++)
        {
            Vector3 d = AoDirs[i];
            float nd = d.Dot(n);
            if (nd < AO_MIN_FACING)
            {
                continue;
            }
            totalW += nd;
            for (int step = 1; step <= AO_STEPS; step++)
            {
                Vector3 sp = p + d * step;
                if (Blocks.IsSolid(getVoxel(Mathf.RoundToInt(sp.X), Mathf.RoundToInt(sp.Y), Mathf.RoundToInt(sp.Z))))
                {
                    occ += nd * (1f - (float)(step - 1) / AO_STEPS);
                    break;
                }
            }
        }
        return totalW > 1e-5f ? Mathf.Clamp(occ / totalW, 0f, 1f) : 0f;
    }

    // Central-difference surface normal at world (wx, wy, wz). Looks at the
    // four cardinal neighbour columns, finds each one's surface height within
    // ±NORMAL_SCAN_RANGE of wy, and forms the gradient. Columns with no
    // surface in range fall back to wy, which produces a flat-direction
    // contribution — correct for cliff edges where we want grass to read as
    // perpendicular to the local surface, not leaning hard down the cliff.
    private static Vector3 ComputeSurfaceNormal(int wx, int wy, int wz, System.Func<int, int, int, int> getVoxel)
    {
        float yE = SurfaceYAt(wx + 1, wy, wz, getVoxel);
        float yW = SurfaceYAt(wx - 1, wy, wz, getVoxel);
        float yN = SurfaceYAt(wx, wy, wz + 1, getVoxel);
        float yS = SurfaceYAt(wx, wy, wz - 1, getVoxel);
        float dyx = (yE - yW) * 0.5f;
        float dyz = (yN - yS) * 0.5f;
        return new Vector3(-dyx, 1f, -dyz).Normalized();
    }

    // Search outward from wy for the first solid voxel with air directly above
    // — that voxel's y is the column's surface height. ±NORMAL_SCAN_RANGE
    // tolerates single-voxel ledges; beyond that we treat the column as a
    // cliff and return wy unchanged.
    private static float SurfaceYAt(int wx, int wy, int wz, System.Func<int, int, int, int> getVoxel)
    {
        for (int radius = 0; radius <= NORMAL_SCAN_RANGE; radius++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                if (radius == 0 && sign == -1) { continue; }
                int y = wy + sign * radius;
                if (Blocks.IsSolid(getVoxel(wx, y, wz)) && !Blocks.IsSolid(getVoxel(wx, y + 1, wz)))
                {
                    return y;
                }
            }
        }
        return wy;
    }

    // Density multiplier for one painted voxel from the cliff geometry around
    // it: 1 on open ground, ramping down to 0 at a cliff lip, and a hard 0 for
    // a voxel the group won't accept at all (the lip column itself, or a shelf
    // narrower than MinLedgeWidthVoxels).
    //
    // Walks the four cardinal directions outward, carrying the ground level
    // with it, so a long grade stays continuous and only a real step ends a run.
    private static float ComputeEdgeFactor(int wx, int wy, int wz, DetailGroupData group, System.Func<int, int, int, int> getVoxel)
    {
        int step = group.cliffStepVoxels;
        int setback = group.edgeSetbackVoxels;
        int minWidth = group.minLedgeWidthVoxels;
        if (step <= 0 || (setback <= 0 && minWidth <= 1))
        {
            return 1f;
        }
        int maxSteps = Math.Max(setback, minWidth - 1);

        int runE = GroundRun(wx, wy, wz, 1, 0, step, maxSteps, getVoxel, out bool dropE);
        int runW = GroundRun(wx, wy, wz, -1, 0, step, maxSteps, getVoxel, out bool dropW);
        int runN = GroundRun(wx, wy, wz, 0, 1, step, maxSteps, getVoxel, out bool dropN);
        int runS = GroundRun(wx, wy, wz, 0, -1, step, maxSteps, getVoxel, out bool dropS);

        // Narrow ground is only a LEDGE when a drop bounds it. Ground pinched
        // between two walls is a corridor or a cave passage and keeps its
        // detail; the same width with a drop on one side is the 1m shelf
        // sticking out of a cliff face that we want bare.
        if (minWidth > 1
            && ((runE + runW + 1 < minWidth && (dropE || dropW))
                || (runN + runS + 1 < minWidth && (dropN || dropS))))
        {
            return 0f;
        }

        if (setback <= 0)
        {
            return 1f;
        }
        // Undropped runs (walls, or the probe cap) leave the distance beyond the
        // setback, which reads as full density.
        int edgeDist = maxSteps + 1;
        if (dropE) { edgeDist = Math.Min(edgeDist, runE); }
        if (dropW) { edgeDist = Math.Min(edgeDist, runW); }
        if (dropN) { edgeDist = Math.Min(edgeDist, runN); }
        if (dropS) { edgeDist = Math.Min(edgeDist, runS); }
        return Mathf.Clamp(edgeDist / (float)setback, 0f, 1f);
    }

    // How many columns can be stepped onto from (wx, wy, wz) heading (dx, dz)
    // before the ground breaks, capped at maxSteps (0 = the immediate neighbour
    // already breaks). `dropped` says which way it broke: true = the ground fell
    // away (a cliff lip), false = a wall rose in front of it or the cap was hit.
    private static int GroundRun(int wx, int wy, int wz, int dx, int dz, int step, int maxSteps,
        System.Func<int, int, int, int> getVoxel, out bool dropped)
    {
        int refY = wy;
        for (int i = 1; i <= maxSteps; i++)
        {
            int nx = wx + dx * i;
            int nz = wz + dz * i;
            if (TryColumnSurface(nx, refY, nz, step, getVoxel, out int surfaceY))
            {
                refY = surfaceY;
                continue;
            }
            // No surface in the band: solid at the reference level means the
            // column carries on upward (a wall), otherwise it fell away.
            dropped = !Blocks.IsSolid(getVoxel(nx, refY, nz));
            return i - 1;
        }
        dropped = false;
        return maxSteps;
    }

    // Surface height (highest solid voxel with a non-solid one above it) of the
    // column at (wx, wz) within ±(step - 1) of refY — the band that still counts
    // as continuous ground. False when the column has no surface in that band.
    // Water is non-solid, so a shallow shore reads as continuous off its floor
    // and only a genuine drop-off into deep water counts as an edge.
    private static bool TryColumnSurface(int wx, int refY, int wz, int step,
        System.Func<int, int, int, int> getVoxel, out int surfaceY)
    {
        for (int y = refY + step - 1; y >= refY - step + 1; y--)
        {
            if (Blocks.IsSolid(getVoxel(wx, y, wz)) && !Blocks.IsSolid(getVoxel(wx, y + 1, wz)))
            {
                surfaceY = y;
                return true;
            }
        }
        surfaceY = refY;
        return false;
    }

    public struct InstanceData
    {
        public Transform3D Transform;
        public Vector3 Normal;
        public Color GroundTint;
        // Porosity of the ground tile under the sprite (BlockSurfaceData.Porosity).
        // Packed into the MultiMesh per-instance custom data's .w channel
        // (INSTANCE_CUSTOM.w) and folded into the detail shader's wet darken.
        public float Porosity;
        // Baked hemisphere occlusion (0 = open, 1 = sheltered). Packed into the
        // MultiMesh per-instance color alpha (INSTANCE_COLOR.a) and applied by
        // the detail shader's ao_factor, mirroring the terrain's COLOR.a AO.
        public float Ao;
    }
}
