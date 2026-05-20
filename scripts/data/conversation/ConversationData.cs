using Godot;
using Godot.Collections;

// Authored branching dialogue for an NPC. Mirrors the BrainData / BehaviorNode
// shape — a flat pool of branches referenced by `name`, with a separate
// `entryBranches` list that picks the opening line.
//
// Flow:
//   1. On conversation start, walk `entryBranches` in order. The first entry
//      whose condition evaluates true (or is null) picks the starting branch
//      by name.
//   2. The selected ConversationBranch's `text` is the NPC's line. Its
//      `responses` (filtered by their conditions) are shown to the player.
//   3. Picking a response advances to that response's destination branch by
//      name. An empty destination ends the conversation.
//
// Conditions sit on the edges — entries gate which branch starts the convo,
// per-response conditions gate which choices the player can pick. Subclass
// ConversationCondition to add new predicates.
[GlobalClass]
public partial class ConversationData : Resource
{
    // Walked in order on conversation start; first valid entry wins. Put a
    // null-condition entry last as the unconditional fallback.
    [Export] public Array<ConversationEntry> entryBranches;

    // Every branch reachable in this conversation. Referenced by `name` from
    // entries and responses.
    [Export] public Array<ConversationBranch> branches;
}
