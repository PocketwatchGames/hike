using Godot;

// A Sprite3D that renders through the sprite_lit shader and authors itself
// from standard Sprite3D properties. Authoring contract:
//
//   Texture        - the source texture (Sprite3D base property)
//   RegionEnabled  - enable to draw a sub-rect (Sprite3D base property)
//   RegionRect     - the sub-rect in pixels (Sprite3D base property); if
//                    RegionEnabled is false the full texture is used.
//   Mirror         - when true, a 50% coin flip at spawn time decides
//                    whether to horizontally mirror the sprite. Gives
//                    grass patches, trees etc. per-instance variety
//                    without authoring separate flipped art.
//   ScaleMin/Max   - uniform multiplier applied to the sprite's on-screen
//                    size, rolled once at spawn from [min..max]. Default
//                    (1, 1) is "no variation". Stored on the Node3D as
//                    Scale; the sprite shaders read it out of MODEL_MATRIX
//                    so shadow proxy, reflection, and AO decal all scale
//                    together through the normal parent transform chain.
//
// Pixel-size, centered, and offset are derived from Texture / RegionRect
// so authors don't have to keep them in sync.
//
// Shadows: the visible LitSprite never casts (CastShadow.Off). At runtime
// it spawns two hidden children:
//   - A sun-billboarded shadow caster Sprite3D (CastShadow.ShadowsOnly) that
//     contributes its silhouette to Godot's directional shadow atlas. The
//     caster's vertex math sun-aligns automatically because INV_VIEW_MATRIX
//     during the shadow pass is the directional light's camera. Set
//     CastsShadow = false to suppress this (e.g. an undiscovered mob).
//   - An AO Decal projecting straight down for ground-contact darkening.
//     This works in any environment (cave, surface) and is independent of
//     directional shadow — the visual "this thing is on this floor" cue.
//
// In the editor this node falls back to Sprite3D's default unshaded path so
// the sprite is visible while authoring colliders. The sprite_lit shader is
// applied at runtime by Duplicate()ing the shared MaterialTemplate and
// binding the per-instance shader params.
[Tool]
[GlobalClass]
public partial class LitSprite : Sprite3D
{
    // When true, each instance has a 50% chance of being horizontally
    // mirrored at spawn. The coin flip is XOR'd into Sprite3D.FlipH in
    // _Ready (so an author who sets FlipH on the scene keeps that as
    // the baseline, with Mirror adding a coin flip around it). FlipH is
    // then the authoritative stored state — the shaders read it through
    // the sprite_mirror uniform because Sprite3D's built-in FlipH drives
    // mesh UVs, which these texelFetch-based shaders ignore.
    [Export] public bool Mirror { get; set; }

    // When true, _Process flips `sprite_mirror` each frame based on whether
    // the sprite's world yaw points to the left or right of the camera.
    // Intended for side-view character art so the sprite's facing direction
    // tracks the node's Rotation.Y. FlipH still acts as the authored
    // baseline (useful when the art was drawn facing the opposite side)
    // and is XOR'd with the yaw-derived flip.
    [Export] public bool MirrorByYaw { get; set; }

    // When true, Apply() forces Offset to (-width/2, 0) — i.e. the sprite's
    // anchor sits at the center of its bottom edge. Good for grounded
    // props (trees, grass, mobs) so the Node3D's world position lands on
    // the ground and the sprite rises from there. Re-applied whenever
    // anything that affects width changes (TextureChanged, region). When
    // false, Offset is left as-authored so non-grounded sprites can
    // anchor themselves arbitrarily. Centered is always forced false —
    // the sprite shaders rely on that for their FRAGCOORD→tex_coord math.
    [Export] public bool CenteredAtBase { get; set; } = true;

