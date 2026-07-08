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
    // 3D model shown while the arrow is embedded in a live mob (ArrowStuck).
    // This is the same scene the in-flight Projectile renders
    // (scenes/projectiles/arrow_model.tscn), so a stuck arrow reads as the
    // exact object that was fired rather than the flat worldSprite billboard.
    // Loose arrows that drop to the ground stay on worldSprite for parity with
    // every other Loot pickup; only the embedded case uses the model. Null
    // falls back to the worldSprite billboard. Distinct from the inherited
    // LootData.worldModel (the on-ground mesh) — an embedded arrow is not the
    // same visual as a dropped one, so it gets its own slot.
    [Export] public PackedScene stuckModel;

    // Arrows never enter the inventory — a pickup reclaims them into the firing
    // weapon's ammo pool (see Loot), so they're their own category, neither
    // backpack material nor an equip slot.
    protected override EItemCategory ComputeCategory() => EItemCategory.Ammo;
}
