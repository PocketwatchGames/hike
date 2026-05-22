using Godot;
using Godot.Collections;

// A set of player choices presented together as one chooser menu. Branches
// point at a group via `Branch.exitGroup` to surface its responses after
// the branch's lines finish typing; the same group can be the exit target
// of multiple branches (loops, return flows).
//
// Visibility for the responses inside is always computed against ONE
// canonical branch — whichever branch in the conversation has
// `isPrimaryGroupEntry = true` AND `exitGroup` matching this group. That
// keeps the visible set stable regardless of which path the player took
// to reach the group, instead of fluctuating with whatever lines the most
// recent branch happened to contain.
//
// Maps cleanly onto a future bipartite flowchart editor: branches and
// response groups are the two node types; edges go branch→group via
// `Branch.exitGroup` and group→branch via `Response.destination`.
[GlobalClass]
public partial class ConversationResponseGroup : Resource
{
    // Referenced by `Branch.exitGroup` and (via destination lookup) other
    // groups in the same conversation. Keep unique within a ConversationData.
    [Export] public StringName name;

    // Player choices shown when this group is reached. Empty = the
    // conversation closes when a branch exits to this group (same as
    // branch.exitGroup being empty).
    [Export] public Array<ConversationResponse> responses;
}
