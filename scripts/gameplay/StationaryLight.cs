using Godot;

[GlobalClass]
public partial class StationaryLight : Node3D
{
    // Per-channel tint (alpha ignored). White = neutral; warm orange for fire,
    // cyan for magic, etc. Channel weights scale the deposited contribution.
    [Export] private Color _lightColor = new(1f, 0.75f, 0.4f);

    // This light's falloff. Distance (reach, voxels) + Falloff (curve shape) also
    // size its flood radius; Brightness is the open-space core intensity (1 ≈
    // white). See LightEngine.ResolveTuning.
    [Export(PropertyHint.Range, "1,32,0.5")] private float _distance = 10f;
    [Export(PropertyHint.Range, "0.3,4,0.05")] private float _falloff = 1.25f;
    [Export(PropertyHint.Range, "0,3,0.01")] private float _brightness = 0.9f;

    // Opt-in flicker. While active, the source's amplitude is re-rolled in
    // [_flickerMin, _flickerMax] every 1/_flickerHz seconds. Each roll costs
    // an O(footprint) chunk re-deposit, so 10–15Hz is the sweet spot —
    // organic without thrashing the light system.
    [Export] private bool _flicker = false;
    [Export] private float _flickerMin = 0.7f;
    [Export] private float _flickerMax = 1.0f;
    [Export] private float _flickerHz = 12f;

    // Fade envelope — the deposited light ramps from 0→full over _fadeInDuration
    // when toggled on and full→0 over _fadeOutDuration when toggled off. Set
    // either to 0 for an instant snap. Combines multiplicatively with flicker.
    // Only runtime toggles fade; spawn/streaming activation snaps (see SetActive's
    // fade arg) so torches don't all flare up as chunks load.
    [Export(PropertyHint.Range, "0,5,0.05")] private float _fadeInDuration = 0.5f;
    [Export(PropertyHint.Range, "0,5,0.05")] private float _fadeOutDuration = 0.5f;

    private WorldState _worldData;
    private Sim _world;
    private Vector3I _baseWorldPos;
    private bool _active = true;
    private LightSource _source;
    // Fade level (0 = off, 1 = full) eased toward _fadeTarget, and the current
    // flicker factor (1 = no dim). The world holds footprint × _fade × _flickerAmp.
    private float _fade = 1f;
    private float _fadeTarget = 1f;
    private float _flickerAmp = 1f;
    // Once registered, the source stays in the world's source list for the
    // lifetime of this node. Off-states are expressed as amplitude=0 rather
    // than a remove+re-add — the cached footprint is reused, skipping the flood
    // recompute on every re-ignition (relevant for cyclic lights like fire
    // traps; harmless for one-shot lights like torches).
    private bool _registered;
    private float _flickerTimer;

    public override void _Ready()
    {
        SetProcess(false);
    }

    public void Initialize(WorldState worldData, Sim sim, Vector3I baseWorldPos)
    {
        _worldData = worldData;
        _world = sim;
        _baseWorldPos = baseWorldPos;
        _source = new LightSource
        {
            Position = baseWorldPos,
            Color = _lightColor,
            Distance = _distance,
            Falloff = _falloff,
            Brightness = _brightness,
        };
    }

    // fade=false snaps instantly (used for spawn/streaming activation); the
    // default fades over _fadeInDuration / _fadeOutDuration for runtime toggles.
    public void SetActive(bool active, bool fade = true)
    {
        _active = active;
        if (_source == null) { return; }

        // Never registered and staying off: nothing has been deposited, so
        // there's nothing to fade — park the envelope and skip processing.
        if (!active && !_registered)
        {
            _fade = 0f;
            _fadeTarget = 0f;
            UpdateProcessing();
            return;
        }

        bool ramp = fade && (active ? _fadeInDuration > 0f : _fadeOutDuration > 0f);
        _fadeTarget = active ? 1f : 0f;

        if (active && !_registered)
        {
            // First activation pays the footprint compute. Subsequent
            // toggles ride the amplitude path below.
            _world.AddLightSource(_source);
            _registered = true;
            _flickerTimer = 0f;
            _flickerAmp = 1f;
            _fade = ramp ? 0f : 1f;
        }
        else if (active)
        {
            _flickerTimer = 0f;
            if (!ramp) { _fade = 1f; }
        }
        else if (!ramp) // !active && _registered
        {
            _fade = 0f;
        }

        ApplyAmplitude();
        UpdateProcessing();
    }

    public override void _Process(double delta)
    {
        if (_source == null || _world == null) { return; }
        using var _prof = Profiler.Sample("StationaryLight.Process");

        // Fade envelope: ease toward _fadeTarget over the tunable duration.
        if (_fade != _fadeTarget)
        {
            float dur = _fadeTarget > _fade ? _fadeInDuration : _fadeOutDuration;
            float step = dur > 0f ? (float)delta / dur : 1f;
            _fade = Mathf.MoveToward(_fade, _fadeTarget, step);
        }

        // Flicker re-roll on its own timer while active.
        if (_active && _flicker)
        {
            _flickerTimer -= (float)delta;
            if (_flickerTimer <= 0f)
            {
                _flickerTimer = 1f / Mathf.Max(_flickerHz, 0.01f);
                // Far lights hold steady (amp 1) — only lights near the player
                // re-deposit their flicker.
                _flickerAmp = WithinFlickerRange() ? (float)GD.RandRange(_flickerMin, _flickerMax) : 1f;
            }
        }

        // SetLightAmplitude is a no-op when the product is unchanged, so a settled
        // light costs nothing here until the fade or flicker moves it.
        ApplyAmplitude();
        UpdateProcessing();
    }

    // Push the current effective amplitude (fade × flicker) to the world.
    private void ApplyAmplitude()
    {
        _world.SetLightAmplitude(_source, _fade * _flickerAmp);
    }

    // Process only while a fade is in flight or an active light is flickering;
    // a settled, steady light parks itself.
    private void UpdateProcessing()
    {
        SetProcess(_fade != _fadeTarget || (_active && _flicker));
    }

    private bool WithinFlickerRange()
    {
        Player p = _world.player;
        if (p == null) { return true; }
        float cull = _worldData.SimData.blockLightFlickerCullDistance;
        return (p.GlobalPosition - GlobalPosition).LengthSquared() <= cull * cull;
    }
}
