using Godot;

public enum EWeaponState
{
	Ready,
	Charging,
	Active
}

public partial class Player : CharacterBody3D
{
	readonly WeaponState[] _weapons = new WeaponState[(int)EItemSlot.Count];
	int? _activeWeaponSlot;
	ulong _weaponPressTime;
	ulong _weaponActivateTime;
	EWeaponState _weaponState;

	static readonly string[] _weaponActions = new[] { "AttackMelee", "AttackRanged" };

	int? GetActiveWeapon()
	{
		if (_activeWeaponSlot.HasValue)
		{
			WeaponState activeWeapon = _weapons[_activeWeaponSlot.Value];
			return activeWeapon != null && activeWeapon.data != null && _world.GameTimeMs < _weaponActivateTime + activeWeapon.data.activeTime ? _activeWeaponSlot : null;
		}
		return null;
	}

	void WeaponActivate(WeaponState weapon, int slot)
	{
		ulong curTime = _world.GameTimeMs;
		weapon.lastWeaponEventIndex = -1;
		_weaponActivateTime = curTime;
		_activeWeaponSlot = slot;
		_weaponState = EWeaponState.Active;
		weapon.cooldownTime = curTime + (ulong)(1000 * (weapon.data.cooldownTime + weapon.data.activeTime));
		ProcessWeaponEvents(weapon, slot);
	}

	bool CanUseWeapon(WeaponState weapon)
	{
		return !GetActiveWeapon().HasValue && weapon.data != null && weapon.cooldownTime <= _world.GameTimeMs && (!weapon.data.useAmmo || weapon.ammo > 0);
	}

	void WeaponPress(WeaponState weapon, int slot)
	{
		ulong curTime = _world.GameTimeMs;
		if (!CanUseWeapon(weapon))
		{
			return;
		}

		_activeWeaponSlot = slot;
		if (weapon.data.activateOnRelease)
		{
			_weaponState = EWeaponState.Charging;
			_weaponActivateTime = curTime;
			_weaponPressTime = curTime;
		}
		else
		{
			WeaponActivate(weapon, slot);
		}
	}

	void WeaponRelease(WeaponState weapon, int slot)
	{
		if (weapon.data == null || _activeWeaponSlot != slot || _weaponState != EWeaponState.Charging)
		{
			return;
		}
		if (weapon.data.activateOnRelease)
		{
			WeaponActivate(weapon, slot);
		}
	}

	void DoWeaponEvent(WeaponState weapon, EItemSlot slot, WeaponEvent weaponEvent)
	{
		switch (weaponEvent.type)
		{
			case EWeaponEventType.Melee:
				DoMeleeWeaponEvent(weapon, weaponEvent);
				break;
			case EWeaponEventType.Hitscan:
				DoHitscanWeaponEvent(weapon, weaponEvent);
				break;
			case EWeaponEventType.UseAmmo:
				weapon.ammo--;
				break;
		}
	}

	void DoMeleeWeaponEvent(WeaponState weapon, WeaponEvent weaponEvent)
	{
		Vector3 damagePos = GlobalPosition + Vector3.Up + GlobalTransform.Basis.Z * weaponEvent.meleeRange;
		var query = new PhysicsShapeQueryParameters3D();
		query.Shape = new SphereShape3D() { Radius = weaponEvent.meleeRadius };
		query.Transform = new Transform3D(Basis.Identity, damagePos);
		query.CollisionMask = (uint)ECollisionLayer.HurtBox;
		query.CollideWithAreas = true;
		query.CollideWithBodies = false;

		var results = GetWorld3D().DirectSpaceState.IntersectShape(query);
		foreach (var result in results)
		{
			var collider = result["collider"].Obj;
			if (collider is HurtBox hurtBox && hurtBox != _hurtBox)
			{
				hurtBox.Hit(weapon.data.damageData, this);
			}
		}

		DebugSphere.Create(
			_world,
			new Color(1f, 0f, 0f, 0.3f),
			0.15f,
			damagePos,
			weaponEvent.meleeRadius
		);
	}

	void DoHitscanWeaponEvent(WeaponState weapon, WeaponEvent weaponEvent)
	{
		Vector3 origin = GlobalPosition + Vector3.Up;
		Vector3 direction = GlobalTransform.Basis.Z;
		Vector3 rayEnd = origin + direction * weaponEvent.hitScanRange;

		var spaceState = GetWorld3D().DirectSpaceState;

		Godot.Collections.Array<Rid> bodyExclude = [GetRid()];

		// Find the nearest environment hit to clip the ray against world geometry.
		var envQuery = PhysicsRayQueryParameters3D.Create(origin, rayEnd);
		envQuery.CollisionMask = (uint)ECollisionLayer.Environment;
		envQuery.CollideWithAreas = false;
		envQuery.CollideWithBodies = true;
		envQuery.Exclude = bodyExclude;
		var envResult = spaceState.IntersectRay(envQuery);

		Vector3 hitPos = rayEnd;
		if (envResult.Count > 0)
		{
			hitPos = (Vector3)envResult["position"];
		}

		// Cast against hurtboxes up to the clipped end point.
		var hurtQuery = PhysicsRayQueryParameters3D.Create(origin, hitPos);
		hurtQuery.CollisionMask = (uint)ECollisionLayer.HurtBox;
		hurtQuery.CollideWithAreas = true;
		hurtQuery.CollideWithBodies = false;
		if (_hurtBox != null)
		{
			hurtQuery.Exclude = [_hurtBox.GetRid()];
		}

		var hurtResult = spaceState.IntersectRay(hurtQuery);
		if (hurtResult.Count > 0)
		{
			var collider = hurtResult["collider"].Obj;
			if (collider is HurtBox hurtBox && hurtBox != _hurtBox)
			{
				hurtBox.Hit(weapon.data.damageData, this);
				hitPos = (Vector3)hurtResult["position"];
			}
		}

		DebugBox.Create(
			_world,
			new Color(1f, 0f, 0f, 0.3f),
			0.15f,
			origin,
			hitPos,
			0.1f,
			0.1f
		);
	}

	void ProcessWeaponEvents(WeaponState weapon, int slot)
	{
		if (weapon.data == null || weapon.data.events == null)
		{
			return;
		}
		ulong curTime = _world.GameTimeMs;
		for (int i = weapon.lastWeaponEventIndex + 1; i < weapon.data.events.Count; i++)
		{
			WeaponEvent weaponEvent = weapon.data.events[i];
			if (curTime >= _weaponActivateTime + weaponEvent.time)
			{
				DoWeaponEvent(weapon, (EItemSlot)slot, weaponEvent);
				weapon.lastWeaponEventIndex = i;
			}
			else
			{
				break;
			}
		}
	}

	void ProcessWeapon(WeaponState weapon, int slot, bool inputPressed, float dt)
	{
		if (weapon == null || weapon.data == null)
		{
			return;
		}
		if (_activeWeaponSlot == slot && _weaponState == EWeaponState.Active)
		{
			ProcessWeaponEvents(weapon, slot);
			if (_world.GameTimeMs >= _weaponActivateTime + weapon.data.activeTime)
			{
				_activeWeaponSlot = null;
				_weaponState = EWeaponState.Ready;
			}
		}
	}
}
