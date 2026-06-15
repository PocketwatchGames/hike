using Godot;
using Godot.Collections;

// Authored branching dialogue for an NPC. Bipartite graph: branches and
// response groups alternate as nodes. A branch types its lines, then
// hands off to its exitGroup; the player picks a response from the
// group, which transitions to a new branch; and so on. Both node types
// are referenced by name within this resource.
//
// Flow:
//   1. On conversation start, walk `entryBranches` in order. The first
//      entry whose condition evaluates true (or is null) picks the
//      opening branch by name.
//   2. The branch's lineLocKeys are typed out. After the last line,
//      branch.endActions fire and the branch's `exitGroup` is shown
//      (or the conversation closes if exitGroup is empty).
//   3. The chooser presents the group's responses, filtered by
//      ConversationVisibility. Visibility math uses the group's
//      canonical entry branch (the one flagged isPrimaryGroupEntry),
//      not whichever branch the player actually came from — so the
//      visible set stays stable across loop-backs.
//   4. Picking a response fires its actions and advances to its
//      destination branch by name. An empty destination ends the
//      conversation.
//
// Conditions sit on the edges — entries gate which branch starts the
// convo, per-response conditions gate which choices the player can pick.
// Subclass ConversationCondition to add new predicates.
[GlobalClass]
public partial class ConversationData : Resource
{
    // Walked in order on conversation start; first valid entry wins. Put a
    // null-condition entry last as the unconditional fallback.
    [Export] public Array<ConversationEntry> entryBranches;

    // Every branch reachable in this conversation. Referenced by `name`
    // from entries (start point) and responses (destination).
    [Export] public Array<ConversationBranch> branches;

    // Every response group reachable in this conversation. Referenced by
    // `name` from branches (via `exitGroup`).
    [Export] public Array<ConversationResponseGroup> responseGroups;
}
