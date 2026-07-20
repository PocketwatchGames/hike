using Godot;

// Per-instance discovery record for one charted map marker, stored in
// Knowledge.DiscoveredMarkers keyed by quantized world position. Markers are
// stationary landmarks (forge, campfire), so the rounded WorldPosition is a
// stable identity across chunk unload / save — entity sim states carry no unique
// id. Held in two Knowledge stores (party pool + active member) and unioned by
// MAX Level on read/merge, matching the rest of the two-tier knowledge model.
//
// Carries the DISPLAY data (icon + name) copied from the MapMarker node at
// discovery, so the maps can still draw a charted marker after its host chunk
// unloads and the node is freed. The node's behavior config (identify mode /
// radius) is NOT copied here — it's only needed while the node is live.
//
// Dynamic host state (a campfire's lit/unlit) is also NOT stored here — that's a
// separate axis read live from the host / sim state at render time.
public class MapMarkerRecord
{
    public Vector3 WorldPosition;
    public EMapMarkerLevel Level;
    // Icon shown once Identified (until then the maps draw a shared "?"). Handed
    // over by the node at discovery so it survives the node being freed.
    public Texture2D Icon;
    // Name shown on hover once Identified.
    public StringName DisplayName;
    // Two-state appearance (campfire lit/unlit): when true the maps pick
    // ActiveModulate vs IconModulate by the live host state (see
    // SimState.IsMarkerActive), read at render time — the ACTIVE state itself
    // is never stored here, only the two tints and whether the marker has states.
    public bool HasActiveState;
    public Color IconModulate = Colors.White;
    public Color ActiveModulate = Colors.White;

    public MapMarkerRecord() { }

    public MapMarkerRecord(Vector3 worldPosition, EMapMarkerLevel level, Texture2D icon, StringName displayName,
        bool hasActiveState, Color iconModulate, Color activeModulate)
    {
        WorldPosition = worldPosition;
        Level = level;
        Icon = icon;
        DisplayName = displayName;
        HasActiveState = hasActiveState;
        IconModulate = iconModulate;
        ActiveModulate = activeModulate;
    }

    // Dictionary key for a marker at this world position. Rounds to the nearest
    // meter — markers are authored at stationary positions, so per-meter
    // quantization dedups the same landmark across reloads without colliding
    // distinct landmarks.
    public static Vector3I KeyFor(Vector3 worldPos) => new Vector3I(
        Mathf.RoundToInt(worldPos.X),
        Mathf.RoundToInt(worldPos.Y),
        Mathf.RoundToInt(worldPos.Z));
}
