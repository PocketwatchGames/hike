using System;
using System.Collections.Generic;
using Godot;

// Global manager for static-prop sprite rendering, batched into MultiMeshes
// keyed by (texture, pass).
//
// One MultiMesh per bucket, world-wide. ~700 prop draws (sprite + shadow
// proxy + AO decal per PropInstance × hundreds of props) collapse to a
// handful (~one per atlas × pass actually populated). Same structural play
// WorldDetailScatter makes for scattered grass — the difference is that
// props are authored as scenes and register a node (MultimeshPropSprite)
// at runtime, rather than being computed in bulk by the chunk mesher.
//
// Earlier iterations forked buckets on quantized forward_offset, but every
// archetype has a unique forward_offset (tuned per-archetype to its
// cylinder collider radius), so the fork was 1:1 with archetype count and
// defeated atlas-level batching. forward_offset moved to per-instance
// data (packed into INSTANCE_COLOR.b) so all archetypes sharing an atlas
// collapse to one draw call per pass.
//
// Lifecycle:
//   - World owns one instance, created in World.Initialize.
//   - Each MultimeshPropSprite calls Register(...) in _Ready and
//     Unregister(...) in _ExitTree. The sprite supplies its own per-instance
//     state (transform, terrain normal, align, atlas region/size), since it
//     authored those values and knows them at scene-load time.
//   - The scene tree freeing a prop's PackedScene (chunk eviction, save/load,
//     editor refresh) cascades _ExitTree on the MultimeshPropSprite, which
//     pulls the instance out of its bucket. So the manager stays consistent
//     with the active entity set without an explicit chunk-coord index.
//   - Bucket rebuilds are deferred to end-of-frame: register/unregister marks
//     the bucket dirty; one rebuild per frame burst regardless of how many
//     props arrived or left.
//
// Bucket key (Texture, ForwardOffset, Pass) chosen by what genuinely forks
// the draw call: different sampler binding (texture), different camera-
// relative offset baked into the vertex shader (forward_offset, can't be
// per-instance without a spare channel), different shader (pass — visible
// vs. shadow vs. reflection). Everything else (align_to_terrain, mirror,
// scale, terrain_normal) lives per-instance — see the
// sprite_prop_multimesh.gdshader header for the channel allocation.
public partial class WorldPropScatter : Node3D
{
    private const string MULTIMESH_NAME_PREFIX = "PropScatter_";

    // Pulls a short, identifiable name out of a texture resource for use in
    // the remote-tree bucket node names. Imported textures often have an
    // empty ResourceName, so we fall back to the path's filename stem (e.g.
    // "res://assets/textures/props/decor.png" → "decor").
    private static string TextureLabel(Texture2D tex)
    {
        if (tex == null)
        {
            return "null";
        }
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

    // Pack atlas pixel coords (origin.x, origin.y) or (size.x, size.y) into
    // a single float for INSTANCE_COLOR transport. 12 bits per channel →
    // each component must be < 4096. float32's 24-bit mantissa exactly
    // represents integers up to 2^24, well above our maximum packed value
    // of 4095*4096 + 4095 = 16,773,375. Decoded in the shader as
    //   x = packed >> 12, y = packed & 0xFFF.
    private const int ATLAS_PACK_SHIFT = 4096;
    private static float PackAtlasComponents(int x, int y)
    {
        return x * ATLAS_PACK_SHIFT + y;
    }

    private static QuadMesh _sharedUnitQuad;
    private static QuadMesh GetUnitQuad()
    {
        // Centered horizontally, base at Y=0 — matches the sprite_lit
        // authoring convention (offset = (-W/2, 0)) and the shader's
        // "VERTEX.x in [-0.5, 0.5], VERTEX.y in [0, 1]" assumption.
        _sharedUnitQuad ??= new QuadMesh
        {
            Size = new Vector2(1f, 1f),
            CenterOffset = new Vector3(0f, 0.5f, 0f),
        };
        return _sharedUnitQuad;
    }

    public enum Pass
    {
        Visible,
        Shadow,
        // Reflection bucket — populated when MultimeshPropSprite finds
        // water below the sprite at _Ready time. Reuses INSTANCE_COLOR.b
        // for water_y (the visible-pass slot is forward_offset; same byte
        // position, different per-pass meaning). Source position lives in
        // MODEL_MATRIX[3] same as the other buckets — the shader does the
        // mirror-across-water-plane geometry on its own.
        Reflection,
        // Visible-only render into the BlockLightShadowProjector's
        // SubViewport. Same instance data as Shadow (same billboard
        // math, same alpha cut), but writes ALBEDO=1 and is on the
        // projector layer so only the projector camera renders it.
        // Doesn't cast sun/moon shadows — Shadow does that.
        BlockLightShadow,
    }

    // Internal so the internal Bucket can store one as a field — same
    // accessibility relaxation Bucket needed for Handle.
    internal struct BucketKey : IEquatable<BucketKey>
    {
        public Texture2D Texture;
        public Pass Pass;

        public bool Equals(BucketKey other)
        {
            return Texture == other.Texture && Pass == other.Pass;
        }
        public override bool Equals(object obj) => obj is BucketKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Texture, Pass);
    }

