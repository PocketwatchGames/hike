using Godot;

// Single-action runner shared by Player (phase 2) and Mob (phase 6). Holds
// one in-flight PlayerAction plus an optional queued action that promotes
// when the active one ends. Drives the press → Charging → Active → Ready
// state machine.
//
// Multi-tier charging: tiers are walked highest-to-lowest in SelectTier.
// During Charging, tier selection is recomputed each tick; when it changes,
// the newly-selected tier's readyEvents fire (announce "you've reached
// Heavy"). On release/auto-activate, the currently-selected tier becomes
// the active one and runs its events on its own timeline.
public class ActionRunner
{
	private readonly IActionActor _actor;
	private PlayerAction _action;

	// Pending action to run when the current one finishes. Captured at press
	// time during the late-Active queue window. Dropped on abort/interrupt
	// (queue does not survive cancellation).
	private PlayerAction _queuedAction;
	private bool _hasQueued;

	// Active charge-loop instance and the PackedScene driving it. The scene
	// reference lets a tier promotion mid-charge (e.g., bow snap→charged)
	// preserve the running loop when the new tier reuses the same scene
	// instead of audibly restarting the draw.
	private Fx _chargeLoop;
	private PackedScene _chargeLoopScene;

	public ActionRunner(IActionActor actor)
	{
		_actor = actor;
		_action = default;
	}

	public bool IsBusy => _action.IsBusy;
	public EActionPhase Phase => _action.phase;
	public ref readonly PlayerAction Current => ref _action;

	// `_action.chargeT` is sampled at activation. UI that needs to read the
	// in-progress charge fraction during Charging (e.g. the aiming reticle's
	// spread preview) goes through this instead.
	public float CurrentChargeT
	{
		get
		{
			if (_action.phase != EActionPhase.Charging)
			{
				return _action.chargeT;
			}
			float elapsed = (_actor.GameTimeMs - _action.pressMs) / 1000f;
			return ComputeChargeT(_action.profile, _action.selectedTier, elapsed);
		}
	}
	public bool LocksMovement => _action.IsBusy && (
		(_action.profile != null && _action.profile.locksMovement)
		|| (_action.interactiveAction != null && _action.interactiveAction.locksMovement));

	// Begin a new action. Returns true if started OR queued. Returns false
	// if the runner is busy in a state where queueing isn't possible (mid-
	// Charging, or Active outside the queue window) or the profile is empty.
	public bool TryStart(ItemActionProfile profile, ActionContext context)
	{
		if (profile == null || profile.chargedActions == null || profile.chargedActions.Count == 0)
		{
			return false;
		}

		// Per-item cooldown gate. Re-checked at promotion time too; cooldown
		// may still be running when the queue fires.
		if (context.primaryItem != null && context.primaryItem.cooldownExpireMs > _actor.GameTimeMs)
		{
			return false;
		}

		if (_action.IsBusy)
		{
			return TryQueue(profile, context);
		}

		StartImmediate(profile, context);
		return true;
	}

	// Begin a new interactive action. No queueing, no charging — the action
	// enters Active immediately and walks `events` over `durationSeconds`.
	// Returns false if the runner is busy or the action is null.
	public bool TryStart(InteractiveAction action, ActionContext context)
	{
		if (action == null || _action.IsBusy)
		{
			return false;
		}
		ulong now = _actor.GameTimeMs;
		_action = new PlayerAction
		{
			phase = EActionPhase.Active,
			interactiveAction = action,
			context = context,
			pressMs = now,
			activateMs = now,
			endMs = now + (ulong)(action.durationSeconds * 1000f),
			selectedTierIndex = -1,
			lastEventIndex = -1,
		};
		WalkActiveEvents(now);
		if (_action.phase == EActionPhase.Active && now >= _action.endMs)
		{
			EndActive();
		}
		return true;
	}

	// Input release. If currently Charging, transition to Active using the
	// selected tier (if any reached). Returns true if a transition happened.
	public bool OnInputReleased()
	{
		if (_action.phase != EActionPhase.Charging)
		{
			return false;
		}
		ItemAction tier = _action.selectedTier;
		if (tier == null)
		{
			AbortCharging();
			return true;
		}
		EnterActive(tier, _actor.GameTimeMs);
		return true;
	}

