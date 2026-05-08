using Godot;

// Common plumbing for every Sprite3D that renders through one of our custom
// pixel-art shaders. Two concrete subclasses today:
//   - LitSprite       : upright camera-facing billboard (sprite_lit shader)
//   - FlatLitSprite   : flat-on-ground camera-yawed quad (sprite_lit_flat shader)
//
// What lives here:
//   - Shared-material cache (GetSharedMaterial) — every sprite that binds the
//     same (template, texture) combo points at one ShaderMaterial so Godot can
//     batch their draws.
//   - Per-instance shader-uniform pushes (PushAlignmentUniform) — base pushes
//     to the visible sprite only; subclasses extend to also push to their
//     proxies (shadow caster, water reflection, etc.).
//   - Authoring conveniences read by every sprite shader: Mirror coin flip,
//     uniform scale roll, region/texture rect derivation.
//   - Per-instance shader values that exist on every variant: Visibility
//     (dither fade), Silhouette / SilhouetteTint, XrayAmount.
//   - VisibilityChanged → SetProcess gate, so hidden sprites stop ticking.
//
// What does NOT live here (variant-specific):
//   - Upright-billboard things (yaw mirror, AlignToTerrain, ForwardOffset,
//     shadow proxy, water reflection) → LitSprite.
//   - Flat-on-ground things (hover offset over terrain) → FlatLitSprite.
[Tool]
[GlobalClass]
public partial class SpriteBase : Sprite3D
{
    // When true, each instance has a 50% chance of being horizontally
    // mirrored at spawn. The coin flip is XOR'd into Sprite3D.FlipH in
    // _Ready (so an author who sets FlipH on the scene keeps that as
    // the baseline, with Mirror adding a coin flip around it). FlipH is
    // then the authoritative stored state — the shaders read it through
    // the sprite_mirror uniform because Sprite3D's built-in FlipH drives
    // mesh UVs, which these texelFetch-based shaders ignore.
    [Export] public bool Mirror { get; set; }

    // Index into MinimapFoliageColors palette. 0 = no minimap stamp; non-zero
    // stamps the matching darken-multiplier at this sprite's XZ during the
    // minimap's prop pass. Same semantic as MultimeshPropSprite.MinimapFoliageId,
    // exposed here so LitSprite / FlatLitSprite / interactives can also
    // appear on the map.
    [Export] public byte MinimapFoliageId { get; set; } = 0;

    // Anchor mode for the sprite's Offset. Default is overridden per
    // subclass — upright sprites anchor at center-bottom, flat sprites
    // anchor at full center. ApplyOffset() is virtual so each subclass
    // picks its own convention; the flag is exposed so authors can
    // disable the override and supply Offset by hand.
    [Export] public bool CenteredAtBase { get; set; } = true;

    // Random uniform-scale range, applied once at spawn as Node3D.Scale.
    // Default (1, 1) disables variation. Inclusive bounds. The sprite
    // shaders read scale out of MODEL_MATRIX so the visible sprite,
    // shadow proxy, and water reflection all pick it up via the standard
    // parent transform chain — no shader uniform plumbing.
    [Export] public float ScaleMin { get; set; } = 1.0f;
    [Export] public float ScaleMax { get; set; } = 1.0f;

    // Discovery fade (0 = fully dithered away, 1 = fully opaque). Pushed to
    // every per-instance render data the subclass owns (visible + any
    // proxies) so the cast shadow / reflection stipple in lockstep with the
    // visible body. Mob.cs drives this off its discovery state over a ~0.1s
    // fade.
    public float Visibility
    {
        get => _visibility;
        set
        {
            // Short-circuit on equal value so the shader-uniform push is
            // skipped for stable frames. Owners (Mob, Player, etc.) used to
            // cache the last-applied value externally; doing it here means
            // every caller benefits without having to remember to.
            if (_visibility == value)
            {
                return;
            }
            _visibility = value;
            PushAlignmentUniform("visibility", value);
        }
    }
    private float _visibility = 1f;

