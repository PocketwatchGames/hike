using Godot;

// Turns a painted document by quarter turns, clockwise as seen on the painter's
// map (+X right, +Z down — which is also clockwise seen from above in the
// world). One turn sends world +X to +Z.
//
// Unlike a resize this is EXACT: every column, chunk and voxel lands on exactly
// one cell, so four turns reproduce the input byte for byte. That is the check
// for any change here — a single wrong axis anywhere breaks the round trip.
//
// Moving the pixels is not the whole job. Three things carry a DIRECTION and
// must turn as well, and nothing would report forgetting one: the painted
// wind's angle, a stamp's rotation, and an entity's facing.
//
// Derived content re-rolls rather than turning: the prop fill and the mob
// scatter hash their world column, so a region keeps its lists but not the
// exact tree that stood on each spot.
public static class WorldMapRotate
{
    // A quarter turn of the wind layer's angle byte (a full turn = 256).
    private const int WIND_QUARTER_TURN = 64;

    // Do NOT run this with the painter open by hand — go through
    // WorldMapPainter.ApplyDocumentRewrite, which saves first and reopens after.
    public static bool Run(WorldMapData data, int quarterTurnsClockwise)
    {
        if (data == null)
        {
            GD.PrintErr("worldmap_rotate: no document.");
            return false;
        }
        int turns = Mathf.PosMod(quarterTurnsClockwise, 4);
        if (turns == 0)
        {
            GD.Print("worldmap_rotate: a whole number of full turns, nothing to do.");
            return true;
        }

        int oldChunksX = data.sizeChunksX;
        int oldChunksZ = data.sizeChunksZ;
        var state = new WorldMapState(data);
        for (int i = 0; i < turns; i++)
        {
            TurnClockwise(state, data);
        }
        state.InvalidateVoxelEdits();

        state.Save();
        if (!string.IsNullOrEmpty(data.ResourcePath))
        {
            Error err = ResourceSaver.Save(data, data.ResourcePath);
            if (err != Error.Ok)
            {
                GD.PrintErr($"worldmap_rotate: layers were rewritten but {data.ResourcePath} could not be saved ({err}) — "
                    + $"set sizeChunksX/sizeChunksZ to {data.sizeChunksX}/{data.sizeChunksZ} by hand or the document will not match its images.");
                return false;
            }
        }
        GD.Print($"worldmap_rotate: turned {turns * 90} deg clockwise, {oldChunksX}x{oldChunksZ} -> "
            + $"{data.sizeChunksX}x{data.sizeChunksZ} chunks. Re-bake the world.");
        return true;
    }

    // Texel (px, pz) of a W x H layer lands on (H - 1 - pz, px) of the H x W one.
    private static void TurnClockwise(WorldMapState state, WorldMapData data)
    {
        int oldW = data.ImageWidth;
        int oldH = data.ImageHeight;
        int oldMinX = data.WorldMinX;
        int oldMinZ = data.WorldMinZ;

        // Footprints are measured at the OLD extent and rotation, before anything
        // below changes either.
        SubscenePlacement[] stamps = state.Placements.placements;
        var footprints = new Rect2I[stamps.Length];
        for (int i = 0; i < stamps.Length; i++)
        {
            footprints[i] = stamps[i] == null ? new Rect2I() : state.FootprintOf(stamps[i]);
        }

        (data.sizeChunksX, data.sizeChunksZ) = (data.sizeChunksZ, data.sizeChunksX);
        int newMinX = data.WorldMinX;
        int newMinZ = data.WorldMinZ;

        state.Elevation = Turn(state.Elevation);
        state.Water = Turn(state.Water);
        state.Ground = Turn(state.Ground);
        state.WaterType = Turn(state.WaterType);
        state.Paving = Turn(state.Paving);
        state.BlockingProps = Turn(state.BlockingProps);
        state.Mobs = Turn(state.Mobs);
        state.Scalars = Turn(state.Scalars);
        state.Region = Turn(state.Region);
        state.Zone = Turn(state.Zone);
        state.Wind = Turn(state.Wind);
        TurnWindAngles(state.Wind);
        state.Tunnels = TurnTunnels(state.Tunnels);

        Vector2I TurnCell(Vector2I world)
        {
            int px = world.X - oldMinX;
            int pz = world.Y - oldMinZ;
            return new Vector2I(newMinX + oldH - 1 - pz, newMinZ + px);
        }

        for (int i = 0; i < stamps.Length; i++)
        {
            SubscenePlacement stamp = stamps[i];
            if (stamp == null)
            {
                continue;
            }
            // A scene's own turn is anticlockwise (SubsceneRotator: +Z faces +X),
            // so a clockwise turn is three of them.
            stamp.rotation = (ESubsceneRotation)(((int)stamp.rotation + 3) & 3);
            stamp.anchorXZ = TurnStampAnchor(state, stamp, footprints[i], oldH, newMinX, newMinZ,
                oldMinX, oldMinZ);
        }

        foreach (EntityPlacement entity in state.Placements.entities)
        {
            if (entity == null)
            {
                continue;
            }
            entity.anchorXZ = TurnCell(entity.anchorXZ);
            // Facing is yaw about +Y in eighths with +Z at Deg0 and +X at Deg90;
            // a clockwise map turn takes +X to +Z, i.e. two eighths back.
            entity.facing = (EEntityFacing)(((int)entity.facing + 6) & 7);
        }

        if (state.Placements.hasSpawn)
        {
            state.Placements.spawnXZ = TurnCell(state.Placements.spawnXZ);
        }
    }

