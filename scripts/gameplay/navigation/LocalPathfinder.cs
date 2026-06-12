using System.Collections.Generic;
using Godot;

// Local layered A* over a WalkabilityGrid. Nodes are (i, j, layer) — a column
// can hold several stacked walkable surfaces (cave floor under outdoor ground,
// a bridge over a path), and the search moves between them, so it produces a
// list of world waypoints (each carrying the surface Y of its layer) from a
// start surface to a goal surface, consulting per-cell cost and asymmetric
// vertical step rules. Pathfinder is stateless aside from its
// reusable open/closed scratch arrays — caller owns the grid and the
// resulting path. One instance per MobNavigator is enough; the scratch
// arrays grow to fit the grid on first use and stay sized for subsequent
// queries.
//
// Costs: cardinal step = cell.cost * 1.0, diagonal step = cell.cost * sqrt2.
//
// Vertical step rules (per neighbour expansion, evaluated against the
// caller's TraversalProfile):
//   * Up-step: dy = neighbour.surfaceY - current.surfaceY; if dy >
//     profile.maxStepHeight (and !canClimb), the step is refused.
//   * Down-step: dy < 0; abs(dy) > profile.maxStepHeight requires
//     allowFalling=true AND abs(dy) <= profile.maxFallHeight (and
//     !canClimb). Climbers descend freely.
//   * Diagonals on a height delta are refused outright — only cardinal
//     steps are allowed to cross a height change. This avoids routing a
//     mob through the corner of a 2-voxel ledge.
//
// allowFalling is the per-call knob that lets behaviors opt into chase
// drops while wander stays grounded. Wander always passes false.
//
// Heuristic: octile distance, admissible for 8-connected grids with the
// above cardinal/diagonal costs. Search expands to a hard expansion budget
// to bound worst-case cost when the goal is unreachable; on budget overrun
// the partial path to the closest-explored cell is returned and the caller
// can decide whether to repath or steer directly.
public class LocalPathfinder
{
    // Hard cap on A* node expansions per query. At grid sizes ≤33×33
    // (~1100 cells) a fully unreachable goal expansion will hit this cap
    // and bail with the closest-so-far path; reachable goals settle in
    // well under it. Bump if grid extents grow.
    private const int MaxExpansions = 4096;

    private const float Sqrt2 = 1.41421356f;

    // Per-node A* state, indexed (j * size + i) * MaxColumnLayers + layer.
    // Sized to the grid on first use, reused across queries — cheap clear via
    // _generation tagging so we don't pay an Array.Clear over thousands of
    // cells per call.
    private float[] _gScore;
    private float[] _fScore;
    private int[] _cameFrom;     // parent cell index, -1 for none
    private int[] _generation;   // last-visited generation tag
    private int _gen;

    private readonly PriorityQueue<int, float> _open = new();

    // Scratch path-cell list. Caller copies out before calling Find again.
    private readonly List<int> _scratch = new();

