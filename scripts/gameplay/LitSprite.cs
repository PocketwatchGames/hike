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

    private bool _ready;

    public override void _Ready()
    {
        _ready = true;
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
