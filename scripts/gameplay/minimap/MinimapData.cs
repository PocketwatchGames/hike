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
        // Indexed into MinimapTileColors at render time.
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
    // `detailPalette` is the chunk's zone palette (ZoneGenData.DetailGroups);
    // null is allowed — foliage stays 0 in that case.
    //
    // Pure-air and pure-water chunks contribute nothing (output zeroed) since
    // they don't define an air/ground or air/water boundary themselves.
    public static void GenerateSurfaceRow(
        ChunkState chunk,
        DetailGroupData[] detailPalette,
        EnvironmentKitData[] kitPalette,
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
            int pureKitId = chunk.GetKitId(0, ChunkState.SIZE - 1, 0);
            int pureOverlayId = chunk.GetOverlayId(0, ChunkState.SIZE - 1, 0);
            byte tileId = (byte)ResolveSurfaceTileId(pureType, topWorldY, pureKitId, pureOverlayId, kitPalette);
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
                                int kitId = chunk.GetKitId(x, y, z);
                                int overlayId = chunk.GetOverlayId(x, y, z);
                                bestHeight = height;
                                bestTile = (byte)ResolveSurfaceTileId(v, worldY, kitId, overlayId, kitPalette);
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
    // For each (x,z) column in plan:
    //   * Find the topmost solid voxel in the slice's Y range with air above
    //     it (within the slice or in the next-higher Y, whichever is closer).
    //     That's the floor; its tile id colors the pixel.
    //   * If no air-above voxel exists in the slice (column is solid through
    //     the slice top), the pixel is a wall — TileId from the topmost solid
    //     in the slice, FloorFlag = 0.
    //   * If the entire slice is air in this column, TileId = 0, FloorFlag = 0.
    //   * Ceiling flag is set if the voxel just above the slice top is solid
    //     (still inside this chunk; ignores cross-chunk for now).
    //
    // Pure-air chunks produce all zero (caller can skip allocating a slice
    // tile for them). Pure-solid chunks produce all-wall tiles (no floors).
    public static void GenerateSliceTile(
        ChunkState chunk,
        int sliceInChunk,
        DetailGroupData[] detailPalette,
        EnvironmentKitData[] kitPalette,
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
            // floors, no ceiling flag. Wall slot is kit-agnostic so any
            // pure-solid chunk (stone, terrain, whatever) reads as the
            // single dark-grey Wall color.
            var wall = new SliceCell { TileId = (byte)MinimapTileColors.WALL_SLOT, FoliageId = 0, Flags = 0 };
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
                int floorY = -1;     // local Y of topmost-with-air-above
                int topSolidY = -1;  // local Y of topmost solid in slice (for wall pixels)
                bool sawAirAbove = false;
                VoxelType floorVoxel = VoxelType.Air;
                byte floorFoliage = 0;

                // Scan top-down within slice. Track whether the voxel one
                // above (in-slice or in the chunk above) was air; first
                // solid after an air voxel is the floor.
                if (sliceTopY < ChunkState.SIZE)
                {
                    sawAirAbove = chunk.Voxels[x, sliceTopY, z] == VoxelType.Air;
                }
                else if (worldState != null)
                {
                    // Top slice: peek into the chunk directly above. Without
                    // this, deep-underground chunks (solid above) misclassify
                    // their wall columns as floors and pick up the surface
                    // biome color above.
                    int wx = chunk.ChunkCoord.X * ChunkState.SIZE + x;
                    int wyAbove = (chunk.ChunkCoord.Y + 1) * ChunkState.SIZE;
                    int wz = chunk.ChunkCoord.Z * ChunkState.SIZE + z;
                    sawAirAbove = worldState.GetVoxelWorld(wx, wyAbove, wz) == VoxelType.Air;
                }
                else
                {
                    sawAirAbove = true;
                }

                for (int y = sliceTopY - 1; y >= sliceBaseY; y--)
                {
                    VoxelType v = chunk.Voxels[x, y, z];
                    bool isSolid = v != VoxelType.Air;
                    if (isSolid)
                    {
                        if (topSolidY < 0)
                        {
                            topSolidY = y;
                        }
                        if (sawAirAbove && floorY < 0)
                        {
                            floorY = y;
                            floorVoxel = v;
                            floorFoliage = ResolveFoliageId(chunk, detailPalette, x, y, z);
                        }
                        sawAirAbove = false;
                    }
                    else
                    {
                        sawAirAbove = true;
                    }
                }

                byte flags = 0;
                byte tileId = 0;
                byte foliageId = 0;

                if (floorY >= 0)
                {
                    flags |= SliceFlagFloor;
                    int floorKitId = chunk.GetKitId(x, floorY, z);
                    int floorOverlayId = chunk.GetOverlayId(x, floorY, z);
                    tileId = (byte)ResolveSurfaceTileId(floorVoxel, chunkBaseY + floorY, floorKitId, floorOverlayId, kitPalette);
                    foliageId = floorFoliage;
                }
                else if (topSolidY >= 0)
                {
                    // Wall-only pixel (slice fully solid in this column —
                    // underground rock or inside-cliff). Always paints with
                    // the dedicated Wall slot regardless of biome / kit so
                    // tunnels read consistently dark grey.
                    tileId = (byte)MinimapTileColors.WALL_SLOT;
                }
                // else: open air column. tileId stays 0, no floor flag.

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
    // The same kit-and-overlay resolution applies in both modes; only the
    // FlatTile vs WallTile lookup differs for AUTO terrain.
    private static int ResolveSurfaceTileId(VoxelType type, int worldY, int kitId, int overlayId, EnvironmentKitData[] kitPalette, bool useWallTile = false)
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
                ? ResolveKitWallTile(kitId, kitPalette)
                : ResolveKitFlatTile(kitId, kitPalette);
        }
        return ApplyBand(baseTile, worldY);
    }

    private static int ResolveKitFlatTile(int kitId, EnvironmentKitData[] kitPalette)
    {
        if (kitPalette == null || kitId < 0 || kitId >= kitPalette.Length)
        {
            return VoxelTypeInfo.TILE_GRASS_TOP;
        }
        EnvironmentKitData kit = kitPalette[kitId];
        if (kit == null)
        {
            return VoxelTypeInfo.TILE_GRASS_TOP;
        }
        return kit.FlatTile;
    }

    private static int ResolveKitWallTile(int kitId, EnvironmentKitData[] kitPalette)
    {
        if (kitPalette == null || kitId < 0 || kitId >= kitPalette.Length)
        {
            return VoxelTypeInfo.TILE_STONE;
        }
        EnvironmentKitData kit = kitPalette[kitId];
        if (kit == null)
        {
            return VoxelTypeInfo.TILE_STONE;
        }
        return kit.WallTile;
    }

    private static int ApplyBand(int baseTile, int worldY)
    {
        if (VoxelTypeInfo.TileVariants.TryGetValue(baseTile, out VoxelTypeInfo.TileVariantInfo variants) && variants.Bands > 1)
        {
            int band = Mathf.FloorToInt((worldY - VoxelTypeInfo.TILE_BAND_ORIGIN_Y) / VoxelTypeInfo.TILE_BAND_HEIGHT);
            band = ((band % variants.Bands) + variants.Bands) % variants.Bands;
            return baseTile + band * variants.VariantsPerBand;
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
