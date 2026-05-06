using Godot;

[GlobalClass]
public partial class StationaryLight : Node3D
{
    // Reach in voxel-units. Up to LightEngine.MAX_LIGHT (60). Roughly
    // emission/FALLOFF_PER_VOXEL voxels of visible light radius.
    [Export] private int _lightEmission = 56;
    // Per-channel tint (alpha ignored). White = neutral; warm orange for fire,
    // cyan for magic, etc. Channel weights scale the deposited contribution.
    [Export] private Color _lightColor = new(1f, 0.75f, 0.4f);

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
    // than a remove+re-add — the cached footprint is reused, skipping the
    // ~1ms diffusion solve on every re-ignition (relevant for cyclic lights
    // like fire traps; harmless for one-shot lights like torches).
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
            Level = _lightEmission,
            Color = _lightColor,
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
        float amp = (float)GD.RandRange(_flickerMin, _flickerMax);
        _world.SetLightAmplitude(_source, amp);
    }
}
