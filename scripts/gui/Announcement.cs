using Godot;

// Categorizes an Announcement so the HUD can route it to the right surface.
// Region announcements are dispatched to HudRegionBanner; everything else
// renders on HudAnnouncementPanel. Append new entries — never reorder — so
// future announcement subclasses don't shift existing tags around.
public enum EAnnouncementType
{
	Region,
	Recipe,
	ItemIdentified,
	LanguageLearned,
	LevelUp,
	Boss,
	MobDiscovered,
	MobLevelUp,
	GiftReceived,
}

// Carrier for a queued HUD announcement. Built by whoever discovers the
// underlying knowledge / event (Forge, WorldSimState, Player, etc.) and
// handed to GameClient.Announce, which forwards to the HUD's queue. The
// HUD owns sequencing and dispatch — Region routes to HudRegionBanner,
// every other type renders on HudAnnouncementPanel.
//
// Kept as a plain C# class (not a Resource) because instances are
// constructed per-event and have no .tres authoring story.
public class Announcement
{
	public EAnnouncementType type;

	// Two-line label content used by HudAnnouncementPanel. Region
	// announcements ignore both and read region.displayName instead.
	public string title;
	public string subtitle;

	// Optional inline icon shown next to the title on the panel.
	public Texture2D icon;

	// Optional one-shot sound played when the panel/banner fades in.
	// When null, the surface falls back to its scene-baked default
	// (HudRegionBanner has its own ambient sound for region entries).
	public AudioStream sound;

	// Region context used by HudRegionBanner — only set when type=Region.
	public RegionData region;
}
