using Godot;
using Godot.Collections;
using System;
using System.Collections.Generic;

// World pickup. The pickup model is decided at run time per (player,
// inventory) pair: if the player already has a same-kind non-full stack and
// the whole pile would top off into those existing stacks, walking near the
// pile is enough — InteractArea.BodyEntered fires and the loot deposits.
// Otherwise (fresh item, full stacks, non-stackable, or explicitly dropped by
// the player) the same area's interact-highlight path takes over and pickup
// runs through the action runner so the player has to press Interact. One
// Area3D drives both — the auto-pickup probe and the interact-highlight scan
// share the same volume so the two modes can't disagree on range.
[GlobalClass]
public partial class Loot : RigidBody3D, IInteractive, IWorldEntity
{
	[Export] private CollisionShape3D _collisionShape;
	[Export] private AnimationPlayer _animationPlayer;
	[Export] private Area3D _interactArea;
	[Export] private HurtBox _hurtBox;
	[Export] private Node3D _hudNode;
	[Export] private Sprite3D _sprite;
	// Anchor a LootData.worldModel is parented under when the dropped item
	// renders as a 3D mesh instead of the flat sprite (e.g. the elite crown).
	[Export] private Node3D _modelAnchor;
	[Export] private PackedScene _pickupEffectScene;
	[Export] private PackedScene _spawnEffectScene;
	// Played at the loot's position when it expires (LootData.removeTimeMs).
	// Same Fx.Create one-shot pattern as the pickup/spawn effects; null leaves
	// the despawn silent (e.g. test scenes that don't author a remove cue).
	[Export] private PackedScene _removeEffectScene;
	// How long after spawn an impulse-launched pickup becomes grabbable, even
	// if it's still moving. Lets arrows be recovered mid-flight after the
	// initial firing arc has cleared the shooter; loot that comes to rest
	// before this elapses unlocks pickup at rest via Settle() instead.
	[Export] private float _pickupReadyDelaySeconds = 0.6f;

	// Longest-side pixel budget for an auto-derived world pickup sprite. When
	// an item authors no worldSprite, its (full-res) inventory icon is
	// point-downsampled to this many chunky pixels (see GetChunkyPickupTexture)
	// so the dropped pickup reads at the same chunky scale as hand-authored
	// pickup art instead of rendering the big inventory icon 1:1.
	[Export] private int _pickupMaxChunkyPixels = 16;

	// Water physics tuning. Same shape as MobData / PlayerData — small items
	// bob more aggressively to the surface, ride currents readily, and don't
	// sink fast when displaced downward. Defaults chosen for typical
	// stackable loot; per-loot overrides can be authored on derived scenes
	// (e.g. a heavy iron ingot with low buoyancy that just sinks).
	[Export] private float _buoyancyAcceleration = 15f;
	[Export] private float _waterDrag = 5f;
	[Export] private float _waterSinkSpeed = 1f;
	[Export] private float _waterSurfaceOffset = 0.3f;
	[Export] private float _waterCurrentDrag = 3f;

	// Loot magnet. A material pickup inside the player's attract sphere
	// (Player._pickupAttractArea) flies toward them when the path is clear:
	// _magnetAcceleration ramps its speed toward the player up to _magnetMaxSpeed,
	// aimed _magnetTargetHeight up the player's body. The item stays a rigidbody
	// while seeking, so it flies "using collision" (a wall it clips into stops
	// it), and losing line of sight drops it back to normal physics until the
	// path clears. Per-loot so a heavy pickup could feel more sluggish.
	[Export] private float _magnetAcceleration = 45f;
	[Export] private float _magnetMaxSpeed = 14f;
	[Export] private float _magnetTargetHeight = 1.0f;
	// Height above the loot's origin the line-of-sight ray leaves from — lifts
	// the origin off the ground so a lip of terrain right at its feet doesn't
	// read as blocked. (Its own Passive body is never on the Solid ray mask, so
	// the loot can't self-block regardless.)
	[Export] private float _losRayHeight = 0.3f;

	// Authored interaction list. The first entry's events should include an
	// OpenInteractive event that triggers Complete() — that's how the runner
	// signals "the loot has been collected."
	[Export] private Array<InteractiveAction> _actions = new();

	private LootSimState _simState;
	private bool _pickedUp;
	private bool _removed;
	private Sim _world;
	private Vector3 _initialImpulse;
	private Player _picker;
	private bool _playSpawnEffects;
	// GameTimeMs stamp captured the first frame the pickup ticks, so its
	// pickup-ready delay and LootData.removeTimeMs despawn run on the sim clock
	// (slow with slow-mo, frame-rate independent, matching the codebase's
	// duration convention) rather than wall-clock frames. Local to the live
	// instance — re-stamps at 0 age if the chunk unloads and re-streams the loot.
	private ulong _spawnTimeMs;
	private bool _spawnStamped;
	// Water-physics state. _swimming flips when the loot's feet voxel is
	// water; _gravityScaleSwimActive tracks whether engine gravity has been
	// zeroed so ApplyWaterPhysics alone controls vertical motion (mirrors
	// Mob's pattern). _gravityScaleAuthored captures the scene-authored
	// gravity_scale once so the value is restored verbatim on water exit.
	private bool _swimming;
	private float _waterSurfaceY;
	private float _gravityScaleAuthored;
	private bool _gravityScaleCaptured;
	private bool _gravityScaleSwimActive;

