using System.IO;
using Godot;

// One collected treasure map: a pre-rolled dig location (near where the map was
// found) and a random map heading. It only points at the treasure — the actual
// buried object exists independently in the world (a BuriedSpot placed when the
// map is revealed). Held in SimState.TreasureMaps and serialized by SaveGame; the
// map removes itself when its tracked object is unearthed (Sim.TryDig →
// SimState.RemoveTreasureMapAt). Runtime state, not authored data.
public class TreasureMapState
{
    // Surface world position the X marks — the buried object's location. The
    // treasure-map render centers here, and the map is matched to the dug spot
    // by this position.
    public Vector3 DigLocation;

    // Map heading in radians, fed to the minimap shader's map_rotation. Rolled
    // per map so each reads as its own oriented drawing rather than north-up.
    public float MapRotation;

    public TreasureMapState()
    {
    }

    public TreasureMapState(Vector3 digLocation, float mapRotation)
    {
        DigLocation = digLocation;
        MapRotation = mapRotation;
    }

    // Deterministic per-location heading so a map's orientation is fixed
    // (predetermined by the treasure's position), not re-rolled each read —
    // two maps to the same hole are drawn the same way up.
    public static float DeriveRotation(Vector3 location)
    {
        int h = (Mathf.RoundToInt(location.X) * 73856093) ^ (Mathf.RoundToInt(location.Z) * 19349663);
        return (h & 0xFFFF) / 65535f * Mathf.Tau;
    }

    public void Serialize(BinaryWriter w)
    {
        w.Write(DigLocation.X);
        w.Write(DigLocation.Y);
        w.Write(DigLocation.Z);
        w.Write(MapRotation);
    }

    public static TreasureMapState Deserialize(BinaryReader r)
    {
        Vector3 loc = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        float rot = r.ReadSingle();
        return new TreasureMapState(loc, rot);
    }
}
