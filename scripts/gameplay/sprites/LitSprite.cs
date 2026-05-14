using Godot;

// Upright camera-facing pixel-art sprite that renders through the sprite_lit
// shader. The bulk of the per-sprite plumbing (shared-material cache,
// visibility/silhouette/xray uniforms, mirror coin flip, scale roll) lives
// on SpriteBase; this class adds the upright-billboard machinery:
//
//   - Yaw mirror: per-frame flip of `sprite_mirror` based on the sprite's
//     world yaw vs the camera, so side-view art tracks the character's
//     facing.
//   - AlignToTerrain / TerrainNormal: shader uniforms that roll the sprite
//     around its forward axis to lean with sloped ground. Defaults are
//     "upright on flat ground."
//   - ForwardOffset: shader-side push toward the camera along the
//     billboard-forward axis (so a sprite parks at the front edge of a
//     cylinder collider as the camera yaws).
//   - Shadow proxy: a sun-billboarded ShadowsOnly Sprite3D child that
//     contributes its silhouette to Godot's directional shadow atlas.
//     The visible sprite never casts (CastShadow.Off) — billboarded sprites
//     cast edge-on slivers from the sun's POV.
//   - Block-light shadow proxy: a visible-only sibling on a dedicated
//     layer for the BlockLightShadowProjector pass.
//   - Water reflection: a flipped child sprite, anchored across the water
//     surface under the sprite's XZ column.
//
// In the editor this node falls back to Sprite3D's default unshaded path so
// the sprite is visible while authoring colliders. The sprite_lit shader is
// applied at runtime by binding a shared MaterialTemplate-derived material.
[Tool]
[GlobalClass]
public partial class LitSprite : SpriteBase
{
    // When true, _Process flips `sprite_mirror` each frame based on whether
    // the sprite's world yaw points to the left or right of the camera.
    // Intended for side-view character art so the sprite's facing direction
    // tracks the node's Rotation.Y. FlipH still acts as the authored
    // baseline (useful when the art was drawn facing the opposite side)
    // and is XOR'd with the yaw-derived flip.
    [Export] public bool MirrorByYaw { get; set; }

    // Blend between upright billboarding (0) and terrain-aligned billboarding
    // (1) — maps directly to the sprite_lit shader's align_to_terrain uniform.
    // TerrainNormal is the surface normal the shader rolls toward when
    // AlignToTerrain > 0 (world-space). Owners that want terrain alignment
    // are responsible for sourcing a real normal — e.g. TallGrass raycasts
    // down at spawn — otherwise the default (world-up) keeps the sprite
    // upright regardless of AlignToTerrain.
    public float AlignToTerrain
    {
        get => _alignToTerrain;
        set
        {
            if (_alignToTerrain == value)
            {
                return;
            }
            _alignToTerrain = value;
            PushAlignmentUniform("align_to_terrain", value);
        }
    }
    private float _alignToTerrain = 0f;

    public Vector3 TerrainNormal
    {
        get => _terrainNormal;
        set
        {
            if (_terrainNormal == value)
            {
                return;
            }
            _terrainNormal = value;
            PushAlignmentUniform("terrain_normal", value);
        }
    }
    private Vector3 _terrainNormal = Vector3.Up;

    // Shifts the rendered sprite toward the camera by this many meters along
    // the horizontal billboard-forward axis (the same axis sprite_lit uses to
    // face the camera). Because the offset is re-derived in the vertex shader
    // from the current camera direction, it stays "toward camera" as the
    // camera yaws — a plain node Z translation wouldn't (it bakes into
    // MODEL_MATRIX as a fixed world vector and only reads as forward from
    // one angle). Used to park a sprite at the front edge of a cylinder
    // collider so the visuals line up with the walkable footprint.
    [Export] public float ForwardOffset
    {
        get => _forwardOffset;
        set
        {
            if (_forwardOffset == value)
            {
                return;
            }
            _forwardOffset = value;
            PushAlignmentUniform("forward_offset", value);
        }
    }
    private float _forwardOffset = 0f;

