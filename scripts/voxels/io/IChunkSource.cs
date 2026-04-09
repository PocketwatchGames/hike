using System.Collections.Generic;
using Godot;

// Source of chunk data. v1 implementation reads from a packed world file at
// startup; future streaming and save-delta layers can implement the same
// interface without touching anything that consumes it.
public interface IChunkSource
{
    Vector3I Min { get; }
    Vector3I Max { get; }
    Vector3 Spawn { get; }

    IEnumerable<Vector3I> EnumerateChunkCoords();

    bool TryLoadChunk(Vector3I coord, out ChunkState state, out List<EntitySimState> entities);
}