    // Silhouette blend (0 = lit normally, 1 = replaced by SilhouetteTint).
    // Only meaningful on the visible + reflection shaders — the shadow caster
    // outputs binary alpha into the atlas and has no color channel to tint
    // (RenderingServer harmlessly ignores the push for that name there).
    public float Silhouette
    {
        get => _silhouette;
        set
        {
            if (_silhouette == value)
            {
                return;
            }
            _silhouette = value;
            PushAlignmentUniform("silhouette_amount", value);
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
            if (_silhouetteTint == value)
            {
                return;
            }
            _silhouetteTint = value;
            Vector3 rgb = new(value.R, value.G, value.B);
            PushAlignmentUniform("silhouette_tint", rgb);
        }
    }
    private Color _silhouetteTint = Colors.Black;

    // Per-instance fade for the X-ray (next_pass) silhouette. 1 = X-ray
    // fully on whenever occluded, 0 = X-ray entirely suppressed. Player +
    // mobs leave this at 1 so they always silhouette through cover; an
    // InteractiveXray driver pushes 0→1→0 on interactives so chests/doors
    // only X-ray while the player is in probe range.
    public float XrayAmount
    {
        get => _xrayAmount;
        set
        {
            if (_xrayAmount == value)
            {
                return;
            }
            _xrayAmount = value;
            PushAlignmentUniform("xray_amount", value);
        }
    }
    private float _xrayAmount = 1f;

    // Material template wired per-scene via [Export]. Bound through the
    // shared-material cache so every sprite that hits the same (template,
    // texture) combo points at the same ShaderMaterial — the precondition
    // Godot needs to batch their draws.
    [Export] public ShaderMaterial MaterialTemplate { get; set; }

    // Shared materials, keyed by (template, texture). sprite_texture lives
    // on the material because sampler2D can't be an instance uniform in
    // Godot 4; everything else is per-instance.
    private static readonly System.Collections.Generic.Dictionary<(ShaderMaterial, Texture2D), ShaderMaterial> _sharedMaterials = new();

    protected static ShaderMaterial GetSharedMaterial(ShaderMaterial template, Texture2D texture)
    {
        if (template == null || texture == null)
        {
            return null;
        }
        var key = (template, texture);
        if (!_sharedMaterials.TryGetValue(key, out ShaderMaterial mat))
        {
            mat = (ShaderMaterial)template.Duplicate();
            mat.SetShaderParameter("sprite_texture", texture);
            // If the template carries a next_pass (e.g. the character X-ray
            // silhouette wired into sprite_lit_character.tres), Duplicate()'s
            // shallow copy leaves NextPass pointing at the shared template
            // resource — so every character texture would clobber that single
            // material's `sprite_texture` uniform and end up rendering the
            // same character in every silhouette. Specialize per (template,
            // texture) by duplicating the next_pass too and binding the
            // matching texture into it.
            if (template.NextPass is ShaderMaterial nextTemplate)
            {
                ShaderMaterial nextMat = (ShaderMaterial)nextTemplate.Duplicate();
                nextMat.SetShaderParameter("sprite_texture", texture);
                mat.NextPass = nextMat;
            }
            _sharedMaterials[key] = mat;
        }
        return mat;
    }

    // Push a per-sprite shader value into the per-instance render data of
    // the visible sprite. Subclasses override to also push to their proxies
    // (shadow caster, water reflection, etc.) so a Visibility / Silhouette
    // change ripples through every sibling pass at once. RenderingServer
    // silently ignores names a shader doesn't declare, so pushing
    // silhouette to the shadow proxy is a harmless no-op.
    protected virtual void PushAlignmentUniform(StringName name, Variant value)
    {
        Rid selfRid = GetInstance();
        if (selfRid.IsValid)
        {
            RenderingServer.InstanceGeometrySetShaderParameter(selfRid, name, value);
        }
    }

    // True when _Process has work to do (water reflection update OR yaw
    // mirror flip). Static props leave this false so SetProcess stays off
    // across visibility toggles. Subclasses set this in Apply() based on
    // their per-frame needs; the base never toggles it.
    protected bool _needsProcess;

    public override void _Ready()
    {
        if (!Engine.IsEditorHint())
        {
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

            // Subscribe to Node3D's VisibilityChanged signal once and toggle
            // SetProcess based on IsVisibleInTree. The signal fires when
            // *this* node OR any ancestor flips its Visible flag, which is
            // exactly when the cached state could have changed. After this
            // hookup the engine itself stops dispatching _Process to hidden
            // sprites — no per-frame IsVisibleInTree call, no gate sample.
            // At ~900 sprites that's 0.2+ ms/frame back.
            VisibilityChanged += OnVisibilityChanged;
            SetProcess(IsVisibleInTree());
        }
        TextureChanged += Apply;
        Apply();
    }

