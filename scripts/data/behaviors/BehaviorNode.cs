using Godot;
using Godot.Collections;

// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class BehaviorNode : Resource
{
    // Per-brain instance name. Transitions reference sibling nodes by this name.
    [Export] public StringName name;
    [Export] public BehaviorData data;
    [Export] public Array<BehaviorNodeTransition> transitions;
    // Abilities the mob's species must ALL have for this node to exist; see EMobAbility.
    [Export] public EMobAbility requiredAbilities;
}
