using System.Collections.Generic;
using System.IO;
using Godot;

// The party's map of the world: fog-of-war reveal, named regions and landmark
// markers. Unlike Knowledge it has no per-member provisional tier — whatever the
// controlled character sees is charted here immediately and permanently, so a
// death never costs the party any of its map. Plain runtime state on Party.
public class MapChart
{
    public readonly ExplorationMask Exploration = new();
    public readonly HashSet<RegionData> DiscoveredRegions = new();

    // Keyed by quantized world position (MapMarkerRecord.KeyFor).
    public readonly Dictionary<Vector3I, MapMarkerRecord> DiscoveredMarkers = new();

    // Inside a shared EntitySerializer table (SaveGame).
    public void Serialize(BinaryWriter w)
    {
        Exploration.Serialize(w);
        w.Write(DiscoveredRegions.Count);
        foreach (RegionData region in DiscoveredRegions)
        {
            EntitySerializer.WriteRef(w, region);
        }
        w.Write(DiscoveredMarkers.Count);
        foreach (MapMarkerRecord m in DiscoveredMarkers.Values)
        {
            w.Write(m.WorldPosition.X);
            w.Write(m.WorldPosition.Y);
            w.Write(m.WorldPosition.Z);
            w.Write((int)m.Level);
            EntitySerializer.WriteRef(w, m.Icon);
            w.Write(m.DisplayName?.ToString() ?? "");
            w.Write(m.HasActiveState);
            WriteColor(w, m.IconModulate);
            WriteColor(w, m.ActiveModulate);
        }
    }

    public void Deserialize(BinaryReader r)
    {
        Exploration.Deserialize(r);
        DiscoveredRegions.Clear();
        int regions = r.ReadInt32();
        for (int i = 0; i < regions; i++)
        {
            RegionData region = EntitySerializer.ReadRef<RegionData>(r);
            if (region != null)
            {
                DiscoveredRegions.Add(region);
            }
        }
        DiscoveredMarkers.Clear();
        int markers = r.ReadInt32();
        for (int i = 0; i < markers; i++)
        {
            var position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var level = (EMapMarkerLevel)r.ReadInt32();
            Texture2D icon = EntitySerializer.ReadRef<Texture2D>(r);
            string name = r.ReadString();
            bool hasActiveState = r.ReadBoolean();
            Color iconModulate = ReadColor(r);
            Color activeModulate = ReadColor(r);
            DiscoveredMarkers[MapMarkerRecord.KeyFor(position)] = new MapMarkerRecord(position, level, icon,
                name.Length > 0 ? new StringName(name) : null, hasActiveState, iconModulate, activeModulate);
        }
    }

    private static void WriteColor(BinaryWriter w, Color c)
    {
        w.Write(c.R);
        w.Write(c.G);
        w.Write(c.B);
        w.Write(c.A);
    }

    private static Color ReadColor(BinaryReader r)
    {
        return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
