using Godot;

// Pure helpers that turn a single ChunkState into the minimap's per-pixel
// data — no GPU, no allocations beyond the output buffers the caller supplies.
//
// Two passes:
//   GenerateSurfaceRow  — overworld heightmap contribution at OutdoorMetersPerPixel
//                         (currently 2m/pixel → 8x8 pixels per chunk).
//   GenerateSliceTile   — indoor/underground plan-view tile at IndoorMetersPerPixel
//                         (1m/pixel → 16x16 per chunk per slice; 4 slices per chunk).
//
// Caller is responsible for monotonic merging across chunks (the same column
// may be updated by multiple vertically-stacked chunk loads — only the highest
// surface should win) and for stamping prop-sourced foliage on top of the
// detail-scatter foliage this pass produces.
public static class MinimapData
{
    // 2 meters per outdoor minimap pixel → 8x8 pixels per chunk. Must divide
    // ChunkState.SIZE evenly so chunk boundaries stay pixel-aligned.
    public const int OutdoorMetersPerPixel = 2;
    public const int OutdoorPixelsPerChunk = ChunkState.SIZE / OutdoorMetersPerPixel;
    public const int OutdoorPixelsPerChunkSq = OutdoorPixelsPerChunk * OutdoorPixelsPerChunk;

    // 1 meter per indoor pixel → 16x16 per chunk per slice. Indoor maps trade
    // memory for fidelity — a 1m corridor still reads as a 1px line.
    public const int IndoorMetersPerPixel = 1;
    public const int IndoorPixelsPerChunk = ChunkState.SIZE / IndoorMetersPerPixel;
    public const int IndoorPixelsPerChunkSq = IndoorPixelsPerChunk * IndoorPixelsPerChunk;

    // Vertical slice height in voxels (= meters). 4 lines up with the
    // building-floor / cave-stratum cadence; ChunkState.SIZE / 4 = 4 slices
    // per chunk.
    public const int PlateauHeight = 4;
    public const int SlicesPerChunk = ChunkState.SIZE / PlateauHeight;

    // Vertical clearance needed for the player to walk through a column.
    // Used by GenerateSliceTile: a column that has no contiguous air run of
    // this length within the slice is classified as a wall rather than a
    // floor / open-air cell.
    public const int MinHeadroomVoxels = 2;

    // Reserved palette slot for slice-view wall interiors (columns that are
    // solid all the way through with no air above). Doesn't correspond to a
    // BlockData entry — the slice generator writes this index directly so
    // every biome's solid-wall pixels read the same color regardless of terrain
    // or voxel type. Picked above the BlockCatalog's active range; the LUT
    // builder paints this slot from GameClient.minimapWallSlotColor.
    public const int WallSlotIndex = 32;

    // Bit values for SliceCell.Flags. Floor = a solid voxel was found in the
    // slice's Y range with air above it (i.e. you could stand on it). Ceiling
    // = the voxel directly above the slice's top is solid (the column has a
    // roof; the slice represents an indoor space, not a top-of-world pixel).
    public const byte SliceFlagFloor = 1 << 0;
    public const byte SliceFlagCeiling = 1 << 1;

    // Sentinel: no surface contribution from this chunk for this column.
    // Caller's monotonic merge treats Height = 0 as "skip" so a higher chunk's
    // contribution (even just 1) overrides it.
    public const ushort NoSurfaceHeight = 0;

    public struct SurfaceCell
    {
        // World Y of the top face (= Y_topSolid + 1). 0 = no surface.
        public ushort Height;
        // Resolved tile layer id (0..VoxelTypeInfo.TILE_VARIANT_TABLE_SIZE-1).
        // Indexed into the BlockCatalog-driven tile LUT at render time
        // (see Minimap.BuildTileLutTexture).
        public byte TileId;
        // Detail-scatter foliage stamp. 0 = none. Caller resolves priority
        // against existing pixel state via MinimapFoliageColors.
        public byte FoliageId;
    }

