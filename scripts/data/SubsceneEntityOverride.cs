using Godot;

// One stamp's own copy of one of its scene's entities — which lock THIS
// building's front door takes. Keyed by the scene entity's id rather than by
// position or list order, both of which move when the scene is re-authored.
//
// Holds the fork and nothing else: where the entity stands and which way it
// faces are the scene's, so a stamp can retune its entities but not move or
// remove them.
[GlobalClass]
public partial class SubsceneEntityOverride : Resource
{
    [Export] public int entityId;
    [Export] public SpawnEntryData custom;
}