    // Random uniform-scale range, applied once at spawn as Node3D.Scale.
    // Default (1, 1) disables variation. Inclusive bounds. The sprite
    // shaders read scale out of MODEL_MATRIX so the visible sprite,
    // shadow proxy, water reflection, and AO decal all pick it up via
    // the standard parent transform chain — no shader uniform plumbing.
    [Export] public float ScaleMin { get; set; } = 1.0f;
    [Export] public float ScaleMax { get; set; } = 1.0f;

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
            _forwardOffset = value;
            PushAlignmentUniform("forward_offset", value);
        }
    }
    private float _forwardOffset = 0f;

    // Discovery fade (0 = fully dithered away, 1 = fully opaque). Pushed to
    // all three materials so the cast shadow and water reflection stipple
    // in lockstep with the visible body. Mob.cs drives this off its
    // discovery state over a ~0.1s fade.
    public float Visibility
    {
        get => _visibility;
        set
        {
            _visibility = value;
            PushAlignmentUniform("visibility", value);
        }
    }
    private float _visibility = 1f;

    // Silhouette blend (0 = lit normally, 1 = replaced by SilhouetteTint).
    // Only pushed to the visible + reflection materials — the shadow caster
    // outputs binary alpha into the atlas and has no color channel to tint.
    public float Silhouette
    {
        get => _silhouette;
        set
        {
            _silhouette = value;
            if (MaterialOverride is ShaderMaterial mat)
            {
                mat.SetShaderParameter("silhouette_amount", value);
            }
            _reflectionMaterial?.SetShaderParameter("silhouette_amount", value);
        }
    }
    private float _silhouette = 0f;

    // Flat color the silhouette blends to at Silhouette = 1. Default black
    // reads against almost any background; callers can tint it per-mob
    // (e.g. colored silhouettes for different faction memories).
    public Color SilhouetteTint
    {
        get => _silhouetteTint;
        set
        {
            _silhouetteTint = value;
            Vector3 rgb = new(value.R, value.G, value.B);
            if (MaterialOverride is ShaderMaterial mat)
            {
                mat.SetShaderParameter("silhouette_tint", rgb);
            }
            _reflectionMaterial?.SetShaderParameter("silhouette_tint", rgb);
        }
    }
    private Color _silhouetteTint = Colors.Black;

    // Mirror the alignment uniform onto all three active materials (visible
    // sprite, shadow caster, water reflection) so a rolled sprite's cast
    // shadow and reflection match its world-space shape.
    private void PushAlignmentUniform(string name, Variant value)
    {
        if (MaterialOverride is ShaderMaterial mat)
        {
            mat.SetShaderParameter(name, value);
        }
        if (_shadowProxy?.MaterialOverride is ShaderMaterial smat)
        {
            smat.SetShaderParameter(name, value);
        }
        _reflectionMaterial?.SetShaderParameter(name, value);
    }

    // Swap the rendered region of the sprite sheet. Cheap enough to call
    // per-frame from an animator — unlike Apply(), this does not duplicate
    // materials (so per-instance uniforms like Visibility/Silhouette stay)
    // and does not re-ensure the shadow/reflection/AO children. The shader
    // does its own texelFetch from sprite_region_origin + sprite_size, so
    // animating is just "push a new region" to the three live materials and
    // keep the shadow proxy + reflection's Sprite3D mesh bounds in sync.
    public void SetFrame(Rect2 region)
    {
        if (RegionEnabled && RegionRect == region)
        {
            return;
        }
        RegionEnabled = true;
        RegionRect = region;

        Vector2I size = new((int)region.Size.X, (int)region.Size.Y);
        Vector2I origin = new((int)region.Position.X, (int)region.Position.Y);

        if (CenteredAtBase)
        {
            Offset = new Vector2(-size.X / 2.0f, 0);
        }

        PushAlignmentUniform("sprite_size", size);
        PushAlignmentUniform("sprite_region_origin", origin);

        if (_shadowProxy != null)
        {
            _shadowProxy.RegionEnabled = true;
            _shadowProxy.RegionRect = region;
            _shadowProxy.Offset = Offset;
        }
        if (_reflection != null)
        {
            _reflection.RegionEnabled = true;
            _reflection.RegionRect = region;
            _reflection.Offset = Offset;
        }
    }

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

    // Width/depth (in world units) of the AO decal projected under the
    // sprite. Defaults to roughly cover a 1-voxel sprite footprint.
    [Export] public float AoDecalSize { get; set; } = 1.5f;
    // Vertical extent of the decal projection box. Larger = floating sprites
    // can still find a floor below them; the built-in distance fade keeps
    // the blob from looking weirdly strong at extreme hover heights.
    [Export] public float AoDecalDepth { get; set; } = 4.0f;

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
        }
    }
    private bool _castsShadow = true;

    private ShaderMaterial _materialTemplate;
    private ShaderMaterial _shadowCasterTemplate;
    private ShaderMaterial _reflectionTemplate;
    private Texture2D _aoDecalTexture;

    private Sprite3D _shadowProxy;
    private Sprite3D _reflection;
    private ShaderMaterial _reflectionMaterial;
    private Decal _aoDecal;

    public override void _Ready()
    {
        // The visible sprite never casts directly — the proxy below does, with
        // sun-aligned billboard math. Casting from the visible (camera-aligned)
        // sprite produces edge-on slivers from the sun's POV.
        CastShadow = ShadowCastingSetting.Off;
        // Fall back to canonical resources so scenes don't have to re-wire
        // every LitSprite when the shadow + AO system is added. Scene-level
        // overrides still win.
        if (!Engine.IsEditorHint())
        {
            _materialTemplate ??= GD.Load<ShaderMaterial>("res://resources/materials/sprite_lit.tres");
            _shadowCasterTemplate ??= GD.Load<ShaderMaterial>("res://resources/materials/sprite_shadow_caster.tres");
            _reflectionTemplate ??= GD.Load<ShaderMaterial>("res://resources/materials/sprite_reflection.tres");
            _aoDecalTexture ??= GD.Load<Texture2D>("res://resources/materials/ao_blob.tres");
            // Resolve the random mirror flip once and XOR it into FlipH,
            // which becomes the authoritative stored state read by Apply().
            // Rolling once here keeps subsequent Apply() calls (from
            // TextureChanged, etc.) from re-randomizing.
            if (Mirror && GD.Randf() < 0.5f)
            {
                FlipH = !FlipH;
            }
            // Roll the per-instance scale once and bake it into the
            // transform. If the author left the range at its (1, 1)
            // default this is a no-op.
            float s = (float)GD.RandRange(ScaleMin, ScaleMax);
            Scale = new Vector3(s, s, s);
        }
        TextureChanged += Apply;
        Apply();
    }

    // Derives (size, origin) in integer pixels from the Sprite3D's RegionRect
    // when enabled, falling back to the full texture size when not. Shaders
    // and the Offset math need integer values, so the cast happens here.
    private void GetSpriteRect(out Vector2I size, out Vector2I origin)
    {
        if (RegionEnabled && RegionRect.Size.X > 0 && RegionRect.Size.Y > 0)
        {
            size = new Vector2I((int)RegionRect.Size.X, (int)RegionRect.Size.Y);
            origin = new Vector2I((int)RegionRect.Position.X, (int)RegionRect.Position.Y);
            return;
        }
        if (Texture != null)
        {
            Vector2 ts = Texture.GetSize();
            size = new Vector2I((int)ts.X, (int)ts.Y);
            origin = Vector2I.Zero;
            return;
        }
        size = new Vector2I(1, 1);
        origin = Vector2I.Zero;
    }

    private void Apply()
    {
        Centered = false;
        // The runtime sprite_lit shader does its own per-pixel sizing using
        // the `sprite_chunky` global, so PixelSize=1 there. The editor
        // preview has no shader, so we bake the same scale into PixelSize
        // to match in-game size — read straight from project.godot so it
        // can't drift.
        PixelSize = Engine.IsEditorHint() ? GetEditorPixelSize() : 1.0f;
        GetSpriteRect(out Vector2I spriteSize, out Vector2I regionOrigin);
        if (CenteredAtBase)
        {
            Offset = new Vector2(-spriteSize.X / 2.0f, 0);
        }

        if (Engine.IsEditorHint())
        {
            MaterialOverride = null;
            return;
        }

        if (_materialTemplate == null)
        {
            GD.PushError($"LitSprite '{Name}' is missing MaterialTemplate.");
            MaterialOverride = null;
            return;
        }

        var mat = (ShaderMaterial)_materialTemplate.Duplicate();
        mat.SetShaderParameter("sprite_texture", Texture);
        mat.SetShaderParameter("sprite_size", spriteSize);
        mat.SetShaderParameter("sprite_region_origin", regionOrigin);
        mat.SetShaderParameter("sprite_mirror", FlipH);
        mat.SetShaderParameter("align_to_terrain", _alignToTerrain);
        mat.SetShaderParameter("terrain_normal", _terrainNormal);
        mat.SetShaderParameter("forward_offset", _forwardOffset);
        MaterialOverride = mat;

        EnsureShadowProxy(spriteSize, regionOrigin);
        EnsureAoDecal();
        EnsureReflection(spriteSize, regionOrigin);
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint())
        {
            return;
        }
        UpdateReflection();
        UpdateYawMirror();
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
    private void UpdateYawMirror()
    {
        if (!MirrorByYaw)
        {
            return;
        }
        Camera3D cam = GetViewport()?.GetCamera3D();
        if (cam == null)
        {
            return;
        }
        Vector3 forward = GlobalBasis.Z;
        forward.Y = 0f;
        Vector3 camRight = cam.GlobalBasis.X;
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
        if (_shadowCasterTemplate == null || Texture == null)
        {
            return;
        }
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
            // Visible-pass output is suppressed by ShadowsOnly anyway, but we
            // still need the mesh to have valid bounds. Sprite3D handles that.
            AddChild(_shadowProxy);
        }
        _shadowProxy.Texture = Texture;
        _shadowProxy.Offset = Offset;
        _shadowProxy.RegionEnabled = RegionEnabled;
        _shadowProxy.RegionRect = RegionRect;

        var smat = (ShaderMaterial)_shadowCasterTemplate.Duplicate();
        smat.SetShaderParameter("sprite_texture", Texture);
        smat.SetShaderParameter("sprite_size", spriteSize);
        smat.SetShaderParameter("sprite_region_origin", regionOrigin);
        smat.SetShaderParameter("sprite_mirror", FlipH);
        smat.SetShaderParameter("align_to_terrain", _alignToTerrain);
        smat.SetShaderParameter("terrain_normal", _terrainNormal);
        _shadowProxy.MaterialOverride = smat;
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
        if (_reflectionTemplate == null || Texture == null)
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

        _reflectionMaterial = (ShaderMaterial)_reflectionTemplate.Duplicate();
        _reflectionMaterial.SetShaderParameter("sprite_texture", Texture);
        _reflectionMaterial.SetShaderParameter("sprite_size", spriteSize);
        _reflectionMaterial.SetShaderParameter("sprite_region_origin", regionOrigin);
        _reflectionMaterial.SetShaderParameter("sprite_mirror", FlipH);
        _reflectionMaterial.SetShaderParameter("align_to_terrain", _alignToTerrain);
        _reflectionMaterial.SetShaderParameter("terrain_normal", _terrainNormal);
        _reflection.MaterialOverride = _reflectionMaterial;
    }

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
        Vector3 src = GlobalPosition;
        float? waterY = FindWaterSurfaceY(src);
        if (!waterY.HasValue)
        {
            _reflection.Visible = false;
            return;
        }
        // Geometric mirror, capped. Within MaxReflectionAboveWater meters
        // above the water surface, the reflection uses the true mirror
        // position (2*(water_y - src.y) from the source) — so a player
        // jumping or bobbing above water sees their reflection move down
        // on screen accordingly. Beyond that cap, the reflection's anchor
        // stops sinking and falls back to "feet at waterline" so a sprite
        // standing far above the water (tree on a cliff over a lake)
        // doesn't anchor its reflection so deep that it disappears below
        // the seabed.
        float aboveWater = src.Y - waterY.Value;
        float localY;
        if (aboveWater <= MaxReflectionAboveWater)
        {
            localY = 2f * (waterY.Value - src.Y);
        }
        else
        {
            localY = waterY.Value - src.Y;
        }
        _reflection.Position = new Vector3(0f, localY, 0f);
        _reflection.Visible = true;
        _reflectionMaterial.SetShaderParameter("water_y", waterY.Value);
        _reflectionMaterial.SetShaderParameter("source_world_pos", src);
    }

    // Return the world Y of the nearest water surface at this sprite's XZ
    // column. Handles two cases:
    //   (a) Sprite IS in water (swimming): walk upward until we exit
    //       water; the exit voxel's bottom face is the surface.
    //   (b) Sprite is above water: walk downward until we hit water;
    //       that voxel's top face is the surface.
    // Null if no water is within search depth.
    private float? FindWaterSurfaceY(Vector3 world)
    {
        WorldState ws = World.Current?.WorldState;
        if (ws == null)
        {
            return null;
        }
        int wx = Mathf.FloorToInt(world.X);
        int wz = Mathf.FloorToInt(world.Z);
        int startY = Mathf.FloorToInt(world.Y);

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

    private void EnsureAoDecal()
    {
        if (_aoDecalTexture == null)
        {
            return;
        }
        if (_aoDecal == null)
        {
            _aoDecal = new Decal();
            _aoDecal.Name = "AoDecal";
            // Project straight down. Decal's local -Y is the projection axis,
            // so positioning it slightly above the anchor with a tall depth
            // means floating sprites still find a floor below them.
            _aoDecal.Position = new Vector3(0f, AoDecalDepth * 0.5f, 0f);
            _aoDecal.Size = new Vector3(AoDecalSize, AoDecalDepth, AoDecalSize);
            _aoDecal.AlbedoMix = 1.0f;
            _aoDecal.Modulate = new Color(1f, 1f, 1f, 1f);
            // Fade with distance from the projection origin so floating
            // sprites get a faint, larger AO suggestion rather than the same
            // hard blob as a grounded sprite.
            _aoDecal.DistanceFadeEnabled = true;
            _aoDecal.DistanceFadeBegin = 4f;
            _aoDecal.DistanceFadeLength = 2f;
            AddChild(_aoDecal);
        }
        _aoDecal.TextureAlbedo = _aoDecalTexture;
    }

    private static float GetEditorPixelSize()
    {
        Variant entry = ProjectSettings.GetSetting("shader_globals/sprite_chunky");
        if (entry.VariantType == Variant.Type.Dictionary)
        {
            var dict = entry.AsGodotDictionary();
            if (dict.TryGetValue("value", out Variant value))
            {
                return (float)value;
            }
        }
        return 1.0f;
    }
}
