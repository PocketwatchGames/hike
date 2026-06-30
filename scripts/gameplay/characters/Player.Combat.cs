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

	// Pure prediction — no state mutation. See Mob.GetHitType for the
	// networked-play motivation.
	private EHitResult GetHitType(HitInfo hit)
	{
		// Receiver-side resistance fold. ApplyResistance scales healthDamage,
		// armor-penetration chance, blunt mult, and knockback magnitude in place using
		// the diverse-site rules so the prediction below matches the actual
		// apply in OnHurtBoxHit.
		ApplyResistance(ref hit);
		if (hit.healthDamage <= 0f)
		{
			return EHitResult.None;
		}
		if (_armor > 0f && !hit.ArmorPenetrated)
		{
			return EHitResult.Armor;
		}
		if (_health <= 0f)
		{
			return EHitResult.None;
		}
		return hit.healthDamage >= _health ? EHitResult.Lethal : EHitResult.Health;
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
			// Surface / refresh the attacker's combat-objective panel on the HUD.
			GameClient.Current?.NotifyMobEngaged(attackingMob.SimState?.Species);
		}

		// Capture the charging weapon's guard BEFORE TryInterrupt — a weapon
		// authored to interrupt-on-damage would otherwise leave Charging here
		// and the hit that ended the charge wouldn't be blocked. The guard was
		// up when the hit landed, so it catches this one and then drops.
		WeaponState blockWeapon = GetChargingBlockWeapon();
		// Damage may interrupt an in-flight action (gated by profile +
		// per-tier canInterrupt). External interruption fires BEFORE damage
		// is applied so abortEvents can run on coherent pre-damage state.
		_runner?.TryInterrupt();
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
		// Armor handling. Bypass-aware split: a portion of `incomingDamage`
		// skips armor entirely (discrete `ArmorPenetrated` = full bypass; continuous
		// `armorBypassFraction` = partial), the rest is "absorbable" and
		// piles onto the armor chip scaled by `1 + hit.blunt`. Overflow
		// doesn't bleed into health on the absorbed portion — only the
		// pre-resolved bypass lands. The recharge window resets on any
		// absorbable hit that touches armor, including blows that land while
		// armor is already depleted (a sustained beating keeps the recover
		// window from starting). A pure-penetration hit (armorPenetration=1,
		// etc.) never touches armor and so doesn't extend the window.
		float bypassFraction = hit.ArmorPenetrated ? 1f : hit.armorBypassFraction;
		float bypassed = incomingDamage * bypassFraction;
		float absorbable = incomingDamage - bypassed;
		// Weapon block armor takes the absorbable slice FIRST while the player
		// is charging a guard-bearing weapon — the held charge doubles as a
		// shield. When the guard eats the slice, only the pre-resolved bypass
		// continues past it (matching the central-armor "overflow doesn't
		// bleed" rule below). AbsorbWeaponBlock also re-arms the weapon's
		// recharge delay on any guard-touching hit, even at zero guard.
		float blockAbsorbed = AbsorbWeaponBlock(blockWeapon, ref absorbable, hit.blunt);
		if (blockAbsorbed > 0f)
		{
			incomingDamage = bypassed;
			// Guard reaction one-shot, played over the held charge pose (resolves
			// the wielded weapon's Block override; no-op if it authors none). The
			// blocking weapon is the one being charged, i.e. the wielded weapon.
			PlayOneShot(EAnimation.Block, overridesCharge: true);
		}
		float armorAbsorbed = 0f;
		if (absorbable > 0f && _armor > 0f)
		{
			float armorDamage = absorbable * (1f + hit.blunt);
			float armorBefore = _armor;
			_armor = Mathf.Max(0f, _armor - armorDamage);
			armorAbsorbed = armorBefore - _armor;
			RefreshArmorRecharge(_armor > 0f);
			incomingDamage = bypassed;
		}
		else if (absorbable > 0f && MaxArmor > 0f)
		{
			// Armor already depleted but the player has armor capacity: the hit
			// lands fully on health (incomingDamage unchanged), yet we push the
			// recover window out so repeated blows don't let armor refill.
			RefreshArmorRecharge(false);
		}

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

		// Floating-number HUD feedback. Armor chip and armor-penetrated health damage
		// both show — total = whatever the bar actually moved (capped by what
		// armor / health had to give). DoT hits route into the per-second
		// accumulator so a fast-ticking burn / poison zone emits one rolled-up
		// number per second; single hits fire onDamage immediately.
		float totalShown = blockAbsorbed + armorAbsorbed + Mathf.Max(0f, incomingDamage);
		if (totalShown > 0f)
		{
			if (hit.dot)
			{
				_dotHud.AddDamage(totalShown);
			}
			else
			{
				GameClient client = GameClient.Current;
				client?.onDamage?.Invoke(GlobalPosition, totalShown, EHudTextType.DamageLight);
				client?.FlashDamage(totalShown);
			}
		}

		// On-hit effects — immediate-apply entries land now, the rest funnel
		// into the per-effect meter, folding any applyTrigger from a crossed
		// threshold back onto the hit before hitstun/knockback resolution so an
		// OnDizzy modifier can amplify those reads on the same hit that landed
		// dizzy. Skipped when this hit was lethal (`_health` already reduced
		// above): a dead player shouldn't accrue meters or catch fire/poison,
		// matching the `_health > 0f` gate on the knockback reads below.
		if (_health > 0f)
		{
			_statusEffects?.ApplyHitBuildups(ref hit);
		}

		// Hitstun + knockback: latch the flinch + knockback windows so
		// per-frame ticks can count them down. Direction comes from the
		// sender via HitInfo.hitDirection; a zero direction drops knockback
		// entirely regardless of distance. Death overrides the hitstun anim
		// because the Die one-shot above latches first.
		if (hit.hitstun > 0f && _health > 0f)
		{
			_hitstunTime = Mathf.Max(_hitstunTime, hit.hitstun);
			PlayOneShot(EAnimation.Hitstun);
		}
		if (hit.knockbackDistance > 0f && hit.knockbackTime > 0f && hit.hitDirection != Vector3.Zero && _health > 0f)
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
			// Charging guard soaks the absorbable slice before central armor.
			// A DoT that chips the guard (burn) re-arms its recharge delay; one
			// that bypasses it (poison, absorbable==0) leaves it alone, same as
			// central armor. Blunt isn't modeled on status ticks, so the chip is
			// unscaled here. Status ticks don't interrupt actions, so querying
			// the guard at the call site is safe (no TryInterrupt ordering concern).
			AbsorbWeaponBlock(GetChargingBlockWeapon(), ref absorbable, 0f);
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
