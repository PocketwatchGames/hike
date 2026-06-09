using System.Collections.Generic;
using Godot;

// Batched renderer for transient footprint ground marks. Replaces the old
// one-Node3D-per-print path (Footprint.cs + footprint_visible/discoverable
// scenes): every print laid with the same actor texture collapses into a
// single MultiMesh draw, with no per-print Node, no per-print _Process, and
// one shared (per-texture) material instead of a unique material per print.
//
// Structural sibling of WorldPropScatter — World owns one, created in
// World.Initialize — but where WorldPropScatter batches static props that
// register/unregister handles, footprints are transient and animated, so
// this class owns the per-instance lifetime simulation itself:
//   - lifetime fade (alpha ramps to 0 over the print's duration, then the
//     slot is recycled),
//   - the mob-print discovery gate (the player must perceive the print
//     before it fades in — ported off the per-print Discoverable node).
//
// One MultiMesh bucket per actor footprint texture (a MultiMesh = one mesh +
// one material = one albedo texture, so distinct textures can't share a
// bucket). Buckets render on the ground-stain projector layer (layer 5);
// GroundStainProjector composites them into the lit ground shaders.
//
// GPU writes are deferred entirely to _Process (Spawn only mutates CPU state),
// mirroring WorldPropScatter: InstanceCount is set to the exact live count and
// every live instance is rewritten each frame. This keeps the MultiMesh AABB
// tight around the live prints (a fixed oversized InstanceCount caches its
// AABB at the world origin and gets the whole bucket frustum-culled once the
// player walks away from origin). CPU slots are packed [0, Count) and removal
// is an order-independent swap with the last live slot.
[GlobalClass]
public partial class FootprintScatter : Node3D
{
    // Per-texture instance budget. A fresh print past this recycles the
    // oldest slot in that bucket. Generous: at ~15s lifetimes the player +
    // a few nearby mobs stay well under this.
    private const int BUCKET_CAPACITY = 1024;

    // Lift the quad just off the ground so it sits inside the projector
    // frustum without z-fighting the terrain (matches the old footprint
    // scene's Quad at y=0.05).
    private const float QUAD_HEIGHT_OFFSET = 0.05f;

    // Shared unit plane (XZ, faces +Y, centered) — every bucket's MultiMesh
    // references the same mesh; per-instance size comes from the instance
    // transform. Created in code like WorldPropScatter's shared unit quad.
    private static PlaneMesh _sharedQuad;
    private static PlaneMesh GetQuad()
    {
        _sharedQuad ??= new PlaneMesh { Size = Vector2.One };
        return _sharedQuad;
    }

    private struct Slot
    {
        // Baked once at spawn (position + yaw + non-uniform XZ scale).
        public Transform3D Transform;
        // Ground position the discovery perception samples at.
        public Vector3 Position;
        // RGB tint + baseline (spawn) alpha in A.
        public Color Tint;
        public ulong SpawnTimeMs;
        public float DurationSeconds;
        // Mob prints laid while unperceived gate on discovery; player prints
        // (and prints laid while the mob was already perceived) are false and
        // show immediately.
        public bool Gated;
        public PerceivedByPlayerState Perception;
        // Smoothed 0..1 noticed-by-player factor (pinned to 1 for ungated).
        public float DiscoveryAlpha;
    }

    private class Bucket
    {
        public MultiMesh Mm;
        public MultiMeshInstance3D Mmi;
        public readonly Slot[] Slots = new Slot[BUCKET_CAPACITY];
        public int Count;
    }

    private readonly Dictionary<Texture2D, Bucket> _buckets = new();