    public struct SliceCell
    {
        public byte TileId;
        public byte FoliageId;
        public byte Flags;
    }

    // Generate the overworld surface contribution from `chunk`. Each output
    // cell covers a OutdoorMetersPerPixel x OutdoorMetersPerPixel block of
    // voxels in plan; the cell takes the max-height column in the block (so
    // pillars / cliff edges read crisp) and the tile + foliage of that
    // winning column.
    //
    // `detailPalette` is the active world's detail palette (the deduplicated
    // set of DefaultDetail groups across all kits, uploaded via
    // ChunkMesh.SetDetailGroups); null is allowed — foliage stays 0 in
    // that case.
    //
    // Pure-air and pure-water chunks contribute nothing (output zeroed) since
    // they don't define an air/ground or air/water boundary themselves.
    public static void GenerateSurfaceRow(
        ChunkState chunk,
        DetailGroupData[] detailPalette,
        TerrainData[] terrainPalette,
        SurfaceCell[] output)
    {
        if (output.Length < OutdoorPixelsPerChunkSq)
        {
            GD.PushError($"MinimapData.GenerateSurfaceRow: output too small ({output.Length} < {OutdoorPixelsPerChunkSq})");
            return;
        }

        for (int i = 0; i < OutdoorPixelsPerChunkSq; i++)
        {
            output[i] = default;
        }

        ChunkState.EChunkFill fill = chunk.GetFill(out VoxelType pureType);
        int chunkBaseY = chunk.ChunkCoord.Y * ChunkState.SIZE;

        if (fill == ChunkState.EChunkFill.Pure)
        {
            // Pure-air / pure-water: no contribution. The actual surface is
            // in another chunk (the one above pure-water for ocean surface;
            // never for pure-air which is sky).
            if (pureType == VoxelType.Air || pureType == VoxelType.Water)
            {
                return;
            }

            // Pure-solid (Stone, Terrain, etc.): the top face of this chunk is
            // the surface contribution. A higher chunk above (if also solid)
            // will out-rank this via the caller's monotonic merge.
            int topWorldY = chunkBaseY + ChunkState.SIZE - 1;
            int height = topWorldY + 1;
            int pureTerrainId = chunk.GetTerrainId(0, ChunkState.SIZE - 1, 0);
            int pureOverlayId = chunk.GetOverlayId(0, ChunkState.SIZE - 1, 0);
            byte tileId = (byte)ResolveSurfaceTileId(pureType, topWorldY, pureTerrainId, pureOverlayId, terrainPalette);
            var cell = new SurfaceCell
            {
                Height = (ushort)height,
                TileId = tileId,
                FoliageId = 0,
            };
            for (int i = 0; i < OutdoorPixelsPerChunkSq; i++)
            {
                output[i] = cell;
            }
            return;
        }

        // Mixed: per-column scan inside this chunk.
        for (int pz = 0; pz < OutdoorPixelsPerChunk; pz++)
        {
            for (int px = 0; px < OutdoorPixelsPerChunk; px++)
            {
                ushort bestHeight = 0;
                byte bestTile = 0;
                byte bestFoliage = 0;

                for (int dz = 0; dz < OutdoorMetersPerPixel; dz++)
                {
                    for (int dx = 0; dx < OutdoorMetersPerPixel; dx++)
                    {
                        int x = px * OutdoorMetersPerPixel + dx;
                        int z = pz * OutdoorMetersPerPixel + dz;

                        // Top-down scan: first non-air voxel is this column's surface.
                        for (int y = ChunkState.SIZE - 1; y >= 0; y--)
                        {
                            VoxelType v = chunk.Voxels[x, y, z];
                            if (v == VoxelType.Air)
                            {
                                continue;
                            }
                            int worldY = chunkBaseY + y;
                            ushort height = (ushort)(worldY + 1);
                            if (height > bestHeight)
                            {
                                int TerrainId = chunk.GetTerrainId(x, y, z);
                                int overlayId = chunk.GetOverlayId(x, y, z);
                                bestHeight = height;
                                bestTile = (byte)ResolveSurfaceTileId(v, worldY, TerrainId, overlayId, terrainPalette);
                                bestFoliage = ResolveFoliageId(chunk, detailPalette, x, y, z);
                            }
                            break;
                        }
                    }
                }

                output[pz * OutdoorPixelsPerChunk + px] = new SurfaceCell
                {
                    Height = bestHeight,
                    TileId = bestTile,
                    FoliageId = bestFoliage,
                };
            }
        }
    }

