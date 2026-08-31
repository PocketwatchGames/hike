using Godot;

// Highest and lowest edited voxel per column, derived from a document's tunnel
// mask.
//
// It exists purely so a query does not have to scan a column to bedrock to learn
// that nothing was ever carved into it — which is what every cutaway and surface
// query would otherwise do, per texel, per rebuild.
//
// The second cache with an invalidation protocol (see TerrainField): the mask is
// the truth and these extents are a summary of it, so anything that writes the
// mask owes this a Note, and anything that rewrites it wholesale — an undo
// restore, a resize — owes it an InvalidateAll.
public class VoxelEditOverlay
{
    private readonly WorldMapState Map;

    private int[,] _topEdit;
    private int[,] _botEdit;

    public VoxelEditOverlay(WorldMapState map)
    {
        Map = map;
    }

    // Highest edited voxel in the column, or below the world floor for none.
    public int Top(int px, int pz)
    {
        EnsureTopEdits();
        return _topEdit[px, pz];
    }

    // Lowest edited voxel in the column, or int.MaxValue for none.
    public int Bottom(int px, int pz)
    {
        EnsureTopEdits();
        return _botEdit[px, pz];
    }

    // Fold one just-written edit into the extents.
    public void Note(int px, int pz, int wy, byte edit)
    {
        EnsureTopEdits();
        if (edit != WorldMapState.EditNone)
        {
            _topEdit[px, pz] = Mathf.Max(_topEdit[px, pz], wy);
            _botEdit[px, pz] = Mathf.Min(_botEdit[px, pz], wy);
            return;
        }
        if (_topEdit[px, pz] != wy && _botEdit[px, pz] != wy)
        {
            return;
        }
        // Only a column that just lost its highest or lowest edit rescans, and
        // only its own height.
        int top = Map.Data.WorldMinY - 1;
        int bot = int.MaxValue;
        for (int y = 0; y < Map.Data.VoxelHeight; y++)
        {
            if (Map.Tunnels[px, y, pz] != WorldMapState.EditNone)
            {
                top = Map.Data.WorldMinY + y;
                bot = Mathf.Min(bot, top);
            }
        }
        _topEdit[px, pz] = top;
        _botEdit[px, pz] = bot;
    }

    // Anything that rewrites the mask wholesale drops the summary instead of
    // maintaining it.
    public void InvalidateAll()
    {
        _topEdit = null;
    }

    private void EnsureTopEdits()
    {
        if (_topEdit != null)
        {
            return;
        }
        _topEdit = new int[Map.Data.ImageWidth, Map.Data.ImageHeight];
        _botEdit = new int[Map.Data.ImageWidth, Map.Data.ImageHeight];
        int floor = Map.Data.WorldMinY - 1;
        for (int px = 0; px < Map.Data.ImageWidth; px++)
        {
            for (int pz = 0; pz < Map.Data.ImageHeight; pz++)
            {
                _topEdit[px, pz] = floor;
                _botEdit[px, pz] = int.MaxValue;
            }
        }
        // In the mask's own memory order (ly is its middle index), scanning UP so
        // the last write per column is its highest edit. The obvious loop —
        // per column, downward until something is found — walks the array
        // against its stride and touches every one of its ~7M bytes as a cache
        // miss.
        for (int px = 0; px < Map.Data.ImageWidth; px++)
        {
            for (int ly = 0; ly < Map.Data.VoxelHeight; ly++)
            {
                for (int pz = 0; pz < Map.Data.ImageHeight; pz++)
                {
                    if (Map.Tunnels[px, ly, pz] != WorldMapState.EditNone)
                    {
                        _topEdit[px, pz] = Map.Data.WorldMinY + ly;
                        _botEdit[px, pz] = Mathf.Min(_botEdit[px, pz], Map.Data.WorldMinY + ly);
                    }
                }
            }
        }
    }
}
