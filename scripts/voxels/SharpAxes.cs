// Per-axis opt-in to the DC mesher's sharp-corner path, stamped per voxel in
// ChunkState.Shape. Each flagged axis: (1) snaps the cell's vertex coord on that
// axis to 0/0.5/1 via the majority-side rule, and (2) for X|Y|Z together,
// flat-shades quads (so floor <-> wall transitions read as creases). Mask axes
// independently:
//   SharpAxes.Y alone  -> flat floors/ceilings, walls keep organic curve.
//   SharpAxes.All      -> fully blocky, square building edges in all axes.
// The Y snap is a hard step — 1-voxel height differentials stay crisp, not
// smoothed. Intentional slopes (ramps, authored terrain blends) author
// SharpAxes.None so the mesher averages the cell via the normal surface-nets
// path and produces a smooth slope.
//
// Each block authors the default stamped when it is written (BlockData
// .defaultShape); the mesher reads the stamped per-voxel value, never the
// block, so worldgen and the editor can override per voxel.
[System.Flags]
public enum SharpAxes
{
    None = 0,
    X = 1,
    Y = 2,
    Z = 4,
    All = X | Y | Z,
}
