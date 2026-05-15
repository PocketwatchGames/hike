using Godot;

// Project-global registry of shared sprite material templates. Wired in
// autoloads/material_registry.tscn (registered as a Godot autoload), so the
// instance is in the scene tree before any scene's _Ready runs. Subclasses
// of SpriteBase (LitSprite, FlatLitSprite) read their material template
// from here at Apply() time instead of carrying a per-scene [Export]
// MaterialTemplate slot — which means authors pick a high-level enum
// ("Standard"/"Character") on the sprite node and the registry resolves
// the actual ShaderMaterial. Keeps every scene from re-wiring the same
// three-or-four standard templates by hand.
//
// Editor: NOT [Tool], so Instance is null when scenes are open in the
// editor. That's fine — every SpriteBase subclass returns from Apply()
// before touching materials when Engine.IsEditorHint() is true.
[GlobalClass]
public partial class MaterialRegistry : Node
{
    public static MaterialRegistry Instance { get; private set; }

    [ExportGroup("Lit (upright billboard)")]
    [Export] public ShaderMaterial LitStandard { get; set; }
    [Export] public ShaderMaterial LitCharacter { get; set; }

    [ExportGroup("Lit (flat ground)")]
    [Export] public ShaderMaterial LitFlat { get; set; }

    // Proxy passes that LitSprite spawns alongside its visible sprite. Each
    // is a single canonical template across the whole game — no per-scene
    // variation — so authors don't need to re-wire them on every sprite. A
    // null entry is fine; LitSprite gracefully skips the corresponding
    // proxy when the entry is absent.
    [ExportGroup("Lit proxies")]
    // Sun-aligned ShadowsOnly proxy that contributes silhouettes to Godot's
    // directional shadow atlas.
    [Export] public ShaderMaterial LitShadowCaster { get; set; }
    // Visible-only sibling on the BlockLightShadowProjector layer (block-
    // light projector reads silhouettes from a SubViewport on this layer).
    [Export] public ShaderMaterial LitBlockLightShadowCaster { get; set; }
    // Flipped child sprite under nearby water surfaces. Reads the same
    // ripple normal-map pair voxel_water uses.
    [Export] public ShaderMaterial LitReflection { get; set; }

    public override void _EnterTree()
    {
        Instance = this;
    }
}
