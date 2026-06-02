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
//                   ground tile's wetness porosity (BlockData.Porosity) so the
//                   sprite's wet darken scales by the same fraction as the
//                   ground beneath it.
//   - color       : (r,g,b,1) — ground color of the solid voxel under the
//                   sprite (a terrain's load-computed flat-tile average, or
//                   VoxelTypeInfo.GroundTint for authored-override types). The
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
        System.Func<int, int, int, VoxelType> getVoxel,
        System.Func<int, int, int, int> getTerrainId,
        DetailGroupData[] groups,
        TerrainData[] terrains)
    {
        if (groups == null || groups.Length == 0)
        {
            return null;
        }

        // Bucket per DetailEntry so each (mesh, material) becomes one MultiMesh
        // entry contribution. WorldDetailScatter aggregates buckets across
        // chunks into one MultiMesh per entry.
        var buckets = new Dictionary<DetailEntry, List<InstanceData>>();

        // Cumulative-weight scratch reused per group so we don't re-allocate
        // per voxel. Sized lazily to the largest group seen.
        float[] cumulativeWeights = null;

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
                    if (group == null || group.Entries == null || group.Entries.Count == 0)
                    {
                        continue;
                    }

                    int strength = data.DetailStrength[x, y, z];
                    if (strength == 0)
                    {
                        continue;
                    }

                    // Estimate the surface normal from neighbour heights once
                    // per painted voxel; all instances scattered on this voxel
                    // share the same normal. The shader uses this to roll
                    // sprites in their billboard plane so they lean with the
                    // slope when viewed across it.
                    Vector3 normal = ComputeSurfaceNormal(chunkWx + x, chunkWy + y, chunkWz + z, getVoxel);

                    // Ground tint for rooting the sprite's base visually. All
                    // instances on this voxel share the same tint. AUTO-Terrain
                    // voxels inherit their kit's GroundTint (because the actual
                    // rendered tile comes from the kit's FlatTile, not from the
                    // VoxelType); authored-override types and TerrainPath keep
                    // the fixed VoxelType tint (TerrainPath is always dirt in
                    // the shader, independent of kit).
                    VoxelType voxelType = getVoxel(chunkWx + x, chunkWy + y, chunkWz + z);
                    Color groundTint;
                    if (voxelType == VoxelType.Terrain && terrains != null)
                    {
                        int TerrainId = getTerrainId(chunkWx + x, chunkWy + y, chunkWz + z);
                        if (TerrainId >= 0 && TerrainId < terrains.Length && terrains[TerrainId] != null)
                        {
                            groundTint = terrains[TerrainId].GroundTint;
                        }
                        else
                        {
                            groundTint = VoxelTypeInfo.GetGroundTint(voxelType);
                        }
                    }
                    else
                    {
                        groundTint = VoxelTypeInfo.GetGroundTint(voxelType);
                    }

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
                    // SAME BlockData.Porosity the terrain shader folds into its
                    // wet darken (wet_dark = saturation * porosity). Resolved in
                    // lockstep with groundTint above (overlay > terrain FlatTile >
                    // authored voxel tile). The detail shader multiplies its
                    // wet_factor by this so a wet blade darkens by the same
                    // fraction as the ground it's rooted in; without it sprites
                    // darken at full strength (porosity 1) and read too dark over
                    // the same wet terrain (which only darkens by its porosity).
                    float groundPorosity = ResolveGroundPorosity(
                        voxelType, chunkWx + x, chunkWy + y, chunkWz + z, getTerrainId, overlayId, terrains);

                    EnsureCumulativeWeights(group, ref cumulativeWeights, out float totalWeight);
                    if (totalWeight <= 0f)
                    {
                        continue;
                    }

                    int slots = group.InstancesPerVoxel;
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
                        DetailEntry entry = PickEntry(group, cumulativeWeights, totalWeight, entryRoll);
                        if (entry == null || entry.Texture == null)
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
                        float scaleMult = Mathf.Lerp(entry.ScaleMin, entry.ScaleMax, scaleT);

                        // Per-instance world size comes from the texture's
                        // pixel dimensions divided by PIXELS_PER_UNIT, so
                        // trimming a PNG shrinks the sprite automatically.
                        // ScaleMult is the per-instance ScaleMin..ScaleMax
                        // roll — layered on as a uniform multiplier.
                        float worldW = entry.Texture.GetWidth() / PIXELS_PER_UNIT * scaleMult;
                        float worldH = entry.Texture.GetHeight() / PIXELS_PER_UNIT * scaleMult;

                        // Sit on top of the solid voxel. y+1 is the air-voxel
                        // floor. Transforms are emitted in WORLD space — the
                        // global WorldDetailScatter manager places its
                        // MultiMeshInstance3Ds at world origin, so chunks
                        // can no longer rely on a parent's world offset
                        // (the prior per-chunk attach had MultiMeshInstance3D
                        // as a child of ChunkMesh, whose Position was the
                        // chunk world origin).
                        var worldPos = new Vector3(chunkWx + x + jx, chunkWy + y + 1f, chunkWz + z + jz);
                        var basis = Basis.Identity.Scaled(new Vector3(worldW, worldH, 1f));
                        var transform = new Transform3D(basis, worldPos);

                        if (!buckets.TryGetValue(entry, out List<InstanceData> list))
                        {
                            list = new List<InstanceData>();
                            buckets[entry] = list;
                        }
                        list.Add(new InstanceData { Transform = transform, Normal = normal, GroundTint = groundTint, Porosity = groundPorosity });
                    }
                }
            }
        }

        return buckets.Count > 0 ? buckets : null;
    }

    // Underlying ground porosity for one painted voxel, mirroring
    // GroundTypeResolver's BlockData resolution order (overlay > terrain
    // FlatTile > authored voxel tile) so the value matches the porosity the
    // terrain shader reads for the same surface. Defaults to BlockData's 0.5
    // when no block resolves.
    private static float ResolveGroundPorosity(
        VoxelType voxelType, int wx, int wy, int wz,
        System.Func<int, int, int, int> getTerrainId, int overlayId, TerrainData[] terrains)
    {
        const float DEFAULT_POROSITY = 0.5f;
        BlockCatalog catalog = BlockCatalog.Active;
        if (catalog == null)
        {
            return DEFAULT_POROSITY;
        }

        if (overlayId != 0)
        {
            BlockData overlay = catalog.GetByAtlasIndex(overlayId);
            if (overlay != null)
            {
                return overlay.Porosity;
            }
        }

        if (voxelType == VoxelType.Terrain)
        {
            int terrainId = getTerrainId(wx, wy, wz);
            if (terrains != null && terrainId >= 0 && terrainId < terrains.Length
                && terrains[terrainId] != null && terrains[terrainId].FlatTile != null)
            {
                return terrains[terrainId].FlatTile.Porosity;
            }
            BlockData defaultFlat = catalog.DefaultFlatTile;
            return defaultFlat != null ? defaultFlat.Porosity : DEFAULT_POROSITY;
        }

        BlockData block = catalog.GetByAtlasIndex(VoxelTypeInfo.GetTileForFace(voxelType, 0));
        return block != null ? block.Porosity : DEFAULT_POROSITY;
    }

    private static void EnsureCumulativeWeights(DetailGroupData group, ref float[] scratch, out float total)
    {
        int n = group.Entries.Count;
        if (scratch == null || scratch.Length < n)
        {
            scratch = new float[n];
        }
        float running = 0f;
        for (int i = 0; i < n; i++)
        {
            DetailEntry e = group.Entries[i];
            float w = e != null ? Mathf.Max(0f, e.Weight) : 0f;
            running += w;
            scratch[i] = running;
        }
        total = running;
    }

    private static DetailEntry PickEntry(DetailGroupData group, float[] cumulative, float total, uint roll)
    {
        float r = ((roll & 0xFFFFFF) / (float)0xFFFFFF) * total;
        int n = group.Entries.Count;
        for (int i = 0; i < n; i++)
        {
            if (r <= cumulative[i])
            {
                return group.Entries[i];
            }
        }
        return group.Entries[n - 1];
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

    // Central-difference surface normal at world (wx, wy, wz). Looks at the
    // four cardinal neighbour columns, finds each one's surface height within
    // ±NORMAL_SCAN_RANGE of wy, and forms the gradient. Columns with no
    // surface in range fall back to wy, which produces a flat-direction
    // contribution — correct for cliff edges where we want grass to read as
    // perpendicular to the local surface, not leaning hard down the cliff.
    private static Vector3 ComputeSurfaceNormal(int wx, int wy, int wz, System.Func<int, int, int, VoxelType> getVoxel)
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
    private static float SurfaceYAt(int wx, int wy, int wz, System.Func<int, int, int, VoxelType> getVoxel)
    {
        for (int radius = 0; radius <= NORMAL_SCAN_RANGE; radius++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                if (radius == 0 && sign == -1) { continue; }
                int y = wy + sign * radius;
                if (VoxelTypeInfo.IsSolid(getVoxel(wx, y, wz)) && !VoxelTypeInfo.IsSolid(getVoxel(wx, y + 1, wz)))
                {
                    return y;
                }
            }
        }
        return wy;
    }

    public struct InstanceData
    {
        public Transform3D Transform;
        public Vector3 Normal;
        public Color GroundTint;
        // Porosity of the ground tile under the sprite (BlockData.Porosity).
        // Packed into the MultiMesh per-instance custom data's .w channel
        // (INSTANCE_CUSTOM.w) and folded into the detail shader's wet darken.
        public float Porosity;
    }
}
