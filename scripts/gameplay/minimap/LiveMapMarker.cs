using System;
using Godot;

// A marker drawn LIVE at an entity's current world position on the minimap and
// world map — always visible, with NO fog-of-war reveal gate and NO campfire
// banking. The moving/ephemeral counterpart to MapMarker (which charts STATIC
// landmarks discovered by exploration and recorded into Knowledge).
//
// Authored as a child node in an entity scene (a talkable NPC, the player's
// grave), carrying its own icon + tint so the two travel together, per-scene.
// The owning entity assigns ActiveCondition to gate when it draws (NPC alive &
// un-recruited, party member dead). Position tracks the parent since it's a
// child. The node self-registers with World on _Ready and unregisters on
// TreeExiting; the map overlays iterate the registry each redraw and draw the
// markers whose IsActive is currently true.
[GlobalClass]
public partial class LiveMapMarker : Node3D
{
    // Icon drawn at the entity's live position; null skips the marker.
    [Export] public Texture2D icon;
    // Tint applied to the icon.
    [Export] public Color modulate = Colors.White;

    // Live gate supplied by the owning entity (e.g. NPC alive & talkable, party
    // member dead). Null => draw whenever an icon is set.
    public Func<bool> ActiveCondition;

    public bool IsActive => icon != null && (ActiveCondition == null || ActiveCondition());
    public Texture2D Icon => icon;
    public Color Modulate => modulate;
    public Vector3 WorldPosition => GlobalPosition;

    private Sim _world;

    public override void _Ready()
    {
        _world = Sim.Current;
        if (_world != null)
        {
            _world.RegisterLiveMapMarker(this);
            TreeExiting += () => _world.UnregisterLiveMapMarker(this);
        }
    }
}
