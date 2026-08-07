using System.Collections.Generic;
using Godot;

// One exported integer read off a scene's ROOT node, cached per scene path.
//
// For the load-time passes that hold a PackedScene and no node: entity-owned
// voxels are reconciled before any entity is spawned. Reads the packed data
// (SceneState) rather than instantiating — that walk covers every prop in the
// world, and pulling a whole model hierarchy into memory to read one int would
// be most of the cost of the pass.
//
// Only OVERRIDDEN properties are stored in a scene, so a value left at the
// class default isn't found here — which is why every cache supplies the same
// number as its fallback.
public sealed class ScenePropertyCache
{
    private readonly Dictionary<string, int> _byScene = new();
    private readonly StringName _property;
    private readonly int _fallback;

    public ScenePropertyCache(StringName property, int fallback)
    {
        _property = property;
        _fallback = fallback;
    }

    public int Get(PackedScene scene)
    {
        if (scene == null)
        {
            return _fallback;
        }
        string key = scene.ResourcePath;
        if (!string.IsNullOrEmpty(key) && _byScene.TryGetValue(key, out int cached))
        {
            return cached;
        }
        int value = ReadRoot(scene);
        if (!string.IsNullOrEmpty(key))
        {
            _byScene[key] = value;
        }
        return value;
    }

    private int ReadRoot(PackedScene scene)
    {
        const int ROOT_NODE = 0;
        SceneState state = scene.GetState();
        if (state == null || state.GetNodeCount() <= ROOT_NODE)
        {
            return _fallback;
        }
        int count = state.GetNodePropertyCount(ROOT_NODE);
        for (int i = 0; i < count; i++)
        {
            if (state.GetNodePropertyName(ROOT_NODE, i) == _property)
            {
                return state.GetNodePropertyValue(ROOT_NODE, i).AsInt32();
            }
        }
        return _fallback;
    }
}
