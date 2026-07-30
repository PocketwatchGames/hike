using Godot;

// The world's player-spawn point. Its own aspect because it lives directly on
// WorldState rather than in a chunk — the template for any future world-level
// scalar the editor learns to author.
public sealed class SpawnAspect : IEditorEditAspect
{
    private Vector3 _before;
    private Vector3 _after;
    private bool _captured;

    public void Touch(WorldState world)
    {
        if (!_captured)
        {
            _before = world.Spawn;
            _captured = true;
        }
    }

    public bool CaptureAfter(WorldState world)
    {
        _after = world.Spawn;
        return _after != _before;
    }

    public void Restore(WorldState world, bool redo, EditorRefresh refresh)
    {
        world.Spawn = redo ? _after : _before;
    }
}
