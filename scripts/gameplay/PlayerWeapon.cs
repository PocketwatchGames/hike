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

	// Starts the interactive's action at `actionIndex` through the runner and
	// stashes (interactive, actionIndex) on the player so the movement-lock
	// and Interacting-anim checks elsewhere can key off _curInteractive.
	// Returns true on a successful start. _PhysicsProcess clears
	// _curInteractive when the runner finishes the action.
	bool TryStartInteractiveAction(IInteractive interactive, int actionIndex = 0)
	{
		if (_runner == null || _runner.IsBusy || interactive == null)
		{
			return false;
		}
		var actions = interactive.GetActions(this);
		if (actions == null || actionIndex < 0 || actionIndex >= actions.Count)
		{
			return false;
		}
		InteractiveAction action = actions[actionIndex];
		if (action == null)
		{
			return false;
		}
		var context = new ActionContext
		{
			verb = action.verb,
			primaryInteractive = interactive,
			interactiveActionIndex = actionIndex,
			supportingItems = GatherSupportingItems(action.requirements),
		};
		if (!_runner.TryStart(action, context))
		{
			return false;
		}
		SetCurInteractive(interactive, actionIndex);
		return true;
	}

	// Walk a requirements list and gather any supporting items the action
	// needs from the inventory (e.g. lockpicks). Currently only resolves
	// HasReagentRequirement; future requirement types may add more. Returns
	// null when nothing matches — saves a list allocation in the common case.
	System.Collections.Generic.List<ItemState> GatherSupportingItems(Godot.Collections.Array<ActionRequirement> requirements)
	{
		if (requirements == null || _inventory == null)
		{
			return null;
		}
		System.Collections.Generic.List<ItemState> result = null;
		for (int r = 0; r < requirements.Count; r++)
		{
			if (requirements[r] is not HasReagentRequirement reagentReq) { continue; }
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
