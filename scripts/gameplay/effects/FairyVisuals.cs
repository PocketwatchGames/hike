using Godot;

// Drives the fairy mob's presentation: a small glowing orb that bobs gently in
// place and carries its own soft lightsource. Authored as its own node (NOT
// under the Mob's discovery-gated _mesh) so the glow is always visible — a
// fairy should read as a hovering light, not fade in and out with player
// awareness. The bob is purely cosmetic; the Mob body stays on the ground and
// the navigator drives locomotion.
//
// Implements IVanishFade so the Mob's escape ascent (Mob.TickVanish) can fade
// the orb and its light out together as the fairy shoots into the sky and
// despawns.
public interface IVanishFade
{
    // visible01: 1 = fully present, 0 = gone. Called each frame of the vanish.
    void SetFade(float visible01);
}

[GlobalClass]
public partial class FairyVisuals : Node3D, IVanishFade
{
    [Export] private MeshInstance3D _orb;
    [Export] private OmniLight3D _light;

    // Vertical bob: the orb oscillates +/- _bobAmplitude (metres) around its
    // authored rest height at _bobFrequency Hz. Phase is randomized per
    // instance so a cluster of fairies doesn't pulse in lockstep.
    [Export] private float _bobAmplitude = 0.15f;
    [Export] private float _bobFrequency = 0.8f;

    private float _orbRestY;
    private float _baseLightEnergy;
    private float _bobPhase;

    public override void _Ready()
    {
        if (_orb != null)
        {
            _orbRestY = _orb.Position.Y;
        }
        if (_light != null)
        {
            _baseLightEnergy = _light.LightEnergy;
        }
        // Randomize starting phase so neighbouring fairies bob out of sync.
        _bobPhase = (float)GD.RandRange(0.0, Mathf.Tau);
    }

    public override void _Process(double delta)
    {
        if (_orb == null)
        {
            return;
        }
        _bobPhase += (float)delta * _bobFrequency * Mathf.Tau;
        Vector3 p = _orb.Position;
        p.Y = _orbRestY + Mathf.Sin(_bobPhase) * _bobAmplitude;
        _orb.Position = p;
    }

    public void SetFade(float visible01)
    {
        float v = Mathf.Clamp(visible01, 0f, 1f);
        if (_orb != null)
        {
            // Shrink to a point AND fade alpha as it rises — robust whether or
            // not the orb material's transparency pipeline kicks in.
            _orb.Scale = Vector3.One * Mathf.Max(0.001f, v);
            // GeometryInstance3D.Transparency: 0 = opaque, 1 = invisible.
            _orb.Transparency = 1f - v;
        }
        if (_light != null)
        {
            _light.LightEnergy = _baseLightEnergy * v;
        }
    }
}