    // Generate one slice tile (16x16 cells) for the given vertical slice band
    // within `chunk`. The slice's Y range is [sliceInChunk * PlateauHeight,
    // (sliceInChunk+1) * PlateauHeight) in chunk-local coords.
    //
    // Classification per (x,z) column:
    //   * WALL — no contiguous run of MinHeadroomVoxels (=2) air voxels
    //     anywhere in the slice. The column can't be walked through at this
    //     elevation band, so it paints with the dedicated Wall slot.
    //   * FLOOR — column is passable AND has a topmost solid voxel with
    //     >= MinHeadroomVoxels of air above it (counting up through the
    //     slice and into voxels above it). Tile color comes from that voxel.
    //   * OPEN AIR — passable but no standable surface within the slice.
    //     TileId = 0, no floor flag.
    //   * Ceiling flag is set if the voxel just above the slice top is solid
    //     (still inside this chunk; ignores cross-chunk for now).
    //
    // Pure-air chunks produce all zero (caller can skip allocating a slice
    // tile for them). Pure-solid chunks produce all-wall tiles (no floors).
    public static void GenerateSliceTile(
        ChunkState chunk,
        int sliceInChunk,
        DetailGroupData[] detailPalette,
        TerrainData[] terrainPalette,
        WorldState worldState,
        SliceCell[] output)
    {
        if (sliceInChunk < 0 || sliceInChunk >= SlicesPerChunk)
        {
            GD.PushError($"MinimapData.GenerateSliceTile: sliceInChunk {sliceInChunk} out of range [0,{SlicesPerChunk})");
            return;
        }
        if (output.Length < IndoorPixelsPerChunkSq)
        {
            GD.PushError($"MinimapData.GenerateSliceTile: output too small ({output.Length} < {IndoorPixelsPerChunkSq})");
            return;
        }

        for (int i = 0; i < IndoorPixelsPerChunkSq; i++)
        {
            output[i] = default;
        }

        ChunkState.EChunkFill fill = chunk.GetFill(out VoxelType pureType);
        int chunkBaseY = chunk.ChunkCoord.Y * ChunkState.SIZE;
        int sliceBaseY = sliceInChunk * PlateauHeight;
        int sliceTopY = sliceBaseY + PlateauHeight; // exclusive

        if (fill == ChunkState.EChunkFill.Pure)
        {
            if (pureType == VoxelType.Air)
            {
                return;
            }
            // Pure non-air: every column is walls all the way through. No
            // floors, no ceiling flag. Wall slot is terrain-agnostic so any
            // pure-solid chunk (stone, terrain, whatever) reads as the
            // single dark-grey Wall color.
            var wall = new SliceCell { TileId = (byte)WallSlotIndex, FoliageId = 0, Flags = 0 };
            for (int i = 0; i < IndoorPixelsPerChunkSq; i++)
            {
                output[i] = wall;
            }
            return;
        }

        for (int z = 0; z < IndoorPixelsPerChunk; z++)
        {
            for (int x = 0; x < IndoorPixelsPerChunk; x++)
            {
                // Count air voxels stacked directly above the slice (up to
                // MinHeadroomVoxels). These count toward headroom for a floor
                // sitting at the slice top. The deep-underground case (chunk
                // above is solid) is what prevents wall columns from being
                // misclassified as floors and picking up the surface biome
                // color above.
                int airAboveSlice = 0;
                for (int i = 0; i < MinHeadroomVoxels; i++)
                {
                    int yAbove = sliceTopY + i;
                    VoxelType vAbove;
                    if (yAbove < ChunkState.SIZE)
                    {
                        vAbove = chunk.Voxels[x, yAbove, z];
                    }
                    else if (worldState != null)
                    {
                        int wx = chunk.ChunkCoord.X * ChunkState.SIZE + x;
                        int wz = chunk.ChunkCoord.Z * ChunkState.SIZE + z;
                        int wyAbove = chunk.ChunkCoord.Y * ChunkState.SIZE + yAbove;
                        vAbove = worldState.GetVoxelWorld(wx, wyAbove, wz);
                    }
                    else
                    {
                        vAbove = VoxelType.Air;
                    }
                    if (vAbove != VoxelType.Air) { break; }
                    airAboveSlice++;
                }

                // Top-down scan within slice. Track:
                //   * floorY  — topmost solid with >= MinHeadroomVoxels of
                //               contiguous air above (in-slice + above-slice).
                //   * anySolid — at least one solid voxel exists in the slice.
                //   * maxAirRunInSlice — longest contiguous air run within
                //                        the slice itself; >= MinHeadroomVoxels
                //                        means the column is passable.
                int floorY = -1;
                VoxelType floorVoxel = VoxelType.Air;
                byte floorFoliage = 0;
                bool anySolid = false;
                int curAirRun = airAboveSlice; // ongoing air run, seeded from above
                int maxAirRunInSlice = 0;
                int curAirRunInSlice = 0;

                for (int y = sliceTopY - 1; y >= sliceBaseY; y--)
                {
                    VoxelType v = chunk.Voxels[x, y, z];
                    if (v != VoxelType.Air)
                    {
                        anySolid = true;
                        if (curAirRun >= MinHeadroomVoxels && floorY < 0)
                        {
                            floorY = y;
                            floorVoxel = v;
                            floorFoliage = ResolveFoliageId(chunk, detailPalette, x, y, z);
                        }
                        if (curAirRunInSlice > maxAirRunInSlice) { maxAirRunInSlice = curAirRunInSlice; }
                        curAirRun = 0;
                        curAirRunInSlice = 0;
                    }
                    else
                    {
                        curAirRun++;
                        curAirRunInSlice++;
                    }
                }
                if (curAirRunInSlice > maxAirRunInSlice) { maxAirRunInSlice = curAirRunInSlice; }

                bool passable = maxAirRunInSlice >= MinHeadroomVoxels;

                byte flags = 0;
                byte tileId = 0;
                byte foliageId = 0;

                if (!passable && anySolid)
                {
                    // Wall: can't walk through this column at this elevation.
                    // Painted with the dedicated Wall slot regardless of
                    // biome / terrain so tunnels read consistently dark grey.
                    tileId = (byte)WallSlotIndex;
                }
                else if (floorY >= 0)
                {
                    flags |= SliceFlagFloor;
                    int floorTerrainId = chunk.GetTerrainId(x, floorY, z);
                    int floorOverlayId = chunk.GetOverlayId(x, floorY, z);
                    tileId = (byte)ResolveSurfaceTileId(floorVoxel, chunkBaseY + floorY, floorTerrainId, floorOverlayId, terrainPalette);
                    foliageId = floorFoliage;
                }
                // else: passable open-air column. TileId stays 0, no flag.

                if (sliceTopY < ChunkState.SIZE && chunk.Voxels[x, sliceTopY, z] != VoxelType.Air)
                {
                    flags |= SliceFlagCeiling;
                }

                output[z * IndoorPixelsPerChunk + x] = new SliceCell
                {
                    TileId = tileId,
                    FoliageId = foliageId,
                    Flags = flags,
                };
            }
        }
    }