	// Per-tick advance. Runs Charging → tier-selection / readyEvents /
	// auto-activate; runs Active → event walker / end-of-active transition.
	public void Tick()
	{
		if (_action.phase == EActionPhase.Charging)
		{
			ulong now = _actor.GameTimeMs;
			WalkChargeEvents(now);
			UpdateChargingTierSelection(now);
			MaybeAutoActivate(now);
			return;
		}
		if (_action.phase == EActionPhase.Active)
		{
			ulong now = _actor.GameTimeMs;
			WalkActiveEvents(now);
			if (now >= _action.endMs)
			{
				EndActive();
			}
		}
	}

	// Player-initiated cancel. Charging always cancels; weapon Active cancels
	// only if the selected tier opts in via canAbort. Interactive Active is
	// always abortable — the player can walk away from a chest mid-open.
	public bool TryAbort()
	{
		if (_action.phase == EActionPhase.Charging)
		{
			AbortCharging();
			return true;
		}
		if (_action.phase == EActionPhase.Active)
		{
			if (_action.interactiveAction != null)
			{
				AbortInteractive();
				return true;
			}
			if (_action.selectedTier?.canAbort ?? false)
			{
				AbortActive();
				return true;
			}
		}
		return false;
	}

	// External (damage, stagger). Charging cancels iff profile.interruptOnDamage;
	// weapon Active cancels only if the selected tier opts in via canInterrupt;
	// interactive Active cancels iff interactiveAction.interruptOnDamage.
	public bool TryInterrupt()
	{
		if (_action.phase == EActionPhase.Charging)
		{
			if (_action.profile != null && _action.profile.interruptOnDamage)
			{
				AbortCharging();
				return true;
			}
			return false;
		}
		if (_action.phase == EActionPhase.Active)
		{
			if (_action.interactiveAction != null)
			{
				if (_action.interactiveAction.interruptOnDamage)
				{
					AbortInteractive();
					return true;
				}
				return false;
			}
			if (_action.selectedTier?.canInterrupt ?? false)
			{
				AbortActive();
				return true;
			}
		}
		return false;
	}

	private void StartImmediate(ItemActionProfile profile, ActionContext context)
	{
		ulong now = _actor.GameTimeMs;
		int targetComboIndex = ResolveTargetComboIndex(profile, context, now);
		int tier0Index = SelectTierIndex(profile, context, 0f, targetComboIndex);
		ItemAction tier0 = tier0Index >= 0 ? profile.chargedActions[tier0Index] : null;
		_action = new PlayerAction
		{
			phase = EActionPhase.Charging,
			profile = profile,
			context = context,
			pressMs = now,
			activateMs = 0,
			endMs = 0,
			selectedTier = tier0,
			selectedTierIndex = tier0Index,
			lastEventIndex = -1,
			chargeT = 0f,
			targetComboIndex = targetComboIndex,
		};

		// Fire chargeEvents at t=0 and announce the initial tier (if reached
		// at t=0, like a Light tier with chargeTime=0 — readyEvents say
		// "weapon armed"). No-op when tier0 is null (rare; only if all tiers
		// have chargeTime > 0).
		WalkChargeEvents(now);
		if (tier0 != null)
		{
			FireEventList(tier0.readyEvents);
			StartChargeEffects(tier0);
		}

		// Auto-activate path: top tier with chargeTime <= 0 fires same tick.
		MaybeAutoActivate(now);
	}

	private bool TryQueue(ItemActionProfile profile, ActionContext context)
	{
		// Queue policy: only during Active, only on profiles that opt in.
		// One slot — overwrite on multiple presses (latest press wins).
		if (_action.phase != EActionPhase.Active)
		{
			return false;
		}
		ItemActionProfile current = _action.profile;
		if (current == null || !current.queueable)
		{
			return false;
		}
		ulong now = _actor.GameTimeMs;
		ulong windowMs = (ulong)(current.queueWindowSeconds * 1000f);
		if (_action.endMs <= now || (_action.endMs - now) > windowMs)
		{
			return false;
		}

		_queuedAction = new PlayerAction
		{
			profile = profile,
			context = context,
		};
		_hasQueued = true;
		return true;
	}