    // Lay down a print. Called from World.SpawnFootprint (which the
    // FootprintEmitter routes player/mob footsteps through). CPU-only — the
    // instance is uploaded to the GPU on the next _Process.
    public void Spawn(Texture2D texture, Vector2 size, Color tint, Vector3 position, float yaw, float durationSeconds, bool gated)
    {
        if (texture == null)
        {
            return;
        }
        Bucket bucket = GetOrCreate(texture);
        if (bucket == null)
        {
            return;
        }

        int idx;
        if (bucket.Count < BUCKET_CAPACITY)
        {
            idx = bucket.Count++;
        }
        else
        {
            idx = OldestIndex(bucket);
        }

        // Rotation about Y by yaw, then non-uniform local scale: width along
        // the print's local X, stride along local Z. Scaling the basis column
        // vectors applies the scale in the rotated (local) frame, so a yawed
        // print stays shear-free.
        Basis basis = new Basis(Vector3.Up, yaw);
        basis.X *= size.X;
        basis.Z *= size.Y;
        var transform = new Transform3D(basis, position + Vector3.Up * QUAD_HEIGHT_OFFSET);

        // Per-ground tints are authored in sRGB (they used to drive the old
        // StandardMaterial3D.AlbedoColor, which Godot converts to linear). A
        // MultiMesh instance color does NOT get that conversion, so convert the
        // RGB here once — otherwise dark prints render washed-out/light (same
        // trap ModelAnimator's source_color push documents). Alpha is coverage,
        // not color, so it stays as-is.
        Color tintLinear = new Color(tint.R, tint.G, tint.B).SrgbToLinear();
        var slot = new Slot
        {
            Transform = transform,
            Position = position,
            Tint = new Color(tintLinear.R, tintLinear.G, tintLinear.B, tint.A),
            SpawnTimeMs = World.Current?.GameTimeMs ?? 0,
            DurationSeconds = Mathf.Max(0.1f, durationSeconds),
            Gated = gated,
            DiscoveryAlpha = gated ? 0f : 1f,
        };
        // Stagger the discovery tick so a burst of prints don't all sample on
        // the same frame (mirrors Discoverable's randomized tick accumulator).
        if (gated)
        {
            slot.Perception.tickAccumulator = (float)GD.RandRange(0.0, PlayerPerception.TickInterval);
        }

        bucket.Slots[idx] = slot;
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("FootprintScatter.Process");
        World world = World.Current;
        SimData sim = world?.SimData;
        ulong now = world?.GameTimeMs ?? 0;
        float dt = (float)delta;

        foreach (Bucket bucket in _buckets.Values)
        {
            // 1. Age out expired prints (CPU-side compaction). Count only
            //    shrinks here, so the existing InstanceCount still covers every
            //    index we write below.
            int i = 0;
            while (i < bucket.Count)
            {
                ulong age = now - bucket.Slots[i].SpawnTimeMs;
                if (1f - (age * 0.001f / bucket.Slots[i].DurationSeconds) <= 0f)
                {
                    int last = bucket.Count - 1;
                    if (i != last)
                    {
                        bucket.Slots[i] = bucket.Slots[last];
                    }
                    bucket.Count = last;
                    continue; // reprocess the swapped-in slot at i
                }
                i++;
            }

            // 2. Size the MultiMesh to the live count (no-op when unchanged).
            //    Setting InstanceCount recomputes the AABB, keeping it tight
            //    around the surviving prints.
            if (bucket.Mm.InstanceCount != bucket.Count)
            {
                bucket.Mm.InstanceCount = bucket.Count;
            }

            // 3. Upload every live instance: static transform + animated color.
            for (int s = 0; s < bucket.Count; s++)
            {
                ref Slot slot = ref bucket.Slots[s];

                ulong age = now - slot.SpawnTimeMs;
                float lifetimeAlpha = Mathf.Max(0f, 1f - (age * 0.001f / slot.DurationSeconds));

                if (slot.Gated && world != null)
                {
                    UpdateDiscovery(world, sim, ref slot, dt);
                }

                bucket.Mm.SetInstanceTransform(s, slot.Transform);
                float alpha = slot.Tint.A * lifetimeAlpha * slot.DiscoveryAlpha;
                bucket.Mm.SetInstanceColor(s, new Color(slot.Tint.R, slot.Tint.G, slot.Tint.B, alpha));
            }
        }
    }

    public override void _ExitTree()
    {
        // MultiMeshInstance3D children are freed with this node; just drop the
        // index so a re-entered world starts clean.
        _buckets.Clear();
    }