    // Resolves the renderer's tile layer id for `type` at world Y `worldY`,
    // mirroring what `voxel_clip.gdshader` does. `useWallTile` controls
    // which face of the voxel the minimap should pretend to be looking at:
    //   false (default): top face — the "looking down at flat ground" view
    //     used for surface heightmap pixels and indoor *floor* pixels (a
    //     solid voxel with air above it).
    //   true: side face — the "looking at a wall" view used for indoor
    //     *wall* pixels (a column that's solid through the entire slice,
    //     no air above, i.e. underground rock or the inside of a cliff).
    // The same terrain-and-overlay resolution applies in both modes; only the
    // FlatTile vs WallTile lookup differs for AUTO terrain.
    private static int ResolveSurfaceTileId(VoxelType type, int worldY, int TerrainId, int overlayId, TerrainData[] terrainPalette, bool useWallTile = false)
    {
        if (overlayId != 0)
        {
            return ApplyBand(overlayId, worldY);
        }
        // Face index: 0 = top, 2 = side. Bottom (1) isn't useful here.
        int baseTile = VoxelTypeInfo.GetTileForFace(type, useWallTile ? 2 : 0);
        if (baseTile == VoxelTypeInfo.TILE_AUTO)
        {
            baseTile = useWallTile
                ? ResolveTerrainWallTile(TerrainId, terrainPalette)
                : ResolveTerrainFlatTile(TerrainId, terrainPalette);
        }
        return ApplyBand(baseTile, worldY);
    }