	// Re-evaluates which tier is currently "selected" given charge elapsed.
	// On a change, fires the new tier's readyEvents. (Phase 4 will add the
	// outgoing tier's unreadyEvents and gate-failure fallback paths.)
	private void UpdateChargingTierSelection(ulong now)
	{
		ItemActionProfile profile = _action.profile;
		if (profile == null)
		{
			return;
		}
		float elapsed = (now - _action.pressMs) / 1000f;
		int newIndex = SelectTierIndex(profile, _action.context, elapsed, _action.targetComboIndex);
		if (newIndex == _action.selectedTierIndex)
		{
			return;
		}
		_action.selectedTierIndex = newIndex;
		_action.selectedTier = newIndex >= 0 ? profile.chargedActions[newIndex] : null;
		if (_action.selectedTier != null)
		{
			FireEventList(_action.selectedTier.readyEvents);
			// StartChargeEffects handles same-scene loop preservation, so a
			// promotion that reuses the same charge loop won't audibly restart.
			StartChargeEffects(_action.selectedTier);
		}
	}

	private void MaybeAutoActivate(ulong now)
	{
		if (_action.phase != EActionPhase.Charging)
		{
			return;
		}
		ItemActionProfile profile = _action.profile;
		if (profile == null || !profile.autoActivateAtMax || profile.chargedActions.Count == 0)
		{
			return;
		}
		// Auto-fire only the highest tier (and only when it's the selected
		// tier — phase 4 adds the requirements gate that may keep selection
		// at a lower tier even past the top's chargeTime).
		int topIndex = profile.chargedActions.Count - 1;
		if (_action.selectedTierIndex != topIndex)
		{
			return;
		}
		ItemAction top = profile.chargedActions[topIndex];
		if (top == null)
		{
			return;
		}
		float elapsed = (now - _action.pressMs) / 1000f;
		if (elapsed + 1e-6f >= top.chargeTime)
		{
			EnterActive(top, now);
		}
	}

	private void EnterActive(ItemAction tier, ulong now)
	{
		FireChargeEndEvents();
		StopChargeLoop();
		ItemEventHandlers.SpawnOnActor(_actor, tier?.releaseEffect);
		float chargeElapsed = (now - _action.pressMs) / 1000f;
		_action.phase = EActionPhase.Active;
		_action.selectedTier = tier;
		_action.selectedTierIndex = IndexOf(_action.profile, tier);
		_action.activateMs = now;
		_action.endMs = now + (ulong)(tier.activeDurationSeconds * 1000f);
		_action.lastEventIndex = -1;
		_action.chargeT = ComputeChargeT(_action.profile, tier, chargeElapsed);

		// Combo bookkeeping (weapon-driving tiers only). The activated tier's
		// comboIndex becomes the weapon's current chain position. comboWindowMs
		// is the time after this action ends during which the next press will
		// target `comboIndex + 1`; zero terminates the chain here.
		if (_action.context.primaryItem is WeaponState weapon)
		{
			weapon.comboIndex = tier.comboIndex;
			weapon.comboExpireMs = now + (ulong)(tier.activeDurationSeconds * 1000f) + (ulong)(tier.cooldownSeconds * 1000f) + tier.comboWindowMs;
		}

		// Apply per-item cooldown. Duration is also stored so HUDs can render
		// progress without re-reading the tier (which is unreachable once the
		// runner advances past Active).
		if (_action.context.primaryItem != null)
		{
			ulong cooldownMs = (ulong)(tier.cooldownSeconds * 1000f);
			_action.context.primaryItem.cooldownExpireMs = now + cooldownMs;
			_action.context.primaryItem.cooldownDurationMs = cooldownMs;
		}

		// Fire any t=0 active events on entry. The walker handles t>0 in Tick.
		WalkActiveEvents(now);
		// Zero-duration Active: exit on the same tick.
		if (now >= _action.endMs)
		{
			EndActive();
		}
	}

	private void EndActive()
	{
		// Interactive actions fire their completion bucket here so authors
		// don't have to align an OpenInteractive event's time to the action's
		// durationSeconds. Weapons skip this — their per-tier `events` walk
		// already handles the swing's impact moment.
		if (_action.interactiveAction != null)
		{
			FireEventList(_action.interactiveAction.completionEvents);
		}

		// Active ended naturally — promote queue if pending and still valid.
		if (_hasQueued)
		{
			ItemActionProfile pending = _queuedAction.profile;
			ActionContext pendingCtx = _queuedAction.context;
			_queuedAction = default;
			_hasQueued = false;
			_action = default;
			// Re-validate the queued action against current state.
			if (pending != null && (pendingCtx.primaryItem == null || pendingCtx.primaryItem.cooldownExpireMs <= _actor.GameTimeMs))
			{
				StartImmediate(pending, pendingCtx);
				return;
			}
		}
		_action = default;
	}

