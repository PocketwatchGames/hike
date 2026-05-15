using Godot;

// Flat-on-ground pixel-art sprite. Renders through the sprite_lit_flat
// shader, which lays the quad in the world XZ plane (camera-yawed) instead
// of as an upright billboard. Pixel-perfect under fixed orthographic
// projection thanks to the same stretch-trick LitSprite uses, just on the
// horizontal depth axis (sprite_stretch_flat = 1/sin(camera pitch)).
//
// Use cases: campfire base / scorch ground, magic circles, blood spatter,
// painted runes — any flat ground decal that benefits from the lit sprite
// pipeline (lightmap, cloud shadow, block-light projector).
//
// Anchor convention: flat sprites always center on their anchor (the
// Node3D position is the middle of the rendered art on the ground). This
// matches how authors think about ground decals — drop the node where you
// want the center, and the sprite radiates out from there. The Sprite3D
// `offset` is forced to (-W/2, -H/2) in ApplyOffset; CenteredAtBase is
// ignored.
//
// Caveat: the sprite rotates with camera yaw to keep the pixel-perfect
// stretch trick valid. That's invisible for rotationally-symmetric art
// but reads as "spinning with the camera" for directional art (an arrow,
// a footprint pointing somewhere). Authors who need world-fixed
// orientation will need a different class+shader (no current use case).
//
// No shadow proxy / water reflection / ForwardOffset / yaw-mirror —
// none apply to a sprite lying flat. This class is intentionally thin;
// the heavy lifting is in SpriteBase.
[Tool]
[GlobalClass]
public partial class FlatLitSprite : SpriteBase
{
    // Flat sprites anchor at full center (the Node3D position is the
    // middle of the sprite, not the bottom edge). Override the base's
    // upright/center-bottom convention via ApplyOffset below.

    public override void _Ready()
    {
        // Bake the -90° X rotation that lays the Sprite3D quad flat. Authors
        // don't have to touch the node transform — drop a FlatLitSprite,
        // wire its template + texture, and the editor immediately previews
        // it lying on the ground. At runtime the flat shader uses
        // skip_vertex_transform and rebuilds vertices from camera basis
        // anyway, so this rotation is invisible to the shader; its only
        // job is keeping the editor preview honest and giving Godot a
        // ground-aligned AABB that matches where the runtime geometry
        // actually lands (horizontal slab around world_origin), so frustum
        // culling stays accurate.
        RotationDegrees = new Vector3(-90f, 0f, 0f);
        base._Ready();
    }

    protected override void ApplyOffset(Vector2I size)
    {
        Offset = new Vector2(-size.X / 2.0f, -size.Y / 2.0f);
    }

    protected override void Apply()
    {
        ApplyCommonAuthoring(out Vector2I spriteSize, out Vector2I regionOrigin);

        if (Engine.IsEditorHint())
        {
            MaterialOverride = null;
            return;
        }

        ShaderMaterial template = MaterialRegistry.Instance?.LitFlat;
        if (template == null)
        {
            GD.PushError($"FlatLitSprite '{Name}': MaterialRegistry has no LitFlat entry.");
            MaterialOverride = null;
            return;
        }

        ShaderMaterial sharedMat = GetSharedMaterial(template, Texture, !Xray);
        if (MaterialOverride != sharedMat)
        {
            MaterialOverride = sharedMat;
        }

        InitInstanceUniformsFor(GetInstance(), spriteSize, regionOrigin);

        // Flat sprites have no per-frame work — no reflection, no yaw mirror.
        // Stay SetProcess(false) so we cost nothing per frame.
        _needsProcess = false;
        SetProcess(false);
    }
}