    // Whether this sprite contributes a flipped child sprite under nearby
    // water surfaces. Off skips reflection entirely (cheapest — props that
    // never see water, e.g. interior furniture, should be Off). On forces a
    // reflection regardless of size. Auto enables it only when the sprite
    // is taller than `AutoReflectMinHeight` in world units — small detail
    // sprites (loot, torches' flame frames, tiny props) get culled because
    // their flipped duplicate is too small to read on a rippled surface.
    public enum ReflectMode { Off, On, Auto }
    [Export] public ReflectMode Reflects { get; set; } = ReflectMode.Auto;
    // World-unit height threshold for ReflectMode.Auto. Computed from the
    // sprite's pixel rect × pixel_size at runtime; defaults to ~1.5m which
    // covers player + most mobs/trees but skips loot/grass/small props.
    [Export(PropertyHint.Range, "0,8,0.05")] public float AutoReflectMinHeight { get; set; } = 1.5f;
    // Cap on the above-water height that drives the geometric mirror.
    // Below this, the reflection tracks player jumps / bobs by sinking
    // below water proportionally; above this, the reflection anchors at
    // the waterline so tall sprites on hills don't have reflections way
    // below the seabed. ~2m is a sensible default — covers jumps and
    // shoulder-deep wading without sinking trees off-world.
    [Export(PropertyHint.Range, "0,8,0.05")] public float MaxReflectionAboveWater { get; set; } = 2.0f;
    // How far below the sprite feet to search for a water surface. 16
    // voxels handles a sprite standing on a pier over a deep lake; beyond
    // that the reflection is so dimmed by water depth tint it won't read.
    [Export(PropertyHint.Range, "0,64,1")] public int WaterReflectionSearchDepth { get; set; } = 16;

    // Toggle for hiding the directional-shadow contribution at runtime
    // (e.g. an undiscovered mob should be totally absent from the scene
    // including its shadow). Updated by owners that need it; the visible
    // sprite stays unaffected.
    public bool CastsShadow
    {
        get => _castsShadow;
        set
        {
            if (_castsShadow == value) { return; }
            _castsShadow = value;
            if (_shadowProxy != null)
            {
                _shadowProxy.CastShadow = _castsShadow
                    ? ShadowCastingSetting.ShadowsOnly
                    : ShadowCastingSetting.Off;
            }
            if (_blockLightShadowProxy != null)
            {
                _blockLightShadowProxy.Visible = _castsShadow;
            }
        }
    }
    private bool _castsShadow = true;

    // Per-scene proxy/reflection material templates wired via [Export].
    // Each LitSprite binds these via the shared-material cache (one
    // ShaderMaterial per (template, texture) so Godot can batch).
    [Export] public ShaderMaterial ShadowCasterTemplate { get; set; }
    // Visible-only sibling of ShadowCasterTemplate, used by the
    // BlockLightShadowProjector pass. Two proxies per sprite, one job
    // each: the sun/moon proxy uses ShadowCasterTemplate (ShadowsOnly,
    // default layer, ALBEDO=0) and the projector proxy uses this
    // template (visible, projector-only layer, ALBEDO=1). Combining
    // them into one CastShadow.On proxy made per-sprite sun/moon
    // shadows vanish even though terrain shadows kept casting.
    [Export] public ShaderMaterial BlockLightShadowCasterTemplate { get; set; }
    [Export] public ShaderMaterial ReflectionTemplate { get; set; }

    private Sprite3D _shadowProxy;
    private Sprite3D _blockLightShadowProxy;
    private Sprite3D _reflection;
    private ShaderMaterial _reflectionMaterial;

    public override void _Ready()
    {
        // The visible sprite never casts directly — the proxy below does, with
        // sun-aligned billboard math. Casting from the visible (camera-aligned)
        // sprite produces edge-on slivers from the sun's POV.
        CastShadow = ShadowCastingSetting.Off;
        base._Ready();
    }

