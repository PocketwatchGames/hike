using System.Collections.Generic;
using Godot;

// Bakes every mantleable ledge lip in a finished world into ChunkState.ClimbLips,
// so the mesher can dress the rock without re-deriving where the ledges are.
//
// WHY IT IS BAKED. The rule (ClimbLedgeMarker) is mostly about rock, but not
// only: a ledge with a fence along its top or a tree at its foot is a wall the
// player cannot cross, and WalkabilityGrid already refuses to mantle it at
// runtime. A chunk build cannot see that — it has voxels and nothing else, and
// entities for a chunk spawn AFTER its mesh exists — so the crust used to
// advertise climbs the game would not allow. Deciding it here, once, where the
// producer has both the voxels and the entity list, is the only place both
// halves of the question are answerable.
//
// It is deliberately NOT re-derived afterwards. A door opening or a prop dying
// does not repaint the rock: the crust is a hint about the shape of the world,
// not a live readout, and re-meshing chunks to chase a moved collider would cost
// far more than the hint is worth. Terrain EDITS do restamp, through
// RestampRegion — there the rock itself changed.
//
// MAIN THREAD ONLY, because reading a prop's colliders means instantiating its
// PackedScene (PropColliderCache). That is why this rides the producers'
// main-thread epilogue next to FoliageStamper rather than sitting in
// WorldFinish, which both producers run off-thread.
public static class ClimbLedgeStamper
{
    public static void Stamp(WorldState world)
    {
        if (world == null)
        {
            return;
        }
        HashSet<Vector3I> blocked = CollectBlockedCells(world);
        System.Func<int, int, int, bool> isBlocked = Predicate(blocked);
        long lips = 0;
        _suppressed = 0;
        // Answering the authoring question "why is there no crust on THIS
        // ledge?" costs a second run of the rule per candidate, so it is only
        // counted when asked for.
        _countSuppressed = CVars.climbDebug.Value;
        foreach (KeyValuePair<Vector3I, ChunkState> entry in world._chunks)
        {
            lips += StampChunk(world, entry.Key, entry.Value, isBlocked);
        }
        GD.Print($"[ClimbLedgeStamper] lips={lips} blockerCells={blocked.Count}"
            + (_countSuppressed ? $" suppressedByCollider={_suppressed}" : ""));
        _countSuppressed = false;
    }

    // Restamp only the chunks a region touches. What a world-editor edit wants:
    // carving a wall two voxels taller makes a ledge, and the crust should
    // follow the rock without re-baking the world.
    public static void RestampRegion(WorldState world, VoxelBox region)
    {
        if (world == null || region.IsEmpty)
        {
            return;
        }
        // A lip one voxel outside the edited box reads columns inside it, so its
        // answer can change too — grow by the rule's own reach before deciding
        // which chunks to redo.
        var grown = new VoxelBox(
            region.Min - Vector3I.One * LipReachVoxels,
            region.Max + Vector3I.One * LipReachVoxels);
        System.Func<int, int, int, bool> isBlocked = Predicate(CollectBlockedCells(world, grown));
        foreach (KeyValuePair<Vector3I, ChunkState> entry in world._chunks)
        {
            Vector3I lo = entry.Key * ChunkState.SIZE;
            Vector3I hi = lo + Vector3I.One * (ChunkState.SIZE - 1);
            if (hi.X < grown.Min.X || lo.X > grown.Max.X
                || hi.Y < grown.Min.Y || lo.Y > grown.Max.Y
                || hi.Z < grown.Min.Z || lo.Z > grown.Max.Z)
            {
                continue;
            }
            StampChunk(world, entry.Key, entry.Value, isBlocked);
        }
    }

    // How far from a lip the rule reads: two columns out on either horizontal
    // axis, and the full rise plus headroom vertically.
    private const int LipReachVoxels = 2 + ClimbLedgeMarker.ClimbRiseVoxels;

    // Widest XZ reach a single prop collider has from its origin, in voxels —
    // a restamp uses it to decide which entities can still matter just outside
    // the edited box. Generous on purpose: the cost of overshooting is a few
    // rasterized shapes, and the cost of undershooting is a stale lip.
    private const int MaxColliderReachVoxels = 8;

    // Lips the rock alone would have granted and a collider took away, counted
    // under climb_debug. Diagnostic only — see Stamp.
    private static bool _countSuppressed;
    private static long _suppressed;

    // One closure for the whole sweep — the rule asks per column, so building it
    // per candidate voxel would allocate millions of delegates across a world.
    private static System.Func<int, int, int, bool> Predicate(HashSet<Vector3I> blocked)
    {
        return (x, y, z) => blocked.Contains(new Vector3I(x, y, z));
    }

