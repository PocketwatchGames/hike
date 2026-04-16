using Godot;

public partial class Light : Node3D
{
    // Reach in voxel-units. Up to LightEngine.MAX_LIGHT (60). Roughly
    // emission/FALLOFF_PER_VOXEL voxels of visible light radius.
    [Export] private int _lightEmission = 56;
    // Per-channel tint (alpha ignored). White = neutral; warm orange for fire,
    // cyan for magic, etc. Channel weights scale the deposited contribution.
    [Export] private Color _lightColor = new(1f, 0.75f, 0.4f);

    private WorldState _worldData;
    private World _world;
    private Vector3I _baseWorldPos;
    private bool _active = true;
    private LightSource _source;

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
        bool wasRegistered = _source != null && _source.Footprint.Count > 0;
        _active = active;
        SetProcess(_active);

        if (_source == null) { return; }

        if (_active && !wasRegistered)
        {
            _world.AddLightSource(_source);
        }
        else if (!_active && wasRegistered)
        {
            _world.RemoveLightSource(_source);
        }
    }
}