    private void OnVisibilityChanged()
    {
        // _needsProcess is the per-sprite "_Process has any work" decision
        // made by the subclass in Apply(); visibility is the per-frame
        // "anyone watching" decision. Both must be true to spend the frame.
        SetProcess(_needsProcess && IsVisibleInTree());
    }

    // Derives (size, origin) in integer pixels from the Sprite3D's RegionRect
    // when enabled, falling back to the full texture size when not. Shaders
    // and the Offset math need integer values, so the cast happens here.
    protected void GetSpriteRect(out Vector2I size, out Vector2I origin)
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

    // Apply Offset for the sprite's anchor convention. Default is
    // upright/center-bottom (CenteredAtBase). FlatLitSprite overrides to
    // anchor at full center.
    protected virtual void ApplyOffset(Vector2I size)
    {
        if (CenteredAtBase)
        {
            Offset = new Vector2(-size.X / 2.0f, 0);
        }
        // else: leave as authored
    }

    // Push the per-instance render data for ONE sprite RID. Used when a
    // sprite (visible / shadow / reflection / etc.) is first created to
    // seed its render data, since shader defaults only apply when nothing
    // has ever been written for that name. Subclasses extend to push their
    // upright-only uniforms after calling base.
    protected virtual void InitInstanceUniformsFor(Rid rid, Vector2I spriteSize, Vector2I regionOrigin)
    {
        if (!rid.IsValid)
        {
            return;
        }
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "sprite_size", spriteSize);
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "sprite_region_origin", regionOrigin);
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "sprite_mirror", FlipH);
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "visibility", _visibility);
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "silhouette_amount", _silhouette);
        Vector3 tintRgb = new(_silhouetteTint.R, _silhouetteTint.G, _silhouetteTint.B);
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "silhouette_tint", tintRgb);
        RenderingServer.InstanceGeometrySetShaderParameter(rid, "xray_amount", _xrayAmount);
    }

    // Swap the rendered region of the sprite sheet. Cheap enough to call
    // per-frame from an animator — unlike Apply(), this does not duplicate
    // materials (so per-instance uniforms like Visibility/Silhouette stay)
    // and does not re-ensure the proxy children. The shader does its own
    // texelFetch from sprite_region_origin + sprite_size, so animating is
    // just "push a new region" to the live materials and let the subclass
    // update its proxies.
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

        ApplyOffset(size);

        PushAlignmentUniform("sprite_size", size);
        PushAlignmentUniform("sprite_region_origin", origin);

        OnFrameChanged(region);
    }

    // Hook for subclasses to keep their proxy sprites' Sprite3D mesh
    // bounds in sync with the new region. Base does nothing; LitSprite
    // mirrors region/offset onto its shadow + reflection proxies.
    protected virtual void OnFrameChanged(Rect2 region) { }

    // Common authoring done before the subclass binds its material.
    // Returns the (size, origin) so the subclass can pass them to its
    // own InitInstanceUniformsFor calls without re-querying the rect.
    protected void ApplyCommonAuthoring(out Vector2I spriteSize, out Vector2I regionOrigin)
    {
        Centered = false;
        // The runtime sprite shaders do their own per-pixel sizing using
        // the `sprite_chunky` global, so PixelSize=1 there. The editor
        // preview has no shader, so we bake the same scale into PixelSize
        // to match in-game size — read straight from project.godot so it
        // can't drift.
        PixelSize = Engine.IsEditorHint() ? GetEditorPixelSize() : 1.0f;
        GetSpriteRect(out spriteSize, out regionOrigin);
        ApplyOffset(spriteSize);
    }

    // Reads the chunky-pixel global from project.godot so the editor
    // preview matches in-game pixel size without a runtime shader.
    protected static float GetEditorPixelSize()
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

    // Subclasses override to do their full Apply: ApplyCommonAuthoring,
    // bind the visible sprite's shared material, ensure proxies, push
    // instance uniforms. Base does nothing useful on its own — a SpriteBase
    // node with no subclass will render with whatever Sprite3D default
    // material it has.
    protected virtual void Apply() { }
}
