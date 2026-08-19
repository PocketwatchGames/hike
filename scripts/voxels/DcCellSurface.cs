using Godot;

// Where the dual-contoured surface actually sits, one point per cell, in the
// mesher's chunk-local space.
//
// Exists so consumers can agree with the DRAWN terrain instead of with the
// voxel lattice. The two disagree by up to a whole cell — dual contouring puts
// one vertex per cell at the density minimizer, and SharpAxes.Y snaps it to 0,
// 0.5 or 1 within the cell — and every consumer that assumed they coincide has
// been wrong in a different way: barriers standing out past a rounded corner in
// thin air, or floating above a surface that snapped down.
public sealed class DcCellSurface
{
    private readonly Vector3[,,] _vert;
    private readonly bool[,,] _has;
    private readonly int _lo;

    public DcCellSurface(Vector3[,,] vert, bool[,,] has, int lo)
    {
        _vert = vert;
        _has = has;
        _lo = lo;
    }

    // Chunk-local position of the surface point in cell (x, y, z), or false
    // where that cell produced no vertex. Coordinates match the terrain mesh's
    // own, so anything built from these lands on the ground that is drawn.
    public bool TryGetLocal(int x, int y, int z, out Vector3 local)
    {
        local = default;
        int i = x - _lo;
        int j = y - _lo;
        int k = z - _lo;
        if (i < 0 || j < 0 || k < 0
            || i >= _has.GetLength(0) || j >= _has.GetLength(1) || k >= _has.GetLength(2)
            || !_has[i, j, k])
        {
            return false;
        }
        local = _vert[i, j, k] + new Vector3(x, y, z);
        return true;
    }
}