	// Loot-magnet state. _attractor is the player whose pickup-attract sphere
	// this loot is currently inside (set/cleared by Player on area enter/exit);
	// _seeking is true only on ticks it's actively flying toward them (eligible +
	// clear LOS). _seekGravityActive tracks whether engine gravity is zeroed for
	// the flight so StopSeeking restores the authored scale exactly once.
	private Player _attractor;
	private bool _seeking;
	private bool _seekGravityActive;

	// Timed-emergence state (LootData.timedEmergence). Drives a buried->risen
	// animation of the visual only — the rigidbody stays settled on the ground.
	// All timing runs on the sim clock (GameTimeMs) so interactivity unlocks
	// deterministically once the rise completes. Inert unless IsTimedEmergent.
	private enum EmergeState { Uninitialized, Hidden, Emerging, Visible, Retracting }
	private EmergeState _emergeState = EmergeState.Uninitialized;
	private ulong _emergeStartMs;   // GameTimeMs the current emerge/retract began.
	private bool _emergePending;    // A staggered transition is scheduled.
	private bool _emergePendingInWindow; // Target window-membership of the pending transition.
	private bool _emergeRawInWindow; // Last-seen window membership, to spot transitions.
	private ulong _emergeDeadlineMs; // GameTimeMs the staggered transition fires.
	private Node3D _emergeVisual;   // Cached sprite (or model anchor) being animated.
	private Vector3 _emergeRestPos; // Authored rest local position/scale, captured once.
	private Vector3 _emergeRestScale;
	private float _emergeFraction;  // 0 = buried/hidden, 1 = fully risen.

	private TimedEmergenceData TimedEmergence =>
		(_simState?.Item?.data ?? _simState?.Data) is LootData ld ? ld.timedEmergence : null;
	private bool IsTimedEmergent => TimedEmergence != null;

	public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

	public override void _Ready()
	{
		if (_interactArea != null)
		{
			_interactArea.BodyEntered += OnInteractAreaBodyEntered;
		}

		if (_hurtBox != null)
		{
			_hurtBox.OnHit = OnHurtBoxHit;
			_hurtBox.PredictHit = _ => new HitPrediction(EHitResult.Object, EDamageTriggerFlags.None);
		}

		if (_initialImpulse != Vector3.Zero)
		{
			CanSleep = false;
			ContactMonitor = true;
			MaxContactsReported = 1;
			ApplyCentralImpulse(_initialImpulse);
		}
		else
		{
			Settle();
		}

		if (IsTimedEmergent)
		{
			// Start buried so there's no one-frame flash of the full sprite
			// before the first _Process tick snaps to the correct phase.
			CacheEmergeVisual();
			ApplyEmergeFraction(0f);
			SetEmergeInteractable(false);
		}
	}

	public override void _Process(double delta)
	{
		if (_pickedUp || _removed || _world == null)
		{
			return;
		}
		ulong now = _world.GameTimeMs;
		if (!_spawnStamped)
		{
			_spawnTimeMs = now;
			_spawnStamped = true;
		}
		ulong ageMs = now - _spawnTimeMs;
		if (IsTimedEmergent)
		{
			// Timed-emergent loot owns its own interact-area gating (only while
			// fully risen), so the standard "enable once the spawn arc clears"
			// path below is skipped for it.
			UpdateEmergence(now);
		}
		// Enable the interact area once the firing arc has cleared without
		// freezing the body — pickup remains available even if the loot is
		// still tumbling. Settle() (called from _IntegrateForces on rest)
		// also sets Monitoring=true, so whichever path fires first wins.
		else if (_interactArea != null && !_interactArea.Monitoring
			&& _pickupReadyDelaySeconds > 0f && ageMs >= (ulong)(_pickupReadyDelaySeconds * 1000f))
		{
			_interactArea.Monitoring = true;
		}
		ItemData data = _simState?.Item?.data ?? _simState?.Data;
		if (data is LootData lootData && lootData.removeTimeMs > 0 && ageMs >= (ulong)lootData.removeTimeMs)
		{
			Expire();
			return;
		}
		ItemState carried = _simState?.Item;
		if (carried != null)
		{
			// Perishable food dropped back into the world keeps spoiling: shed any
			// expired cohorts and despawn once the whole pile is gone.
			carried.PruneExpired(_world.DayNumber);
			if (carried.stackCount <= 0)
			{
				Expire();
				return;
			}
			// A carried instance can also carry its own dawn expiry
			// (ItemState.removeOnDay) — e.g. a time-limited fairy corpse dropped
			// back out is still due to vanish at the next sleep-to-sunrise. Unlike
			// LootData.removeTimeMs (an age-since-spawn duration) this is a day count.
			if (carried.removeOnDay > 0 && _world.DayNumber >= carried.removeOnDay)
			{
				Expire();
			}
		}
	}

	// Force this pickup to despawn now and fire its OnRemovedFromWorld hook,
	// identical to a natural removeTimeMs expiry (same shrink/lift outro).
	// Used by ArrowLootSimState.Recover so the weapon's central ammo-recharge
	// timer can auto-reclaim the oldest outstanding arrow.
	public void RecoverArrow()
	{
		Expire();
	}

