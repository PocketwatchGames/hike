using Godot;

// One mantleable ledge lip, baked. Cell is the top voxel of a two-voxel wall;
// Faces is the ClimbLedgeMarker.Dir* bitmask of the sides the wall can be
// mantled from.
//
// Baked rather than derived at mesh time because the answer depends on the
// entities standing on the ledge as well as on the rock (see
// ClimbLedgeStamper), and entity colliders are not visible from a chunk build.
// It is a fact about the finished world, so it is decided once by whichever
// producer built that world and stored alongside the voxels.
public readonly struct ClimbLip
{
    public readonly Vector3I Cell;
    public readonly byte Faces;

    public ClimbLip(Vector3I cell, int faces)
    {
        Cell = cell;
        Faces = (byte)faces;
    }
}
