using Godot;

// Categorizes an Announcement so the HUD can route it to the right surface.
// Region announcements are dispatched to HudRegionBanner; every other type is
// pushed as a line to the HudEventLog. Append new entries — never reorder — so
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
	GiftReceived,
	// A new member joined the party (a recruited NPC). Event-log line carrying
	// the member's name.
	PartyJoined,
	// Generic event-log notice with no dedicated category — carries its full
	// text in `title`. Used by interactive-action refusals ("Danger Nearby").
	Notice,
}

// Carrier for a queued HUD announcement. Built by whoever discovers the
// underlying knowledge / event (Campfire, SimState, Player, etc.) and
// handed to GameClient.Announce, which forwards to the HUD. The HUD routes
// Region to HudRegionBanner (a serialized full-width banner) and every other
// type to HudEventLog as a fading line.
//
// Kept as a plain C# class (not a Resource) because instances are
// constructed per-event and have no .tres authoring story.
public class Announcement
{
	public EAnnouncementType type;

	// Event-log line content. The log shows "[b]title[/b] subtitle"; a notice
	// with no subtitle is just the title. Region announcements ignore both and
	// read region.displayName instead.
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