	private void Expire()
	{
		if (_removed || _pickedUp)
		{
			return;
		}
		_removed = true;
		// Reuse the PickedUp latch on the sim state — the only thing that
		// flag gates is LootSimState.CreateEntity returning null, which is
		// exactly the behavior we want for expired loot (don't respawn it
		// when the chunk re-streams).
		if (_simState != null)
		{
			_simState.PickedUp = true;
			_simState.OnRemovedFromWorld();
		}
		if (_removeEffectScene != null)
		{
			Fx.Create(_removeEffectScene, GetParent(), Position);
		}
		if (_collisionShape != null)
		{
			_collisionShape.Disabled = true;
		}
		if (_interactArea != null)
		{
			_interactArea.Monitoring = false;
		}
		_world?.RemoveEntity(this);
		// Play the PickedUp shrink/lift animation as the despawn outro — same
		// visual outro as a player pickup, so an arrow timing out reads as
		// "vanished" rather than "popped out of existence." OnPickedUpFinished
		// runs QueueFree once the animation ends. No animation player → free
		// immediately.
		if (_animationPlayer != null)
		{
			_animationPlayer.AnimationFinished += OnPickedUpFinished;
			_animationPlayer.Play("PickedUp");
		}
		else
		{
			QueueFree();
		}
	}

	public override void _IntegrateForces(PhysicsDirectBodyState3D state)
	{
		if (_pickedUp || Freeze)
		{
			return;
		}

		// While flying to the player the magnet drives velocity every tick — don't
		// let a graze-contact settle (and freeze) it mid-flight.
		if (_seeking)
		{
			return;
		}

		// Don't settle (and freeze) while in water — buoyancy and currents
		// need to keep acting on the body so it bobs and drifts. The
		// _PhysicsProcess water path will handle wake-up if a settled item
		// later gets submerged (e.g. tide rises into a stash).
		if (_swimming)
		{
			return;
		}

		if (state.LinearVelocity.LengthSquared() < 0.25f && state.GetContactCount() > 0)
		{
			Settle();
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_pickedUp || _removed || _world == null)
		{
			return;
		}

		if (!_gravityScaleCaptured)
		{
			_gravityScaleAuthored = GravityScale;
			_gravityScaleCaptured = true;
		}

		if (UpdateMagnet((float)delta))
		{
			// Magnet owns the body's motion this tick — skip the water/settle path
			// so buoyancy doesn't fight the seek.
			return;
		}

		UpdateWaterState();

		if (_swimming)
		{
			if (Freeze)
			{
				// Wake the body so buoyancy / currents can move it. Switch
				// off the sprite Bob animation — the rigidbody itself now
				// owns the visible vertical motion, and a sprite-Y bob on
				// top would compete with it.
				Freeze = false;
				_animationPlayer?.Play("Idle");
			}
			if (!_gravityScaleSwimActive)
			{
				GravityScale = 0f;
				_gravityScaleSwimActive = true;
			}
			ApplyWaterPhysics((float)delta);
		}
		else if (_gravityScaleSwimActive)
		{
			GravityScale = _gravityScaleAuthored;
			_gravityScaleSwimActive = false;
		}
	}

	// Mirrors Mob.UpdateWaterState minus the swimDepthThreshold gate — small
	// loot floats in puddles too, so any voxel of water at the body's feet
	// counts as "swimming." _waterSurfaceY is the Y of the first non-water
	// voxel above, used as the buoyancy target.
	private void UpdateWaterState()
	{
		WorldState ws = _world.WorldState;
		Vector3 pos = GlobalPosition;
		int fx = Mathf.FloorToInt(pos.X);
		int fy = Mathf.FloorToInt(pos.Y);
		int fz = Mathf.FloorToInt(pos.Z);
		if (ws.GetVoxelWorld(fx, fy, fz) != VoxelType.Water)
		{
			_swimming = false;
			return;
		}
		int topY = fy;
		while (ws.GetVoxelWorld(fx, topY + 1, fz) == VoxelType.Water)
		{
			topY++;
		}
		_swimming = true;
		_waterSurfaceY = topY + 1;
	}

	// Mirrors Mob.ApplyWaterPhysics: depth-scaled upward buoyancy toward the
	// surface, vertical drag damping the resulting oscillation, and an XZ
	// impulse that drags horizontal velocity toward the local water current
	// so the item rides the river instead of sitting still in the flow.
	private void ApplyWaterPhysics(float dt)
	{
		Vector3 pos = GlobalPosition;
		Vector3 vel = LinearVelocity;
		Vector3 deltaVel = Vector3.Zero;

		float targetY = _waterSurfaceY - _waterSurfaceOffset;
		float depthBelowSurface = targetY - pos.Y;
		if (depthBelowSurface > 0f)
		{
			deltaVel.Y += Mathf.Min(depthBelowSurface, 1f) * _buoyancyAcceleration * dt;
		}
		else
		{
			deltaVel.Y -= _buoyancyAcceleration * 0.5f * dt;
		}

		deltaVel.Y -= vel.Y * _waterDrag * dt;

		Vector3 current = _world.WorldState.SampleWaterCurrent(pos);
		deltaVel.X += (current.X - vel.X) * _waterCurrentDrag * dt;
		deltaVel.Z += (current.Z - vel.Z) * _waterCurrentDrag * dt;

		ApplyImpulse(deltaVel * Mass);

		if (LinearVelocity.Y < -_waterSinkSpeed)
		{
			Vector3 v = LinearVelocity;
			LinearVelocity = new Vector3(v.X, -_waterSinkSpeed, v.Z);
		}
	}