    private static int ResolveTerrainFlatTile(int TerrainId, TerrainData[] terrainPalette)
    {
        if (terrainPalette == null || TerrainId < 0 || TerrainId >= terrainPalette.Length)
        {
            return BlockCatalog.Active.DefaultFlatTileIndex;
        }
        TerrainData terrain = terrainPalette[TerrainId];
        if (terrain == null || terrain.FlatTile == null)
        {
            return BlockCatalog.Active.DefaultFlatTileIndex;
        }
        return terrain.FlatTile.AtlasBaseIndex;
    }

    private static int ResolveTerrainWallTile(int TerrainId, TerrainData[] terrainPalette)
    {
        if (terrainPalette == null || TerrainId < 0 || TerrainId >= terrainPalette.Length)
        {
            return BlockCatalog.Active.DefaultWallTileIndex;
        }
        TerrainData terrain = terrainPalette[TerrainId];
        if (terrain == null || terrain.WallTile == null)
        {
            return BlockCatalog.Active.DefaultWallTileIndex;
        }
        return terrain.WallTile.AtlasBaseIndex;
    }

    private static int ApplyBand(int baseTile, int worldY)
    {
        BlockData block = BlockCatalog.Active.GetByAtlasIndex(baseTile);
        if (block != null && block.Bands > 1)
        {
            int band = Mathf.FloorToInt((worldY - VoxelTypeInfo.TILE_BAND_ORIGIN_Y) / VoxelTypeInfo.TILE_BAND_HEIGHT);
            band = ((band % block.Bands) + block.Bands) % block.Bands;
            return baseTile + band * block.VariantsPerBand;
        }
        return baseTile;
    }

    // DetailGroup is 1-based — 0 means "no scatter painted on this voxel".
    // Returns 0 if the voxel has no scatter, the palette is missing, or the
    // resolved DetailGroupData has MinimapFoliageId = 0 (group opted out of
    // appearing on the minimap).
    private static byte ResolveFoliageId(ChunkState chunk, DetailGroupData[] palette, int x, int y, int z)
    {
        if (palette == null)
        {
            return 0;
        }
        int groupId = chunk.GetDetailGroup(x, y, z);
        if (groupId <= 0)
        {
            return 0;
        }
        int paletteIndex = groupId - 1;
        if (paletteIndex >= palette.Length)
        {
            return 0;
        }
        DetailGroupData group = palette[paletteIndex];
        if (group == null)
        {
            return 0;
        }
        return group.MinimapFoliageId;
    }
}
