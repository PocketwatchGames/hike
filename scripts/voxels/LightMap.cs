using System.Collections.Generic;
using Godot;

// Per-voxel light texture sampled by the world shaders via the `light_map`
// global. RGBA8: R = sun mask (0..255), G/B/A = block light R/G/B (summed per
// channel at deposit, byte-saturated). See WindowedVolumeMap for the toroidal
// player-centric windowing all five maps share.
//
// SUN DILATION: sunlight propagates into AIR only, so a voxel with geometry in
// it has a sun value of 0. A shader sampling near a surface therefore has the
// ground's own black texels inside its trilinear footprint, dragging the sample
// toward zero by a fraction that cycles with the surface's sub-voxel position —
// which is the slope banding that made the per-vertex sun bake necessary in the
// first place. Each geometry-bearing voxel is written the max sun of its six
// face neighbours instead, which removes the sink while leaving every air cell
// (what fog, motes, models, particles and detail sprites actually sample) at
// its true propagated value.
public class LightMap : WindowedVolumeMap
{
    private static readonly (int dx, int dy, int dz)[] FaceNeighbors =
    {
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
    };

    private const int CELLS = ChunkState.SIZE;
    private const int PLANE_LENGTH = CELLS * CELLS * CELLS;

    // Held for the cross-chunk neighbour reads the dilation needs at chunk
    // borders — EncodeChunkPixels is handed only the chunk itself.
    private readonly WorldState _world;

    // Cached sun channel per chunk. The sun byte is most of the encode cost — a
    // density test per voxel plus a six-neighbour max for every voxel carrying
    // geometry — and it moves only when SUNLIGHT moves. Block light re-dirties a
    // chunk constantly (every torch flicker tick re-deposits its footprint), and
    // without this every one of those re-derived a sun channel that hadn't
    // changed since world load.
    //
    // Validity is stamped, not signalled: an entry records the ChunkState
    // identity and SunlightVersion of the chunk AND its six face neighbours
    // (the dilation reads one voxel past each edge), and is rebuilt when any of
    // the seven differs. Nothing has to remember to invalidate it, and a chunk
    // that unloaded and reloaded fails the identity check rather than silently
    // reusing a plane baked from the old data.
    private sealed class SunPlane
    {
        public readonly byte[] Bytes = new byte[PLANE_LENGTH];
        public readonly ChunkState[] StampedChunks = new ChunkState[1 + 6];
        public readonly int[] StampedVersions = new int[1 + 6];
    }

    private readonly Dictionary<Vector3I, SunPlane> _sunPlanes = new();
    // Chunks encoded at least once. Most chunks are encoded exactly once — at
    // world load, or as the window slides over them — and never again, so they
    // must not each cost a retained 4 KB plane. A chunk earns one on its SECOND
    // encode, which is the signature of sitting near something that re-dirties
    // it (a flickering torch). First encodes go through the shared scratch.
    private readonly HashSet<Vector3I> _encodedOnce = new();
    private readonly byte[] _sunScratch = new byte[PLANE_LENGTH];
    private readonly List<Vector3I> _evictScratch = new();

    public LightMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
        : base(world, centerChunk, windowDiameterChunks, ChunkState.SIZE, 4, Image.Format.Rgba8)
    {
        _world = world;
        InitialEncodeAndUpload(world);
    }

    protected override void EncodeChunkPixels(ChunkState chunk, byte[] dst)
    {
        byte[] sun = ResolveSunPlane(chunk);
        // Indexed straight off the arrays rather than through GetBlockLight:
        // this runs 4096 times per dirty chunk and every index here is in range
        // by construction, so the accessor's bounds guard is pure overhead.
        ushort[,,] blockR = chunk.BlockLightR;
        ushort[,,] blockG = chunk.BlockLightG;
        ushort[,,] blockB = chunk.BlockLightB;
        for (int lz = 0; lz < CELLS; lz++)
        {
            for (int ly = 0; ly < CELLS; ly++)
            {
                int sunRow = (lz * CELLS + ly) * CELLS;
                int rowOffset = sunRow * 4;
                for (int lx = 0; lx < CELLS; lx++)
                {
                    int o = rowOffset + lx * 4;
                    dst[o + 0] = sun[sunRow + lx];
                    dst[o + 1] = EncodeChannel(blockR[lx, ly, lz]);
                    dst[o + 2] = EncodeChannel(blockG[lx, ly, lz]);
                    dst[o + 3] = EncodeChannel(blockB[lx, ly, lz]);
                }
            }
        }
    }

    private static byte EncodeChannel(ushort v)
    {
        return v > ChunkState.BLOCK_LIGHT_TEXTURE_MAX ? (byte)ChunkState.BLOCK_LIGHT_TEXTURE_MAX : (byte)v;
    }

