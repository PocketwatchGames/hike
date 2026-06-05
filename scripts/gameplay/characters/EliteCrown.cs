using Godot;

// Spinning, bobbing emissive halo that floats over an elite mob (see
// Mob.IsElite). The mesh uses crown_lit.tres — the same model_lit_character +
// model_xray render stack the mob body uses — so the crown dithers, silhouettes
// and X-rays through cover exactly like the mob, but reads as a strong gold glow
// when visible. Mob spawns one per elite, parents it under the mob's mesh
// container above the head, drives its discovery presentation each frame via
// SetDiscoveryVisuals, and frees it on death.
[GlobalClass]
public partial class EliteCrown : Node3D
{
    // The halo mesh. Spun + bobbed in _Process and the target of the per-instance
    // discovery uniforms. A child of this node so the root stays a clean anchor
    // whose transform the Mob owns (head position).
    [Export] private MeshInstance3D _halo;

    // Full revolutions per minute of the in-place spin (about the vertical axis —
    // Godot's TorusMesh lies flat in the XZ plane, so a Y spin reads as a halo
    // turning in its own plane).
    [Export] private float _spinDegreesPerSecond = 60f;
    // Vertical bob: peak offset (meters) and oscillations per second around the
    // halo's authored rest height.
    [Export] private float _bobAmplitude = 0.12f;
    [Export] private float _bobFrequency = 0.8f;

    // Instance-uniform names — must match model_lit_body.gdshaderinc /
    // model_xray.gdshader (and ModelAnimator.SetDiscoveryVisuals).
    private static readonly StringName VisibilityParam = "visibility";
    private static readonly StringName SilhouetteParam = "silhouette_amount";
    private static readonly StringName XrayParam = "xray_amount";

    // Authored rest height of the halo; the bob oscillates around it.
    private float _bobCenterY;
    private float _spinDegrees;
    private float _bobTime;

    // Last pushed discovery values, so a settled crown pays no per-frame uniform
    // marshal (mirrors Mob's gating of the model push).
    private float _lastVisibility = float.NaN;
    private float _lastSilhouette = float.NaN;
    private float _lastXray = float.NaN;

    public override void _Ready()
    {
        if (_halo != null)
        {
            _bobCenterY = _halo.Position.Y;
            // A self-lit halo casting a shadow reads as a black disk on the
            // ground — never wanted.
            _halo.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
    }

    public override void _Process(double delta)
    {
        if (_halo == null)
        {
            return;
        }
        _spinDegrees = Mathf.PosMod(_spinDegrees + _spinDegreesPerSecond * (float)delta, 360f);
        _bobTime += (float)delta;
        float bob = Mathf.Sin(_bobTime * Mathf.Tau * _bobFrequency) * _bobAmplitude;
        _halo.Position = new Vector3(0f, _bobCenterY + bob, 0f);
        _halo.Rotation = new Vector3(0f, Mathf.DegToRad(_spinDegrees), 0f);
    }

    // Push the mob's discovery presentation onto the halo so it fades, silhouettes
    // and X-rays in lockstep with the mob. Same three uniforms Mob feeds its body
    // meshes; silhouette_tint is left at its default (flat black) to match. Gated
    // on change — values only move during a fade.
    public void SetDiscoveryVisuals(float visibility, float silhouette, float xrayAmount)
    {
        if (_halo == null)
        {
            return;
        }
        if (visibility == _lastVisibility && silhouette == _lastSilhouette && xrayAmount == _lastXray)
        {
            return;
        }
        _halo.SetInstanceShaderParameter(VisibilityParam, visibility);
        _halo.SetInstanceShaderParameter(SilhouetteParam, silhouette);
        _halo.SetInstanceShaderParameter(XrayParam, xrayAmount);
        _lastVisibility = visibility;
        _lastSilhouette = silhouette;
        _lastXray = xrayAmount;
    }
}
