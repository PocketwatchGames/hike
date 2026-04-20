using Godot;

// A Sprite3D that renders through the sprite_lit shader and authors itself
// from a small set of pixel-space settings. Authoring contract:
//
//   Texture        - the source texture (Sprite3D base property)
//   SpriteSize     - source pixel rect to draw (W x H)
//   RegionOrigin   - top-left of that rect inside Texture (atlas offset)
//
// All other Sprite3D fiddly bits (centered, offset, pixel_size, region_*)
// are derived from the above so authors don't have to keep them in sync.
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
    [Export]
    public Vector2I SpriteSize
    {
        get => _spriteSize;
        set { _spriteSize = value; Apply(); }
    }
    private Vector2I _spriteSize = new(1, 1);

    [Export]
    public Vector2I RegionOrigin
    {
        get => _regionOrigin;
        set { _regionOrigin = value; Apply(); }
    }
    private Vector2I _regionOrigin = Vector2I.Zero;

    [Export] public ShaderMaterial MaterialTemplate { get; set; }
    [Export] public ShaderMaterial ShadowCasterTemplate { get; set; }
    [Export] public ShaderMaterial ReflectionTemplate { get; set; }
    [Export] public Texture2D AoDecalTexture { get; set; }

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

    private bool _ready;
    private Sprite3D _shadowProxy;
    private Sprite3D _reflection;
    private ShaderMaterial _reflectionMaterial;
    private Decal _aoDecal;

    public override void _Ready()
    {
        _ready = true;
        // The visible sprite never casts directly — the proxy below does, with
        // sun-aligned billboard math. Casting from the visible (camera-aligned)
        // sprite produces edge-on slivers from the sun's POV.
        CastShadow = ShadowCastingSetting.Off;
        // Fall back to canonical resources so scenes don't have to re-wire
        // every LitSprite when the shadow + AO system is added. Scene-level
        // overrides still win.
        if (!Engine.IsEditorHint())
        {
            ShadowCasterTemplate ??= GD.Load<ShaderMaterial>("res://resources/materials/sprite_shadow_caster.tres");
            ReflectionTemplate ??= GD.Load<ShaderMaterial>("res://resources/materials/sprite_reflection.tres");
            AoDecalTexture ??= GD.Load<Texture2D>("res://resources/materials/ao_blob.tres");
        }
        TextureChanged += Apply;
        Apply();
    }

    private void Apply()
    {
        if (!_ready)
        {
            return;
        }
        Centered = false;
        // The runtime sprite_lit shader does its own per-pixel sizing using
        // the `sprite_chunky` global, so PixelSize=1 there. The editor
        // preview has no shader, so we bake the same scale into PixelSize
        // to match in-game size — read straight from project.godot so it
        // can't drift.
        PixelSize = Engine.IsEditorHint() ? GetEditorPixelSize() : 1.0f;
        Offset = new Vector2(-_spriteSize.X / 2.0f, 0);

        if (Texture != null && _spriteSize.X > 0 && _spriteSize.Y > 0)
        {
            RegionEnabled = true;
            RegionRect = new Rect2(_regionOrigin.X, _regionOrigin.Y, _spriteSize.X, _spriteSize.Y);
        }
        else
        {
            RegionEnabled = false;
        }

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

        var mat = (ShaderMaterial)MaterialTemplate.Duplicate();
        mat.SetShaderParameter("sprite_texture", Texture);
        mat.SetShaderParameter("sprite_size", _spriteSize);
        mat.SetShaderParameter("sprite_region_origin", _regionOrigin);
        MaterialOverride = mat;

        EnsureShadowProxy();
        EnsureAoDecal();
        EnsureReflection();
    }

    public override void _Process(double delta)
    {
        if (!_ready || Engine.IsEditorHint())
        {
            return;
        }
        UpdateReflection();
    }

    private void EnsureShadowProxy()
    {
        if (ShadowCasterTemplate == null || Texture == null)
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

        var smat = (ShaderMaterial)ShadowCasterTemplate.Duplicate();
        smat.SetShaderParameter("sprite_texture", Texture);
        smat.SetShaderParameter("sprite_size", _spriteSize);
        smat.SetShaderParameter("sprite_region_origin", _regionOrigin);
        _shadowProxy.MaterialOverride = smat;
    }

    private void EnsureReflection()
    {
        if (ReflectionTemplate == null || Texture == null)
        {
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

        _reflectionMaterial = (ShaderMaterial)ReflectionTemplate.Duplicate();
        _reflectionMaterial.SetShaderParameter("sprite_texture", Texture);
        _reflectionMaterial.SetShaderParameter("sprite_size", _spriteSize);
        _reflectionMaterial.SetShaderParameter("sprite_region_origin", _regionOrigin);
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
        // Mirror the sprite's anchor across the water surface. The child's
        // LOCAL position is the delta from parent; 2*(water_y - src.y).
        _reflection.Position = new Vector3(0f, 2f * (waterY.Value - src.Y), 0f);
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
        if (AoDecalTexture == null)
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
        _aoDecal.TextureAlbedo = AoDecalTexture;
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