	// --- Loot magnet -------------------------------------------------------
	// Called by Player when this loot enters/leaves that player's pickup-attract
	// sphere. The per-tick seek in UpdateMagnet decides eligibility + LOS; here we
	// only track which player is in range, preferring the active (controlled)
	// member so an idle party member standing nearby can't claim the magnet.
	public void OnEnterAttractRange(Player player)
	{
		if (player == null)
		{
			return;
		}
		if (_attractor != null && _attractor != player
			&& IsInstanceValid(_attractor) && _attractor.IsActive && !player.IsActive)
		{
			return;
		}
		_attractor = player;
	}

	public void OnExitAttractRange(Player player)
	{
		if (_attractor == player)
		{
			_attractor = null;
			StopSeeking();
		}
	}

	// Drive the loot toward its attractor when it's an eligible material pickup
	// with a clear line of sight. Returns true on ticks it's actively seeking
	// (the caller then hands motion entirely to the magnet). A blocked path or
	// lost eligibility stops the seek and lets normal physics drop it to ground.
	private bool UpdateMagnet(float dt)
	{
		Player p = _attractor;
		if (p != null && !IsInstanceValid(p))
		{
			p = _attractor = null;
		}
		if (p == null || !IsMagnetEligible(p) || !HasLineOfSight(p))
		{
			if (_seeking)
			{
				StopSeeking();
			}
			return false;
		}

		if (!_seeking)
		{
			_seeking = true;
			// Guarantee the interact area is probing so contact pickup fires even
			// if the post-spawn arc delay hasn't elapsed, and stop the idle bob —
			// the magnet owns the loot's vertical motion now.
			if (_interactArea != null)
			{
				_interactArea.Monitoring = true;
			}
			_animationPlayer?.Play("Idle");
		}
		Freeze = false;
		if (!_seekGravityActive)
		{
			GravityScale = 0f;
			_seekGravityActive = true;
		}

		Vector3 target = p.GlobalPosition + Vector3.Up * _magnetTargetHeight;
		Vector3 dir = (target - GlobalPosition).Normalized();
		Vector3 vel = LinearVelocity + dir * _magnetAcceleration * dt;
		if (vel.LengthSquared() > _magnetMaxSpeed * _magnetMaxSpeed)
		{
			vel = vel.Normalized() * _magnetMaxSpeed;
		}
		LinearVelocity = vel;
		return true;
	}

	// Hand motion back to normal physics after a seek ends (out of range, LOS
	// lost, or no longer fits). Restores the authored gravity so the loot falls
	// and re-settles — the "drop" — and _IntegrateForces re-freezes it at rest.
	private void StopSeeking()
	{
		_seeking = false;
		if (_seekGravityActive)
		{
			if (_gravityScaleCaptured)
			{
				GravityScale = _gravityScaleAuthored;
			}
			_seekGravityActive = false;
		}
	}

	// Whether this loot should fly to `player`: a depositable material whose whole
	// stack currently fits, not flagged interact-only, and the player is the active
	// (controlled) member. Re-checked every seek tick so it drops the instant the
	// backpack fills or control switches away.
	private bool IsMagnetEligible(Player player)
	{
		if (_pickedUp || _removed || _simState == null || _simState.RequireInteract)
		{
			return false;
		}
		if (player?.Inventory == null || !player.IsActive)
		{
			return false;
		}
		if (!_simState.CanPickup(player) || !_simState.ShouldDepositToInventory())
		{
			return false;
		}
		ItemData data = _simState.Item?.data ?? _simState.Data;
		if (data == null || !data.IsMaterial)
		{
			return false;
		}
		return player.Inventory.CanFullyAdd(data, _simState.Item?.stackCount ?? 1);
	}

