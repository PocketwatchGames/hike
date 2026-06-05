using Godot;

// One editable row in PlayerAnimManifest.Clips — the per-clip authoring for a
// single animation FBX. Name is the clip name (= the source FBX filename,
// lower-cased: Idle.fbx -> "idle"); Loop toggles the baked loop mode; Speed
// time-scales the baked clip (1 = source speed, 2 = twice as fast, 0.5 = half).
// All of it is baked into swordsman_anims.res when the manifest rebuilds — see
// PlayerAnimManifest. New clips discovered in the source folder get a default
// row appended automatically on rebuild.
[Tool]
[GlobalClass]
public partial class PlayerAnimClipSetting : Resource
{
    [Export] public string Name;
    [Export] public bool Loop = true;

    // Range hint keeps the spinbox sane and avoids the sub-0.01 default-step
    // snapping trap; or_greater allows >4 if ever needed.
    [Export(PropertyHint.Range, "0.1,4,0.05,or_greater")]
    public float Speed = 1f;
}
