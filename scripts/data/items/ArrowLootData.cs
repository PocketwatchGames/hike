using Godot;

// Loot dropped at the impact point of a fired arrow. Distinguished from plain
// LootData so the Loot scene can route pickup back to the source weapon's
// ammo pool instead of the player's inventory. The arrow→weapon binding is
// runtime-only and lives on ArrowLootSimState; this resource is just a marker
// plus authoring slot for the per-arrow sprite, timeout, and other tuning
// fields inherited from LootData (removeTimeMs in particular — bow arrows set
// this to 30000).
[GlobalClass]
public partial class ArrowLootData : LootData
{
}