	private void AbortCharging()
	{
		FireChargeEndEvents();
		StopChargeLoop();
		if (_action.selectedTier != null)
		{
			ItemEventHandlers.SpawnOnActor(_actor, _action.selectedTier.chargeCancelEffect);
		}
		// Profile-level abort events fire only when no tier was reached.
		if (_action.selectedTier == null && _action.profile != null)
		{
			FireEventList(_action.profile.abortEvents);
		}
		_action = default;
		_queuedAction = default;
		_hasQueued = false;
	}

	private void AbortActive()
	{
		_action = default;
		_queuedAction = default;
		_hasQueued = false;
	}

	private void AbortInteractive()
	{
		_action = default;
		_queuedAction = default;
		_hasQueued = false;
	}

	private void WalkChargeEvents(ulong now)
	{
		ItemActionProfile profile = _action.profile;
		if (profile == null || profile.chargeEvents == null)
		{
			return;
		}
		for (int i = _action.lastEventIndex + 1; i < profile.chargeEvents.Count; i++)
		{
			ItemEvent ev = profile.chargeEvents[i];
			if (ev == null)
			{
				_action.lastEventIndex = i;
				continue;
			}
			if (now >= _action.pressMs + ev.time)
			{
				DispatchEvent(ev);
				_action.lastEventIndex = i;
			}
			else
			{
				break;
			}
		}
	}

	private void WalkActiveEvents(ulong now)
	{
		Godot.Collections.Array<ItemEvent> events;
		if (_action.interactiveAction != null)
		{
			events = _action.interactiveAction.interactEvents;
		}
		else
		{
			ItemAction tier = _action.selectedTier;
			events = tier?.events;
		}
		if (events == null)
		{
			return;
		}
		for (int i = _action.lastEventIndex + 1; i < events.Count; i++)
		{
			ItemEvent ev = events[i];
			if (ev == null)
			{
				_action.lastEventIndex = i;
				continue;
			}
			if (now >= _action.activateMs + ev.time)
			{
				DispatchEvent(ev);
				_action.lastEventIndex = i;
			}
			else
			{
				break;
			}
		}
	}

	private void FireChargeEndEvents()
	{
		if (_action.profile == null)
		{
			return;
		}
		FireEventList(_action.profile.chargeEndEvents);
	}

	private void FireEventList(Godot.Collections.Array<ItemEvent> list)
	{
		if (list == null)
		{
			return;
		}
		for (int i = 0; i < list.Count; i++)
		{
			ItemEvent ev = list[i];
			if (ev != null)
			{
				DispatchEvent(ev);
			}
		}
	}

	// `type` is a bitmask — each flag's handler runs in sequence so a single
	// event can fire several behaviors at once (e.g. ApplyEffect | DecrementStack
	// for a healing potion's release tick). Order matters where one handler
	// can invalidate another's inputs: visuals first, then state mutations,
	// then DecrementStack last because it can null out primaryItem when the
	// stack hits zero.
	private void DispatchEvent(ItemEvent ev)
	{
		EItemEventType t = ev.type;
		if ((t & EItemEventType.PlayAnim) != 0)
		{
			_actor.PlayAnim(ev.animName);
		}
		if ((t & EItemEventType.ApplyStatusEffect) != 0)
		{
			ItemEventHandlers.DoApplyEffect(_actor, ev, ref _action);
		}
		if ((t & EItemEventType.Melee) != 0)
		{
			ItemEventHandlers.DoMelee(_actor, ev, ref _action);
		}
		if ((t & EItemEventType.Hitscan) != 0)
		{
			ItemEventHandlers.DoHitscan(_actor, ev, ref _action);
		}
		if ((t & EItemEventType.UseAmmo) != 0)
		{
			ItemEventHandlers.DoUseAmmo(_actor, ev, ref _action);
		}
		if ((t & EItemEventType.ToggleMovingLight) != 0)
		{
			ItemEventHandlers.DoToggleMovingLight(_actor, ev, ref _action);
		}
		if ((t & EItemEventType.OpenInteractive) != 0)
		{
			ItemEventHandlers.DoOpenInteractive(_actor, ev, ref _action);
		}
		if ((t & EItemEventType.ConsumeFromInventory) != 0)
		{
			ItemEventHandlers.DoConsumeFromInventory(_actor, ev, ref _action);
		}
		if ((t & EItemEventType.DecrementStack) != 0)
		{
			ItemEventHandlers.DoDecrementStack(_actor, ev, ref _action);
		}
	}

