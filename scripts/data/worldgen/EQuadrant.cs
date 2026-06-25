// World quadrant around the origin, in the chunk grid. Matches the legacy
// PickZoneIndex split: east = chunkX >= 0, north = chunkZ >= 0.
public enum EQuadrant
{
    NE, // X >= 0, Z >= 0
    NW, // X <  0, Z >= 0
    SE, // X >= 0, Z <  0
    SW, // X <  0, Z <  0
}
