using Godot;

// What one SubscenePlacement puts in one of its scene's marker pools. The scene
// authors the POSITIONS (MarkerSimState, tagged with the pool name); the
// placement authors the CONTENT — so the same `.hikescene` stamped twice can
// hold a hermit in one copy and nothing in the other, without forking the file.
//
// A pool the placement says nothing about stays empty: a tagged marker is a
// candidate, never an unconditional spawn.
[GlobalClass]
public partial class SubsceneVariant : Resource
{
    // Marker pool to fill — matches EntitySimState.Tag on the scene's markers.
    // A tag with no markers in the scene places nothing (and warns).
    [Export] public string poolTag = "";

    // Dealt out ONE ENTRY PER MARKER, in list order, across the markers chosen
    // from the pool — five villagers fill five spots rather than piling into
    // one. For a cluster at a single marker, make the entry a SpawnGroupData
    // and let it scatter its own members. Shared as its own asset like the POI
    // placements' content, so two placements can reuse one list.
    [Export] public SpawnListData content;

    // How many of the pool's markers to fill, picked at random.
    //   0 (default) — one per content entry, i.e. place the list exactly once.
    //                 Adding a sixth villager then needs no second edit here.
    //   n           — fill n markers, CYCLING the content list, so one entry
    //                 and count 3 puts three of the same thing in three spots.
    // Clamped to the markers the scene actually has (short pools warn).
    [Export(PropertyHint.Range, "0,16,1,or_greater")] public int count;
}