	private int SelectTierIndex(ItemActionProfile profile, in ActionContext context, float chargeElapsedSeconds, int comboIndex)
	{
		// Highest-to-lowest, return first whose comboIndex matches the chain
		// target AND whose chargeTime is reached AND whose requirements all
		// pass. Requirements failing fall through to the next lower tier — a
		// Strong attack short on mana drops to Weak (within the same combo
		// step). The combo filter is fixed for the duration of the charge.
		for (int i = profile.chargedActions.Count - 1; i >= 0; i--)
		{
			ItemAction action = profile.chargedActions[i];
			if (action == null) { continue; }
			if (action.comboIndex != comboIndex) { continue; }
			if (chargeElapsedSeconds + 1e-6f < action.chargeTime) { continue; }
			if (!RequirementsMet(action, context)) { continue; }
			return i;
		}
		return -1;
	}

	// Pick which combo step a fresh press should target. If the weapon's chain
	// window is still open, try `previousComboIndex + 1` and verify the profile
	// has a matching action; otherwise (or on no match) fall back to 0.
	private static int ResolveTargetComboIndex(ItemActionProfile profile, in ActionContext context, ulong now)
	{
		if (context.primaryItem is not WeaponState weapon || now >= weapon.comboExpireMs)
		{
			return 0;
		}
		int next = weapon.comboIndex + 1;
		for (int i = 0; i < profile.chargedActions.Count; i++)
		{
			ItemAction action = profile.chargedActions[i];
			if (action != null && action.comboIndex == next)
			{
				return next;
			}
		}
		return 0;
	}

	private bool RequirementsMet(ItemAction tier, in ActionContext context)
	{
		if (tier.requirements == null || tier.requirements.Count == 0)
		{
			return true;
		}
		for (int i = 0; i < tier.requirements.Count; i++)
		{
			ActionRequirement req = tier.requirements[i];
			if (req == null) { continue; }
			if (!req.Evaluate(_actor, context))
			{
				return false;
			}
		}
		return true;
	}

	private static int IndexOf(ItemActionProfile profile, ItemAction tier)
	{
		if (profile == null || tier == null)
		{
			return -1;
		}
		for (int i = 0; i < profile.chargedActions.Count; i++)
		{
			if (profile.chargedActions[i] == tier)
			{
				return i;
			}
		}
		return -1;
	}

	private void StartChargeEffects(ItemAction tier)
	{
		PackedScene newLoop = tier?.chargeLoopEffect;

		// Same loop scene already running — keep it going so a tier promotion
		// mid-charge doesn't audibly restart the draw. Skip chargeStartEffect
		// too: the "you've reached this tier" cue is readyEvents' job.
		if (newLoop != null && newLoop == _chargeLoopScene)
		{
			return;
		}

		// Different (or no) loop on the new tier — stop whatever's running and
		// start fresh.
		StopChargeLoop();
		if (tier == null)
		{
			return;
		}
		ItemEventHandlers.SpawnOnActor(_actor, tier.chargeStartEffect);
		if (newLoop != null)
		{
			_chargeLoop = ItemEventHandlers.SpawnOnActor(_actor, newLoop);
			_chargeLoopScene = newLoop;
		}
	}

	private void StopChargeLoop()
	{
		if (_chargeLoop != null)
		{
			_chargeLoop.Stop();
			_chargeLoop = null;
			_chargeLoopScene = null;
		}
	}

	public static float ComputeChargeT(ItemActionProfile profile, ItemAction selectedTier, float chargeElapsedSeconds)
	{
		// Sampling window precedence:
		//   1. selectedTier.maxChargeSeconds (per-action explicit window —
		//      lets a single-tier weapon scale curves over its hold time)
		//   2. profile's top-tier chargeTime (multi-tier without per-action
		//      curves — phase 3 weapons like the multi-tier club don't sample
		//      chargeT in their event handlers, so this fallback is mostly
		//      diagnostic)
		// Returns 0 when no meaningful window exists.
		if (selectedTier != null && selectedTier.maxChargeSeconds > 0f)
		{
			return Mathf.Clamp(chargeElapsedSeconds / selectedTier.maxChargeSeconds, 0f, 1f);
		}
		if (profile == null || profile.chargedActions.Count == 0)
		{
			return 0f;
		}
		ItemAction top = profile.chargedActions[profile.chargedActions.Count - 1];
		if (top == null || top.chargeTime <= 0f)
		{
			return 0f;
		}
		return Mathf.Clamp(chargeElapsedSeconds / top.chargeTime, 0f, 1f);
	}
}
