using Godot;
using Godot.Collections;


// What a RUN in this world begins with: its quests, its party, and what the
// player already knows. Owned by the world rather than by the generator, because
// a painted world has all three and no generator — WorldGen and the map painter
// both hand one of these to WorldState.BindStartContent.
//
// It is also what a .hike records. The file header stores this resource's PATH
// (WorldFile v48) and re-resolves it on load, because initialKnowledge is
// authored as embedded sub-resources with no path of their own — the owner is
// the only addressable thing. Storing the path of a WorldGenData instead, as it
// did, meant opening any world dragged the whole generator graph (zones, terrain
// approaches, spawn lists) into memory to read three fields.
[GlobalClass]
public partial class WorldStartData : Resource
{
    // This world's authored scripted content — quests today, scripted events
    // later. Threaded onto WorldState.ScriptData at load (GameClient.Init).
    // Separate from SimData, which is generic cross-session content. Null = no
    // scripted content in this world.
    [Export] public WorldScriptData scriptData;

    // The party the run begins with. Each PlayerState is one playable character
    // (identity + appearance + stat sheet + its own starting loadout + traits);
    // the first entry is the initially-controlled member. GameClient.Init clones
    // these templates into the runtime SimState.Party at game start. This
    // replaces the old single CharacterCreationState + the shared per-world
    // loadout (starting gear is now per-character, on PlayerState).
    [Export] public PlayerState[] startingParty = System.Array.Empty<PlayerState>();

    // Things the player already knows about when the run begins. Each
    // entry is a TeachableConcept subclass — ItemTeachable identifies an
    // item by name, RecipeTeachable seeds a recipe into the cookbook,
    // LanguageTeachable grants language components, RegionTeachable
    // reveals a map region, MobTeachable seeds a bestiary entry. Applied
    // via the same Teach() path that scrolls / NPC rewards use, so a
    // "starter pack" of knowledge composes the same way mid-run rewards
    // do. Announcements are suppressed during initial application (see
    // GameClient.SuppressAnnouncements) — the player shouldn't see a
    // stack of banners on the first frame.
    [Export] public Array<TeachableConcept> initialKnowledge = new();
}
