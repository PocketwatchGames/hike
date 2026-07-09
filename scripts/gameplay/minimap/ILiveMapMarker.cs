using Godot;

// A marker drawn LIVE at an entity's current world position on the minimap and
// world map — always visible, with NO fog-of-war reveal gate and NO campfire
// banking. This is the moving/ephemeral counterpart to MapMarker (which charts
// STATIC landmarks discovered by exploration and recorded into Knowledge):
// talkable NPCs (which wander) and fallen party members (a grave where they
// fell). Implementers register with World.RegisterLiveMapMarker on spawn and
// unregister on removal; the map overlays iterate the registry each redraw and
// draw those whose ShouldShowMapMarker is currently true.
public interface ILiveMapMarker
{
    // Whether the marker should draw right now (e.g. NPC alive, party member dead).
    bool ShouldShowMapMarker { get; }
    // Current world position — read live each redraw so the icon tracks movement.
    Vector3 MapMarkerWorldPosition { get; }
    // Icon to draw; null skips the marker.
    Texture2D MapMarkerIcon { get; }
    // Tint applied to the icon.
    Color MapMarkerModulate { get; }
}
