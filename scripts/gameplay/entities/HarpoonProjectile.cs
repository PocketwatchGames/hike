using Godot;

// A thrown weapon on a line. It flies like any flat Projectile, and wherever the
// flight ends — a creature, a wall, or the end of its range — the impact resolves
// as usual and the line reels it back to the thrower's chest, passing through
// everything, until it is caught and freed. A creature struck farther than
// pullStandoff is dragged back to that distance at the reel speed: the hit's
// knockback is replaced with the pull, so the receiver's knockback resistance is a
// chance to hold its ground, and the damage profile's hitstun should cover the
// longest drag. While out, the weapon is out of its wielder's hand
// (WeaponState.outOfHandObject).
[GlobalClass]
public partial class HarpoonProjectile : Projectile
{
	// Speed (m/s) the line reels in at — both the empty return and a hooked
	// creature's drag, so a catch arrives with the harpoon.
	[Export] public float reelSpeed = 16f;
	// Horizontal distance (m) from the thrower a hooked creature is dragged to. A
	// creature already inside it takes the hit with no pull. 0 = never pulls.
	[Export] public float pullStandoff = 4f;
	// The harpoon counts as caught once it is this close to the thrower's chest.
	[Export] public float catchRadius = 0.5f;
	// The outbound-only loop riding the harpoon (its flight whoosh), stopped when
	// the reel starts. Loops left out of this keep going until the catch.
	[Export] public Fx flightLoop;
	// One-shots on the thrower: as the line starts reeling in, and on the catch.
	[Export] public PackedScene reelStartEffect;
	[Export] public PackedScene catchEffect;

	private bool _reeling;

	public override void _Ready()
	{
		base._Ready();
		if (SourceWeapon != null)
		{
			SourceWeapon.outOfHandObject = this;
		}
	}

	public override void _ExitTree()
	{
		base._ExitTree();
		if (SourceWeapon != null && SourceWeapon.outOfHandObject == this)
		{
			SourceWeapon.outOfHandObject = null;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!_reeling)
		{
			base._PhysicsProcess(delta);
			return;
		}
		if (!GodotObject.IsInstanceValid(_source) || _source is not Node3D thrower)
		{
			base.EndFlight();
			return;
		}
		Vector3 catchPoint = thrower.GlobalPosition + Vector3.Up * ItemEventHandlers.ArcLaunchHeight;
		Vector3 toCatch = catchPoint - GlobalPosition;
		float distance = toCatch.Length();
		float step = reelSpeed * (float)delta;
		if (distance <= Mathf.Max(catchRadius, step))
		{
			SpawnOnThrower(catchEffect);
			base.EndFlight();
			return;
		}
		Vector3 dir = toCatch / distance;
		_velocity = dir * reelSpeed;
		GlobalPosition += dir * step;
		// Reeled in head-first-away, the way a line drags it.
		Vector3 up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		LookAt(GlobalPosition - dir, up);
	}

	protected override void ModifyHit(ref HitInfo hit, HurtBox target)
	{
		hit.knockbackDistance = 0f;
		hit.knockbackTime = 0f;
		if (pullStandoff <= 0f || reelSpeed <= 0f || !GodotObject.IsInstanceValid(_source) || _source is not Node3D thrower)
		{
			return;
		}
		Vector3 toThrower = thrower.GlobalPosition - target.GlobalPosition;
		toThrower.Y = 0f;
		float pull = toThrower.Length() - pullStandoff;
		if (pull <= 0f)
		{
			return;
		}
		hit.knockbackDirection = toThrower.Normalized();
		hit.knockbackDistance = pull;
		hit.knockbackTime = pull / reelSpeed;
	}

	// The flight is over; keep the trail and reel in rather than freeing.
	protected override void EndFlight()
	{
		_reeling = true;
		if (flightLoop != null && GodotObject.IsInstanceValid(flightLoop))
		{
			flightLoop.Stop();
		}
		SpawnOnThrower(reelStartEffect);
	}

	private void SpawnOnThrower(PackedScene scene)
	{
		if (GodotObject.IsInstanceValid(_source) && _source is IActionActor thrower)
		{
			ItemEventHandlers.SpawnOnActor(thrower, scene);
		}
	}
}
