using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	// Called when the player commits a weapon action. Latches CombatEngaged if a
	// triggered, dangerous hostile is within _combatEngageRange, so a guard
	// companion escalates the instant the player swings near a threat.
	public void TryEngageCombatFromWeaponUse()
	{
		if (CombatEngaged || _world?.MobSpatialHash == null)
		{
			return;
		}
		_combatEngageScratch.Clear();
		_world.MobSpatialHash.QueryRadius(GlobalPosition, _combatEngageRange, _combatEngageScratch);
		for (int i = 0; i < _combatEngageScratch.Count; i++)
		{
			Mob m = _combatEngageScratch[i];
			if (m == null || !m.alive || m.mobData == null || !m.mobData.dangerous)
			{
				continue;
			}
			if (Teams.AreAllied(m.ActorTeam, ETeam.Player))
			{
				continue;
			}
			if (m.IsTriggered)
			{
				NotifyCombatEngaged();
				break;
			}
		}
		_combatEngageScratch.Clear();
	}

	// Pure prediction — no state mutation. See Mob.PredictHit for the
	// networked-play motivation. The player surfaces no crit/backstab triggers
	// (mobs don't crit/backstab the player), so the flags are always None.
	private HitPrediction PredictHit(HitInfo hit)
	{
		// Receiver-side resistance fold. ApplyResistance scales healthDamage,
		// armor-penetration chance, blunt mult, and knockback magnitude in place using
		// the diverse-site rules so the prediction below matches the actual
		// apply in OnHurtBoxHit.
		ApplyResistance(ref hit);
		EHitResult result;
		if (hit.healthDamage <= 0f)
		{
			result = EHitResult.None;
		}
		else if (_armor > 0f && !hit.ArmorPenetrated)
		{
			result = EHitResult.Armor;
		}
		else if (_health <= 0f)
		{
			result = EHitResult.None;
		}
		else
		{
			result = hit.healthDamage >= _health ? EHitResult.Lethal : EHitResult.Health;
		}
		return new HitPrediction(result, EDamageTriggerFlags.None);
	}

	// Fold receiver resistances onto the live hit in place. Damage tags
	// (Damage / Fire / Magical / Poison / Electrical / Ranged / Melee) scale
	// healthDamage; ArmorPenetration scales the bypass-chance roll; Blunt scales the
	// (1 + blunt) armor-chip multiplier; Knockback scales knockbackDistance
	// and knockbackTime. Each site only applies if the hit carries the
	// corresponding tag — a non-ArmorPenetration hit is unaffected by
	// ArmorPenetration-resist, etc. Modulating in place means hit.ArmorPenetrated and the downstream armor /
	// knockback formulas automatically pick up the receiver's resistances
	// without each call site re-asking.
	private void ApplyResistance(ref HitInfo hit)
	{
		// General defensive-level resistance (Armor forge upgrade), applied to all
		// incoming damage regardless of tag — the damage counterpart of the combat-
		// buildup resist in StatusEffectController. Ahead of the tags==None guard so
		// even an untagged damaging hit is reduced.
		float levelResist = IncomingLevelResist;
		if (levelResist != 1f)
		{
			hit.healthDamage *= levelResist;
		}
		if (hit.tags == EStat.None)
		{
			return;
		}
		EStat damageTags = hit.tags & StatModifierUtil.DamageScaleTags;
		if (damageTags != EStat.None)
		{
			hit.healthDamage *= ComposeMaskMul(damageTags);
		}
		if ((hit.tags & EStat.ArmorPenetration) != 0)
		{
			hit.armorPenetration *= ComposeMaskMul(EStat.ArmorPenetration);
		}
		if ((hit.tags & EStat.Blunt) != 0)
		{
			hit.blunt *= ComposeMaskMul(EStat.Blunt);
		}
		if ((hit.tags & EStat.Knockback) != 0)
		{
			float scale = ComposeMaskMul(EStat.Knockback);
			hit.knockbackDistance *= scale;
			hit.knockbackTime *= scale;
		}
	}

	// Break the combo chain on every weapon the player wields, so getting hit
	// resets it regardless of which slot is mid-combo (or if unarmed).
	private void ResetWeaponCombos()
	{
		_inventory?.GetWeapon(EInventorySlot.WeaponMelee)?.ResetCombo();
		_inventory?.GetWeapon(EInventorySlot.WeaponRanged)?.ResetCombo();
		_unarmedWeapon?.ResetCombo();
	}

	private void OnHurtBoxHit(HitInfo hit)
	{
		if (CVars.invulnerable.Value)
		{
			return;
		}
		// Fold receiver resistances into the hit (damage / armor-penetration-chance /
		// blunt mult / knockback magnitude) before any side effect fires.
		// A {Damage, 0} modifier on an active dash i-frame status drops
		// healthDamage to 0 here, and the early-return below skips interrupt
		// / sneak side-effects so a dashing player isn't disturbed by a hit
		// that did nothing.
		ApplyResistance(ref hit);
		float incomingDamage = hit.healthDamage;
		if (incomingDamage <= 0f && hit.buildups == null)
		{
			return;
		}

		// Relay aggro to the companion so it prioritizes whoever is mauling its
		// master — even hits the companion never witnessed. Mirrors the mob-side
		// attribution in Mob.Damage: pre-armor health damage * the hit's
		// aggroMultiplier, credited toward the attacking mob in the companion's
		// own aggro table (ThreatScan then ranks hostiles by it).
		if (incomingDamage > 0f && hit.aggroMultiplier > 0f && hit.source is Mob masterAttacker)
		{
			_world?.Companion?.AddAggro(masterAttacker, incomingDamage * hit.aggroMultiplier);
		}

		// Taking damage from a mob counts as entering combat — releases a guard
		// companion to escalate from wary to attacking (see Player.CombatEngaged).
		if (incomingDamage > 0f && hit.source is Mob attackingMob)
		{
			NotifyCombatEngaged();
		}

		// Capture the sneaking guard BEFORE `_sneaking = false` below — being
		// hit breaks sneak, but the guard was up when the hit landed, so it
		// catches this one and then drops.
		WeaponState blockWeapon = GetSneakBlockWeapon();
		// Damage may interrupt an in-flight action (gated by profile +
		// per-tier canInterrupt). External interruption fires BEFORE damage
		// is applied so abortEvents can run on coherent pre-damage state.
		_runner?.TryInterrupt();
		// A landed hit breaks any in-progress attack combo — the next swing
		// starts fresh at the lead-in (see WeaponState.ResetCombo) — and drops
		// any queued follow-up tap.
		ResetWeaponCombos();
		ClearQueuedInput();
		// Being hit also cancels an open boon-pick modal (fairy corpse) — a
		// no-op when it isn't showing. Leaves the corpse unspent in the world.
		GameClient.Current?.InterruptUpgradeSelection();
		// Discrete hits (anything that isn't a per-frame DoT tick) snap the
		// player out of bird's-eye view. Continuous burn / poison zones keep
		// the overlook intact so a moment of bad air doesn't repeatedly cancel
		// the fly-back-down — UNLESS the player is hidden up a climbable tree,
		// where taking damage from any source (DoT included) is the cue to
		// leave the tree.
		if (_birdsEye && (!hit.dot || _hidden))
		{
			RequestEndBirdsEye();
		}
		_sneaking = false;
		// Layered damage resolution. Bypass-aware split first: a portion of
		// `incomingDamage` skips armor entirely (discrete `ArmorPenetrated` =
		// full bypass; continuous `armorBypassFraction` = partial), the rest is
		// "absorbable" and runs the guard → central-armor layers below. Each
		// layer lowers `absorbable`; whatever is left plus the bypass lands on
		// health at the end.
		float bypassFraction = hit.ArmorPenetrated ? 1f : hit.armorBypassFraction;
		float bypassed = incomingDamage * bypassFraction;
		float absorbable = incomingDamage - bypassed;
		// Parry: a well-timed, rechargeable deflection of a mob's melee blow.
		// Within the window opened when the crouch began, if the guard is off its
		// recharge cooldown and the whole blow is no larger than the weapon's
		// maxParryDamage, the hit is fully negated — the downstream armor / health /
		// buildup / hitstun / knockback are all skipped — and the attacker is
		// counter-struck. Only a discrete hit from a Mob qualifies (no DoT ticks,
		// traps, or projectiles). Independent of the pool size, so a knife with no
		// passive block still parries; a successful parry re-arms the block recharge
		// delay (SpendParryGuard) so parries can't be spammed.
		bool parried = false;
		if (blockWeapon != null && !hit.dot && hit.source is Mob
			&& _parryDeadlineMs > 0 && (_world?.GameTimeMs ?? 0) < _parryDeadlineMs
			&& IsGuardReadyToParry(blockWeapon)
			&& incomingDamage <= blockWeapon.data.maxParryDamage)
		{
			parried = true;
			absorbable = 0f;
			bypassed = 0f;
			SpendParryGuard(blockWeapon);
			TryParry(blockWeapon, hit.source);
			// Parry reaction one-shot over the sneak pose (resolves the wielded
			// weapon's Block override; no-op if it authors none).
			PlayOneShot(EAnimation.Block);
		}
		// Passive block guard takes the absorbable slice while the player is
		// sneaking with a guard-bearing melee weapon — the sneak crouch doubles as
		// a shield. Overflow model: the guard absorbs up to its charge and passes
		// only the unabsorbed overflow to central armor (not the whole slice).
		// AbsorbWeaponBlock also re-arms the weapon's recharge delay on any
		// guard-touching hit, even at zero guard. Skipped on a parry (the blow was
		// already fully negated above).
		float blockAbsorbed = 0f;
		if (!parried)
		{
			blockAbsorbed = AbsorbWeaponBlock(blockWeapon, ref absorbable, hit.blunt);
			if (blockAbsorbed > 0f)
			{
				// Guard reaction one-shot over the sneak pose (resolves the wielded
				// weapon's Block override; no-op if it authors none).
				PlayOneShot(EAnimation.Block);
			}
		}
		// Central armor chips at (1 + blunt) on whatever survived the guard.
		// Armor that SURVIVES the chip soaks the whole remaining slice (none
		// through); the blow that BREAKS it (chip >= remaining armor) shatters
		// it and provides no protection — the remaining slice lands on health.
		// The recharge window resets on any absorbable hit that touches armor,
		// including blows that land while armor is already depleted (a sustained
		// beating keeps the recover window from starting).
		float armorAbsorbed = 0f;
		if (absorbable > 0f && _armor > 0f)
		{
			float armorDamage = absorbable * (1f + hit.blunt);
			if (armorDamage < _armor)
			{
				// Armor survives: soaks the whole remaining slice.
				armorAbsorbed = armorDamage;
				_armor -= armorDamage;
				RefreshArmorRecharge(true);
				absorbable = 0f;
			}
			else
			{
				// Breaking blow: armor shatters and stops nothing — the
				// remaining slice lands on health (absorbable left intact).
				_armor = 0f;
				RefreshArmorRecharge(false);
			}
		}
		else if (absorbable > 0f && MaxArmor > 0f)
		{
			// Armor already depleted but the player has armor capacity: the
			// slice lands on health, yet we push the recover window out so
			// repeated blows don't let armor refill.
			RefreshArmorRecharge(false);
		}
		// Health takes the pre-resolved bypass plus whatever no layer absorbed.
		incomingDamage = bypassed + absorbable;

		bool wasAlive = _health > 0f;
		_health = Mathf.Max(0f, _health - incomingDamage);
		if (_health <= 0f)
		{
			// Death blood + VO are fired on the alive→dead transition only —
			// a follow-up hit on an already-dead body shouldn't re-emit.
			if (wasAlive)
			{
				SpawnWorldEffect(_deathFx);
				SpawnVoice(_voice?.death);
				HandleDeath();
			}
			PlayOneShot(EAnimation.Die);
		}
		// Per-hit blood / hurt VO. Suppressed for continuous DoT hits (the
		// owning DamageZone pulses these on its own fxIntervalSeconds via
		// OnHurtBoxFxPulse) so a smear-damage zone doesn't spawn a fresh
		// blood spurt every physics frame.
		else if (incomingDamage > 0f && !hit.dot)
		{
			SpawnWorldEffect(_bloodDamageFx);
			SpawnVoice(_voice?.hurt);
			// Slight camera shake on actual health damage. Shares the !hit.dot
			// gate with blood/VO so a continuous burn zone doesn't sustain
			// shake every frame; range=0 since the player IS the camera target.
			GameCamera.Current?.Shake?.AddImpulse(0.12f, 0.15f, GlobalPosition, 0f, GlobalPosition);
		}

		// On-hit effects — immediate-apply entries land now, the rest funnel
		// into the per-effect meter, folding any applyTrigger from a crossed
		// threshold back onto the hit before hitstun/knockback resolution so an
		// OnDizzy modifier can amplify those reads on the same hit that landed
		// dizzy. Skipped when this hit was lethal (`_health` already reduced
		// above): a dead player shouldn't accrue meters or catch fire/poison,
		// matching the `_health > 0f` gate on the knockback reads below. Resolved
		// before the HUD feedback below so a zero-damage hit that still lands
		// buildup isn't mislabeled a "MISS!".
		bool appliedBuildup = false;
		if (_health > 0f && !parried)
		{
			appliedBuildup = _statusEffects?.ApplyHitBuildups(ref hit) ?? false;
			// On-damaged trait reactions (Thin Skinned → its "+5% damage taken"
			// debuff). Discrete hits only — a continuous DoT tick shouldn't re-arm
			// the debuff every physics frame; the tag filter on each effect further
			// scopes it (e.g. physical only).
			if (!hit.dot)
			{
				_statusEffects?.TriggerOnDamaged(hit.tags);
			}
		}

		// Floating HUD feedback. A hit the charged weapon guard absorbed reads
		// "BLOCKED!" (blue); any damage that still got past the guard (armor chip +
		// penetrated health) shows as a number alongside it — so a partial block
		// shows both. `bypassShown` excludes the guard-absorbed slice so the number
		// isn't double-counted with the block. MISS! is mutually exclusive with
		// BLOCKED! — it only fires on a hit that neither blocked, dealt a visible
		// number, nor landed any buildup. DoT hits roll into the per-second accumulator.
		float bypassShown = armorAbsorbed + Mathf.Max(0f, incomingDamage);
		GameClient client = GameClient.Current;
		if (hit.dot)
		{
			float dotTotal = blockAbsorbed + bypassShown;
			if (dotTotal > 0f)
			{
				_dotHud.AddDamage(dotTotal);
			}
		}
		else if (parried)
		{
			// A parry fully negated the blow — its own callout, no number/MISS.
			client?.onHudText?.Invoke(GlobalPosition, Loc.Get(Loc.Keys.combat_parried), EHudTextType.Parried);
		}
		else
		{
			bool blocked = blockAbsorbed > 0f;
			bool showNumber = Mathf.RoundToInt(bypassShown) > 0;
			if (blocked)
			{
				client?.onHudText?.Invoke(GlobalPosition, Loc.Get(Loc.Keys.combat_blocked), EHudTextType.Blocked);
			}
			if (showNumber)
			{
				client?.onDamage?.Invoke(GlobalPosition, bypassShown, hit.HudDamageType());
				client?.FlashDamage(bypassShown);
			}
			else if (!blocked)
			{
				// No number and nothing blocked. A landed buildup still shows a "0"
				// so the hit registers; an inert hit reads "MISS!". (When blocked,
				// BLOCKED! already signals the connect, so neither fires.)
				if (appliedBuildup)
				{
					client?.onHudText?.Invoke(GlobalPosition, "0", EHudTextType.DamageLight);
				}
				else
				{
					client?.onHudText?.Invoke(GlobalPosition, Loc.Get(Loc.Keys.combat_miss), EHudTextType.Miss);
				}
			}
		}

		// Hitstun + knockback: latch the flinch + knockback windows so
		// per-frame ticks can count them down. Direction comes from the
		// sender via HitInfo.hitDirection; a zero direction drops knockback
		// entirely regardless of distance. Death overrides the hitstun anim
		// because the Die one-shot above latches first.
		if (hit.hitstun > 0f && _health > 0f && !parried)
		{
			_hitstunTime = Mathf.Max(_hitstunTime, hit.hitstun);
			PlayOneShot(EAnimation.Hitstun);
		}
		if (hit.knockbackDistance > 0f && hit.knockbackTime > 0f && hit.hitDirection != Vector3.Zero && _health > 0f && !parried)
		{
			Vector3 dir = hit.hitDirection;
			dir.Y = 0f;
			if (dir.LengthSquared() > 0.0001f)
			{
				// Constant-velocity knockback: distance/time gives the m/s
				// the body holds during the window so it covers exactly
				// `distance` meters in `time` seconds. _PhysicsProcess
				// forces this onto Velocity.X/Z each tick (overriding the
				// input-driven rebuild) and the trailing edge in TickHitstun
				// snaps horizontal back to zero so the body stops cleanly.
				float speed = hit.knockbackDistance / hit.knockbackTime;
				_knockbackVelocity = dir.Normalized() * speed;
				_knockbackTime = Mathf.Max(_knockbackTime, hit.knockbackTime);
			}
		}
	}

	// Deliver the parry counter-strike after a clean, well-timed sneak-block:
	// hit the attacker back with the blocking weapon's parryDamageProfileKey
	// profile. Only a melee attacker — the Mob that dealt the blocked blow — is
	// countered; a projectile / hazard source is left alone. No-op if the weapon
	// authors no parry profile (empty / unmapped key). Closes the window so the
	// counter fires once per crouch (the hit that triggered it also drops sneak,
	// so re-crouching is what reopens it).
	private void TryParry(WeaponState weapon, Node attacker)
	{
		_parryDeadlineMs = 0;
		if (weapon?.data == null || attacker is not Mob mob || !mob.alive)
		{
			return;
		}
		DamageData damage = weapon.data.GetDamage(weapon.data.parryDamageProfileKey);
		if (damage == null)
		{
			return;
		}
		Vector3 dir = mob.GlobalPosition - GlobalPosition;
		dir.Y = 0f;
		mob.Hit(new HitInfo(damage, this, dir, ETeam.Player));
	}

	// Signed HP delta from a status-effect tick. Positive heals, negative
	// damages. ArmorPenetration in [0, 1] controls the armor bypass on the damage
	// branch — 1 (default for status effects) drops everything straight onto
	// health, matching the historical "poison ignores armor" feel; less than
	// 1 routes the absorbable slice through armor and chips the bar. Heals
	// skip armor entirely. Doesn't run the OnHurtBoxHit hit pipeline —
	// status ticks don't interrupt actions or pump per-frame impact fx.
	private void ApplyStatusHealthDelta(float delta, float armorPenetration)
	{
		if (delta == 0f || _health <= 0f)
		{
			return;
		}
		bool wasAlive = _health > 0f;
		float before = _health;
		if (delta > 0f)
		{
			// Heal-over-time effects climb to MaxHealth the same way Heal()
			// does — drain doesn't reduce the effective cap, and any drain
			// the heal climbs into is forgiven to preserve the
			// `Health + DrainedHealth <= MaxHealth` invariant.
			_health = Mathf.Clamp(_health + delta, 0f, MaxHealth);
			_drainedHealth = Mathf.Min(_drainedHealth, Mathf.Max(0f, MaxHealth - _health));
		}
		else
		{
			// Damage branch: split between armor chip (absorbable) and direct
			// HP loss (bypassed) per the effect's armorPenetration. Identical math to
			// the OnHurtBoxHit armor block, scoped down to the fields the
			// status path mutates.
			float damage = -delta;
			float p = Mathf.Clamp(armorPenetration, 0f, 1f);
			float bypassed = damage * p;
			float absorbable = damage - bypassed;
			// Sneaking guard soaks the absorbable slice before central armor.
			// A DoT that chips the guard (burn) re-arms its recharge delay; one
			// that bypasses it (poison, absorbable==0) leaves it alone, same as
			// central armor. Blunt isn't modeled on status ticks, so the chip is
			// unscaled here. Status ticks don't break sneak, so the guard stays
			// up across a burn/poison zone while the player keeps sneaking.
			AbsorbWeaponBlock(GetSneakBlockWeapon(), ref absorbable, 0f);
			// A DoT that bypasses armor (poison / heal, armorPenetration=1) has
			// armorDamage==0 and never enters here, so it can't stall recovery.
			// One that chips armor (burn, armorPenetration<1) refreshes the
			// recharge window exactly like a direct hit.
			float armorDamage = absorbable;
			if (armorDamage > 0f && _armor > 0f)
			{
				_armor = Mathf.Max(0f, _armor - armorDamage);
				RefreshArmorRecharge(_armor > 0f);
			}
			else if (armorDamage > 0f && MaxArmor > 0f)
			{
				RefreshArmorRecharge(false);
			}
			_health = Mathf.Max(0f, _health - bypassed);
		}
		// Status-effect ticks already fire at 1Hz from StatusEffectController,
		// so route directly through onDamage / onHeal — no DoT accumulation
		// needed. Use the realized HP change rather than `delta` so a heal
		// that climbed into the MaxHealth cap (or a damage tick that bottomed
		// at 0) only announces what actually moved.
		float change = _health - before;
		GameClient client = GameClient.Current;
		if (client != null)
		{
			if (change > 0f)
			{
				client.onHeal?.Invoke(GlobalPosition, change, EHudTextType.HealLight);
			}
			else if (change < 0f)
			{
				client.onDamage?.Invoke(GlobalPosition, -change, EHudTextType.DamageLight);
			}
		}
		if (_health <= 0f && wasAlive)
		{
			SpawnWorldEffect(_deathFx);
			SpawnVoice(_voice?.death);
			HandleDeath();
			PlayOneShot(EAnimation.Die);
		}
	}

	// Common bookkeeping on the alive→dead transition. Cancels any in-flight
	// action (weapon charge / consumable / interactive), tears down dash and
	// sprint, drops sneak / aim / hitstun-driven knockback, releases the
	// active interactive, and fires onDied so GameClient can start the
	// death-screen sequence. Position / velocity / animation are left to
	// _PhysicsProcess — the corpse falls under gravity and the Die one-shot
	// (latched by the caller) holds the pose.
	private void HandleDeath()
	{
		_runner?.TryAbort();
		_pendingWeaponPressSlot = null;
		_pendingWeaponPressActionName = null;
		_contextSensitiveAttackSlot = null;
		ClearQueuedInput();
		_dashTimeRemaining = 0f;
		_sprinting = false;
		_sneaking = false;
		_aiming = false;
		_hitstunTime = 0f;
		_knockbackTime = 0f;
		_knockbackVelocity = Vector3.Zero;
		_jumpHeld = false;
		if (_curInteractive != null)
		{
			SetCurInteractive(null);
		}
		if (_highlightInteractive != null)
		{
			_highlightInteractive = null;
			onHighlightChanged?.Invoke(null);
		}
		_inputMove = Vector3.Zero;
		_inputLook = Vector3.Zero;
		// A member that died while concealed (the tree-climb bird's-eye hides the
		// model subtree) must still leave a VISIBLE corpse so it can be found and
		// revived — the bird's-eye fly-down that would normally restore the model
		// never completes once the death sequence takes over. Restore it here.
		if (_hidden)
		{
			_hidden = false;
			SetModelVisible(true);
		}
		onDied?.Invoke(this);
	}

	// Console / scripted death entry point. Drops health to zero on the alive
	// branch only — re-calling on an already-dead body is a silent no-op so a
	// stray `die` press doesn't re-fire the death audio / animation. Runs the
	// same blood + VO + animation latch as a fatal hit so the death sequence
	// reads identically regardless of source.
	public void Kill()
	{
		if (_health <= 0f)
		{
			return;
		}
		_health = 0f;
		SpawnWorldEffect(_deathFx);
		SpawnVoice(_voice?.death);
		HandleDeath();
		PlayOneShot(EAnimation.Die);
	}

	// Reset for respawn. Keeps inventory / equipped gear / learned languages /
	// armor max — those are run-scope, not life-scope — but restores
	// pools and clears every per-life condition (status effects, wetness,
	// thermal acclimation, hitstun, dash cooldown). Hard-teleports via
	// TeleportTo so the stuck-recovery history can't yank the body back to
	// where it died. Caller (GameClient) is responsible for snapping the
	// camera to the new position.
	public void Respawn(Vector3 position)
	{
		_statusEffects?.Clear();
		_coldState = null;
		_hotState = null;
		_drainedHealth = 0f;
		_bloodRegenStartMs = 0;
		_bodyTemperature = _world?.SampleAirTemperature(position) ?? 70f;
		_warmthZoneCount = 0;
		_warmthBonus = 0f;
		_safeZoneCount = 0;
		_health = MaxHealth;
		_armor = MaxArmor;
		_stamina = MaxStamina;
		_armorRecharging = false;
		_armorDepleted = false;
		_armorRechargeStartMs = 0;
		_staminaRechargeStartMs = 0;
		_dashTimeRemaining = 0f;
		_dashCooldownEndMs = 0;
		_hitstunTime = 0f;
		_knockbackTime = 0f;
		_knockbackVelocity = Vector3.Zero;
		_sneaking = false;
		_sprinting = false;
		_aiming = false;
		_jumpHeld = false;
		_oneShotClip = default;
		_oneShotIsHitstun = false;
		_oneShotOverridesCharge = false;
		_grounded = false;
		_coyoteTimeEndMs = 0;
		TeleportTo(position);
		// Force the animator off the Die clip so the first post-respawn frame
		// shows the idle pose instead of holding the corpse. UpdateAnimation
		// will repick on the next physics tick.
		if (_animator != null && data != null)
		{
			StringName idleName = AnimName(EAnimation.Idle);
			if (idleName != default)
			{
				_animator.Play(idleName);
			}
		}
	}

	// Counts the per-hit flinch + knockback windows down each physics tick.
	// The hitstun anim is latched as a one-shot in OnHurtBoxHit, so this
	// method only owns the state-clear for that timer — the animator falls
	// out of the anim naturally when it finishes or another one-shot replaces
	// it. Knockback is two-phase: while _knockbackTime > 0 the horizontal
	// velocity is forced to _knockbackVelocity (in the velocity rebuild
	// below); on the trailing edge we snap it back to zero and clear the
	// cached vector so the next frame's input rebuild starts clean.
	private void TickHitstun(float dt)
	{
		if (_hitstunTime > 0f)
		{
			_hitstunTime = Mathf.Max(0f, _hitstunTime - dt);
		}
		if (_knockbackTime > 0f)
		{
			_knockbackTime = Mathf.Max(0f, _knockbackTime - dt);
			if (_knockbackTime <= 0f)
			{
				Velocity = new Vector3(0f, Velocity.Y, 0f);
				_knockbackVelocity = Vector3.Zero;
			}
		}
	}
}