    // The chunk's sun channel, rebuilt only when its stamp no longer matches.
    private byte[] ResolveSunPlane(ChunkState chunk)
    {
        Vector3I coord = chunk.ChunkCoord;
        if (_sunPlanes.TryGetValue(coord, out SunPlane plane))
        {
            // Anything in the dictionary was filled and stamped when it landed
            // there, so the stamp alone decides whether it is still good.
            if (StampMatches(chunk, coord, plane))
            {
                return plane.Bytes;
            }
        }
        else if (_encodedOnce.Add(coord))
        {
            // First encode of this chunk — don't retain a plane for it yet.
            using (Profiler.Sample("LightMap.SunEncode"))
            {
                FillSunPlane(chunk, _sunScratch);
            }
            return _sunScratch;
        }
        else
        {
            plane = new SunPlane();
            _sunPlanes[coord] = plane;
        }

        using (Profiler.Sample("LightMap.SunEncode"))
        {
            FillSunPlane(chunk, plane.Bytes);
            Stamp(chunk, coord, plane);
        }
        return plane.Bytes;
    }

    private bool StampMatches(ChunkState chunk, Vector3I coord, SunPlane plane)
    {
        if (plane.StampedChunks[0] != chunk || plane.StampedVersions[0] != chunk.SunlightVersion)
        {
            return false;
        }
        for (int i = 0; i < FaceNeighbors.Length; i++)
        {
            (int dx, int dy, int dz) = FaceNeighbors[i];
            ChunkState n = _world.GetChunk(new Vector3I(coord.X + dx, coord.Y + dy, coord.Z + dz));
            if (plane.StampedChunks[i + 1] != n) { return false; }
            if (n != null && plane.StampedVersions[i + 1] != n.SunlightVersion) { return false; }
        }
        return true;
    }

    private void Stamp(ChunkState chunk, Vector3I coord, SunPlane plane)
    {
        plane.StampedChunks[0] = chunk;
        plane.StampedVersions[0] = chunk.SunlightVersion;
        for (int i = 0; i < FaceNeighbors.Length; i++)
        {
            (int dx, int dy, int dz) = FaceNeighbors[i];
            ChunkState n = _world.GetChunk(new Vector3I(coord.X + dx, coord.Y + dy, coord.Z + dz));
            plane.StampedChunks[i + 1] = n;
            plane.StampedVersions[i + 1] = n?.SunlightVersion ?? 0;
        }
    }

    private void FillSunPlane(ChunkState chunk, byte[] sun)
    {
        int baseX = chunk.ChunkCoord.X * CELLS;
        int baseY = chunk.ChunkCoord.Y * CELLS;
        int baseZ = chunk.ChunkCoord.Z * CELLS;
        for (int lz = 0; lz < CELLS; lz++)
        {
            for (int ly = 0; ly < CELLS; ly++)
            {
                int row = (lz * CELLS + ly) * CELLS;
                for (int lx = 0; lx < CELLS; lx++)
                {
                    // Density is the geometry test the mesher uses, so Barrier
                    // (an invisible light/nav marker with no surface) stays dark
                    // rather than leaking its neighbours' sun back through a
                    // shut door.
                    int sunRaw = Density.TypeDensity(chunk.Voxels[lx, ly, lz]) < 0
                        ? DilatedSunlight(chunk, lx, ly, lz, baseX, baseY, baseZ)
                        : chunk.GetSunlight(lx, ly, lz);
                    sun[row + lx] = (byte)((sunRaw * 255) / LightEngine.MAX_LIGHT);
                }
            }
        }
    }

    // Drop planes for chunks the window has left behind, so the cache stays
    // bounded by the window rather than by everywhere the player has walked.
    protected override void OnWindowMoved()
    {
        _evictScratch.Clear();
        foreach (Vector3I coord in _sunPlanes.Keys)
        {
            if (!InWindow(coord))
            {
                _evictScratch.Add(coord);
            }
        }
        for (int i = 0; i < _evictScratch.Count; i++)
        {
            _sunPlanes.Remove(_evictScratch[i]);
        }

        _evictScratch.Clear();
        foreach (Vector3I coord in _encodedOnce)
        {
            if (!InWindow(coord))
            {
                _evictScratch.Add(coord);
            }
        }
        for (int i = 0; i < _evictScratch.Count; i++)
        {
            _encodedOnce.Remove(_evictScratch[i]);
        }
    }

    // Max sun over the six face neighbours. Six rather than all 26 because the
    // trilinear footprint spans two texels per axis, so a face neighbour is what
    // a sample straddling this surface actually reaches; a cell open only
    // diagonally sits in a corner whose neighbours are dark regardless.
    private int DilatedSunlight(ChunkState chunk, int lx, int ly, int lz, int baseX, int baseY, int baseZ)
    {
        int best = 0;
        for (int i = 0; i < FaceNeighbors.Length; i++)
        {
            (int dx, int dy, int dz) = FaceNeighbors[i];
            int nx = lx + dx;
            int ny = ly + dy;
            int nz = lz + dz;
            int sun;
            if (nx >= 0 && nx < CELLS && ny >= 0 && ny < CELLS && nz >= 0 && nz < CELLS)
            {
                sun = chunk.GetSunlight(nx, ny, nz);
            }
            else
            {
                sun = _world.GetSunlightWorld(baseX + nx, baseY + ny, baseZ + nz);
            }
            if (sun > best)
            {
                best = sun;
            }
        }
        return best;
    }
}