    // Push every per-instance shader value to a single instance RID. Used
    // when a sprite (visible / shadow / reflection) is first created to
    // seed its render data. Adds the upright-only uniforms (align_to_terrain,
    // terrain_normal, forward_offset) on top of the base's common ones.
    protected override void InitInstanceUniformsFor(Rid rid, Vector2I spriteSize, Vector2I regionOrigin)
    {
        base.InitInstanceUniformsFor(rid, spriteSize, regionOrigin);
        if (!rid.IsValid)
        {
            return;
        }
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "align_to_terrain", _alignToTerrain);
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "terrain_normal", _terrainNormal);
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "forward_offset", _forwardOffset);
    }

    // Push a per-sprite shader value into the per-instance render data of the
    // visible sprite + shadow proxy + water reflection (whichever exist).
    // After the instance-uniform refactor every per-sprite value lives in
    // RenderingServer instance data, NOT on the (now shared) materials, so
    // multiple LitSprites can share one ShaderMaterial and Godot can batch
    // their draws. RenderingServer silently ignores names a shader doesn't
    // declare, so pushing silhouette to the shadow proxy is a harmless no-op.
    protected override void PushAlignmentUniform(StringName name, Variant value)
    {
        base.PushAlignmentUniform(name, value);
        if (_shadowProxy != null)
        {
            Rid shadowRid = _shadowProxy.GetInstance();
            if (shadowRid.IsValid)
            {
                RenderingServer.InstanceGeometrySetShaderParameter(shadowRid, name, value);
            }
        }
        if (_blockLightShadowProxy != null)
        {
            Rid projRid = _blockLightShadowProxy.GetInstance();
            if (projRid.IsValid)
            {
                RenderingServer.InstanceGeometrySetShaderParameter(projRid, name, value);
            }
        }
        if (_reflection != null)
        {
            Rid reflRid = _reflection.GetInstance();
            if (reflRid.IsValid)
            {
                RenderingServer.InstanceGeometrySetShaderParameter(reflRid, name, value);
            }
        }
    }

    // Keep the proxy sprites' Sprite3D mesh bounds in sync when a frame
    // animator swaps the region. Material/uniform pushes happen in the base.
    protected override void OnFrameChanged(Rect2 region)
    {
        if (_shadowProxy != null)
        {
            _shadowProxy.RegionEnabled = true;
            _shadowProxy.RegionRect = region;
            _shadowProxy.Offset = Offset;
        }
        if (_blockLightShadowProxy != null)
        {
            _blockLightShadowProxy.RegionEnabled = true;
            _blockLightShadowProxy.RegionRect = region;
            _blockLightShadowProxy.Offset = Offset;
        }
        if (_reflection != null)
        {
            _reflection.RegionEnabled = true;
            _reflection.RegionRect = region;
            _reflection.Offset = Offset;
        }
    }

    protected override void Apply()
    {
        ApplyCommonAuthoring(out Vector2I spriteSize, out Vector2I regionOrigin);

        if (Engine.IsEditorHint())
        {
            MaterialOverride = null;
            return;
        }

        if (MaterialTemplate == null)
        {
            GD.PushError($"LitSprite '{Name}' is missing MaterialTemplate.");
            MaterialOverride = null;
            return;
        }

        // Bind the shared material for this (template, texture) — every
        // LitSprite that hits the same combo points at the same material,
        // which is what enables Godot's draw-call batching. Per-sprite values
        // live in RenderingServer instance data and are pushed below.
        ShaderMaterial sharedMat = GetSharedMaterial(MaterialTemplate, Texture);
        if (MaterialOverride != sharedMat)
        {
            MaterialOverride = sharedMat;
        }

        // Push the per-instance render data for the visible sprite. Texture/
        // region change on every animation frame group, so they go through
        // here every Apply; the rest only differ when properties have been
        // mutated, but pushing is cheap and idempotent.
        InitInstanceUniformsFor(GetInstance(), spriteSize, regionOrigin);

        EnsureShadowProxy(spriteSize, regionOrigin);
        EnsureReflection(spriteSize, regionOrigin);

        // Static-prop fast path: if this sprite has no water reflection and
        // no MirrorByYaw, _Process has literally nothing to do —
        // UpdateReflection's first line returns on _reflection == null,
        // UpdateYawMirror's first line returns on !MirrorByYaw. Most world
        // props (trees, barrels, decor) hit this case and shouldn't pay
        // the per-frame profiler-scope + two-null-check overhead. The
        // _needsProcess flag is AND-ed with visibility in the
        // VisibilityChanged callback (in SpriteBase) so static props stay
        // SetProcess(false) across visibility toggles. Mobs/players have
        // MirrorByYaw=true and keep ticking.
        _needsProcess = _reflection != null || MirrorByYaw;
        SetProcess(_needsProcess && IsVisibleInTree());
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint())
        {
            return;
        }
        // No IsVisibleInTree gate here — the VisibilityChanged hookup in
        // SpriteBase._Ready calls SetProcess(false) when this sprite goes
        // hidden, so we only get here on visible frames. The next time
        // visibility flips back on, the first _Process tick re-derives
        // reflection position and mirror state from scratch
        // (UpdateReflection's _lastReflectionVoxel cache will see the
        // position-changed delta).
        using var _profLitSprite = Profiler.Sample("LitSprite.Process");
        using (Profiler.Sample("LitSprite.UpdateReflection"))
        {
            UpdateReflection();
        }
        using (Profiler.Sample("LitSprite.UpdateYawMirror"))
        {
            UpdateYawMirror();
        }
    }

    // Per-frame flip based on the sprite's world yaw vs the camera. A
    // character whose forward (GlobalBasis.Z — the +Z convention matching
    // Player/Mob's Atan2(x, z) yaw setter) points to the left of camera-
    // right gets `sprite_mirror` XOR'd to true, so side-view art reads as
    // consistently facing whichever way the character is walking. FlipH
    // stays as the authored baseline (for art drawn facing a given side)
    // and is XOR'd into the final uniform.
    private bool _yawMirrorInitialized;
    private bool _yawMirrorLast;
    // Effective mirror state currently pushed to the shader: FlipH XOR the
    // camera-yaw flip. Read by external snapshot consumers (e.g.
    // DashGhostTrail) that need to render the same mirrored frame the source
    // is showing this tick. Falls back to FlipH alone before UpdateYawMirror
    // has cached a value (first frame post-spawn, or MirrorByYaw disabled).
    public bool EffectiveMirror => _yawMirrorInitialized ? _yawMirrorLast : FlipH;
    // Camera lookup is a tree traversal (viewport → active camera stack);
    // caching it saves that cost per LitSprite per frame. Refreshed lazily
    // when the cached reference becomes invalid (camera freed, scene swap).
    private Camera3D _cachedCamera;

    private void UpdateYawMirror()
    {
        if (!MirrorByYaw)
        {
            return;
        }
        if (_cachedCamera == null || !IsInstanceValid(_cachedCamera))
        {
            _cachedCamera = GetViewport()?.GetCamera3D();
            if (_cachedCamera == null)
            {
                return;
            }
        }
        Vector3 forward = GlobalBasis.Z;
        forward.Y = 0f;
        Vector3 camRight = _cachedCamera.GlobalBasis.X;
        camRight.Y = 0f;
        // dot < 0 means the character's forward points to the left side of
        // the camera's view; flip the sprite to match. Exact-zero (facing
        // directly toward/away) resolves to "not flipped" and stays stable
        // there, so a character walking straight at the camera won't
        // flicker between the two states.
        bool yawMirror = forward.Dot(camRight) < 0f;
        bool finalMirror = FlipH ^ yawMirror;
        if (_yawMirrorInitialized && finalMirror == _yawMirrorLast)
        {
            return;
        }
        _yawMirrorLast = finalMirror;
        _yawMirrorInitialized = true;
        PushAlignmentUniform("sprite_mirror", finalMirror);
    }

    private void EnsureShadowProxy(Vector2I spriteSize, Vector2I regionOrigin)
    {
        if (Texture == null)
        {
            return;
        }
        if (ShadowCasterTemplate != null)
        {
            if (_shadowProxy == null)
            {
                _shadowProxy = new Sprite3D();
                _shadowProxy.Name = "ShadowProxy";
                _shadowProxy.Centered = false;
                _shadowProxy.PixelSize = 1.0f;
                _shadowProxy.CastShadow = _castsShadow
                    ? ShadowCastingSetting.ShadowsOnly
                    : ShadowCastingSetting.Off;
                _shadowProxy.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
                AddChild(_shadowProxy);
            }
            _shadowProxy.Texture = Texture;
            _shadowProxy.Offset = Offset;
            _shadowProxy.RegionEnabled = RegionEnabled;
            _shadowProxy.RegionRect = RegionRect;

            ShaderMaterial sharedShadow = GetSharedMaterial(ShadowCasterTemplate, Texture);
            if (_shadowProxy.MaterialOverride != sharedShadow)
            {
                _shadowProxy.MaterialOverride = sharedShadow;
            }
            InitInstanceUniformsFor(_shadowProxy.GetInstance(), spriteSize, regionOrigin);
        }

        // Second proxy: visible-only on the projector layer, so the
        // BlockLightShadowProjector's SubViewport can read silhouettes.
        // Cast shadow OFF — sun/moon atlas casting is handled by the
        // ShadowsOnly proxy above on the default layer. Main camera's
        // cull mask excludes SHADOW_PROXY_LAYER_MASK, so this proxy
        // doesn't appear visibly in the player's view.
        if (BlockLightShadowCasterTemplate != null)
        {
            if (_blockLightShadowProxy == null)
            {
                _blockLightShadowProxy = new Sprite3D();
                _blockLightShadowProxy.Name = "BlockLightShadowProxy";
                _blockLightShadowProxy.Centered = false;
                _blockLightShadowProxy.PixelSize = 1.0f;
                _blockLightShadowProxy.CastShadow = ShadowCastingSetting.Off;
                _blockLightShadowProxy.Layers = BlockLightShadowProjector.SHADOW_PROXY_LAYER_MASK;
                _blockLightShadowProxy.Visible = _castsShadow;
                _blockLightShadowProxy.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
                AddChild(_blockLightShadowProxy);
            }
            _blockLightShadowProxy.Texture = Texture;
            _blockLightShadowProxy.Offset = Offset;
            _blockLightShadowProxy.RegionEnabled = RegionEnabled;
            _blockLightShadowProxy.RegionRect = RegionRect;

            ShaderMaterial sharedProj = GetSharedMaterial(BlockLightShadowCasterTemplate, Texture);
            if (_blockLightShadowProxy.MaterialOverride != sharedProj)
            {
                _blockLightShadowProxy.MaterialOverride = sharedProj;
            }
            InitInstanceUniformsFor(_blockLightShadowProxy.GetInstance(), spriteSize, regionOrigin);
        }
    }

    // Decides whether to spawn / keep a flipped reflection child for this
    // sprite. Auto compares the sprite's rendered world-space height
    // (pixels × pixel_size) against `AutoReflectMinHeight` so tall props
    // and characters reflect while small detail sprites don't pay the cost.
    private bool ShouldReflect(Vector2I spriteSize)
    {
        switch (Reflects)
        {
            case ReflectMode.Off:
                return false;
            case ReflectMode.On:
                return true;
            default:
                // Sprite world-space height: pixel_size IS 1 world-unit-per-
                // pixel here at runtime (per Apply()), so the chunky-pixel
                // global scales the rendered size. Use the project's chunky
                // pixel scale to derive the visible meters.
                float pxSize = GetEditorPixelSize();
                return spriteSize.Y * pxSize >= AutoReflectMinHeight;
        }
    }

    private void EnsureReflection(Vector2I spriteSize, Vector2I regionOrigin)
    {
        if (ReflectionTemplate == null || Texture == null)
        {
            return;
        }
        if (!ShouldReflect(spriteSize))
        {
            // Tear down any existing reflection if Reflects was changed at
            // runtime (e.g. inspector tweak in editor) so a previously-On
            // sprite cleans up properly.
            if (_reflection != null)
            {
                _reflection.QueueFree();
                _reflection = null;
                _reflectionMaterial = null;
            }
            return;
        }
        if (_reflection == null)
        {
            _reflection = new Sprite3D();
            _reflection.Name = "WaterReflection";
            _reflection.Centered = false;
            _reflection.PixelSize = 1.0f;
            _reflection.CastShadow = ShadowCastingSetting.Off;
            _reflection.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
            _reflection.Visible = false;
            AddChild(_reflection);
        }
        _reflection.Texture = Texture;
        _reflection.Offset = Offset;
        _reflection.RegionEnabled = RegionEnabled;
        _reflection.RegionRect = RegionRect;

        // Shared reflection material per (template, texture) — same batching
        // win as the shadow proxy. _reflectionMaterial is kept around because
        // UpdateReflection pushes water_y / source_world_pos to it; with the
        // shared-material model those are now per-instance uniforms pushed via
        // RenderingServer instead, so _reflectionMaterial only serves as the
        // "do I currently have a reflection" flag and the shared-material
        // reference for completeness.
        ShaderMaterial sharedRefl = GetSharedMaterial(ReflectionTemplate, Texture);
        if (sharedRefl != null)
        {
            // Reflection shader samples the same ripple normal-map pair voxel_water
            // uses (sprite_prop_reflection_multimesh.gdshader does the same).
            // Without this the default-white sampler collapses the physics-derived
            // UV jitter to a constant tilt at every fragment, so reflections render
            // but never actually shimmer. Setting unconditionally — the shared
            // material caches across instances, so re-setting the same textures on
            // a hit is a cheap no-op.
            sharedRefl.SetShaderParameter("ripple_tex_a", GD.Load<Texture2D>("res://assets/textures/water_ripple_a.tres"));
            sharedRefl.SetShaderParameter("ripple_tex_b", GD.Load<Texture2D>("res://assets/textures/water_ripple_b.tres"));
        }
        if (_reflection.MaterialOverride != sharedRefl)
        {
            _reflection.MaterialOverride = sharedRefl;
        }
        _reflectionMaterial = sharedRefl;
        InitInstanceUniformsFor(_reflection.GetInstance(), spriteSize, regionOrigin);
    }

    // Floored voxel coord + Y the cached reflection result was computed at.
    // The reflection result is invariant within an XZ voxel column (water
    // surface lookup floors XZ) and within the same Y voxel (the in-water
    // vs above-water branch keys off the floored Y of src vs water_y).
    // Caching by float distance instead caused the short-circuit to miss
    // every frame on RigidBody3D-anchored sprites: mobs that are visually
    // stationary still take micro-rotation steps in Mob._PhysicsProcess
    // (yaw lerp toward targetYaw), which shifts GlobalPosition of any
    // child sprite that isn't at the exact pivot, busting a sub-mm
    // distance epsilon. The voxel-keyed check absorbs all sub-voxel
    // motion. INT_MIN sentinel in X forces the first call through.
    private Vector3I _lastReflectionVoxel = new(int.MinValue, 0, 0);
    private float _lastReflectionSrcY = float.NaN;
    private float _cachedWaterY = float.NaN;
    private bool _cachedWaterYHasValue;

    // Per-frame: query the water surface Y under this sprite, position the
    // flipped copy on the opposite side of the surface, and push source
    // position into the shader (reflection lighting matches the sprite's
    // source voxel, not wherever the reflection happens to land).
    private void UpdateReflection()
    {
        if (_reflection == null || _reflectionMaterial == null)
        {
            return;
        }
        if (!CVars.spriteReflections.Value)
        {
            if (_reflection.Visible)
            {
                _reflection.Visible = false;
            }
            return;
        }
        Vector3 src = GlobalPosition;
        Vector3I voxel = new(
            Mathf.FloorToInt(src.X),
            Mathf.FloorToInt(src.Y),
            Mathf.FloorToInt(src.Z));
        // Two-tier cache.
        //   Tier 1 (voxel changed): re-run FindWaterSurfaceY. This is the
        //          expensive path — up to 5 rings × 25 columns × 16 voxels
        //          of voxel reads. Only triggers on voxel-coordinate change.
        //   Tier 2 (voxel same, src.Y changed): keep the cached waterY,
        //          just re-derive localY and re-push the per-frame transform.
        //          Skips the voxel scan entirely so a sprite jittering by
        //          1e-7 Y doesn't pay 2000 voxel reads to land on the same
        //          waterY value.
        //   Tier 3 (voxel same, src.Y same): full short-circuit.
        bool voxelChanged = voxel != _lastReflectionVoxel;
        if (!voxelChanged && src.Y == _lastReflectionSrcY)
        {
            return;
        }
        _lastReflectionSrcY = src.Y;

        float? waterY;
        if (voxelChanged)
        {
            using (Profiler.Sample("LitSprite.FindWaterSurfaceY"))
            {
                waterY = FindWaterSurfaceY(src);
            }
            _lastReflectionVoxel = voxel;
            _cachedWaterY = waterY ?? 0f;
            _cachedWaterYHasValue = waterY.HasValue;
        }
        else
        {
            waterY = _cachedWaterYHasValue ? _cachedWaterY : null;
        }
        if (!waterY.HasValue)
        {
            if (_reflection.Visible)
            {
                _reflection.Visible = false;
            }
            return;
        }
        // Two-mode anchor (matches sprite_prop_reflection_multimesh.gdshader's
        // logic for static props):
        //   - Source IN water (above_water <= 0): true geometric mirror
        //     (2*(water_y - src.y)). Half-submerged sources need the visible
        //     half of the reflection to be optically continuous with the
        //     above-water source — that's only what a real mirror gives.
        //   - Source ABOVE water: flip about the source's BASE (localY = 0).
        //     The reflection's top sits at src.y (the player's / mob's feet),
        //     and the body extends downward. The fragment shader's stencil
        //     mask clips us to water-visible pixels and the world-Y discard
        //     fires only for the in-water case, so a player on a 5-meter
        //     cliff doesn't lose their reflection; the shape just paints onto
        //     whichever water surface is visible from their XZ direction.
        //     Inaccurate optically but reads correctly and unifies with how
        //     props handle above-water reflections.
        float aboveWater = src.Y - waterY.Value;
        float localY;
        if (aboveWater <= 0f)
        {
            localY = 2f * (waterY.Value - src.Y);
        }
        else
        {
            localY = 0f;
        }
        _reflection.Position = new Vector3(0f, localY, 0f);
        _reflection.Visible = true;
        // water_y / source_world_pos are now per-instance uniforms on the
        // reflection sprite — the (potentially shared) ReflectionMaterial
        // can no longer carry per-sprite values.
        Rid reflRid = _reflection.GetInstance();
        if (reflRid.IsValid)
        {
            RenderingServer.InstanceGeometrySetShaderParameter(reflRid, "water_y", waterY.Value);
            RenderingServer.InstanceGeometrySetShaderParameter(reflRid, "source_world_pos", src);
        }
    }

    // XZ search radius (in voxels) for the water surface lookup. Mirrors
    // MultimeshPropSprite.WATER_SEARCH_XZ_RADIUS. The sprite's own column
    // is checked first; if no water there (player standing right at the
    // shoreline whose floored XZ lands on dry ground), expand outward in
    // concentric square rings until we find a water column. Same per-body
    // water surface lands at the same Y across the whole pond, so the
    // first hit's Y is the right reflection plane regardless of which
    // neighbor column it came from.
    private const int WATER_SEARCH_XZ_RADIUS = 4;

    // Return the world Y of the nearest water surface within
    // WATER_SEARCH_XZ_RADIUS XZ voxels. Handles two cases per column:
    //   (a) Sprite IS in water (swimming): walk upward until we exit
    //       water; the exit voxel's bottom face is the surface.
    //   (b) Sprite is above water: walk downward until we hit water;
    //       that voxel's top face is the surface.
    // Null if no water is within search depth in any of the rings.
    private float? FindWaterSurfaceY(Vector3 world)
    {
        WorldState ws = World.Current?.WorldState;
        if (ws == null)
        {
            return null;
        }
        int cx = Mathf.FloorToInt(world.X);
        int cz = Mathf.FloorToInt(world.Z);
        int startY = Mathf.FloorToInt(world.Y);

        // Ring-expand outward: r=0 is just (cx, cz); r=1 is the 8 cells
        // around it; etc. Skip cells on the interior of each ring (only
        // the boundary contributes new cells), so each cell is checked
        // exactly once.
        for (int r = 0; r <= WATER_SEARCH_XZ_RADIUS; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (r > 0 && Mathf.Abs(dx) != r && Mathf.Abs(dz) != r)
                    {
                        continue;
                    }
                    float? y = FindWaterInColumn(ws, cx + dx, startY, cz + dz);
                    if (y.HasValue)
                    {
                        return y;
                    }
                }
            }
        }
        return null;
    }

    // Vertical search within one XZ column. Inside-water case walks up
    // until the column exits water; outside-water case walks down until
    // it enters water. Returns the world Y of the surface (the air-voxel
    // floor sitting directly above the topmost water voxel). Null if no
    // water within WaterReflectionSearchDepth either way.
    private float? FindWaterInColumn(WorldState ws, int wx, int startY, int wz)
    {
        if (ws.GetVoxelWorld(wx, startY, wz) == VoxelType.Water)
        {
            int maxY = startY + WaterReflectionSearchDepth;
            for (int y = startY + 1; y <= maxY; y++)
            {
                if (ws.GetVoxelWorld(wx, y, wz) != VoxelType.Water)
                {
                    return y;
                }
            }
            return null;
        }

        int minY = startY - WaterReflectionSearchDepth;
        for (int y = startY - 1; y >= minY; y--)
        {
            if (ws.GetVoxelWorld(wx, y, wz) == VoxelType.Water)
            {
                return y + 1;
            }
        }
        return null;
    }
}
