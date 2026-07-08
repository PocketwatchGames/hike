// How a Sensed ("?") map marker is promoted to Identified. The Sensed step is
// ALWAYS map-reveal (the fog passing over the marker's position); this enum
// governs only the next step up. Author-chosen per MapMarkerData so each kind
// of landmark picks the identify trigger that fits it.
public enum EMapMarkerIdentifyMode
{
    // Identify once the player is within MapMarkerData.identifyRadius. Works for
    // any landmark, needs no perception node.
    Proximity,
    // Identify when a sibling Discoverable on the same host hits Discovered —
    // reuses line-of-sight / light gating. Only for hosts that opt into perception.
    Perception,
    // Identify only when the player interacts with the host (host calls Identify()).
    Interaction,
}
