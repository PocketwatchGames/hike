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
//   - The same ArrowLootData.removeTimeMs that expires loose arrows on the
//     ground also expires arrows still embedded in a live mob, so a missed
//     pickup window doesn't permanently lock up an ammo slot.
[GlobalClass]
public partial class ArrowStuck : Node3D, IWeaponArrow
{
    private WeaponState _sourceWeapon;
    private ArrowLootData _data;
    private float _ageSeconds;

    public WeaponState SourceWeapon => _sourceWeapon;

    public float GetReplenishProgress()
    {
        if (_data == null || _data.removeTimeMs <= 0)
        {
            return 0f;
        }
        return Mathf.Clamp(_ageSeconds * 1000f / _data.removeTimeMs, 0f, 1f);
    }

    public static ArrowStuck Create(Mob mob, ArrowLootData data, WeaponState sourceWeapon, Vector3 worldHitPos, Vector3 hitDirection)
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

        // Embedded arrows use the same 3D model the in-flight Projectile fired
        // (arrow data's worldModel → scenes/projectiles/arrow_model.tscn), so
        // the arrow stuck in the mob reads as the object that was shot rather
        // than the flat worldSprite billboard. Loose ground arrows keep the
        // sprite (Loot), so only this embedded case carries the model. Orient
        // it along the shot's travel direction the same way Projectile.Launch
        // aims the in-flight visual (LookAt points local -Z down the flight).
        if (data.worldModel != null)
        {
            Node3D model = data.worldModel.Instantiate<Node3D>();
            stuck.AddChild(model);
            if (hitDirection.LengthSquared() > 1e-6f)
            {
                Vector3 fwd = hitDirection.Normalized();
                Vector3 up = Mathf.Abs(fwd.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
                stuck.LookAt(stuck.GlobalPosition + fwd, up);
            }
            return stuck;
        }

        // Fallback when no model is authored: programmatic Sprite3D from the
        // arrow data's worldSprite. Unshaded billboard, no orientation — the
        // stuck arrow is a small detail and reads fine flat.
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

    public override void _Process(double delta)
    {
        if (_data == null || _data.removeTimeMs <= 0)
        {
            return;
        }
        _ageSeconds += (float)delta;
        if (_ageSeconds * 1000f < _data.removeTimeMs)
        {
            return;
        }
        // Notify the parent mob first so it can drop us from _stuckArrows
        // before we free — otherwise its _ExitTree would later iterate a
        // freed reference and double-fire ReturnAmmoOnRemoval. The mob
        // forwards to ReturnAmmoOnRemoval itself, which returns ammo and
        // frees this node.
        if (GetParent() is Mob mob)
        {
            mob.OnStuckArrowExpired(this);
        }
        else
        {
            ReturnAmmoOnRemoval();
        }
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
