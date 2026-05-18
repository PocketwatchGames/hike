using Godot;

// Visual + ammo-binding for an arrow currently stuck in a mob. Parented to
// the Mob so it follows the mob's motion. No collision, no interact area —
// the arrow only becomes pickable after the mob dies (Mob.Die drops each
// stuck arrow as a loose ArrowLoot with an outward impulse) or returns
// ammo if the mob is removed without dying.
//
// Lifetime contract:
//   - Create() while the mob is alive — caller stashes it on the mob and
//     registers it with the source weapon.
//   - DropAsLoot(impulse) is called by Mob.Die for each stuck arrow. The
//     arrow detaches from the weapon's tracking list (no ammo bump), a
//     fresh ArrowLootSimState is spawned at the stuck world position to
//     take over the binding, and this node frees itself.
//   - ReturnAmmoOnRemoval() is called by Mob._ExitTree if the mob is
//     unloaded with the arrow still stuck (no death). Fires the standard
//     OnArrowRemoved so the weapon recovers 1 ammo.
[GlobalClass]
public partial class ArrowStuck : Node3D, IWeaponArrow
{
    private WeaponState _sourceWeapon;
    private ArrowLootData _data;

    public WeaponState SourceWeapon => _sourceWeapon;

    public static ArrowStuck Create(Mob mob, ArrowLootData data, WeaponState sourceWeapon, Vector3 worldHitPos)
    {
        if (mob == null || data == null || sourceWeapon == null)
        {
            return null;
        }
        var stuck = new ArrowStuck();
        stuck._data = data;
        stuck._sourceWeapon = sourceWeapon;
        mob.AddChild(stuck);
        stuck.GlobalPosition = worldHitPos;

        // Programmatic Sprite3D — the texture is the arrow data's authored
        // worldSprite (resource), which we're allowed to bind at runtime.
        // No LitSprite shading on this pass; the stuck arrow is a small
        // detail and reads fine with the default unshaded sprite.
        Texture2D texture = data.worldSprite ?? data.inventorySprite;
        if (texture != null)
        {
            var sprite = new Sprite3D();
            sprite.Texture = texture;
            sprite.PixelSize = 0.0738f;
            sprite.Centered = true;
            sprite.AlphaCut = SpriteBase3D.AlphaCutMode.OpaquePrepass;
            sprite.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
            stuck.AddChild(sprite);
        }
        return stuck;
    }

    // Transition stuck → loose loot. Removes the stuck instance from the
    // weapon's tracking without bumping ammo (the new ArrowLootSimState
    // re-registers, so the net count is unchanged), then frees self.
    public void DropAsLoot(Vector3 impulse)
    {
        WeaponState weapon = _sourceWeapon;
        ArrowLootData data = _data;
        Vector3 worldPos = GlobalPosition;
        _sourceWeapon = null;
        _data = null;
        weapon?.DetachArrow(this);
        if (weapon != null && data != null && World.Current != null)
        {
            World.Current.SpawnArrowLoot(worldPos, impulse, data, weapon);
        }
        QueueFree();
    }

    // Mob removed without dying — fire the standard removal hook so the
    // weapon recovers 1 ammo, then free self.
    public void ReturnAmmoOnRemoval()
    {
        WeaponState weapon = _sourceWeapon;
        _sourceWeapon = null;
        _data = null;
        weapon?.OnArrowRemoved(this);
        QueueFree();
    }
}
