using System;
using System.Collections.Generic;
using Godot;

// Global manager for detail-sprite scatter MultiMeshInstance3Ds.
//
// One MultiMesh per DetailEntry, world-wide. ~250 chunks × 2-3 entries each
// ≈ 500-750 multimesh draw calls collapses to ~5-10 (one per unique
// DetailEntry actually scattered in the loaded set).
//
// Lifecycle:
//   - World owns a single instance via World.Initialize.
//   - ChunkMesh.Build computes its instance contributions (via
//     ChunkDetailScatter.Compute) and posts them to SetChunk(coord, ...).
//   - On chunk eviction, ChunkMesh._ExitTree calls RemoveChunk(coord).
//   - The manager batches dirty-entry rebuilds to end-of-frame so a flurry
//     of chunk loads/unloads only triggers one rebuild per affected entry.
//
// Rebuild model is "deferred recompose": when a chunk's contributions
// change, mark each affected DetailEntry dirty. Next _Process tick, rebuild
// each dirty entry by concatenating the instance lists from all currently-
// loaded chunks. O(total_instances) per dirty entry per rebuild burst.
// Chunk crossings happen at human pace, so this is fine.
public partial class WorldDetailScatter : Node3D
{
    // Stamped onto MultiMesh names so they show up identifiably in the remote
    // scene tree.
    private const string MULTIMESH_NAME_PREFIX = "DetailScatterGlobal_";

    // Shared unit quad mirrors ChunkDetailScatter's. Centered at (0, 0.5, 0)
    // so VERTEX.y is in [0, 1], matching the shader's "base at Y=0" convention.
    private static QuadMesh _sharedUnitQuad;
    private static QuadMesh GetUnitQuad()
    {
        _sharedUnitQuad ??= new QuadMesh
        {
            Size = new Vector2(1f, 1f),
            CenterOffset = new Vector3(0f, 0.5f, 0f),
        };
        return _sharedUnitQuad;
    }

    // Per DetailEntry: the live MultiMeshInstance3D + the latest contributions
    // from every chunk. Rebuilds happen by concatenating all chunk lists for
    // this entry into a single MultiMesh instance buffer.
    private class EntryBucket
    {
        public MultiMeshInstance3D Mmi;
        public MultiMesh Mm;
        // chunkCoord → instances contributed by that chunk for this entry.
        // Removed when the chunk evicts its scatter.
        public Dictionary<Vector3I, List<ChunkDetailScatter.InstanceData>> PerChunk = new();
        // CVar subscription so a single subscription per bucket toggles
        // visibility for all instances of this entry (rather than per-chunk).
        public Action<CVar> OnVisibilityChanged;
    }

    private readonly Dictionary<DetailEntry, EntryBucket> _buckets = new();
    private readonly HashSet<DetailEntry> _dirty = new();

    public override void _Ready()
    {
        // Cheap polling — Process only does work on frames where chunks
        // changed.
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (_dirty.Count == 0)
        {
            return;
        }
        foreach (DetailEntry entry in _dirty)
        {
            if (_buckets.TryGetValue(entry, out EntryBucket bucket))
            {
                Rebuild(entry, bucket);
            }
        }
        _dirty.Clear();
    }

    // Submit (or replace) a chunk's contribution for one or more DetailEntries.
    // Pass an empty / missing entry to clear it for that chunk.
    public void SetChunk(Vector3I chunkCoord, Dictionary<DetailEntry, List<ChunkDetailScatter.InstanceData>> contributions)
    {
        // Clear out any previous contributions from this chunk that are no
        // longer present in the new submission. This handles e.g. a chunk
        // that previously scattered grass but now scatters only flowers —
        // its grass entry needs to forget the old slots.
        foreach (KeyValuePair<DetailEntry, EntryBucket> kv in _buckets)
        {
            if (kv.Value.PerChunk.Remove(chunkCoord))
            {
                _dirty.Add(kv.Key);
            }
        }

        if (contributions == null)
        {
            return;
        }
        foreach (KeyValuePair<DetailEntry, List<ChunkDetailScatter.InstanceData>> kv in contributions)
        {
            DetailEntry entry = kv.Key;
            List<ChunkDetailScatter.InstanceData> instances = kv.Value;
            if (entry == null || instances == null || instances.Count == 0)
            {
                continue;
            }
            EntryBucket bucket = GetOrCreateBucket(entry);
            bucket.PerChunk[chunkCoord] = instances;
            _dirty.Add(entry);
        }
    }