    // The anchor is placed so the bake's floor(anchor - sub.Anchor) puts the
    // turned scene exactly on the turned footprint. Rotating the anchor POINT
    // instead is only exact for a whole-voxel sub.Anchor; with a half-voxel one
    // the floor lands a voxel off on one axis.
    private static Vector2I TurnStampAnchor(WorldMapState state, SubscenePlacement stamp, Rect2I footprint,
        int oldH, int newMinX, int newMinZ, int oldMinX, int oldMinZ)
    {
        SubsceneState turned = footprint.Size == Vector2I.Zero ? null : state.SubsceneFor(stamp);
        if (turned == null)
        {
            GD.PushWarning($"worldmap_rotate: '{stamp.path}' did not load; its anchor was turned as a point "
                + "and may sit a voxel off once the scene is back.");
            int px = stamp.anchorXZ.X - oldMinX;
            int pz = stamp.anchorXZ.Y - oldMinZ;
            return new Vector2I(newMinX + oldH - pz, newMinZ + px);
        }
        int originX = newMinX + oldH - footprint.Position.Y - footprint.Size.Y;
        int originZ = newMinZ + footprint.Position.X;
        return new Vector2I(Mathf.CeilToInt(originX + turned.Anchor.X), Mathf.CeilToInt(originZ + turned.Anchor.Z));
    }

    // Raw bytes, a whole pixel at a time, so it is format-agnostic (Rf heights
    // and Rgba8 indices alike) and copies every value exactly.
    private static Image Turn(Image src)
    {
        if (src == null)
        {
            return null;
        }
        int w = src.GetWidth();
        int h = src.GetHeight();
        byte[] s = src.GetData();
        int stride = s.Length / Mathf.Max(1, w * h);
        var d = new byte[s.Length];
        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = h - 1 - z;
                int dz = x;
                System.Array.Copy(s, (x + z * w) * stride, d, (dx + dz * h) * stride, stride);
            }
        }
        return Image.CreateFromData(h, w, false, src.GetFormat(), d);
    }

    // The angle (R) points the wind along (cos, sin) in world XZ, so a clockwise
    // map turn adds a quarter. Unpainted chunks (G = 0) are left alone.
    private static void TurnWindAngles(Image wind)
    {
        if (wind == null)
        {
            return;
        }
        byte[] d = wind.GetData();
        int stride = d.Length / Mathf.Max(1, wind.GetWidth() * wind.GetHeight());
        if (stride < 2)
        {
            GD.PrintErr($"worldmap_rotate: wind layer has {stride}-byte pixels, expected Rgba8 — angles NOT turned.");
            return;
        }
        for (int i = 0; i < d.Length; i += stride)
        {
            if (d[i + 1] != 0)
            {
                d[i] = (byte)((d[i] + WIND_QUARTER_TURN) & 0xFF);
            }
        }
        wind.SetData(wind.GetWidth(), wind.GetHeight(), false, wind.GetFormat(), d);
    }

    private static ushort[,,] TurnTunnels(ushort[,,] src)
    {
        if (src == null)
        {
            return null;
        }
        int w = src.GetLength(0);
        int vh = src.GetLength(1);
        int h = src.GetLength(2);
        var dst = new ushort[h, vh, w];
        for (int x = 0; x < w; x++)
        {
            for (int z = 0; z < h; z++)
            {
                for (int y = 0; y < vh; y++)
                {
                    dst[h - 1 - z, y, x] = src[x, y, z];
                }
            }
        }
        return dst;
    }
}