    public List<Vector3> Find(WalkabilityGrid grid, in TraversalProfile profile, Vector3 startWorld, Vector3 goalWorld, bool allowFalling)
    {
        int size = grid.Size;
        int layers = WalkabilityGrid.MaxColumnLayers;
        int startI = Mathf.FloorToInt(startWorld.X) - grid.OriginX;
        int startJ = Mathf.FloorToInt(startWorld.Z) - grid.OriginZ;
        int goalI = Mathf.FloorToInt(goalWorld.X) - grid.OriginX;
        int goalJ = Mathf.FloorToInt(goalWorld.Z) - grid.OriginZ;

        if (!InBounds(startI, startJ, size) || !InBounds(goalI, goalJ, size))
        {
            // Goal or start outside the local window — caller's responsibility
            // to either expand the grid or fall back to direct steering.
            return null;
        }

        // Bind the world-space start/goal to the specific stacked surface each
        // sits on (nearest layer by Y). No walkable layer in either column
        // means there's nothing to path from/to.
        int startLayer = grid.NearestLayer(startI, startJ, startWorld.Y);
        int goalLayer = grid.NearestLayer(goalI, goalJ, goalWorld.Y);
        if (startLayer < 0 || goalLayer < 0)
        {
            return null;
        }

        EnsureScratch(size * size * layers);
        _gen++;
        _open.Clear();

        int startIdx = (startJ * size + startI) * layers + startLayer;
        int goalIdx = (goalJ * size + goalI) * layers + goalLayer;

        _generation[startIdx] = _gen;
        _gScore[startIdx] = 0f;
        _fScore[startIdx] = Heuristic(startI, startJ, goalI, goalJ);
        _cameFrom[startIdx] = -1;
        _open.Enqueue(startIdx, _fScore[startIdx]);

        int closestIdx = startIdx;
        float closestH = _fScore[startIdx];
        int expansions = 0;

        while (_open.Count > 0 && expansions < MaxExpansions)
        {
            int current = _open.Dequeue();
            if (current == goalIdx)
            {
                return Reconstruct(grid, current, size, layers);
            }

            expansions++;

            int cLayer = current % layers;
            int c2d = current / layers;
            int ci = c2d % size;
            int cj = c2d / size;
            WalkabilityCell currentCell = grid.GetLayer(ci, cj, cLayer);

            for (int dj = -1; dj <= 1; dj++)
            {
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0)
                    {
                        continue;
                    }
                    int ni = ci + di;
                    int nj = cj + dj;
                    if (!InBounds(ni, nj, size))
                    {
                        continue;
                    }

                    bool diagonal = di != 0 && dj != 0;
                    if (diagonal)
                    {
                        // Classic 8-connected corner-pinch fix: refuse
                        // diagonals when either orthogonally-adjacent column
                        // has no walkable surface, so the mob can't slip
                        // through a 1-voxel gap pressed between two solid
                        // columns. Height-agnostic at the column level.
                        if (grid.LayerCount(ci + di, cj) == 0 || grid.LayerCount(ci, cj + dj) == 0)
                        {
                            continue;
                        }
                    }

                    // Consider every stacked surface in the neighbour column;
                    // connect to each one the vertical rules allow. This is
                    // what lets A* move between layers — step down off the
                    // cave mouth onto the floor below, climb a ledge, etc.
                    int nLayers = grid.LayerCount(ni, nj);
                    for (int nLayer = 0; nLayer < nLayers; nLayer++)
                    {
                        WalkabilityCell n = grid.GetLayer(ni, nj, nLayer);

                        // Asymmetric vertical step rules. dy > 0: stepping up
                        // (or climbing); dy < 0: stepping down (or falling).
                        // Climbers ignore both limits; everyone else has a
                        // tight up-limit and a permissive down-limit gated by
                        // the per-call allowFalling flag. Water destinations
                        // bypass the step-up cap and the "allowFalling required"
                        // gate — entering water isn't a climb (the mob swims
                        // up to the surface) and falling in is just a splash,
                        // capped only by maxFallHeight.
                        int dy = n.surfaceY - currentCell.surfaceY;
                        if (!profile.canClimb)
                        {
                            if (n.IsWater)
                            {
                                if (dy < 0 && -dy > profile.maxFallHeight)
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                if (dy > profile.maxStepHeight)
                                {
                                    continue;
                                }
                                if (dy < 0)
                                {
                                    int drop = -dy;
                                    if (drop > profile.maxStepHeight)
                                    {
                                        if (!allowFalling)
                                        {
                                            continue;
                                        }
                                        if (drop > profile.maxFallHeight)
                                        {
                                            continue;
                                        }
                                    }
                                }
                            }
                        }

                        // Diagonals across any height delta are refused — a
                        // step up or a fall must happen on a cardinal axis so
                        // the mob's motion reads cleanly and the impulse layer
                        // doesn't have to interpret diagonal ledges.
                        if (diagonal && dy != 0)
                        {
                            continue;
                        }

                        int nIdx = (nj * size + ni) * layers + nLayer;
                        float stepCost = (diagonal ? Sqrt2 : 1f) * n.cost;
                        float tentativeG = _gScore[current] + stepCost;

                        if (_generation[nIdx] == _gen && tentativeG >= _gScore[nIdx])
                        {
                            continue;
                        }
                        _generation[nIdx] = _gen;
                        _cameFrom[nIdx] = current;
                        _gScore[nIdx] = tentativeG;
                        float h = Heuristic(ni, nj, goalI, goalJ);
                        _fScore[nIdx] = tentativeG + h;
                        _open.Enqueue(nIdx, _fScore[nIdx]);

                        if (h < closestH)
                        {
                            closestH = h;
                            closestIdx = nIdx;
                        }
                    }
                }
            }
        }

        // Goal unreachable within budget — return the partial path to the
        // closest cell we expanded so the mob still makes progress. Caller
        // gets a non-null path that ends short of the goal; the navigator
        // will repath on its next interval.
        if (closestIdx != startIdx)
        {
            return Reconstruct(grid, closestIdx, size, layers);
        }
        return null;
    }

    private List<Vector3> Reconstruct(WalkabilityGrid grid, int endIdx, int size, int layers)
    {
        _scratch.Clear();
        int cur = endIdx;
        while (cur != -1)
        {
            _scratch.Add(cur);
            cur = _cameFrom[cur];
        }
        // Skip the start cell so the path is the sequence of *future*
        // waypoints. If the path is just the start, return an empty list
        // (caller treats as "already there").
        var result = new List<Vector3>(_scratch.Count);
        for (int idx = _scratch.Count - 2; idx >= 0; idx--)
        {
            int cellIdx = _scratch[idx];
            int layer = cellIdx % layers;
            int c2d = cellIdx / layers;
            int i = c2d % size;
            int j = c2d / size;
            result.Add(grid.CellToWorld(i, j, layer));
        }
        return result;
    }

    private static float Heuristic(int i0, int j0, int i1, int j1)
    {
        int di = Mathf.Abs(i1 - i0);
        int dj = Mathf.Abs(j1 - j0);
        int min = Mathf.Min(di, dj);
        int max = Mathf.Max(di, dj);
        return min * Sqrt2 + (max - min);
    }

    private static bool InBounds(int i, int j, int size)
    {
        return i >= 0 && i < size && j >= 0 && j < size;
    }

    private void EnsureScratch(int total)
    {
        if (_gScore == null || _gScore.Length < total)
        {
            _gScore = new float[total];
            _fScore = new float[total];
            _cameFrom = new int[total];
            _generation = new int[total];
        }
    }
}
