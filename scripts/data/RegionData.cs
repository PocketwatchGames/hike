using Godot;

// Named map region. Multiple zones (the small map subdivisions in
// ZoneData) reference the same RegionData when they belong to the
// same named place — "The Hollow Reach", "Verdant Lands", etc. The
// banner / music / loot table hooks live ON the banner scene and
// future systems; this resource just identifies the region and
// carries its display name.
//
// Border zones (ZoneData.region == null) don't change the player's
// current region. GameClient.UpdateRegion handles the dwell +
// distance hysteresis so the banner doesn't flicker on seam
// crossings and the player can't ride a chain of border zones
// forever.
[GlobalClass]
public partial class RegionData : Resource
{
    // Text shown on the banner when the player enters this region.
    // Matches the StringName-as-display-text convention used by
    // ItemData.displayName / InteractiveAction.displayName — wire
    // through Loc later when localization is plumbed across the
    // codebase rather than picking a different convention here.
    [Export] public StringName displayName;
}