	// Clear straight-line path from the loot up to the player's chest. Masks Solid
	// (terrain + porous props) only — the loot's own Passive body and the player's
	// Player-layer body aren't on it, so neither self-blocks the ray.
	private bool HasLineOfSight(Player player)
	{
		Vector3 from = GlobalPosition + Vector3.Up * _losRayHeight;
		Vector3 to = player.GlobalPosition + Vector3.Up * _magnetTargetHeight;
		var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Solid);
		query.CollideWithAreas = false;
		query.CollideWithBodies = true;
		Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
		return hit.Count == 0;
	}

	private void Settle()
	{
		Freeze = true;
		// Timed-emergent loot stays frozen on the ground here, but its
		// monitoring and idle animation are driven by the emergence state
		// machine (only once fully risen) — don't unlock pickup or start the
		// bob while it's still buried outside its window.
		if (IsTimedEmergent)
		{
			return;
		}
		// Enable monitoring after settle so the area only starts probing once
		// the loot is at rest — avoids spurious BodyEntered events from
		// graze-collisions during the post-spawn flight arc.
		if (_interactArea != null)
		{
			_interactArea.Monitoring = true;
		}
		// Loot that flew in (chest emission, player drop) bobs to read as
		// freshly arrived; loot that was already in the world at spawn (world
		// gen, LootSpawnEntry) sits idle so the chunk doesn't pulse.
		_animationPlayer?.Play(_initialImpulse != Vector3.Zero ? "Bob" : "Idle");
	}

	// --- Timed emergence (LootData.timedEmergence) -------------------------
	// The visual rises out of the ground while the current time-of-day is inside
	// the authored window and sinks back when it leaves. Only the visual (sprite
	// or model anchor) moves — the rigidbody stays settled — and the pickup is
	// interactive only while fully risen. Driven on the sim clock so the
	// interactivity gate is frame-rate independent and slow-mo aware.
	private void UpdateEmergence(ulong now)
	{
		TimedEmergenceData te = TimedEmergence;
		if (te == null)
		{
			return;
		}
		bool inWindow = te.Contains(_world.WorldState.TimeOfDay01);

		if (_emergeState == EmergeState.Uninitialized)
		{
			// Snap to the current phase on first tick (or after a chunk
			// re-stream) so loot loaded inside its window is already risen and
			// loot loaded outside it is already buried — no spurious animation.
			CacheEmergeVisual();
			_emergeRawInWindow = inWindow;
			if (inWindow)
			{
				_emergeState = EmergeState.Visible;
				ApplyEmergeFraction(1f);
				SetEmergeInteractable(true);
				_animationPlayer?.Play("Idle");
			}
			else
			{
				_emergeState = EmergeState.Hidden;
				ApplyEmergeFraction(0f);
				SetEmergeInteractable(false);
				_animationPlayer?.Stop();
			}
			return;
		}

		// Schedule a staggered transition when the window opens or closes, so a
		// patch of mushrooms doesn't emerge/retract in unison.
		if (inWindow != _emergeRawInWindow)
		{
			_emergeRawInWindow = inWindow;
			_emergePending = true;
			_emergePendingInWindow = inWindow;
			float delaySeconds = te.staggerSeconds > 0f ? te.staggerSeconds * GD.Randf() : 0f;
			_emergeDeadlineMs = now + (ulong)(delaySeconds * 1000f);
		}
		if (_emergePending && now >= _emergeDeadlineMs)
		{
			_emergePending = false;
			if (_emergePendingInWindow)
			{
				BeginEmerge(now);
			}
			else
			{
				BeginRetract(now);
			}
		}

		// Advance the active rise/retract on the sim clock.
		float durMs = Mathf.Max(1f, te.emergeSeconds * 1000f);
		if (_emergeState == EmergeState.Emerging)
		{
			float f = Mathf.Min(1f, (now - _emergeStartMs) / durMs);
			ApplyEmergeFraction(f);
			if (f >= 1f)
			{
				_emergeState = EmergeState.Visible;
				SetEmergeInteractable(true);
				_animationPlayer?.Play("Idle");
			}
		}
		else if (_emergeState == EmergeState.Retracting)
		{
			float f = Mathf.Max(0f, 1f - (now - _emergeStartMs) / durMs);
			ApplyEmergeFraction(f);
			if (f <= 0f)
			{
				_emergeState = EmergeState.Hidden;
				_animationPlayer?.Stop();
			}
		}
	}

	private void BeginEmerge(ulong now)
	{
		if (_emergeState == EmergeState.Visible || _emergeState == EmergeState.Emerging)
		{
			return;
		}
		_emergeState = EmergeState.Emerging;
		// Stay non-interactive until the rise completes, and take the transform
		// back from the idle bob animation for the duration of the rise.
		SetEmergeInteractable(false);
		_animationPlayer?.Stop();
		// Back-date the start by the already-shown fraction so reversing a
		// partial retract continues smoothly instead of snapping.
		_emergeStartMs = BackdatedStart(now, _emergeFraction);
	}

	private void BeginRetract(ulong now)
	{
		if (_emergeState == EmergeState.Hidden || _emergeState == EmergeState.Retracting)
		{
			return;
		}
		_emergeState = EmergeState.Retracting;
		// No longer grabbable the instant it starts sinking.
		SetEmergeInteractable(false);
		_animationPlayer?.Stop();
		_emergeStartMs = BackdatedStart(now, 1f - _emergeFraction);
	}

	// Start time placed `elapsedFraction` of an emerge duration into the past so
	// the animation resumes from the current shown fraction. Clamped so an early
	// transition (small GameTimeMs) can't underflow the unsigned clock.
	private ulong BackdatedStart(ulong now, float elapsedFraction)
	{
		float durMs = Mathf.Max(1f, (TimedEmergence?.emergeSeconds ?? 0f) * 1000f);
		ulong back = (ulong)(Mathf.Clamp(elapsedFraction, 0f, 1f) * durMs);
		return now > back ? now - back : 0;
	}

	private void CacheEmergeVisual()
	{
		if (_emergeVisual != null)
		{
			return;
		}
		// Animate the active visual: the 3D model anchor when this loot renders a
		// mesh, otherwise the flat sprite.
		bool hasModel = (_simState?.Item?.data ?? _simState?.Data) is LootData ld && ld.worldModel != null;
		_emergeVisual = hasModel ? _modelAnchor : _sprite;
		if (_emergeVisual != null)
		{
			_emergeRestPos = _emergeVisual.Position;
			_emergeRestScale = _emergeVisual.Scale;
		}
	}

	// Pose the visual at `f` along buried(0)->risen(1): scaled from zero and
	// lifted from RiseDistance below the rest pose up to it.
	private void ApplyEmergeFraction(float f)
	{
		_emergeFraction = f;
		if (_emergeVisual == null)
		{
			return;
		}
		float rise = TimedEmergence?.riseDistance ?? 0f;
		_emergeVisual.Visible = f > 0f;
		_emergeVisual.Scale = _emergeRestScale * f;
		_emergeVisual.Position = _emergeRestPos + new Vector3(0f, -rise * (1f - f), 0f);
	}

	private void SetEmergeInteractable(bool on)
	{
		if (_interactArea != null)
		{
			// Gate both the auto-pickup probe (Monitoring) and the
			// interact-highlight detection (Monitorable) so buried loot is
			// neither grabbed nor highlighted.
			_interactArea.Monitoring = on;
			_interactArea.Monitorable = on;
		}
	}

	private void OnHurtBoxHit(HitInfo hit)
	{
	}

	private void OnInteractAreaBodyEntered(Node body)
	{
		if (_pickedUp || body is not Player player)
		{
			return;
		}
		// Body entry only acts when the inventory state allows auto-pickup.
		// Otherwise the same area's interact-highlight path is what the
		// player uses, via the action runner.
		if (!CanAutoPickup(player))
		{
			return;
		}
		_picker = player;
		FinalizePickup();
	}

	// Materials always auto-pickup on contact, provided the whole stack fits —
	// a fresh material claims a new backpack slot, it no longer has to top off
	// an existing stack. Non-materials (weapons / armor) never auto-pickup from
	// the field; they fall through to the press-to-interact path.
	private bool CanAutoPickup(Player player)
	{
		if (_simState == null || _simState.RequireInteract)
		{
			return false;
		}
		if (player?.Inventory == null)
		{
			return false;
		}

		// Per-loot-kind gate (e.g. arrow bound to a specific weapon) — must
		// pass before any auto-pickup path. Without this, a loose arrow's
		// pickup would trigger regardless of which player walks over it.
		if (!_simState.CanPickup(player))
		{
			return false;
		}

		// Loot that doesn't deposit into the inventory (arrows return ammo
		// to the source weapon) auto-picks up on collision unconditionally —
		// no stack-space search needed since nothing is being added to a
		// slot. CanPickup above already vetted the binding.
		if (!_simState.ShouldDepositToInventory())
		{
			return true;
		}

		ItemData data = _simState.Item?.data ?? _simState.Data;
		if (data == null || !data.IsMaterial)
		{
			return false;
		}
		return player.Inventory.CanFullyAdd(data, _simState.Item?.stackCount ?? 1);
	}

	public bool CanInteract() => !_pickedUp && (!IsTimedEmergent || _emergeState == EmergeState.Visible);
	public bool CanActorInteract(Player player)
	{
		if (_pickedUp || player?.Inventory == null)
		{
			return false;
		}
		// Timed-emergent loot is only grabbable once fully risen.
		if (IsTimedEmergent && _emergeState != EmergeState.Visible)
		{
			return false;
		}
		// Per-loot-kind gate — arrow drops bound to a specific weapon refuse
		// pickup unless that weapon is still equipped.
		if (_simState != null && !_simState.CanPickup(player))
		{
			return false;
		}
		// Auto-pickup loot suppresses its own interact highlight — body entry
		// will commit the pickup on the next physics frame, so showing the
		// "press to interact" affordance would just flicker.
		if (CanAutoPickup(player))
		{
			return false;
		}
		// Backpack-fit gate only applies to loot that actually deposits into
		// the inventory. Arrows return to the source weapon's ammo pool and
		// don't take a slot, so a full backpack must not block recovering them.
		if (_simState != null && !_simState.ShouldDepositToInventory())
		{
			return true;
		}
		// A boon-offering pickup (fairy corpse) opens the upgrade screen and
		// never deposits an item, so the backpack-fit gate below doesn't apply.
		if (OffersBoons)
		{
			return true;
		}
		// An apply-on-pickup item (potion drunk, scroll read) runs its effect on
		// interact and never deposits, so the backpack-fit gate doesn't apply.
		if (AppliesOnPickup)
		{
			return true;
		}
		// If the loot carries an item, only allow interact when there's
		// space; otherwise the action would run to completion and silently
		// fail. Armor/weapons can land directly in an empty equip slot, so a
		// full backpack only blocks pickup when there's no slot to equip into.
		if (_simState?.Item != null && player.Inventory.BackpackCount >= player.Inventory.BackpackCapacity)
		{
			ItemData data = _simState.Item.data ?? _simState.Data;
			if (!HasEmptyEquipSlot(player.Inventory, data))
			{
				return false;
			}
		}
		return true;
	}

	private static bool HasEmptyEquipSlot(Inventory inv, ItemData data)
	{
		if (inv == null || data == null)
		{
			return false;
		}
		switch (data)
		{
			case ArmorData armor:
				return inv.GetEquipped(armor.armorSlot) == null;
			case WeaponData weapon:
				return inv.GetEquipped(weapon.CanonicalSlot) == null;
			default:
				return false;
		}
	}

	public Array<InteractiveAction> GetActions(Player player)
	{
		if (_actions == null || _actions.Count == 0)
		{
			return null;
		}
		_picker = player;
		return _actions;
	}

	// Called from the action's OpenInteractive event handler at the
	// authored t=N moment. Deposits the carried item into the picker's
	// inventory (if any) and removes the loot from the world.
	public void Complete(int actionIndex)
	{
		if (_pickedUp || _picker == null)
		{
			return;
		}
		// A boon-offering pickup (fairy corpse) doesn't enter the inventory —
		// interacting opens the upgrade screen and the corpse stays put until a
		// boon is actually claimed. Only fall through to the normal deposit when
		// the offering can't be started.
		if (TryOfferBoons(_picker))
		{
			return;
		}
		// An apply-on-pickup item (potion / scroll) drinks/reads on the spot
		// instead of depositing — like the boon path, this never enters the
		// inventory.
		if (TryApplyOnPickup(_picker))
		{
			return;
		}
		FinalizePickup();
	}

	// True when this loot hands the player a choice of boons on interact instead
	// of depositing an item (the fairy corpse — possibleBoons composed onto its
	// state at spawn, see Sim.ComposeFairyBoons).
	private bool OffersBoons => _simState?.Item != null && _simState.Item.possibleBoons.Count > 0;

	// The apply-on-pickup payload carried by this loot (potion / scroll), or
	// null. Resolved off the composed Item if present, else the raw Data for
	// world-spawned loot that carries no pre-built state.
	private IApplyOnPickup PickupPayload => (_simState?.Item?.data ?? _simState?.Data) as IApplyOnPickup;

	// True when interacting drinks/reads the item on the spot instead of
	// depositing it (see IApplyOnPickup).
	private bool AppliesOnPickup => PickupPayload != null;

	// Open the boon-pick screen for `player` in place of an inventory deposit.
	// The corpse is spent only if a boon is chosen — the completion callback then
	// removes it from the world. Backing out (or taking damage, which GameClient
	// cancels the screen for) leaves it here to try again. Returns false when
	// this loot offers no boons or no selection UI is available, so the caller
	// falls back to the normal pickup.
	private bool TryOfferBoons(Player player)
	{
		if (!OffersBoons || player == null)
		{
			return false;
		}
		Action<List<BoonData>, Action<BoonData>> start = GameClient.Current?.startUpgradeSelection;
		if (start == null)
		{
			return false;
		}
		var choices = new List<BoonData>(_simState.Item.possibleBoons);
		start.Invoke(choices, chosen =>
		{
			// Guard against the corpse having despawned (chunk unloaded) while the
			// modal was open — the picked boon then just doesn't land.
			if (!GodotObject.IsInstanceValid(this) || _pickedUp)
			{
				return;
			}
			ApplyStatusEffect.ApplyBoon(player, chosen);
			RemovePickedUp();
		});
		return true;
	}

	// Apply an apply-on-pickup item's payload (potion drunk, scroll read) to
	// `player` in place of an inventory deposit, then remove the loot. Returns
	// false when this loot carries no such payload so the caller falls back to
	// the normal deposit. Mirrors TryOfferBoons — the item never enters the
	// inventory. The payload decides whether the pickup is spent (removed) or
	// left in place to retry.
	private bool TryApplyOnPickup(Player player)
	{
		IApplyOnPickup payload = PickupPayload;
		if (payload == null || player == null)
		{
			return false;
		}
		if (payload.ApplyOnPickup(player, GlobalPosition))
		{
			RemovePickedUp();
		}
		return true;
	}

	private void FinalizePickup()
	{
		if (_pickedUp)
		{
			return;
		}
		if (!TryDepositItem(_picker))
		{
			return;
		}
		RemovePickedUp();
	}

	// Shared pickup teardown: latch the sim state removed, play the pickup fx +
	// shrink/lift outro, and drop the body from the world. Deposit (or boon
	// application) is the caller's concern — this only removes the loot.
	private void RemovePickedUp()
	{
		if (_pickedUp)
		{
			return;
		}
		_pickedUp = true;
		// Kill any magnet flight velocity so a fast-seeking pickup stops dead at
		// the player instead of drifting on (gravity is zeroed mid-seek) under the
		// PickedUp shrink animation, which would fling the sprite off the body.
		_seeking = false;
		LinearVelocity = Vector3.Zero;
		AngularVelocity = Vector3.Zero;
		Freeze = true;
		if (_simState != null)
		{
			_simState.PickedUp = true;
			_simState.OnRemovedFromWorld();
		}
		if (_pickupEffectScene != null)
		{
			Fx.Create(_pickupEffectScene, GetParent(), Position);
		}
		_collisionShape.Disabled = true;
		_world?.RemoveEntity(this);
		if (_animationPlayer != null)
		{
			_animationPlayer.AnimationFinished += OnPickedUpFinished;
			_animationPlayer.Play("PickedUp");
		}
		else
		{
			QueueFree();
		}
	}

	// Returns true if the pickup should proceed. World-spawned loot with no
	// attached Item synthesizes a fresh ItemState from Data; legacy entries
	// with neither Data nor Item still pick up cleanly (deposit nothing).
	private bool TryDepositItem(Player player)
	{
		if (_simState == null)
		{
			return true;
		}
		// Per-loot-kind gate — refuse the commit so the runner's
		// completionEvents pass turns into a no-op (e.g. an arrow whose
		// source weapon was dropped between highlight and action commit).
		if (!_simState.CanPickup(player))
		{
			return false;
		}
		// Pickup with no inventory deposit — arrow ammo returns to the
		// source weapon via OnRemovedFromWorld; the Loot still despawns
		// normally.
		if (!_simState.ShouldDepositToInventory())
		{
			return true;
		}
		ItemState toAdd = _simState.Item ?? _simState.Data?.CreateState();
		if (toAdd == null)
		{
			return true;
		}
		if (player?.Inventory == null)
		{
			return false;
		}
		Inventory inv = player.Inventory;

		// Field pickup only takes materials into the backpack. Weapons / armor /
		// equipment can't enter the backpack and aren't auto-equipped from the
		// field — a dedicated equipment-pickup flow will handle those — so leave
		// them lying in the world for now.
		if (toAdd.data == null || !toAdd.data.IsMaterial)
		{
			return false;
		}

		int initial = toAdd.stackCount;
		int added = inv.TryAdd(toAdd);
		if (added < initial)
		{
			return false;
		}
		return true;
	}

	private void OnPickedUpFinished(StringName animName)
	{
		QueueFree();
	}

	public void OnSpawned(Sim sim)
	{
		if (_playSpawnEffects && _spawnEffectScene != null)
		{
			Fx.Create(_spawnEffectScene, GetParent(), Position);
		}
	}

	public static Loot Create(Sim sim, LootSimState data, PackedScene scene, Vector3 impulse = default)
	{
		var instance = scene.Instantiate<Loot>();
		instance.Position = data.WorldPosition;
		instance._simState = data;
		instance._world = sim;
		instance._initialImpulse = impulse;
		instance._playSpawnEffects = true;
		ItemData itemData = data.Item?.data ?? data.Data;
		PackedScene worldModel = (itemData as LootData)?.worldModel;
		if (worldModel != null && instance._modelAnchor != null)
		{
			// 3D-model loot (the elite crown halo) renders its authored mesh
			// instead of the flat sprite. The model brings its own material /
			// x-ray stack and idle spin/bob, so hide the sprite quad to avoid a
			// doubled visual rather than swap its texture.
			if (instance._sprite != null)
			{
				instance._sprite.Visible = false;
			}
			instance._modelAnchor.AddChild(worldModel.Instantiate<Node3D>());
		}
		else
		{
			// Swap the world-pickup sprite to the carried item's icon. Prefer an
			// authored worldSprite (already drawn at chunky-pixel resolution);
			// when none is set, point-downsample the full-res inventory icon to
			// the loot's chunky-pixel budget so it reads at the right size and
			// stays crisp (rather than rendering the big icon 1:1 — oversized —
			// or live-minifying it, which swims as the loot bobs). RegionEnabled
			// =false makes SpriteBase.Apply recompute the quad size + center
			// offset for whatever texture lands here.
			Texture2D texture = itemData?.worldSprite
				?? GetChunkyPickupTexture(itemData?.inventorySprite, instance._pickupMaxChunkyPixels);
			if (instance._sprite != null && texture != null)
			{
				instance._sprite.RegionEnabled = false;
				instance._sprite.Texture = texture;
			}
		}
		sim.AddChild(instance);
		return instance;
	}

	// Cache of point-downsampled world-pickup textures, keyed by the source
	// (inventory) texture. Lets an item with no authored worldSprite render a
	// chunky-reduced copy of its inventory icon without hand-authoring a
	// separate small pickup PNG per item. Pure render derivation — the same
	// spirit as SpriteBase's shared-material cache, not authored game data — so
	// it lives in code rather than a .tres. Reduced once per icon per run.
	private static readonly System.Collections.Generic.Dictionary<Texture2D, Texture2D> _chunkyPickupCache = new();

	// Point-downsample `source` so its longest side is ~maxChunkyPixels,
	// snapping to an integer reduction factor so output texels map cleanly onto
	// the chunky-pixel grid the sprite shader fetches per source texel (a 1:1
	// fetch, so the baked-down icon is as shimmer-free as hand-authored pickup
	// art — no fractional live minification to swim as the loot bobs). Returns
	// `source` unchanged when it's already within budget or can't be decoded.
	private static Texture2D GetChunkyPickupTexture(Texture2D source, int maxChunkyPixels)
	{
		if (source == null || maxChunkyPixels <= 0)
		{
			return source;
		}
		if (_chunkyPickupCache.TryGetValue(source, out Texture2D cached))
		{
			return cached;
		}
		Image img = source.GetImage();
		if (img == null)
		{
			return source;
		}
		int longest = Mathf.Max(img.GetWidth(), img.GetHeight());
		if (longest <= maxChunkyPixels)
		{
			// Already chunky enough (also covers tiny placeholder art) — keep
			// the source so it shares the existing 1:1 fetch path untouched.
			_chunkyPickupCache[source] = source;
			return source;
		}
		// Resize needs an uncompressed image; lossless-imported icons decode
		// fine, but bail to the source if some format can't be decompressed.
		if (img.IsCompressed() && img.Decompress() != Error.Ok)
		{
			_chunkyPickupCache[source] = source;
			return source;
		}
		int factor = Mathf.Max(1, Mathf.RoundToInt((float)longest / maxChunkyPixels));
		int w = Mathf.Max(1, Mathf.RoundToInt(img.GetWidth() / (float)factor));
		int h = Mathf.Max(1, Mathf.RoundToInt(img.GetHeight() / (float)factor));
		img.Resize(w, h, Image.Interpolation.Nearest);
		Texture2D reduced = ImageTexture.CreateFromImage(img);
		_chunkyPickupCache[source] = reduced;
		return reduced;
	}

	public override void _ExitTree()
	{
		if (_simState != null && !_pickedUp)
		{
			_simState.WorldPosition = Position;
		}
		base._ExitTree();
	}
}