    public void RemoveChunk(Vector3I chunkCoord)
    {
        foreach (KeyValuePair<DetailEntry, EntryBucket> kv in _buckets)
        {
            if (kv.Value.PerChunk.Remove(chunkCoord))
            {
                _dirty.Add(kv.Key);
            }
        }
    }

    private EntryBucket GetOrCreateBucket(DetailEntry entry)
    {
        if (_buckets.TryGetValue(entry, out EntryBucket bucket))
        {
            return bucket;
        }
        bucket = new EntryBucket();
        bucket.Mm = new MultiMesh();
        bucket.Mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        bucket.Mm.UseCustomData = true;
        bucket.Mm.UseColors = true;
        bucket.Mm.Mesh = GetUnitQuad();
        bucket.Mm.InstanceCount = 0;

        bucket.Mmi = new MultiMeshInstance3D();
        bucket.Mmi.Multimesh = bucket.Mm;
        bucket.Mmi.Name = MULTIMESH_NAME_PREFIX + (entry.Texture != null ? entry.Texture.ResourceName : "unnamed");
        bucket.Mmi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        bucket.Mmi.MaterialOverride = entry.GetMaterial();
        bucket.Mmi.Visible = CVars.detailsVisible.Value;

        // One CVar subscription per bucket. The bucket lives for the world's
        // lifetime, so no per-chunk subscribe/unsubscribe churn.
        EntryBucket b = bucket;
        bucket.OnVisibilityChanged = (cvar) =>
        {
            if (Godot.GodotObject.IsInstanceValid(b.Mmi))
            {
                b.Mmi.Visible = ((CVarBool)cvar).Value;
            }
        };
        CVars.detailsVisible.OnChanged += bucket.OnVisibilityChanged;

        AddChild(bucket.Mmi);
        _buckets[entry] = bucket;
        return bucket;
    }

    private void Rebuild(DetailEntry entry, EntryBucket bucket)
    {
        // Total up the instance count from all chunk contributions, then
        // copy into the MultiMesh's instance buffer in one pass. Order
        // doesn't matter — the renderer doesn't care about index ordering
        // for opaque-queue multimeshes.
        int total = 0;
        foreach (List<ChunkDetailScatter.InstanceData> list in bucket.PerChunk.Values)
        {
            total += list.Count;
        }

        // If the chunk count drops to zero, leave the MultiMeshInstance3D
        // around but with InstanceCount = 0. Cheap to keep, and it'll
        // repopulate the next time a chunk contributes.
        bucket.Mm.InstanceCount = total;
        if (total == 0)
        {
            return;
        }

        int slot = 0;
        foreach (List<ChunkDetailScatter.InstanceData> list in bucket.PerChunk.Values)
        {
            int n = list.Count;
            for (int i = 0; i < n; i++)
            {
                ChunkDetailScatter.InstanceData d = list[i];
                bucket.Mm.SetInstanceTransform(slot, d.Transform);
                // .xyz = terrain normal (sprite lighting/lean); .w = ground
                // tile porosity, read by detail_sprite.gdshader to scale the
                // wet darken so sprites darken by the same fraction as the
                // ground beneath them.
                bucket.Mm.SetInstanceCustomData(slot, new Color(d.Normal.X, d.Normal.Y, d.Normal.Z, d.Porosity));
                // .rgb = ground tint (sprite root pull); .a = baked AO, read by
                // detail_sprite.gdshader's ao_factor to shelter-darken in
                // lockstep with the terrain (which packs AO into its COLOR.a).
                bucket.Mm.SetInstanceColor(slot, new Color(d.GroundTint.R, d.GroundTint.G, d.GroundTint.B, d.Ao));
                slot++;
            }
        }
    }

    public override void _ExitTree()
    {
        // Unsubscribe so no dangling delegates point at freed multimeshes.
        foreach (EntryBucket bucket in _buckets.Values)
        {
            if (bucket.OnVisibilityChanged != null)
            {
                CVars.detailsVisible.OnChanged -= bucket.OnVisibilityChanged;
            }
        }
        _buckets.Clear();
    }
}
