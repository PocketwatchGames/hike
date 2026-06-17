using Godot;

// One palette entry: recolor a named set of meshes toward `color`. Drives the
// `recolor` / `recolor_amount` instance uniforms on model_lit (see
// model_lit_body.gdshaderinc). `amount` 1 = flat replace (good for uniform
// skin), lower values tint while keeping the texture's albedo variation.
// [Tool] so the editor instantiates it as its real type inside a [Tool] parent
// (MobPalette) — see the sub-resource convention in CLAUDE.md.
[Tool]
[GlobalClass]
public partial class MobRecolorEntry : Resource
{
    // FBX node names to recolor (e.g. "SK_GoblinBody"). Match the mesh names
    // in the mob's scene / FBX.
    [Export] public string[] meshNames = System.Array.Empty<string>();
    [Export] public Color color = Colors.White;
    [Export(PropertyHint.Range, "0,1,0.01")] public float amount = 1f;
}
