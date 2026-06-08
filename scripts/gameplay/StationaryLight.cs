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

    private WorldState _worldData;
    private World _world;
    private Vector3I _baseWorldPos;
    private bool _active = true;
    private LightSource _source;
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

    public void Initialize(WorldState worldData, World world, Vector3I baseWorldPos)
    {
        _worldData = worldData;
        _world = world;
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

    public void SetActive(bool active)
    {
        _active = active;
        SetProcess(_active && _flicker);

        if (_source == null) { return; }

        if (_active && !_registered)
        {
            // First activation pays the footprint compute. Subsequent
            // toggles ride the amplitude path below.
            _world.AddLightSource(_source);
            _registered = true;
            _flickerTimer = 0f;
        }
        else if (_active)
        {
            _world.SetLightAmplitude(_source, 1f);
            _flickerTimer = 0f;
        }
        else if (_registered)
        {
            _world.SetLightAmplitude(_source, 0f);
        }
    }

    public override void _Process(double delta)
    {
        if (_source == null || _world == null) { return; }
        _flickerTimer -= (float)delta;
        if (_flickerTimer > 0f) { return; }
        _flickerTimer = 1f / Mathf.Max(_flickerHz, 0.01f);
        using var _prof = Profiler.Sample("StationaryLight.Flicker");
        // Far lights hold steady (amp 1) — SetLightAmplitude is a no-op when the
        // amplitude is unchanged, so a culled light costs nothing per tick. Only
        // lights near the player re-deposit their flicker.
        float amp = WithinFlickerRange() ? (float)GD.RandRange(_flickerMin, _flickerMax) : 1f;
        _world.SetLightAmplitude(_source, amp);
    }

    private bool WithinFlickerRange()
    {
        Player p = _world.player;
        if (p == null) { return true; }
        float cull = _worldData.SimData.BlockLightFlickerCullDistance;
        return (p.GlobalPosition - GlobalPosition).LengthSquared() <= cull * cull;
    }
}