    // Internal (not private) so the public Handle's internal accessors can
    // expose Bucket-typed parameters/returns. Bucket itself stays an
    // implementation detail not visible outside the assembly.
    internal class Bucket
    {
        public BucketKey Key;
        public MultiMeshInstance3D Mmi;
        public MultiMesh Mm;
        public ShaderMaterial Material;
        // Member sprites — the bucket reads each one's snapshot at rebuild
        // time. Static props don't mutate after _Ready, so the snapshot is
        // a single read; if a prop ever needs to dirty its slot (rare —
        // most are static), it can call WorldPropScatter.MarkDirty(handle)
        // which touches its bucket's dirty flag. Not implemented yet
        // because no caller needs it.
        public List<MultimeshPropSprite> Members = new();
        public Action<CVar> OnVisibilityChanged;
    }

    // Handle returned to the sprite. Opaque from the outside — only
    // WorldPropScatter reads its internal bucket pointer (the outer class
    // sees the nested class's private members).
    public class Handle
    {
        private Bucket Bucket;
        internal Handle(Bucket b) { Bucket = b; }
        internal Bucket GetBucket() { return Bucket; }
        internal void Clear() { Bucket = null; }
    }

    private readonly Dictionary<BucketKey, Bucket> _buckets = new();
    private readonly HashSet<Bucket> _dirty = new();

    public override void _Process(double delta)
    {
        if (_dirty.Count == 0)
        {
            return;
        }
        foreach (Bucket b in _dirty)
        {
            Rebuild(b);
        }
        _dirty.Clear();
    }

    // Register a prop's contributions to the visible bucket plus optional
    // shadow and reflection buckets. Returns three handles the sprite
    // stores; pass them back to Unregister on _ExitTree. Null handles
    // mean the sprite opted out of (or didn't qualify for) that pass:
    //   - Shadow:           skipped if sprite.CastsShadow == false.
    //   - BlockLightShadow: skipped if sprite.CastsShadow == false (same
    //                       gate; both proxies represent the sprite's
    //                       silhouette, just for different consumers).
    //   - Reflection:       skipped if MultimeshPropSprite found no water
    //                       at _Ready (snapshot.HasReflection == false).
    public (Handle Visible, Handle Shadow, Handle Reflection, Handle BlockLightShadow) Register(MultimeshPropSprite sprite)
    {
        Handle visible = RegisterIn(sprite, Pass.Visible);
        Handle shadow = sprite.CastsShadow ? RegisterIn(sprite, Pass.Shadow) : null;
        Handle reflection = sprite.Snapshot.HasReflection ? RegisterIn(sprite, Pass.Reflection) : null;
        Handle blockLightShadow = sprite.CastsShadow ? RegisterIn(sprite, Pass.BlockLightShadow) : null;
        return (visible, shadow, reflection, blockLightShadow);
    }

    // Unregister takes both the sprite and the handle: the handle carries
    // the bucket reference, the sprite is the value to remove (we don't
    // store the index per handle since a registered sprite never moves
    // between buckets, so List.IndexOf at unregister time is fine).
    public void Unregister(MultimeshPropSprite sprite, Handle handle)
    {
        if (handle == null)
        {
            return;
        }
        Bucket b = handle.GetBucket();
        if (b == null)
        {
            return;
        }
        int idx = b.Members.IndexOf(sprite);
        if (idx >= 0)
        {
            // Order-independent removal — the multimesh slot ordering doesn't
            // matter for opaque-queue draws (depth-sort is per-fragment via
            // depth_prepass_alpha in the shader).
            int last = b.Members.Count - 1;
            if (idx != last)
            {
                b.Members[idx] = b.Members[last];
            }
            b.Members.RemoveAt(last);
            _dirty.Add(b);
        }
        handle.Clear();
    }

