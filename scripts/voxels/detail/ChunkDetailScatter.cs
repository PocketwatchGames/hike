using System.Collections.Generic;
using Godot;

// Builds MultiMeshInstance3D children for one chunk's painted detail sprites.
//
// One MultiMesh is emitted per (chunk, DetailEntry.Mesh) — i.e. one draw call
// per sprite type per chunk, regardless of instance count. Hundreds of grass
// blades in a chunk cost the same draw-call budget as one. Per-instance:
//   - position    : voxel center + sub-voxel jitter, sitting on top of the
//                   solid voxel that owns the DetailGroup paint
//   - basis       : random yaw (per DetailEntry) + uniform scale jitter
//   - custom data : (ground_color.rgb, _) — the unlit albedo of the voxel
//                   directly below, used by the shader's bottom-edge fade so
//                   the sprite blends into the terrain it's planted on
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

    public static void Build(
        ChunkState data,
        System.Func<int, int, int, VoxelType> getVoxel,
        System.Func<int, int, int, int> getKitId,
        EnvironmentKitData[] kits,
        DetailGroupData[] groups,
        Node3D parent)
    {
        if (groups == null || groups.Length == 0)
        {
            return;
        }

        // Bucket per (group, entry) so each (mesh, material) pair becomes one
        // MultiMesh. Same mesh referenced by two entries would otherwise need
        // an extra dedupe pass — DetailEntry is the bucketing key on purpose
        // so weight/scale/yaw vary independently per entry.
        var buckets = new Dictionary<DetailEntry, List<InstanceData>>();

        // Cumulative-weight scratch reused per group so we don't re-allocate
        // per voxel. Sized lazily to the largest group seen.
        float[] cumulativeWeights = null;

        Vector3I chunkCoord = data.ChunkCoord;

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

                    // Sprite sits on top of the painted (solid) voxel; sample
                    // *that* voxel's tile for the ground-fade color so the
                    // bottom of the sprite matches what the player sees the
                    // sprite touching, not the air voxel above.
                    VoxelType groundVoxel = getVoxel(x, y, z);
                    int groundKit = getKitId(x, y, z);
                    Color groundColor = VoxelTypeInfo.GetGroundAlbedo(groundVoxel, groundKit, kits);

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
                        list.Add(new InstanceData { Transform = transform, GroundColor = groundColor });
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
                Color g = instances[i].GroundColor;
                mm.SetInstanceCustomData(i, new Color(g.R, g.G, g.B, 0f));
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

    private struct InstanceData
    {
        public Transform3D Transform;
        public Color GroundColor;
    }
}
