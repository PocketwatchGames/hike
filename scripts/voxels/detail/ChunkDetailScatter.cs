using System.Collections.Generic;
using Godot;

// Builds MultiMeshInstance3D children for one chunk's painted detail sprites.
//
// One MultiMesh is emitted per (chunk, DetailEntry.Mesh) — i.e. one draw call
// per sprite type per chunk, regardless of instance count. Hundreds of grass
// blades in a chunk cost the same draw-call budget as one. Per-instance:
//   - position    : voxel center + sub-voxel jitter, sitting on top of the
//                   solid voxel that owns the DetailGroup paint
//   - basis       : random yaw (when DetailEntry.RandomYaw) + uniform scale jitter
//   - custom data : (normal.xyz, _) — world-space surface normal estimated
//                   from the painted voxel's neighbour heights. The shader
//                   projects this onto the screen plane and uses it as the
//                   sprite's up axis so blades on a slope lean with the
//                   slope when viewed across the slope, and read as upright
//                   when viewed along the slope.
//
// All scatter inputs (placement, weighted entry pick, scale, yaw) hash from
// (chunkCoord, x, y, z, slot) so the same chunk re-scattered produces the
// same layout — chunk reload doesn't shuffle the field.
public static class ChunkDetailScatter
{
    // Per-painted-voxel candidate slots are bounded by DetailGroupData.
    // InstancesPerVoxel; each slot rolls a hash against DetailStrength/255.

    // Stamped into MultiMesh names so they're identifiable in the remote scene
    // tree while debugging. Children are also tagged so a future eviction pass
    // could find and free them without touching the terrain meshes.
    private const string MULTIMESH_NAME_PREFIX = "DetailScatter_";

    // ±range scanned per neighbour column when estimating surface height for
    // the normal. Covers single-voxel ledges; larger steps are treated as
    // cliffs and the column's height is left as-is (returns wy unchanged),
    // which is the right behaviour for grass at a cliff edge — we don't want
    // it leaning hard down the cliff face.
    private const int NORMAL_SCAN_RANGE = 2;

    public static void Build(
        ChunkState data,
        System.Func<int, int, int, VoxelType> getVoxel,
        DetailGroupData[] groups,
        Node3D parent)
    {
        if (groups == null || groups.Length == 0)
        {
            return;
        }

        // Bucket per DetailEntry so each (mesh, material) becomes one MultiMesh
        // — one draw call per (chunk, entry). DetailEntry is the bucketing key
        // so weight/scale/yaw vary independently per entry.
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
                        if (entry == null || entry.Mesh == null)
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
                        float scale = Mathf.Lerp(entry.ScaleMin, entry.ScaleMax, scaleT);

                        float yaw = 0f;
                        if (entry.RandomYaw)
                        {
                            yaw = (((shapeRoll >> 16) & 0xFFFF) / 65535f) * Mathf.Tau;
                        }

                        // Sit on top of the solid voxel. y+1 is the air-voxel
                        // floor in chunk-local coords; the chunk node's
                        // Position already adds the world offset.
                        var localPos = new Vector3(x + jx, y + 1f, z + jz);
                        var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(scale, scale, scale));
                        var transform = new Transform3D(basis, localPos);

                        if (!buckets.TryGetValue(entry, out List<InstanceData> list))
                        {
                            list = new List<InstanceData>();
                            buckets[entry] = list;
                        }
                        list.Add(new InstanceData { Transform = transform, Normal = normal });
                    }
                }
            }
        }

        if (buckets.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<DetailEntry, List<InstanceData>> kv in buckets)
        {
            DetailEntry entry = kv.Key;
            List<InstanceData> instances = kv.Value;

            var mm = new MultiMesh();
            mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
            mm.UseCustomData = true;
            mm.Mesh = entry.Mesh;
            mm.InstanceCount = instances.Count;
            for (int i = 0; i < instances.Count; i++)
            {
                mm.SetInstanceTransform(i, instances[i].Transform);
                Vector3 n = instances[i].Normal;
                mm.SetInstanceCustomData(i, new Color(n.X, n.Y, n.Z, 0f));
            }

            var mmi = new MultiMeshInstance3D();
            mmi.Multimesh = mm;
            mmi.Name = MULTIMESH_NAME_PREFIX + entry.Mesh.ResourceName;
            // Detail sprites don't cast shadows — at sprite scale the shadow
            // atlas footprint per blade is sub-pixel and just adds noise. The
            // fragment shader still receives shadow attenuation on the sprite
            // itself so it darkens correctly when the terrain shadows it.
            mmi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            // Override the mesh's authored surface material with the runtime-
            // built ShaderMaterial that has Texture + FadeHeight stamped from
            // the DetailEntry. See DetailEntry.GetMaterial for the rationale
            // (editor can't compile detail_sprite.gdshader, so per-entry
            // material .tres files lose their parameters on save).
            mmi.MaterialOverride = entry.GetMaterial();
            parent.AddChild(mmi);
        }
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

    private struct InstanceData
    {
        public Transform3D Transform;
        public Vector3 Normal;
    }
}
