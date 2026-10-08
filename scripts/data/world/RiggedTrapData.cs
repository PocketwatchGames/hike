using Godot;

// A trap rigged to an interactive that springs when it opens — a gas cloud out
// of a chest. Chosen per placement (ChestSpawnEntry.trap), so one chest scene
// serves every trap and key combination.
[GlobalClass]
public partial class RiggedTrapData : Resource
{
    // Instanced as a child of the host when it spawns; its root must be an
    // ITriggerable, fired once when the host opens.
    [Export] public PackedScene scene;
}