    // Advance the mob-print discovery gate: tick perception at ~10Hz until the
    // print is Discovered (monotonic — discovery is permanent for the print's
    // lifetime), then ease the fade-in alpha toward the target.
    private static void UpdateDiscovery(World world, SimData sim, ref Slot slot, float dt)
    {
        if (slot.Perception.state != EPlayerPerceptionState.Discovered)
        {
            slot.Perception.tickAccumulator += dt;
            if (slot.Perception.tickAccumulator >= PlayerPerception.TickInterval)
            {
                float tickDelta = slot.Perception.tickAccumulator;
                slot.Perception.tickAccumulator = 0f;

                float threshold = sim?.FootprintDiscoveryThreshold ?? 1f;
                var inputs = new PerceptionInputs
                {
                    prominence = sim?.FootprintDiscoveryProminence ?? 0.3f,
                    // No Detected phase / HUD for prints — the visual keys on
                    // Discovered, so collapse Detected onto the same threshold.
                    detectedThreshold = threshold,
                    discoveredThreshold = threshold,
                    lightSampleHeight = sim?.FootprintDiscoveryLightSampleHeight ?? 0.05f,
                    losRayHeight = 0f,
                    // Light already encodes "behind a wall / in the dark", and a
                    // per-print raycast across many prints would dominate.
                    skipLineOfSight = true,
                };
                PlayerPerception.Tick(world, slot.Position, in inputs, ref slot.Perception, tickDelta, out _);
            }
        }

        float fadeSeconds = sim?.FootprintDiscoveryFadeSeconds ?? 0.4f;
        float target = slot.Perception.state == EPlayerPerceptionState.Discovered ? 1f : 0f;
        if (slot.DiscoveryAlpha != target)
        {
            slot.DiscoveryAlpha = Mathf.MoveToward(slot.DiscoveryAlpha, target, dt / Mathf.Max(0.01f, fadeSeconds));
        }
    }

    // Index of the oldest live slot — used only on the rare overflow path to
    // pick which print to evict for a fresh one.
    private static int OldestIndex(Bucket bucket)
    {
        int oldest = 0;
        ulong oldestTime = bucket.Slots[0].SpawnTimeMs;
        for (int i = 1; i < bucket.Count; i++)
        {
            if (bucket.Slots[i].SpawnTimeMs < oldestTime)
            {
                oldestTime = bucket.Slots[i].SpawnTimeMs;
                oldest = i;
            }
        }
        return oldest;
    }

    private Bucket GetOrCreate(Texture2D texture)
    {
        if (_buckets.TryGetValue(texture, out Bucket existing))
        {
            return existing;
        }

        Material template = World.Current?.SimData?.FootprintMaterial;
        if (template == null)
        {
            GD.PushError("FootprintScatter: SimData.FootprintMaterial is not set — cannot render footprints.");
            return null;
        }

        var bucket = new Bucket
        {
            Mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = GetQuad(),
                InstanceCount = 0,
            },
        };

        // One material per texture: duplicate the authored template and bind
        // this bucket's albedo texture (a sampler can't be per-instance in
        // Godot 4). Per-print tint + alpha ride INSTANCE_COLOR via the
        // template's vertex_color_use_as_albedo flag.
        var material = (Material)template.Duplicate();
        if (material is StandardMaterial3D std)
        {
            std.AlbedoTexture = texture;
        }

        bucket.Mmi = new MultiMeshInstance3D
        {
            Multimesh = bucket.Mm,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = GroundStainProjector.STAIN_PROXY_LAYER_MASK,
            Name = $"Footprints_{TextureLabel(texture)}",
        };
        AddChild(bucket.Mmi);
        _buckets[texture] = bucket;
        return bucket;
    }

    private static string TextureLabel(Texture2D tex)
    {
        if (!string.IsNullOrEmpty(tex.ResourceName))
        {
            return tex.ResourceName;
        }
        string path = tex.ResourcePath;
        if (string.IsNullOrEmpty(path))
        {
            return "anon";
        }
        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        int start = slash + 1;
        int end = dot > slash ? dot : path.Length;
        return start < end ? path.Substring(start, end - start) : "anon";
    }
}
