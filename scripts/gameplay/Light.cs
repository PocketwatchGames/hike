using System.Collections.Generic;
using Godot;

public partial class Light : Node3D
{
    [Export] private OmniLight3D _light;
    [Export] private int _lightEmission = 14;

    private WorldState _worldData;
    private VoxelWorld _voxelWorld;
    private Vector3I _baseWorldPos;
    private bool _active = true;

    public void Initialize(WorldState worldData, VoxelWorld voxelWorld, Vector3I baseWorldPos)
    {
        _worldData = worldData;
        _voxelWorld = voxelWorld;
        _baseWorldPos = baseWorldPos;
    }

    public void SetActive(bool active)
    {
        _active = active;
        _light.Visible = _active;
        SetProcess(_active);

        var positions = new List<Vector3I> { _baseWorldPos };
        if (_active)
        {
            _worldData.SetBlockLightWorld(_baseWorldPos.X, _baseWorldPos.Y, _baseWorldPos.Z, _lightEmission);
            _voxelWorld.PropagateLighting(positions);
        }
        else
        {
            _voxelWorld.UpdateLighting(positions);
        }
    }
}