    private static bool Near(VoxelBox box, Vector3 position)
    {
        return position.X >= box.Min.X - MaxColliderReachVoxels
            && position.X <= box.Max.X + MaxColliderReachVoxels
            && position.Y >= box.Min.Y - MaxColliderReachVoxels
            && position.Y <= box.Max.Y + MaxColliderReachVoxels
            && position.Z >= box.Min.Z - MaxColliderReachVoxels
            && position.Z <= box.Max.Z + MaxColliderReachVoxels;
    }

    private static int StampChunk(WorldState world, Vector3I coord, ChunkState chunk,
        System.Func<int, int, int, bool> isBlocked)
    {
        int baseX = coord.X * ChunkState.SIZE;
        int baseY = coord.Y * ChunkState.SIZE;
        int baseZ = coord.Z * ChunkState.SIZE;

        List<ClimbLip> found = null;
        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    // Pre-filter off this chunk's own array — a lip is solid
                    // with air directly above, the first two things the rule
                    // tests. Drops the ~95% of a chunk that is buried rock or
                    // open sky before any cross-chunk read.
                    if (!Blocks.IsSolid(chunk.Voxels[x, y, z]))
                    {
                        continue;
                    }
                    int wx = baseX + x;
                    int wy = baseY + y;
                    int wz = baseZ + z;
                    // Read the voxel above out of this chunk's own array where it
                    // is in range — 15 rows in 16 — rather than hashing a chunk
                    // coord for it. The pre-filter runs on every solid voxel in
                    // the world, and the dictionary lookup is what makes a
                    // whole-world pass expensive (see scripts/voxels/CLAUDE.md).
                    int above = y + 1 < ChunkState.SIZE
                        ? chunk.Voxels[x, y + 1, z]
                        : world.GetBlockWorld(wx, wy + 1, wz);
                    if (Blocks.IsSolid(above))
                    {
                        continue;
                    }
                    // No growth authored on this rock means nothing to dress
                    // with, so there is no lip worth recording.
                    if (Blocks.ClimbGrowthLayer(chunk.Voxels[x, y, z]) <= 0)
                    {
                        continue;
                    }
                    int faces = ClimbLedgeMarker.FindClimbLip(world.GetBlockWorld, wx, wy, wz, isBlocked);
                    if (faces == 0)
                    {
                        if (_countSuppressed
                            && ClimbLedgeMarker.FindClimbLip(world.GetBlockWorld, wx, wy, wz) != 0)
                        {
                            _suppressed++;
                        }
                        continue;
                    }
                    found ??= new List<ClimbLip>();
                    found.Add(new ClimbLip(new Vector3I(x, y, z), faces));
                }
            }
        }
        chunk.SetClimbLips(found);
        return found?.Count ?? 0;
    }

    // Every cell a solid prop or interactive collider stands in, resolved the
    // same way Sim's runtime path-blocker grid resolves it — same rasterizer,
    // same Solid-layer rule, same one-row-per-entity Y convention — so the baked
    // hint and the runtime refusal answer from the same footprints.
    private static HashSet<Vector3I> CollectBlockedCells(WorldState world, VoxelBox? within = null)
    {
        var blocked = new HashSet<Vector3I>();
        var cells = new List<Vector3I>();
        foreach (List<EntitySimState> bucket in world._entities.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                EntitySimState state = bucket[i];
                // A restamp only cares about the entities near what was edited,
                // widened by the largest footprint a prop collider plausibly has
                // so a tree just outside the box still blocks cells inside it.
                if (within.HasValue && !Near(within.Value, state.WorldPosition))
                {
                    continue;
                }
                PropCollider[] colliders = PropColliderCache.GetColliders(state.Scene);
                if (colliders.Length == 0)
                {
                    continue;
                }
                var placement = new Transform3D(
                    Basis.FromEuler(new Vector3(0f, state.RotationY, 0f)).Scaled(Vector3.One * state.Scale),
                    state.WorldPosition);
                int floorY = Mathf.FloorToInt(state.WorldPosition.Y);
                for (int c = 0; c < colliders.Length; c++)
                {
                    cells.Clear();
                    PathBlockerRasterizer.RasterizeShape(colliders[c].Shape,
                        placement * colliders[c].Local, floorY, cells);
                    for (int k = 0; k < cells.Count; k++)
                    {
                        blocked.Add(cells[k]);
                    }
                }
            }
        }
        return blocked;
    }
}
