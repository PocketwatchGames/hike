using System.Collections.Generic;
using Godot;

// Input shim that maps weapon-attack input actions to ActionRunner calls.
// All timeline / phase / event-walking logic lives in ActionRunner. The
// generalization to consumable Use, channeled actions, and (phase 5)
// interactives all reuse the same runner, so adding a new input is just
// another entry that constructs a context and calls TryStart.
public partial class Player : CharacterBody3D, IActionActor
{
	static readonly Dictionary<EInventorySlot, string> _weaponActions = new()
	{
		{ EInventorySlot.WeaponLeft, "AttackMelee" },
		{ EInventorySlot.WeaponRight, "AttackRanged" }
	};

	void HandleWeaponInputs()
	{
		foreach (var (slot, actionName) in _weaponActions)
		{
			if (Input.IsActionJustPressed(actionName))
			{
				TryStartWeaponAction(slot);
			}
			if (Input.IsActionJustReleased(actionName))
			{
				ReleaseWeaponAction(slot);
			}
		}
	}

	void TryStartWeaponAction(EInventorySlot slot)
	{
		if (_runner == null || _runner.IsBusy)
		{
			return;
		}
		WeaponState weapon = _inventory?.GetWeapon(slot);
		if (weapon?.data?.actionProfile == null)
		{
			return;
		}
		// Per-weapon ammo gate. Cooldown gate is handled by ActionRunner via
		// ItemState.cooldownExpireMs.
		if (weapon.data.useAmmo && weapon.ammo <= 0)
		{
			return;
		}

		var context = new ActionContext
		{
			verb = EActionVerb.Light,
			primaryItem = weapon,
			sourceSlot = slot,
		};
		_runner.TryStart(weapon.data.actionProfile, context);
	}

	void ReleaseWeaponAction(EInventorySlot slot)
	{
		if (_runner == null || !_runner.IsBusy)
		{
			return;
		}
		// Only the input that started the in-flight action commits its release.
		if (_runner.Current.context.sourceSlot != slot)
		{
			return;
		}
		_runner.OnInputReleased();
	}

	void TryUseActiveConsumable()
	{
		if (_runner == null || _runner.IsBusy)
		{
			return;
		}
		ItemState item = _inventory?.GetActiveConsumable();
		if (item == null || item.data is not ConsumableData consumableData)
		{
			return;
		}
		ItemActionProfile profile = consumableData.actionProfile;
		if (profile == null)
		{
			return;
		}

		var context = new ActionContext
		{
			verb = EActionVerb.Use,
			primaryItem = item,
			sourceSlot = EInventorySlot.Consumable,
		};
		_runner.TryStart(profile, context);
	}

	void ReleaseUseConsumable()
	{
		if (_runner == null || !_runner.IsBusy)
		{
			return;
		}
		if (_runner.Current.context.sourceSlot != EInventorySlot.Consumable)
		{
			return;
		}
		_runner.OnInputReleased();
	}

	// Returns true if an interactive action was started (or attempted to be
	// started) — caller's expectation is "this interactive is now committed
	// to the runner; don't fall through to the legacy interact path."
	// Returns false if the interactive doesn't expose action profiles, in
	// which case the caller falls back to GetInteractTime/Complete.
	bool TryStartInteractiveAction(IInteractive interactive)
	{
		if (_runner == null || _runner.IsBusy || interactive == null)
		{
			return false;
		}
		var profiles = interactive.GetActions(this);
		if (profiles == null || profiles.Count == 0)
		{
			return false;
		}
		EActionVerb verb = interactive.DefaultVerb;
		if (!profiles.TryGetValue(verb, out ItemActionProfile profile) || profile == null)
		{
			return false;
		}
		var context = new ActionContext
		{
			verb = verb,
			primaryInteractive = interactive,
			supportingItems = GatherSupportingItems(profile),
		};
		return _runner.TryStart(profile, context);
	}

	// Walk the action's tier requirements and gather any supporting items
	// the profile needs from the inventory (e.g. lockpicks). Phase 5 only
	// resolves HasReagentRequirement; future requirement types may add
	// more. Returns null if the profile has no reagent-style requirements
	// — saves a list allocation in the common case.
	System.Collections.Generic.List<ItemState> GatherSupportingItems(ItemActionProfile profile)
	{
		if (profile == null || profile.chargedActions == null || _inventory == null)
		{
			return null;
		}
		System.Collections.Generic.List<ItemState> result = null;
		for (int t = 0; t < profile.chargedActions.Count; t++)
		{
			ChargedAction tier = profile.chargedActions[t];
			if (tier?.requirements == null) { continue; }
			for (int r = 0; r < tier.requirements.Count; r++)
			{
				if (tier.requirements[r] is not HasReagentRequirement reagentReq) { continue; }
				if (reagentReq.reagent == null) { continue; }
				foreach (ItemState item in _inventory.EnumerateAll())
				{
					if (item != null && item.data == reagentReq.reagent)
					{
						result ??= new System.Collections.Generic.List<ItemState>();
						if (!result.Contains(item))
						{
							result.Add(item);
						}
					}
				}
			}
		}
		return result;
	}

	// IActionActor — what ActionRunner and ItemEventHandlers read from the
	// player. Position / Forward use the player's body transform; Forward
	// matches the existing aim direction (basis Z axis, same as
	// PlayerWeapon's old Melee/Hitscan code).
	public Vector3 ActorWorldPosition => GlobalPosition;
	public Vector3 ActorForward => GlobalTransform.Basis.Z;
	public ulong GameTimeMs => _world?.GameTimeMs ?? 0;
	public uint AttackHurtboxMask => (uint)ECollisionLayer.HurtBox;
	public Rid? SelfHurtBoxRid => _hurtBox?.GetRid();
	public Node3D AttackerNode => this;
	public void PlayAnim(EAnimation anim)
	{
		PlayOneShot(anim);
	}
}