    private Handle RegisterIn(MultimeshPropSprite sprite, Pass pass)
    {
        Texture2D atlas = sprite.AtlasTexture;
        if (atlas == null)
        {
            GD.PushError($"MultimeshPropSprite '{sprite.Name}' registered with null AtlasTexture.");
            return null;
        }
        var key = new BucketKey
        {
            Texture = atlas,
            Pass = pass,
        };
        Bucket bucket = GetOrCreateBucket(key);
        bucket.Members.Add(sprite);
        _dirty.Add(bucket);
        return new Handle(bucket);
    }

    private Bucket GetOrCreateBucket(BucketKey key)
    {
        if (_buckets.TryGetValue(key, out Bucket bucket))
        {
            return bucket;
        }
        bucket = new Bucket();
        bucket.Key = key;
        bucket.Mm = new MultiMesh();
        bucket.Mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        bucket.Mm.UseCustomData = true;
        bucket.Mm.UseColors = true;
        bucket.Mm.Mesh = GetUnitQuad();
        bucket.Mm.InstanceCount = 0;

        bucket.Material = BuildMaterial(key.Pass, key.Texture);

        bucket.Mmi = new MultiMeshInstance3D();
        bucket.Mmi.Multimesh = bucket.Mm;
        bucket.Mmi.Name = $"{MULTIMESH_NAME_PREFIX}{key.Pass}_{TextureLabel(key.Texture)}";
        bucket.Mmi.MaterialOverride = bucket.Material;
        // Per-pass shadow contribution:
        //   - Visible / Reflection: visible-only on default layer; sun
        //     shadows come from Shadow, block-light from BlockLightShadow.
        //   - Shadow: ShadowsOnly on default layer — contributes only to
        //     the sun/moon shadow atlas, never visible to any camera.
        //   - BlockLightShadow: visible on the projector layer only;
        //     CastShadow.Off so it doesn't double-cast into the sun
        //     atlas. Renders into the BlockLightShadowProjector's
        //     SubViewport. Excluded from the main camera by cull mask.
        if (key.Pass == Pass.Shadow)
        {
            bucket.Mmi.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
        }
        else if (key.Pass == Pass.BlockLightShadow)
        {
            bucket.Mmi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            bucket.Mmi.Layers = BlockLightShadowProjector.SHADOW_PROXY_LAYER_MASK;
        }
        else
        {
            bucket.Mmi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        bucket.Mmi.Visible = CVars.propsVisible.Value;

        // Single CVar subscription per bucket (lives for world's lifetime).
        Bucket b = bucket;
        bucket.OnVisibilityChanged = (cvar) =>
        {
            if (Godot.GodotObject.IsInstanceValid(b.Mmi))
            {
                b.Mmi.Visible = ((CVarBool)cvar).Value;
            }
        };
        CVars.propsVisible.OnChanged += bucket.OnVisibilityChanged;

        AddChild(bucket.Mmi);
        _buckets[key] = bucket;
        return bucket;
    }

    // Material per (pass, atlas). Loaded from a shared template and
    // duplicated per bucket — same pattern DetailEntry uses. sprite_texture
    // is the only per-bucket uniform (sampler2D can't be per-instance in
    // Godot 4). Everything else (region, size, forward_offset, normal,
    // align) is per-instance via INSTANCE_CUSTOM and packed COLOR. Material
    // lives on the MultiMeshInstance3D's MaterialOverride so every bucket
    // shares one QuadMesh while carrying its own ShaderMaterial.
    private static ShaderMaterial BuildMaterial(Pass pass, Texture2D atlas)
    {
        string templatePath = pass switch
        {
            Pass.Visible => "res://resources/materials/sprite_prop_multimesh.tres",
            Pass.Shadow => "res://resources/materials/sprite_prop_shadow_multimesh.tres",
            Pass.Reflection => "res://resources/materials/sprite_prop_reflection_multimesh.tres",
            Pass.BlockLightShadow => "res://resources/materials/sprite_prop_block_light_shadow_multimesh.tres",
            _ => null,
        };
        if (templatePath == null)
        {
            return null;
        }
        var template = GD.Load<ShaderMaterial>(templatePath);
        if (template == null)
        {
            GD.PushError($"WorldPropScatter: could not load template at {templatePath}");
            return null;
        }
        var mat = (ShaderMaterial)template.Duplicate();
        if (atlas != null)
        {
            mat.SetShaderParameter("sprite_texture", atlas);
        }
        // Reflection shader needs the same ripple normal-map pair voxel_water
        // samples so its UV-jitter math reads from the actual rippling
        // surface field rather than the engine's default-white sampler
        // (which collapses to a constant tilt of ~0.577 at every fragment
        // → no shimmer regardless of camera / source / ripple_strength).
        if (pass == Pass.Reflection)
        {
            var rippleA = GD.Load<Texture2D>("res://assets/textures/water_ripple_a.tres");
            var rippleB = GD.Load<Texture2D>("res://assets/textures/water_ripple_b.tres");
            mat.SetShaderParameter("ripple_tex_a", rippleA);
            mat.SetShaderParameter("ripple_tex_b", rippleB);
        }
        return mat;
    }

    private void Rebuild(Bucket bucket)
    {
        int total = bucket.Members.Count;
        bucket.Mm.InstanceCount = total;
        if (total == 0)
        {
            return;
        }

        // INSTANCE_COLOR.b is per-pass: the visible shader uses it for
        // forward_offset, the reflection shader uses it for water_y. The
        // shadow shader ignores it (silhouette is sun-aligned, not camera-
        // relative). Switching here means each pass's bucket gets the right
        // payload while every other channel stays identical across passes.
        bool isReflection = bucket.Key.Pass == Pass.Reflection;
        for (int i = 0; i < total; i++)
        {
            MultimeshPropSprite s = bucket.Members[i];
            // Snapshot is precomputed in MultimeshPropSprite._Ready and held
            // on the sprite — see MultimeshPropSprite.Snapshot for the
            // packing rationale. Reading per rebuild rather than caching
            // copies in the bucket so a future "this prop moved" path can
            // dirty its bucket and have the rebuild see the new values.
            MultimeshPropSprite.SnapshotData snap = s.Snapshot;
            bucket.Mm.SetInstanceTransform(i, snap.Transform);
            bucket.Mm.SetInstanceCustomData(i, new Color(snap.Normal.X, snap.Normal.Y, snap.Normal.Z, snap.Align));
            // INSTANCE_COLOR layout (24-bit mantissa-safe; atlas size capped
            // at 4096 per axis):
            //   r = region_origin.x * 4096 + region_origin.y
            //   g = sprite_size.x   * 4096 + sprite_size.y
            //   b = forward_offset (Visible) | water_y (Reflection) | 0 (Shadow)
            //   a = forward_offset (Reflection — same value the visible
            //       bucket uses; the reflection shader applies the same
            //       sprite_forward push so the reflection's XZ anchor
            //       lines up with the visible sprite's anchor instead of
            //       sitting at the bare prop position) | 0 (other passes)
            float channelB = isReflection ? snap.WaterY : snap.ForwardOffset;
            float channelA = isReflection ? snap.ForwardOffset : 0f;
            bucket.Mm.SetInstanceColor(i, new Color(
                PackAtlasComponents(snap.RegionOrigin.X, snap.RegionOrigin.Y),
                PackAtlasComponents(snap.SpriteSize.X, snap.SpriteSize.Y),
                channelB,
                channelA));
        }
    }

    // Console-friendly summary of every active bucket — hooked up by the
    // `props_stats` CVar action so the user can verify eviction is working.
    // Per-bucket: pass, atlas label, forward-offset quantum, member count
    // (sprites currently registered) and live multimesh InstanceCount (the
    // last-rebuilt count; if Members > InstanceCount the bucket is dirty
    // and pending a _Process tick).
    public string FormatStats()
    {
        if (_buckets.Count == 0)
        {
            return "WorldPropScatter: no active buckets.";
        }
        var sb = new System.Text.StringBuilder();
        sb.Append("WorldPropScatter: ");
        sb.Append(_buckets.Count);
        sb.AppendLine(" bucket(s)");
        int totalMembers = 0;
        int totalInstances = 0;
        foreach (KeyValuePair<BucketKey, Bucket> kv in _buckets)
        {
            Bucket b = kv.Value;
            int members = b.Members.Count;
            int instCount = b.Mm != null ? b.Mm.InstanceCount : 0;
            totalMembers += members;
            totalInstances += instCount;
            sb.Append("  ");
            sb.Append(kv.Key.Pass);
            sb.Append(' ');
            sb.Append(TextureLabel(kv.Key.Texture));
            sb.Append(" members=");
            sb.Append(members);
            sb.Append(" mm.InstanceCount=");
            sb.Append(instCount);
            if (members != instCount)
            {
                sb.Append(" (DIRTY)");
            }
            sb.AppendLine();
        }
        sb.Append("Totals: members=");
        sb.Append(totalMembers);
        sb.Append(" instances=");
        sb.Append(totalInstances);
        return sb.ToString();
    }

    public override void _ExitTree()
    {
        foreach (Bucket bucket in _buckets.Values)
        {
            if (bucket.OnVisibilityChanged != null)
            {
                CVars.propsVisible.OnChanged -= bucket.OnVisibilityChanged;
            }
        }
        _buckets.Clear();
    }
}
