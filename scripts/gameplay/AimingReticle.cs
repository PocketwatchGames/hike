using System.Collections.Generic;
using Godot;

// Aiming overlay attached to the player. Main forward line, two spread lines,
// and ground ring all share aiming_reticle.gdshader.
//
// Geometry strategy: each line is a unit QuadMesh with a billboard vertex
// shader that expands it into a camera-facing ribbon between line_start_world
// and line_end_world. We never touch the mesh transforms; per-frame work is
// just instance-uniform writes. This avoids the BoxMesh "side faces have
// constant cross-section r" problem that prevented shader AA from working
// — a billboard quad's cross-section UV varies smoothly across the visible
// surface, so fwidth+smoothstep AA produces partial-alpha coverage at edges.
// At the inner viewport's chunky resolution that reads as "edge pixels carry
// fractional alpha" — the pixel-art-style AA the reticle wants.
[GlobalClass]
public partial class AimingReticle : Node3D
{
	[Export] private MeshInstance3D _mainLine;
	[Export] private MeshInstance3D _spreadLineLeft;
	[Export] private MeshInstance3D _spreadLineRight;
	[Export] private MeshInstance3D _groundCircle;
	// Small sphere placed at the mainline's endpoint. Shares the aiming reticle
	// material so it inherits the same gradient ramp, depth-based occlusion
	// scaling, and alpha-multiplier fade as the mainline itself.
	[Export] private MeshInstance3D _endKnob;
	// Ballistic-arc preview for Arced aim: a camera-facing ribbon (connected
	// quads) rebuilt each frame along the previewed trajectory (RenderArcRibbon)
	// into a code-created ImmediateMesh (_arcRibbonMesh, assigned in _Ready — a
	// .tscn-authored ImmediateMesh doesn't render). Verts are authored in this
	// node's local space. Null is tolerated — Arced aim then shows no arc.
	[Export] private MeshInstance3D _arcRibbon;

	// Vertical offset of the chest pivot above the player's feet.
	[Export] private float _aimHeight = 1f;
	// World distance from the chest pivot at which the main line's alpha ramp
	// starts (transparent) and finishes (max_alpha).
	[Export] private float _gradientStartDistance = 1f;
	[Export] private float _gradientEndDistance = 3f;
	// Max distance below the line endpoint to search for ground when placing
	// the ground circle. Misses hide both for that frame.
	[Export] private float _maxGroundDropDistance = 30f;
	// Fixed length of each parallel spread marker (centered on the endpoint).
	[Export] private float _spreadLineLength = 1f;
	// When the forward raycast hits a wall, the drop raycast backs off this
	// far along -forward so it starts in open air rather than coplanar with
	// (or fractionally inside) the wall surface.
	[Export] private float _wallBackoff = 0.05f;
	// World-space thicknesses for each ribbon. At the inner viewport's
	// ~13.5 px/m density these map to ~2-inner-pixel-wide lines; the cross-
	// section AA fades the outermost ~0.5 px on each side to partial alpha.
	[Export] private float _mainLineWidth = 0.15f;
	[Export] private float _spreadLineWidth = 0.12f;
	// Ground ring radii in WORLD METERS (mesh-local coords match world units
	// at scale 1, so authoring values directly here works). The unlocked
	// outer/inner pair defines the base ring AND the line thickness used
	// for every ring size — when the bow locks onto a mob the outer grows
	// to the mob's collision radius and the inner trails it by the same
	// `outer − inner` thickness so the band's width stays constant. The
	// PlaneMesh on the ground circle in the scene must be sized large
	// enough to contain the largest outer radius you'll ever lerp to
	// (a typical mob clearanceRadius is ~0.4m, biggest creatures may be
	// well over 1m).
	[Export] private float _groundRingOuterRadius = 0.2f;
	[Export] private float _groundRingInnerRadius = 0.16f;
	// Tint multiplied into the ground ring shader while locked on a mob.
	// Red by default; white (Colors.White) would disable the tint.
	[Export] private Color _groundRingLockedColor = new(1f, 0.25f, 0.2f, 1f);
	// Multiplier applied to the mob's clearanceRadius when sizing the locked
	// ring. clearanceRadius is the tight body half-width used for path
	// clearance; the visible ring reads better when it sits a bit outside the
	// silhouette, so we scale it up here.
	[Export] private float _groundRingLockedRadiusMultiplier = 3f;
	// Extra alpha scale applied to the ground ring only while NOT locked on
	// a mob. Lets the unlocked ring read as quieter without affecting the
	// mainline / knob / spread lines. Snaps in sync with the red-tint switch
	// when lock state changes (the radius transition is the smooth one).
	[Export] private float _groundRingUnlockedAlphaScale = 0.6f;
	// Alpha scale applied to the ground ring while Positional aim is active.
	// Positional mode hides the main beam / spread / knob and uses the ring
	// alone to telegraph the AoE footprint, so it reads as a stronger UI
	// element than the directional-mode ring — 1.0 (fully opaque) by default.
	[Export] private float _groundRingPositionalAlphaScale = 1.0f;
	// Ease-out speed for the VISUAL-ONLY vertical smoothing of the directional
	// beam's endpoint. The underlying aim/fire is unchanged — this only eases the
	// rendered beam tip's Y so a lock onto an elevated target (a perched bird
	// overhead) sweeps up to it instead of snapping. Each frame the tip closes an
	// exp-decay fraction (1 - exp(-dt * this)) of the remaining distance, so the
	// convergence is frame-rate independent and never overshoots. Larger =
	// snappier; 0 freezes the tip. At 16 the tip settles (~99% closed) in ~0.3s.
	[Export] private float _reticleVerticalEaseSpeed = 16.0f;

	// Ground-ring terrain draping. The ground circle's mesh is subdivided and its
	// vertices are displaced in the shader to the ground height sampled from a
	// RETICLE_PATCH_RES² grid of voxel-surface heights resolved here each frame
	// (UpdateGroundUndulation). When disabled (or no voxel world), the shader
	// keeps the flat plane.
	[Export] private bool _undulationEnabled = true;
	// World-space size of the square height-patch sampled under the ground ring.
	// Must EXCEED the ground-circle PlaneMesh footprint (4x4) by at least a cell,
	// because the patch origin is snapped to the sample grid (see
	// UpdateGroundUndulation) and so drifts up to one cell off-center — the extra
	// margin keeps every drawn vertex inside the sampled patch.
	[Export(PropertyHint.Range, "1,16,0.5")] private float _undulationPatchWorldSize = 6f;
	// Max voxels scanned up/down from the player's level when resolving each
	// column's surface height. Bounds the per-frame voxel reads.
	[Export(PropertyHint.Range, "4,64,1")] private int _undulationMaxScanVoxels = 24;

	// Positional cursor sweep speed — GAMEPAD only — as a fraction of the active
	// tier's range per second, scaled by the analog stick deflection (half-pushed
	// stick = half speed; velocity, not a ramp). At full deflection the cursor covers
	// range * _gamepadPositionalSpeedFraction m/s. Mouse aim ignores this — it adds a
	// world-unit motion delta straight to the cursor (range-independent).
	[Export(PropertyHint.Range, "0.1,8,0.1")] private float _gamepadPositionalSpeedFraction = 2f;
	// How long a gamepad positional cursor lingers after aim stops before it
	// resets. The cursor stays visible and fades to zero across this window;
	// moving the aim stick or firing the ranged weapon refills it. A follow-up
	// attack within the window fires at the held cursor; after it expires the
	// next aim re-seeds (first deflection → 50% range). Mouse aim ignores this.
	[Export(PropertyHint.Range, "0,30,0.5")] private float _gamepadPositionalPersistSeconds = 6f;
	// Alpha the reticle dims to (relative to its normal alpha) while the ranged
	// weapon is on cooldown but the player is still aiming. The cursor keeps
	// tracking input so the player can pre-aim the next shot; it just reads as
	// "not ready" rather than vanishing.
	[Export(PropertyHint.Range, "0,1,0.05")] private float _cooldownAlphaScale = 0.35f;

	// GAMEPAD positional / arced target-snap. When the throw's landing point enters a
	// targetable mob's lock-on range (clearanceRadius + _positionalSnapReachMeters) a
	// red ring marks the mob; ease the stick to lock. A locked aim eases onto the mob's
	// feet, follows it (even out of sight, as a memory silhouette), and breaks on a firm
	// stick deflection. Mouse positional aim is unaffected (no snapping).
	// Stick deflection at/below which the cursor counts as "stopped" so a hovered
	// target locks. This is the lock-ON gate: ease the stick below this while the
	// reticle is over a target (or hold it neutral while walking the cursor on) and
	// it locks. Higher = lock while the stick is still partly deflected (forgiving);
	// 0 = must be fully neutral.
	[Export(PropertyHint.Range, "0,0.5,0.01")] private float _positionalLockStopDeadzone = 0.3f;
	// Stick deflection at/above which a HELD lock breaks and free aiming resumes — a
	// firm push or spin in ANY direction. Sits above the lock deadzone with a gap so
	// the lock doesn't flicker: ease below the deadzone to lock, push past this to break.
	[Export(PropertyHint.Range, "0.1,1,0.05")] private float _positionalLockBreakDeflection = 0.7f;
	// Ease-out speed for the locked aim cursor settling onto the target (and springing
	// back after a stick nudge / release). ~18 settles in ~0.25s; higher = snappier.
	[Export(PropertyHint.Range, "0,40,1")] private float _positionalLockEaseSpeed = 18f;
	// MOUSE equivalent of the lock-on deadzone: world-meters-per-second cursor speed
	// at/below which a hovered target locks (the mouse "settled" on it). The mouse has
	// no stick deflection, so it gates on how fast the cursor is moving instead.
	[Export(PropertyHint.Range, "0,30,0.5")] private float _mouseLockSettleSpeed = 6f;
	// Lock-on / break-out REACH in world meters, measured outward from the mob's body
	// (clearanceRadius). The reticle snaps onto / holds a target while it's within
	// clearanceRadius + this of the mob's feet — independent of the drawn ring size.
	[Export(PropertyHint.Range, "0,8,0.25")] private float _positionalSnapReachMeters = 2f;
	// Extra meters BEYOND the weapon's range at which a held lock finally releases.
	// Walking a locked target out of normal range keeps the lock (so you can close
	// back in or reposition); only past range + this distance, or on losing line of
	// sight to the target, does the lock break.
	[Export(PropertyHint.Range, "0,30,0.5")] private float _positionalLockReleaseExtraRange = 8f;
	// If true, a held lock also breaks when line of sight to the target is lost (it
	// ducks behind cover). Default OFF: ARCED weapons (the thrown bomb) lob OVER
	// cover, so requiring LOS would wrongly drop locks on targets you can still hit.
	// Enable for direct-fire positional weapons that need a clear line.
	[Export] private bool _positionalLockBreaksOnLineOfSightLoss = false;
	// World-meter radius searched around the cursor for snap targets. Must exceed the
	// largest mob snap radius (clearanceRadius + _positionalSnapReachMeters).
	[Export(PropertyHint.Range, "0.5,12,0.5")] private float _positionalSnapSearchRadius = 5f;

	// Arced aim: world-space width of the previewed arc ribbon, in meters.
	[Export(PropertyHint.Range, "0.02,0.5,0.01")] private float _arcRibbonWidth = 0.12f;
	// Arced aim: ribbon alpha fades IN over arc length [fadeInStart, fadeInEnd]
	// from the launch point (transparent at the thrower's hand), and fades OUT over
	// the last fadeOutDistance meters before the predicted landing. Per-vertex,
	// measured along the simulated path.
	[Export(PropertyHint.Range, "0,5,0.1")] private float _arcFadeInStart = 1f;
	[Export(PropertyHint.Range, "0,6,0.1")] private float _arcFadeInEnd = 2f;
	[Export(PropertyHint.Range, "0.1,5,0.1")] private float _arcFadeOutDistance = 1f;

	// Linear ease from current outer radius to the new target whenever the
	// target changes (lock on/off, or the targeted mob's clearance differs
	// from the previous one). Inner derives from outer so a single lerp
	// drives the whole ring.
	const float RingTransitionSeconds = 0.15f;

	// Fade duration when the ranged weapon becomes unavailable mid-aim
	// (cooldown after firing, ammo runs out, weapon swapped, etc.). Same
	// constant for fade-in so reticle pop-on is symmetric and not jarring.
	const float FadeDurationSeconds = 0.15f;

	// Fraction of the active tier's range the gamepad cursor jumps to on the
	// first stick deflection after a reset (see AdvanceGroundCursorXZ's seed). 0.5 =
	// halfway out toward the disk edge in the pressed direction.
	const float GamepadPositionalSeedFraction = 0.5f;

	// Ground-ring height-patch resolution. MUST match RETICLE_PATCH_RES in
	// aiming_reticle.gdshader (and the shader's reticle_heights[] array size,
	// which is this squared).
	const int UndulationPatchRes = 16;
	const int UndulationCellCount = UndulationPatchRes * UndulationPatchRes;

	Player _player;
	// Last forward-raycast clamp distance from an active update. Reused as
	// the line length while the reticle is fading out, since the whole point
	// of being unavailable is that we shouldn't be paying for a new raycast.
	float _lastLineLength;
	// Cached "we hit a mob this update" flag so the fade-out path keeps the
	// ground ring in its locked styling instead of snapping back to the
	// default radii while alpha is fading to zero.
	bool _lastMobTargeted;
	// Cached target outer radius for the fade-out path (mob's clearanceRadius
	// when the lock was acquired, base radius otherwise).
	float _lastMobTargetOuter;
	// Cached Positional tier's authored AoE radius — held across fade-out
	// so the ring doesn't snap back to the default outer radius mid-fade.
	float _lastPositionalRadius;
	// 0..1, lerped each frame toward target (1 while available, 0 otherwise).
	// Drives the alpha_multiplier instance uniform on every reticle mesh.
	float _currentAlpha;
	// Lerp state for the ground ring's outer radius. Linear interpolation
	// from `_ringSource` to `_ringTarget` over RingTransitionSeconds,
	// restarted whenever the target changes (lock on/off, or mob swap).
	float _currentOuterRadius;
	float _ringSource;
	float _ringTarget;
	float _ringElapsed;

	// Canonical world-space aim cursor (the ground circle's position).
	// Directional aim writes the dropped endpoint of the forward raycast;
	// Positional aim integrates Player.ConsumeAimInput() into this each frame.
	// Held continuous across mid-charge mode flips so the ground circle
	// glides instead of teleporting between modes. Exposed to downstream
	// consumers (positional fire handlers) via AimWorldPosition.
	Vector3 _cursorWorldPos;
	// Goes false on aim-off so the next aim session re-seeds rather than
	// reusing a stale cursor from minutes ago. Downstream consumers must
	// check HasAimWorldPosition before reading the position.
	bool _cursorValid;
	// True while the held cursor came from a gamepad positional aim — gates the
	// post-aim persistence window in _Process. Cleared by directional / mouse
	// updates and when the window expires.
	bool _positionalPersist;
	// Seconds since the gamepad positional cursor last saw activity (aiming,
	// stick movement, or a ranged attack). Counts up only during the persistence
	// window; reaching _gamepadPositionalPersistSeconds resets the cursor.
	float _persistTimer;
	// Character-relative XZ offset of the cursor from the player, captured each
	// active gamepad positional frame. The persistence window re-derives the
	// world cursor from this + the current player position each frame, so a
	// held cursor follows the player around instead of staying pinned in world
	// space (character-relative persistence).
	Vector2 _persistOffset;

	// GAMEPAD positional target-snap state.
	// Free stick-driven reticle in world XZ — the raw cursor the snap logic tests for a
	// target. Distinct from _cursorWorldPos, which the snap moves to the engaged mob's
	// feet; this keeps integrating underneath so a hover has somewhere to resume from.
	Vector2 _freeCursorXZ;
	bool _freeCursorValid;
	// Mob currently under the reticle (hover) or locked. Null when free.
	Mob _positionalTargetMob;
	// True once a hovered target has been locked (stick came to rest on it). On lock
	// the aim eases onto the feet; a firm stick deflection breaks it.
	bool _positionalLocked;
	// Active positional/arced tier range, cached so the post-aim persistence window
	// can range-check a surviving lock against the player's current position.
	float _lastPositionalRange;
	// REAL (not visual) aim cursor while locked, world XZ: on lock it eases to the
	// target over ~0.25s, the stick still pushes it, and releasing springs it back.
	// _cursorWorldPos is taken from this so the throw fires along the eased line.
	Vector2 _lockCursorXZ;
	// While engaged, the red ground ring is pinned to the target's feet (_ringWorldPos)
	// independent of the aim cursor, so the ring marks the target while the arc eases.
	bool _ringPinned;
	Vector3 _ringWorldPos;
	List<Mob> _snapScratch;

	// Reused per-frame buffer of voxel-surface heights pushed to the ground
	// ring's shader for terrain draping. Allocated once; refilled in
	// UpdateGroundUndulation.
	readonly float[] _undulationHeights = new float[UndulationCellCount];
	// Cached ground-circle shader material — the height array is a material
	// (not instance) uniform, so it's set here. Only the ground-ring vertex path
	// reads it, so sharing the material with the beam/spread lines is harmless.
	ShaderMaterial _groundMaterial;
	// Cache key for the snapped height patch — skip the voxel rescan while the
	// snapped grid origin and the player's voxel level are unchanged (static
	// terrain → identical field). _undulationValid guards the first fill.
	bool _undulationValid;
	float _lastPatchOriginX;
	float _lastPatchOriginZ;
	int _lastAnchorVoxelY;
	// Last frame's resolved aim type — used to detect Pos ↔ Dir transitions
	// so we can snap the player's facing toward the cursor on Pos → Dir
	// (the directional raycast THIS SAME FRAME picks up the new yaw).
	EAimType _lastAimType = EAimType.Directional;

	// Smoothed Y of the directional beam's rendered endpoint (visual only —
	// see _reticleVerticalSmoothTime). `_endYValid` goes false whenever the
	// reticle fully hides so the next aim session snaps to its first endpoint
	// rather than gliding up from a stale value.
	float _smoothedEndY;
	bool _endYValid;

	// _arcPoints is the simulated arced-throw trajectory the dotted preview draws,
	// rebuilt each aiming frame by SolveArcForward. The throw itself recomputes
	// the same fixed-shape hump from the event's authored fields
	// (ItemEventHandlers.TrySolveArcLaunch), so nothing passes between them.
	// The arc ribbon's mesh, created in code and assigned to _arcRibbon in _Ready.
	// Authoring an ImmediateMesh as a .tscn sub-resource loads an instance that
	// accepts surface writes but never registers geometry with the renderer, so
	// the ribbon draws nothing — build it at runtime instead.
	ImmediateMesh _arcRibbonMesh;
	readonly List<Vector3> _arcPoints = new(ArcSimMaxSteps + 8);

	// Preview path simulation: timestep (matches the projectile's physics tick so
	// the predicted bounces line up with the real throw), step cap, the nudge off
	// a surface after a bounce, and the speed below which a bounced projectile is
	// treated as settled (so we stop simulating once it has rolled to rest).
	const float ArcSimStep = 1f / 60f;
	const int ArcSimMaxSteps = 150;
	const float ArcSimSurfaceOffset = 0.03f;
	const float ArcSettleSpeed = 0.5f;

	// World position currently being aimed at — the ground circle anchor
	// (Positional cursor, or an Arced throw's predicted end point). Read by
	// positional fire handlers (AoE drop target) at activation. Always check
	// HasAimWorldPosition first; the value is stale when false.
	public Vector3 AimWorldPosition => _cursorWorldPos;
	public bool HasAimWorldPosition => _cursorValid;

	public void Initialize(Player player)
	{
		_player = player;
	}

	public override void _Ready()
	{
		// Per-line width is set once — the shader checks line_width_world > 0
		// to switch into billboard mode. The ground circle leaves it at 0 so
		// the shader takes the standard MODEL_MATRIX path for the ring.
		SetLineWidth(_mainLine, _mainLineWidth);
		SetLineWidth(_spreadLineLeft, _spreadLineWidth);
		SetLineWidth(_spreadLineRight, _spreadLineWidth);

		// Texture-vs-procedural is auto-detected from the shared material:
		// if the .tres assigns a `line_texture`, the main line samples it; if
		// the slot is empty we fall back to the fwidth+smoothstep coverage AA.
		// Spread lines stay on procedural AA regardless (the main-line
		// texture is a styled element, not a global look).
		if (_mainLine != null)
		{
			bool hasTexture = false;
			if (_mainLine.GetActiveMaterial(0) is ShaderMaterial mat)
			{
				hasTexture = mat.GetShaderParameter("line_texture").As<Texture2D>() != null;
			}
			_mainLine.SetInstanceShaderParameter("use_line_texture", hasTexture ? 1f : 0f);
		}

		// Lerp state starts at the base radius so the first render frame
		// doesn't grow into the unlocked size. RenderReticle drives further
		// changes whenever the target shifts (lock on/off / mob swap).
		_currentOuterRadius = _groundRingOuterRadius;
		_ringSource = _groundRingOuterRadius;
		_ringTarget = _groundRingOuterRadius;
		_ringElapsed = RingTransitionSeconds;
		_lastMobTargetOuter = _groundRingOuterRadius;

		if (_groundCircle != null)
		{
			// Initial radii — RenderReticle drives these per-frame. PlaneMesh
			// size is authored on the scene and must be large enough to
			// contain the largest outer radius the ring will ever use,
			// plus an AA pad, because ring_outer_radius is in mesh-local
			// coords (length(VERTEX.xz)) — radii beyond the mesh edge get
			// clipped by the rasterizer.
			_groundCircle.SetInstanceShaderParameter("ring_outer_radius", _groundRingOuterRadius);
			_groundCircle.SetInstanceShaderParameter("ring_inner_radius", _groundRingInnerRadius);
			_groundMaterial = _groundCircle.GetActiveMaterial(0) as ShaderMaterial;
		}

		// Build the arc ribbon's mesh in code — a .tscn-serialized ImmediateMesh
		// never registers geometry with the renderer (see _arcRibbonMesh).
		if (_arcRibbon != null)
		{
			_arcRibbonMesh = new ImmediateMesh();
			_arcRibbon.Mesh = _arcRibbonMesh;
		}
	}

	static void SetLineWidth(MeshInstance3D mesh, float width)
	{
		if (mesh == null) { return; }
		mesh.SetInstanceShaderParameter("line_width_world", width);
	}

	public override void _Process(double delta)
	{
		if (_player == null)
		{
			return;
		}

		float dt = (float)delta;
		float step = dt / FadeDurationSeconds;

		// Snapshot the device-tagged aim input ONCE per frame. This is the single
		// consume point for the mouse motion-delta accumulator (every other read
		// is gamepad, which is idempotent), so reading it here — before any
		// branch — guarantees the delta is cleared exactly once whether we end up
		// active, persisting, or fading out.
		AimInput aim = _player.ConsumeAimInput();

		// Active = aiming with a ranged weapon equipped. Cooldown does NOT drop
		// us out of active — the cursor keeps tracking input so the player can
		// pre-aim the next shot; it just dims to _cooldownAlphaScale (a shot can't
		// start mid-cooldown anyway, so this is purely the visual "not ready"
		// state, and stops the reticle from vanishing the instant a shot fires).
		bool active = _player.IsAiming && IsRangedWeaponEquipped();

		if (active)
		{
			// On cooldown OR out of ammo → "not ready": keep the cursor tracking
			// (ammo recharges over time, so the player pre-aims the next shot) but
			// dim the reticle so it reads as unavailable rather than vanishing.
			bool notReady = IsRangedWeaponOnCooldown() || IsRangedWeaponOutOfAmmo();
			float targetAlpha = notReady ? _cooldownAlphaScale : 1f;
			_currentAlpha = Mathf.MoveToward(_currentAlpha, targetAlpha, step);
			UpdateReticle(aim, dt);
			// Actively aiming counts as activity — hold the persistence timer full.
			_persistTimer = 0f;
		}
		else if (_cursorValid && _positionalPersist)
		{
			// Aim stopped — the held cursor persists. A surviving target lock keeps
			// tracking the mob's feet; otherwise it reverts to the player-relative offset.
			// Gamepad positional persistence window: aim stopped, but the cursor
			// sticks around so a quick follow-up ranged attack still fires at it
			// (it stays valid → HasAimWorldPosition true). It remains visible and
			// fades linearly to zero across _gamepadPositionalPersistSeconds. Moving the
			// aim stick is "activity" that refills the timer (re-brightening the
			// reticle); a ranged attack re-enters the `active` branch, which also
			// refills it. Only after the full window with neither does the cursor
			// reset, so the next aim re-seeds (first deflection → 50% range).
			// Run the SAME snap resolver as active aiming so walking the held cursor
			// onto a target (left-stick movement) locks on exactly like resting the aim
			// stick on it. When not locked the held cursor is player-relative (moves with
			// you); the resolver then follows / breaks the lock itself.
			Vector3 pp = _player.GlobalPosition;
			if (!_positionalLocked)
			{
				_freeCursorXZ = new Vector2(pp.X + _persistOffset.X, pp.Z + _persistOffset.Y);
				_freeCursorValid = true;
			}
			bool lockedOnMob = ResolvePositionalSnap(aim, pp, _lastPositionalRange, dt, out float ringOuter);

			// A held lock or stick activity keeps the window from expiring.
			bool activity = lockedOnMob
				|| (aim.Device == InputDevice.EDevice.Gamepad && aim.Value.LengthSquared() > 0f);
			_persistTimer = activity ? 0f : _persistTimer + dt;

			if (_persistTimer >= _gamepadPositionalPersistSeconds)
			{
				_cursorValid = false;
				_positionalPersist = false;
				ResetPositionalSnap();
				_currentAlpha = Mathf.Max(_currentAlpha - step, 0f);
			}
			else
			{
				// Held lock stays full bright (timer held at 0); otherwise fade over the window.
				_currentAlpha = _gamepadPositionalPersistSeconds > 0f
					? Mathf.Max(0f, 1f - _persistTimer / _gamepadPositionalPersistSeconds)
					: 0f;
				Vector3 chestWorld = pp + Vector3.Up * _aimHeight;
				// Re-drop the held cursor onto the ground (a lock has already pinned
				// + dropped it onto the feet), then publish. Only Positional aim
				// enters this window — Arced is facing-driven and never persists.
				if (!lockedOnMob)
				{
					DropCursorToGround(chestWorld);
				}
				PublishPositionalFrame(_lastAimType, lockedOnMob, ringOuter, _lastPositionalRadius, pp, dt);
			}
		}
		else
		{
			_currentAlpha = Mathf.Max(_currentAlpha - step, 0f);
			// Aim turned off with no persistence (mouse / directional) —
			// invalidate the cursor so the next aim session re-seeds instead of
			// jumping back to a stale point. `_lastAimType` stays at its
			// last-active value so the fade-out renders with the same ring
			// radius / alpha scale the player was just seeing.
			_cursorValid = false;
			ResetPositionalSnap();
			if (_currentAlpha > 0f)
			{
				// Fade-out path: keep rendering at the cached range so we
				// don't pay for the forward raycast while the weapon is
				// unavailable. Chest / forward still update each frame so
				// the fading reticle follows the player rather than
				// hovering in dead space.
				RenderReticle(_lastAimType, _lastLineLength, clippedAtSurface: false, mobTargeted: _lastMobTargeted, mobTargetOuter: _lastMobTargetOuter, positionalRadius: _lastPositionalRadius, dt: dt);
			}
		}

		if (_currentAlpha > 0f)
		{
			ApplyAlpha(_currentAlpha);
		}
		else
		{
			HideReticle();
		}
	}

	// Aiming-off path: zero every mesh's Visible so a transition from aiming
	// to not-aiming clears any spread / ring state left over from the
	// last visible frame. No raycasts, no uniform writes — cheap.
	void HideReticle()
	{
		if (_mainLine != null) { _mainLine.Visible = false; }
		if (_spreadLineLeft != null) { _spreadLineLeft.Visible = false; }
		if (_spreadLineRight != null) { _spreadLineRight.Visible = false; }
		if (_groundCircle != null) { _groundCircle.Visible = false; }
		if (_endKnob != null) { _endKnob.Visible = false; }
		if (_arcRibbon != null)
		{
			_arcRibbon.Visible = false;
			_arcRibbonMesh?.ClearSurfaces();
		}
		// Next aim session re-seeds the smoothed beam Y from its first endpoint
		// instead of gliding up from wherever the last session left it.
		_endYValid = false;
		ResetPositionalSnap();
	}

	// Mirrors the gate Player uses in TryStartWeaponAction, minus cooldown AND
	// ammo: a weapon must be equipped with an action profile, and the action
	// runner must not be running a DIFFERENT slot's action (charging the bow
	// itself is fine — that's the reticle's whole point of existing during
	// charge). Cooldown (IsRangedWeaponOnCooldown) and ammo (IsRangedWeaponOutOfAmmo)
	// are checked separately so the reticle stays visible-but-dimmed through both
	// — ammo recharges over time, so the player keeps pre-aiming the next shot.
	bool IsRangedWeaponEquipped()
	{
		if (_player?.Inventory == null) { return false; }
		WeaponState weapon = _player.Inventory.GetWeapon(EInventorySlot.WeaponRanged);
		if (weapon?.data?.actionProfile == null) { return false; }
		ActionRunner runner = _player.Runner;
		if (runner != null && runner.IsBusy && runner.Current.context.sourceSlot != EInventorySlot.WeaponRanged)
		{
			return false;
		}
		return true;
	}

	// True when the equipped ranged weapon is mid-cooldown (just fired). The
	// reticle dims rather than hiding during this window.
	bool IsRangedWeaponOnCooldown()
	{
		WeaponState weapon = _player?.Inventory?.GetWeapon(EInventorySlot.WeaponRanged);
		return weapon != null && weapon.cooldownExpireMs > _player.GameTimeMs;
	}

	// True when the equipped ammo-consuming weapon is empty. Like cooldown, the
	// reticle dims rather than hiding so the player can pre-aim while ammo
	// recharges.
	bool IsRangedWeaponOutOfAmmo()
	{
		WeaponState weapon = _player?.Inventory?.GetWeapon(EInventorySlot.WeaponRanged);
		return weapon?.data != null && weapon.data.maxAmmo > 0 && weapon.ammo <= 0;
	}

	// Fresh active update: resolve aim type → update cursor + cached state → render.
	// Handles the Pos ↔ Dir transition by snapping player yaw toward the cursor
	// on Pos → Dir BEFORE running the directional raycast, so the same-frame
	// forward fires through the previous positional cursor.
	void UpdateReticle(AimInput aim, float dt)
	{
		EAimType aimType = ResolveActiveAimType();
		float maxRange = ResolveActiveAimRange();

		// Mode flip: leaving Positional for a facing-driven mode (Directional /
		// Arced) needs the body to face the cursor first so ActorForward this
		// frame reflects the previously-aimed direction. Entering Positional
		// needs no explicit seed — the cursor already sits at the last ground
		// point from the previous tick.
		if (_cursorValid && aimType != EAimType.Positional && _lastAimType == EAimType.Positional)
		{
			_player.SnapAimYawToward(_cursorWorldPos);
		}

		if (aimType == EAimType.Arced)
		{
			UpdateArced(maxRange, dt);
		}
		else if (aimType == EAimType.Directional)
		{
			UpdateDirectional(maxRange, dt);
		}
		else
		{
			UpdateGroundAim(aim, maxRange, dt);
		}
		_lastAimType = aimType;
	}

	// Arced (thrown) aim: facing-driven like Directional — the aim stick / mouse
	// steers the body, and the throw flies a fixed-shape hump along the facing
	// whose horizontal reach is `maxRange` (the charge-scaled projectileMaxRange,
	// via ResolveActiveAimRange). The ribbon previews the exact trajectory
	// (bounces included), ending where the throw ends — it alone is the
	// telegraph; arced draws no ground ring or forward beam.
	void UpdateArced(float maxRange, float dt)
	{
		_positionalPersist = false;
		_ringPinned = false;
		Vector3 playerPos = _player.GlobalPosition;
		// Origin matches DoProjectile's launch (ActorWorldPosition + 1m) so the
		// previewed hump and the real throw share a starting point.
		Vector3 chestWorld = playerPos + Vector3.Up * _aimHeight;
		// Horizontal bearing off the body facing (ActorForward folds auto-aim
		// pitch; the hump's vertical shape is authored, so only yaw matters).
		Vector3 bearing = HorizontalBearing(chestWorld, chestWorld + _player.ActorForward.Normalized());
		SolveArcForward(chestWorld, bearing, maxRange, out Vector3 landing);
		// Cursor = the predicted end point: the seed for a Positional tier
		// taking over on a mid-charge mode flip (the ribbon alone renders the
		// telegraph — arced draws no ground ring).
		_cursorWorldPos = landing;
		_cursorValid = true;
		PublishPositionalFrame(EAimType.Arced, engaged: false, _groundRingOuterRadius, positionalRadius: 0f, playerPos, dt);
	}

	void UpdateDirectional(float maxRange, float dt)
	{
		// Directional aim doesn't use the gamepad positional persistence window.
		_positionalPersist = false;
		_ringPinned = false;
		Vector3 chestWorld = _player.GlobalPosition + Vector3.Up * _aimHeight;
		// Use the player's pitched forward so the main beam, spread, and
		// ground circle anchor all follow the same elevation the next shot
		// will fire along (Player.ActorForward folds in the auto-aim pitch).
		Vector3 forward = _player.ActorForward.Normalized();
		// Weapon drives the mob-lock filter via Mob.CanTarget so the visual
		// telegraph matches whatever weapon-specific rules the assist uses.
		WeaponData weaponData = _player.Inventory?.GetWeapon(EInventorySlot.WeaponRanged)?.data;

		float lineLength = maxRange;
		bool clippedAtSurface = false;
		bool mobTargeted = false;
		float mobTargetOuter = _groundRingOuterRadius;
		if (TryRaycastForward(chestWorld, forward, maxRange, weaponData, out Vector3 forwardHit, out Mob hitMob))
		{
			if (hitMob != null)
			{
				// Extend "range" to the mob's body center along the aim line.
				// This puts the drop ray's origin above the mob's feet instead
				// of at the front of its hurtbox, so the ground circle lands
				// directly under the creature. Project onto `forward` so a
				// mob that's slightly off-axis doesn't pull the endpoint
				// sideways — we want the line to stay on the aim direction
				// and just travel to the depth of the mob center.
				float centerProjection = (hitMob.AimCenter - chestWorld).Dot(forward);
				lineLength = Mathf.Clamp(centerProjection, 0f, maxRange);
				mobTargeted = true;
				// Ground ring grows to sit just outside the mob's footprint.
				// `clearanceRadius` is the tight half-width path uses for
				// body clearance; the visible ring reads better scaled up
				// (designer knob) so it haloes the silhouette rather than
				// hugging it.
				if (hitMob.mobData != null)
				{
					mobTargetOuter = hitMob.mobData.clearanceRadius * _groundRingLockedRadiusMultiplier;
				}
			}
			else
			{
				lineLength = (forwardHit - chestWorld).Length();
				clippedAtSurface = true;
			}
		}
		_lastLineLength = lineLength;
		_lastMobTargeted = mobTargeted;
		_lastMobTargetOuter = mobTargetOuter;

		// Cursor = the ground-dropped endpoint, so Positional can seed from
		// here on the next mode flip and downstream handlers always read a
		// real ground point. Drop from the (possibly clipped) endpoint with
		// the wall-backoff applied so we don't start the ray inside the
		// surface we just hit. Falls back to the un-dropped endpoint if the
		// drop misses (player aiming off a cliff into space).
		Vector3 dropOriginWorld = chestWorld + forward * lineLength;
		if (clippedAtSurface)
		{
			dropOriginWorld -= forward * _wallBackoff;
		}
		if (TryRaycastDown(dropOriginWorld, _maxGroundDropDistance, out Vector3 dropHit))
		{
			_cursorWorldPos = dropHit;
		}
		else
		{
			_cursorWorldPos = dropOriginWorld;
		}
		_cursorValid = true;

		// positionalRadius unused on the Directional path (RenderReticle
		// ignores it when aimType != Positional); pass 0 to make that
		// explicit at the call site.
		RenderReticle(EAimType.Directional, lineLength, clippedAtSurface, mobTargeted, mobTargetOuter, positionalRadius: 0f, dt: dt);
	}

	// Ground-target (Positional) aim: a free cursor picking an AoE drop point.
	// Gamepad gets target snap (hover ring + ease-to-lock, following the mob);
	// mouse keeps the free cursor.
	void UpdateGroundAim(AimInput aim, float maxRange, float dt)
	{
		_lastPositionalRange = maxRange;
		Vector3 playerPos = _player.GlobalPosition;
		Vector3 chestWorld = playerPos + Vector3.Up * _aimHeight;

		// Both devices snap / lock identically; only the cursor advance differs (the
		// stick's rate vs the mouse's world-unit delta), handled inside. Mouse locking
		// is gated by the mouse_target_lock cvar; off → the mouse is a plain free cursor.
		bool engaged;
		float engagedRing = _groundRingOuterRadius;
		if (aim.Device == InputDevice.EDevice.Gamepad || CVars.mouseTargetLock.Value)
		{
			engaged = UpdateCursorWithSnap(aim, playerPos, chestWorld, maxRange, dt, out engagedRing);
		}
		else
		{
			ResetPositionalSnap();
			AdvanceGroundCursorXZ(aim, playerPos, chestWorld, maxRange, dt);
			DropCursorToGround(chestWorld);
			engaged = false;
		}

		// Face the body toward the cursor so the sprite / ActorForward point at the
		// drop target.
		_player.SnapAimYawToward(_cursorWorldPos);

		// AoE footprint radius for the active tier — fed to the ring-radius lerp.
		ItemAction tier = ResolveActiveTier(out _);
		float positionalRadius = Mathf.Max(0f, tier?.positionalAreaRadius ?? _groundRingOuterRadius);

		PublishPositionalFrame(EAimType.Positional, engaged, engagedRing, positionalRadius, playerPos, dt);
	}

	// Shared tail for ground-target frames (active update AND the persistence window):
	// caches the state the fade-out path reuses, captures the player-relative persist
	// offset, and renders. Cursor resolution (snap / arc / ground drop) is the caller's.
	void PublishPositionalFrame(EAimType aimType, bool engaged, float engagedRing, float positionalRadius, Vector3 playerPos, float dt)
	{
		float dx = _cursorWorldPos.X - playerPos.X;
		float dz = _cursorWorldPos.Z - playerPos.Z;
		_lastLineLength = Mathf.Sqrt(dx * dx + dz * dz);
		_lastMobTargeted = engaged;
		_lastMobTargetOuter = engaged ? engagedRing : _groundRingOuterRadius;
		_lastPositionalRadius = positionalRadius;
		// Character-relative offset the persistence window re-anchors the held cursor by.
		_persistOffset = new Vector2(dx, dz);
		RenderReticle(aimType, _lastLineLength, clippedAtSurface: false, mobTargeted: engaged, mobTargetOuter: _lastMobTargetOuter, positionalRadius: positionalRadius, dt: dt);
	}

	// Advances the free reticle from input (stick rate / mouse delta), then runs the
	// shared snap resolver. Returns true (engaged) with the visible lock-ring radius
	// in `ringOuter` when pinned to a target.
	bool UpdateCursorWithSnap(AimInput aim, Vector3 playerPos, Vector3 chestWorld, float maxRange, float dt, out float ringOuter)
	{
		// Advance the FREE cursor only while NOT locked. A held lock advances per-device
		// inside the resolver (gamepad nudges the lock cursor, mouse moves the free
		// probe), keeping the range disk out of it so the clamp can't false-break an
		// out-of-range lock.
		if (!_positionalLocked)
		{
			if (_freeCursorValid)
			{
				_cursorWorldPos.X = _freeCursorXZ.X;
				_cursorWorldPos.Z = _freeCursorXZ.Y;
			}
			AdvanceGroundCursorXZ(aim, playerPos, chestWorld, maxRange, dt);
			_freeCursorXZ = new Vector2(_cursorWorldPos.X, _cursorWorldPos.Z);
			_freeCursorValid = true;
		}

		bool engaged = ResolvePositionalSnap(aim, playerPos, maxRange, dt, out ringOuter);
		if (!engaged)
		{
			DropCursorToGround(chestWorld);
		}
		return engaged;
	}

	// THE single snapping path. Given _freeCursorXZ (already advanced however the
	// cursor moved this frame — the right stick while aiming, or player movement
	// during the persistence window), it maintains / acquires / breaks a target lock
	// and pins _cursorWorldPos. Returns true when engaged, with the VISIBLE lock-ring
	// radius in `ringOuter`. Does not advance the free cursor or resolve Y (callers
	// own those), so aiming and movement reach exactly the same lock behaviour.
	bool ResolvePositionalSnap(AimInput aim, Vector3 playerPos, float maxRange, float dt, out float ringOuter)
	{
		ringOuter = _groundRingOuterRadius;
		_ringPinned = false;
		bool gamepad = aim.Device == InputDevice.EDevice.Gamepad;
		WeaponData weapon = _player.Inventory?.GetWeapon(EInventorySlot.WeaponRanged)?.data;

		// Drop a lock that died, left release range, or is no longer targetable.
		if (_positionalLocked && !IsPositionalLockValid(_positionalTargetMob, weapon, playerPos, maxRange))
		{
			_positionalTargetMob = null;
			_positionalLocked = false;
		}

		if (_positionalLocked)
		{
			// Track the player's REMEMBERED position of the mob (the frozen memory
			// silhouette while out of sight), not the still-simulating live body.
			Vector3 foot = _positionalTargetMob.RememberedPosition;
			Vector2 footXZ = new(foot.X, foot.Z);
			// Re-target: if the locked mob sits beyond the throw's reach (the arc caps
			// short of it) and that capped landing point falls on a different, reachable
			// mob, switch the lock to the nearer target the throw can actually hit.
			float fdx = footXZ.X - playerPos.X;
			float fdz = footXZ.Y - playerPos.Z;
			if (maxRange > 0f && fdx * fdx + fdz * fdz > maxRange * maxRange)
			{
				Vector2 landing = ClampAimToRange(footXZ, playerPos, maxRange);
				Mob nearer = FindPositionalSnapTarget(landing, playerPos, maxRange, weapon, out float _, out Vector3 nearerFoot);
				if (nearer != null && nearer != _positionalTargetMob)
				{
					_positionalTargetMob = nearer;
					foot = nearerFoot;
					footXZ = new Vector2(foot.X, foot.Z);
				}
			}
			float snapR = PositionalSnapRadius(_positionalTargetMob);
			bool brokeOut;
			if (gamepad)
			{
				// A firm push / spin (deflection) breaks; otherwise the stick velocity nudges
				// the lock cursor and the ease (scaled down by deflection so a push moves it)
				// settles it back onto the feet over ~0.25s. Firing mid-ease throws this line.
				float deflection = aim.Value.Length();
				brokeOut = deflection >= _positionalLockBreakDeflection;
				if (!brokeOut)
				{
					if (deflection > 0f && maxRange > 0f)
					{
						float scale = maxRange * _gamepadPositionalSpeedFraction * dt;
						_lockCursorXZ += new Vector2(aim.Value.X * scale, aim.Value.Y * scale);
					}
					float easeK = (1f - Mathf.Exp(-dt * _positionalLockEaseSpeed)) * (1f - Mathf.Min(1f, deflection));
					_lockCursorXZ = _lockCursorXZ.Lerp(footXZ, easeK);
				}
			}
			else
			{
				// Mouse: the cursor follows the mouse directly (the free cursor is the break
				// probe, no range clamp so an out-of-range lock holds); break when it leaves
				// the snap radius. The aim (lock cursor) eases onto the feet meanwhile.
				_freeCursorXZ += aim.Value;
				float ox = _freeCursorXZ.X - footXZ.X;
				float oz = _freeCursorXZ.Y - footXZ.Y;
				brokeOut = ox * ox + oz * oz > snapR * snapR;
				if (!brokeOut)
				{
					float easeK = 1f - Mathf.Exp(-dt * _positionalLockEaseSpeed);
					_lockCursorXZ = _lockCursorXZ.Lerp(footXZ, easeK);
				}
			}

			if (brokeOut)
			{
				// Resume free aiming. Gamepad continues from the lock cursor (the firm stick
				// carries it out); the mouse free cursor is already at the raw cursor position.
				if (gamepad) { _freeCursorXZ = _lockCursorXZ; }
				_freeCursorValid = true;
				_positionalTargetMob = null;
				_positionalLocked = false;
			}
			else
			{
				_cursorWorldPos.X = _lockCursorXZ.X;
				_cursorWorldPos.Z = _lockCursorXZ.Y;
				DropCursorToGround(playerPos + Vector3.Up * _aimHeight);
				if (gamepad) { _freeCursorXZ = _lockCursorXZ; }
				_freeCursorValid = true;
				_ringPinned = true;
				_ringWorldPos = foot;
				ringOuter = PositionalCatchRadius(_positionalTargetMob);
				return true;
			}
		}

		// Hover: is the throw's LANDING point (the cursor clamped to reach) inside a
		// targetable mob's lock-on range? Keying off the reachable point means an out-of-
		// range cursor relocks once the arc falls back onto a target.
		Vector2 aimXZ = ClampAimToRange(_freeCursorXZ, playerPos, maxRange);
		Mob hover = FindPositionalSnapTarget(aimXZ, playerPos, maxRange, weapon, out float hoverRing, out Vector3 hoverFoot);
		if (hover != null)
		{
			_positionalTargetMob = hover;
			// Cursor at rest on the target (right stick eased, or pure movement while
			// walking it on) locks — the aim then eases onto the feet from here. Still
			// moving → just a hover highlight (ring at the feet, aim stays on the reticle)
			// so sweeping through doesn't snag and the lock has somewhere to ease FROM.
			bool settled = gamepad
				? aim.Value.LengthSquared() <= _positionalLockStopDeadzone * _positionalLockStopDeadzone
				: dt > 0f && aim.Value.Length() <= _mouseLockSettleSpeed * dt;
			if (settled)
			{
				_positionalLocked = true;
				_lockCursorXZ = _freeCursorXZ;
			}
			_cursorWorldPos.X = _freeCursorXZ.X;
			_cursorWorldPos.Z = _freeCursorXZ.Y;
			DropCursorToGround(playerPos + Vector3.Up * _aimHeight);
			_ringPinned = true;
			_ringWorldPos = hoverFoot;
			ringOuter = hoverRing;
			return true;
		}

		// Free: no target under the reticle. Y is the caller's to resolve.
		_positionalTargetMob = null;
		_cursorWorldPos.X = _freeCursorXZ.X;
		_cursorWorldPos.Z = _freeCursorXZ.Y;
		return false;
	}

	// Visible red lock-ring radius for a snap target — the same footprint directional
	// lock draws (clearanceRadius scaled by the locked-ring multiplier).
	float PositionalCatchRadius(Mob mob)
	{
		float clearance = mob?.mobData?.clearanceRadius ?? 0.4f;
		return clearance * _groundRingLockedRadiusMultiplier;
	}

	// Lock-on / break-out distance in WORLD METERS: the mob's body half-width
	// (clearanceRadius) plus a flat reach, independent of the drawn ring size.
	float PositionalSnapRadius(Mob mob)
	{
		float clearance = mob?.mobData?.clearanceRadius ?? 0.4f;
		return clearance + _positionalSnapReachMeters;
	}

	// A locked target stays valid while it exists, is still targetable by the
	// weapon, and hasn't been walked past the release distance (the weapon range
	// PLUS _positionalLockReleaseExtraRange — leaving normal range alone keeps the
	// lock so the player can close back in). Optionally also requires line of sight.
	bool IsPositionalLockValid(Mob mob, WeaponData weapon, Vector3 playerPos, float maxRange)
	{
		if (mob == null || !GodotObject.IsInstanceValid(mob) || !mob.CanTarget(weapon))
		{
			return false;
		}
		// Range-check against the REMEMBERED position (where the player thinks the mob
		// is), matching the silhouette the lock tracks.
		Vector3 foot = mob.RememberedPosition;
		float dx = foot.X - playerPos.X;
		float dz = foot.Z - playerPos.Z;
		float release = maxRange + _positionalLockReleaseExtraRange;
		if (release > 0f && dx * dx + dz * dz > release * release)
		{
			return false;
		}
		if (_positionalLockBreaksOnLineOfSightLoss && !HasLineOfSightToMob(mob, playerPos))
		{
			return false;
		}
		return true;
	}

	// Clear line of sight from the player's chest pivot to the mob's body center,
	// against solid environment only. Used to drop a lock when the target ducks
	// behind cover. Returns true when the world isn't queryable so a missing space
	// state never spuriously breaks the lock.
	bool HasLineOfSightToMob(Mob mob, Vector3 playerPos)
	{
		World3D world3D = GetWorld3D();
		if (world3D == null)
		{
			return true;
		}
		Vector3 origin = playerPos + Vector3.Up * _aimHeight;
		using var query = PhysicsRayQueryParameters3D.Create(origin, mob.AimCenter, (uint)ECollisionLayer.Solid);
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;
		if (_player != null)
		{
			query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
		}
		return world3D.DirectSpaceState.IntersectRay(query).Count == 0;
	}

	// Clamp a world XZ point to within `range` of the player (XZ). For an out-of-reach
	// aim this returns where the throw actually LANDS in that direction, so snapping
	// keys off the arc's landing point rather than the unreachable raw cursor.
	Vector2 ClampAimToRange(Vector2 xz, Vector3 playerPos, float range)
	{
		float dx = xz.X - playerPos.X;
		float dz = xz.Y - playerPos.Z;
		float d2 = dx * dx + dz * dz;
		if (range > 0f && d2 > range * range)
		{
			float k = range / Mathf.Sqrt(d2);
			return new Vector2(playerPos.X + dx * k, playerPos.Z + dz * k);
		}
		return xz;
	}

	// Nearest targetable mob whose feet are within range and within its own catch
	// radius of `cursorXZ`. Null when none. Shares Mob.CanTarget with the
	// directional lock so the snap and the actual shot agree on what's targetable.
	Mob FindPositionalSnapTarget(Vector2 cursorXZ, Vector3 playerPos, float maxRange, WeaponData weapon, out float catchR, out Vector3 foot)
	{
		catchR = 0f;
		foot = default;
		MobSpatialHash hash = Sim.Current?.MobSpatialHash;
		if (hash == null)
		{
			return null;
		}
		_snapScratch ??= new List<Mob>(16);
		_snapScratch.Clear();
		Vector3 cursorWorld = new(cursorXZ.X, playerPos.Y, cursorXZ.Y);
		hash.QueryRadius(cursorWorld, _positionalSnapSearchRadius, _snapScratch);

		Mob best = null;
		float bestDistSq = float.PositiveInfinity;
		float maxRangeSq = maxRange * maxRange;
		for (int i = 0; i < _snapScratch.Count; i++)
		{
			Mob mob = _snapScratch[i];
			if (mob == null || !mob.CanTarget(weapon))
			{
				continue;
			}
			Vector3 mf = mob.GlobalPosition;
			float rdx = mf.X - playerPos.X;
			float rdz = mf.Z - playerPos.Z;
			if (maxRange > 0f && rdx * rdx + rdz * rdz > maxRangeSq)
			{
				continue;
			}
			float cdx = mf.X - cursorXZ.X;
			float cdz = mf.Z - cursorXZ.Y;
			float distSq = cdx * cdx + cdz * cdz;
			float snapR = PositionalSnapRadius(mob);
			if (distSq > snapR * snapR || distSq >= bestDistSq)
			{
				continue;
			}
			bestDistSq = distSq;
			best = mob;
			catchR = PositionalCatchRadius(mob);
			foot = mf;
		}
		return best;
	}

	// Drop _cursorWorldPos.Y to the ground beneath its current XZ. Starts from
	// chest height so it doesn't miss surfaces slightly above the player's feet.
	void DropCursorToGround(Vector3 chestWorld)
	{
		Vector3 dropFrom = new(_cursorWorldPos.X, chestWorld.Y, _cursorWorldPos.Z);
		if (TryRaycastDown(dropFrom, _maxGroundDropDistance, out Vector3 groundHit))
		{
			_cursorWorldPos.Y = groundHit.Y;
		}
	}

	// Clear gamepad positional snap state. Called when aim ends or the device
	// switches off gamepad so the next session re-hovers from a fresh free cursor.
	void ResetPositionalSnap()
	{
		_positionalTargetMob = null;
		_positionalLocked = false;
		_freeCursorValid = false;
		_ringPinned = false;
	}

	// Shared Positional/Arced cursor input: seed the cursor on the first aiming
	// frame, advance it from aim input (gamepad = rate, mouse = world-unit delta),
	// then clamp it to a disk of radius maxRange around the player.
	// Writes _cursorWorldPos.X/Z + _cursorValid and sets _positionalPersist for
	// the gamepad post-aim window. Y resolution and any throw solve are the
	// caller's job.
	void AdvanceGroundCursorXZ(AimInput aim, Vector3 playerPos, Vector3 chestWorld, float maxRange, float dt)
	{
		// First frame in this aim session — seed the cursor so it doesn't start at
		// (0,0,0). Gamepad-with-stick-pushed throws straight to 50% range in the
		// pressed direction; otherwise forward-raycast (also keeps the mid-charge
		// Dir → Pos/Arced flip consistent — the flip case has a valid cursor and
		// skips this). Mouse always raycast-seeds: its value is a motion delta,
		// not a direction, so there's nothing to seed from.
		if (!_cursorValid)
		{
			if (aim.Device == InputDevice.EDevice.Gamepad
				&& aim.Value.LengthSquared() > 0f)
			{
				Vector2 seedDir = aim.Value.Normalized();
				float seedDist = maxRange * GamepadPositionalSeedFraction;
				_cursorWorldPos.X = playerPos.X + seedDir.X * seedDist;
				_cursorWorldPos.Z = playerPos.Z + seedDir.Y * seedDist;
				_cursorWorldPos.Y = chestWorld.Y;
				_cursorValid = true;
			}
		}
		if (!_cursorValid)
		{
			Vector3 forward = _player.ActorForward.Normalized();
			WeaponData weaponData = _player.Inventory?.GetWeapon(EInventorySlot.WeaponRanged)?.data;
			if (TryRaycastForward(chestWorld, forward, maxRange, weaponData, out Vector3 forwardHit, out Mob _))
			{
				if (TryRaycastDown(forwardHit, _maxGroundDropDistance, out Vector3 dropHit))
				{
					_cursorWorldPos = dropHit;
				}
				else
				{
					_cursorWorldPos = forwardHit;
				}
			}
			else
			{
				Vector3 fallback = chestWorld + forward * maxRange;
				if (TryRaycastDown(fallback, _maxGroundDropDistance, out Vector3 dropHit))
				{
					_cursorWorldPos = dropHit;
				}
				else
				{
					_cursorWorldPos = fallback;
				}
			}
			_cursorValid = true;
		}

		// Advance the cursor from aim input. The device tag (carried on `aim`)
		// selects the accumulation law — both integrate onto the cursor, but by
		// different rules:
		//
		//  • Gamepad: the right stick is a RATE input — cursor velocity is the raw
		//    deflection scaled by range, at range * _gamepadPositionalSpeedFraction
		//    m/s at full deflection. Keeps moving while the stick is held.
		//  • Mouse: aim.Value is the world-unit cursor DELTA accumulated since the
		//    last frame (range-independent) — add it straight. Moves only when the
		//    mouse moves; range only clamps (below).
		if (aim.Device == InputDevice.EDevice.Gamepad)
		{
			// Gamepad cursors persist after aim-off (see _Process); mouse cursors
			// recenter, so only this path opts into persistence.
			_positionalPersist = true;
			if (aim.Value.LengthSquared() > 0f && maxRange > 0f)
			{
				float scale = maxRange * _gamepadPositionalSpeedFraction * dt;
				_cursorWorldPos.X += aim.Value.X * scale;
				_cursorWorldPos.Z += aim.Value.Y * scale;
			}
		}
		else
		{
			_positionalPersist = false;
			_cursorWorldPos.X += aim.Value.X;
			_cursorWorldPos.Z += aim.Value.Y;
		}

		// Clamp to a disk of radius=maxRange around the player. Re-applied each
		// frame so walking away from the cursor drags it along the disk edge
		// rather than orphaning it past the weapon's reach.
		float dx = _cursorWorldPos.X - playerPos.X;
		float dz = _cursorWorldPos.Z - playerPos.Z;
		float horizDistSq = dx * dx + dz * dz;
		if (maxRange > 0f && horizDistSq > maxRange * maxRange)
		{
			float horizDist = Mathf.Sqrt(horizDistSq);
			float pull = maxRange / horizDist;
			_cursorWorldPos.X = playerPos.X + dx * pull;
			_cursorWorldPos.Z = playerPos.Z + dz * pull;
		}
	}

	// Launch vertical speed for the hump: v0y = √(2·g·rise) reaches a peak of
	// `rise` above the launch point. Purely vertical — the horizontal reach is set
	// separately from the aim distance + fuse. Shared by the reticle preview and
	// DoProjectile so the throw matches.
	public static float ArcLaunchVerticalSpeed(float rise, float gravity)
	{
		return Mathf.Sqrt(2f * gravity * rise);
	}

	// Build the arced throw from `origin` along `bearing`: a fixed-shape hump that
	// rises by the event's rise under its gravity, with horizontal speed set to
	// cover `horizDist` (the charge-scaled projectileMaxRange) over the fuse —
	// the same formula as ItemEventHandlers.TrySolveArcLaunch, so the preview and
	// the real throw agree by construction. Simulates the FULL path — gravity plus
	// bounces (restitution / friction), matching the projectile — over the fuse
	// into _arcPoints, so the ribbon predicts the whole trajectory through its
	// bounces to where it comes to rest / detonates. `landing` is the simulated
	// end point (the ground-ring anchor); falls back to `origin` when the active
	// tier has no arced projectile event (ribbon cleared).
	void SolveArcForward(Vector3 origin, Vector3 bearing, float horizDist, out Vector3 landing)
	{
		landing = origin;
		_arcPoints.Clear();

		ItemEvent arc = FindArcEvent();
		if (arc == null || arc.projectileArcRise <= 0f || arc.projectileGravity <= 0f)
		{
			return;
		}
		float gravity = arc.projectileGravity;
		float fuse = arc.projectileLifetimeSeconds;
		float bounciness = arc.projectileBounciness;
		float friction = arc.projectileFriction;
		if (fuse <= 0f)
		{
			return;
		}

		// Vertical is rise + gravity; horizontal covers the charge-scaled reach
		// over the FUSE (so the reach scales with maxRange/lifetime, not the time
		// to return to foot level).
		float launchVy = ArcLaunchVerticalSpeed(arc.projectileArcRise, gravity);

		// "Fragile" weapon mod: the real throw skips bouncing and detonates on the
		// first surface it meets (ItemEventHandlers.DoProjectile drops it into the
		// non-bounce collision path). Mirror that here by ending the simulated path
		// at the first solid hit instead of reflecting, so the dotted preview stops
		// where the bomb will actually go off. Gated on an impactEvent (the payload
		// on completion), matching DoProjectile's condition.
		// Preview against weapon-global (AllAttacks) detonate mods only — the
		// reticle doesn't track which charge tier the throw will commit to, and
		// the only detonate mod in play (Fragile) is weapon-global. -1 matches
		// AllAttacks-scoped mods and skips charge-specific ones.
		WeaponState rightWeapon = _player?.Inventory?.GetWeapon(EInventorySlot.WeaponRanged);
		bool detonateOnContact = ItemEventHandlers.ArcDetonatesOnContact(arc, rightWeapon, -1);

		Vector3 launchVel = bearing * (horizDist / fuse) + Vector3.Up * launchVy;

		// Step the trajectory exactly like Projectile (gravity before the move,
		// reflect off solids with the same restitution/friction split), recording
		// each point, until the fuse elapses or it settles after a bounce.
		int maxSteps = Mathf.Min(ArcSimMaxSteps, Mathf.Max(1, Mathf.CeilToInt(fuse / ArcSimStep)));
		PhysicsDirectSpaceState3D space = GetWorld3D()?.DirectSpaceState;
		Godot.Collections.Array<Rid> exclude = _player != null
			? new Godot.Collections.Array<Rid> { _player.GetRid() }
			: null;

		Vector3 pos = origin;
		Vector3 vel = launchVel;
		_arcPoints.Add(pos);
		bool bounced = false;
		for (int step = 0; step < maxSteps; step++)
		{
			vel.Y -= gravity * ArcSimStep;
			Vector3 next = pos + vel * ArcSimStep;
			if (space != null)
			{
				using var q = PhysicsRayQueryParameters3D.Create(pos, next, (uint)ECollisionLayer.Solid);
				q.CollideWithBodies = true;
				q.CollideWithAreas = false;
				if (exclude != null)
				{
					q.Exclude = exclude;
				}
				var hit = space.IntersectRay(q);
				if (hit.Count > 0)
				{
					Vector3 hitPos = (Vector3)hit["position"];
					Vector3 normal = (Vector3)hit["normal"];
					_arcPoints.Add(hitPos);
					// Fragile: the throw detonates here — end the preview path.
					if (detonateOnContact)
					{
						break;
					}
					Vector3 vNormal = vel.Dot(normal) * normal;
					Vector3 vTangent = vel - vNormal;
					vel = (-vNormal * bounciness) + (vTangent * (1f - friction));
					pos = hitPos + normal * ArcSimSurfaceOffset;
					bounced = true;
					continue;
				}
			}
			pos = next;
			_arcPoints.Add(pos);
			if (bounced && vel.LengthSquared() < ArcSettleSpeed * ArcSettleSpeed)
			{
				break;
			}
		}
		// The ring anchors where the throw actually ends — the first solid hit for
		// fragile lobs, else where the sim settled / the fuse ran out.
		landing = _arcPoints[_arcPoints.Count - 1];
	}

	// Unit horizontal (XZ) direction from origin toward target; +Z fallback when
	// they share an XZ column (degenerate, straight-up throw).
	static Vector3 HorizontalBearing(Vector3 origin, Vector3 target)
	{
		float dx = target.X - origin.X;
		float dz = target.Z - origin.Z;
		float d = Mathf.Sqrt(dx * dx + dz * dz);
		return d > 1e-4f ? new Vector3(dx / d, 0f, dz / d) : new Vector3(0f, 0f, 1f);
	}

	// First arced projectile event on the active tier (the one the throw / preview
	// reads rise, gravity, fuse, bounce, and friction from). Null when the tier has
	// no such event.
	ItemEvent FindArcEvent()
	{
		ItemAction tier = ResolveActiveTier(out _);
		if (tier?.events == null)
		{
			return null;
		}
		for (int i = 0; i < tier.events.Count; i++)
		{
			ItemEvent ev = tier.events[i];
			if (ev != null
				&& (ev.type & EItemEventType.Projectile) != 0
				&& ev.projectileArcing
				&& ev.projectileScene != null)
			{
				return ev;
			}
		}
		return null;
	}

	// Render path — used by both the live update and the fade-out path. The
	// only state held across frames is the ground-ring radius lerp; the rest
	// (chest, forward, spread sampling) recomputes from current state so the
	// fading reticle still follows the player. `mobTargeted` gates the red
	// tint; `mobTargetOuter` is the outer radius to ease into. Positional
	// aim hides the forward beam / spread markers / knob entirely — the
	// ground circle alone communicates the throw / drop target. `_cursorWorldPos`
	// (written by UpdateDirectional / UpdateGroundAim) is the ground-circle
	// world position regardless of mode.
	void RenderReticle(EAimType aimType, float lineLength, bool clippedAtSurface, bool mobTargeted, float mobTargetOuter, float positionalRadius, float dt)
	{
		bool showForwardBeam = aimType == EAimType.Directional;

		// Ground ring lerp. New target whenever the active-tier footprint
		// or lock state changes — capture the current value as the source
		// and reset the elapsed clock so the next 0.15s plays out linearly
		// from here. A mob lock (directional, or a snapped gamepad positional
		// target) drives the ring to the mob silhouette; an unlocked Positional
		// cursor uses the tier's authored AoE radius; everything else the default.
		float targetOuter;
		if (mobTargeted)
		{
			targetOuter = mobTargetOuter;
		}
		else if (aimType == EAimType.Positional)
		{
			targetOuter = positionalRadius;
		}
		else
		{
			targetOuter = _groundRingOuterRadius;
		}
		if (Mathf.Abs(targetOuter - _ringTarget) > 1e-4f)
		{
			_ringSource = _currentOuterRadius;
			_ringTarget = targetOuter;
			_ringElapsed = 0f;
		}
		_ringElapsed += dt;
		float t = Mathf.Clamp(_ringElapsed / RingTransitionSeconds, 0f, 1f);
		_currentOuterRadius = Mathf.Lerp(_ringSource, _ringTarget, t);
		// Thickness preserved across all sizes — the band's width never
		// changes, only its radius. Inner is clamped at 0 so a target outer
		// smaller than the thickness still renders as a filled disc.
		float thickness = _groundRingOuterRadius - _groundRingInnerRadius;
		float currentInner = Mathf.Max(0f, _currentOuterRadius - thickness);

		if (_groundCircle != null)
		{
			if (aimType == EAimType.Arced)
			{
				// Arced aim is visualized by the dotted hump alone — its end point
				// IS the landing, so a ground ring there would be redundant.
				_groundCircle.Visible = false;
			}
			else
			{
				_groundCircle.Visible = true;
				Vector3 tint = mobTargeted
					? new Vector3(_groundRingLockedColor.R, _groundRingLockedColor.G, _groundRingLockedColor.B)
					: Vector3.One;
				_groundCircle.SetInstanceShaderParameter("ring_outer_radius", _currentOuterRadius);
				_groundCircle.SetInstanceShaderParameter("ring_inner_radius", currentInner);
				_groundCircle.SetInstanceShaderParameter("instance_color", tint);
				Vector3 ringWorld = _ringPinned ? _ringWorldPos : _cursorWorldPos;
				_groundCircle.Position = ToLocal(ringWorld);
				UpdateGroundUndulation(ringWorld);
			}
		}

		// Arc ribbon preview — only Arced populates _arcPoints; every other mode
		// hides it.
		RenderArcRibbon(aimType == EAimType.Arced);

		if (!showForwardBeam)
		{
			// Positional: forward beam, spread markers, and knob are all
			// directional concepts that don't apply when the cursor is a
			// free ground point. Hide them; the ground circle alone tells
			// the player where the action will land.
			if (_mainLine != null) { _mainLine.Visible = false; }
			if (_spreadLineLeft != null) { _spreadLineLeft.Visible = false; }
			if (_spreadLineRight != null) { _spreadLineRight.Visible = false; }
			if (_endKnob != null) { _endKnob.Visible = false; }
			return;
		}

		if (_mainLine != null) { _mainLine.Visible = true; }

		Vector3 chestWorld = _player.GlobalPosition + Vector3.Up * _aimHeight;
		Vector3 forward = _player.ActorForward.Normalized();
		// Right stays horizontal (basis.X) — the spread cone is yaw spread,
		// so the markers should sit at the lateral cone radius in the
		// horizontal plane regardless of how high or low the beam tilts.
		Vector3 right = GlobalTransform.Basis.X.Normalized();

		// Main line: ribbon from chest pivot to the (cached or fresh) endpoint.
		Vector3 mainStart = chestWorld;
		Vector3 mainEnd = chestWorld + forward * lineLength;
		// VISUAL-ONLY vertical smoothing: ease just the rendered tip's Y toward
		// the true endpoint so the beam sweeps up to an elevated lock instead of
		// snapping. Horizontal (yaw) tracking stays instant, and the shot itself
		// fires along _player.ActorForward — this never touches the mechanic. The
		// smoothed Y is shared by the beam, the knob, and the spread markers so
		// the whole forward telegraph rises together. First frame after a hide
		// snaps (no glide-up from a stale value).
		if (!_endYValid)
		{
			_smoothedEndY = mainEnd.Y;
			_endYValid = true;
		}
		else
		{
			// Ease out toward the target by closing an exp-decay fraction of the
			// remaining distance each frame: frame-rate independent, decelerates
			// as it closes, never overshoots.
			float k = 1f - Mathf.Exp(-dt * _reticleVerticalEaseSpeed);
			_smoothedEndY = Mathf.Lerp(_smoothedEndY, mainEnd.Y, k);
		}
		mainEnd.Y = _smoothedEndY;
		SetLineEndpoints(_mainLine, mainStart, mainEnd);
		if (_mainLine != null)
		{
			_mainLine.SetInstanceShaderParameter("gradient_origin_world", chestWorld);
			_mainLine.SetInstanceShaderParameter("gradient_start_distance", _gradientStartDistance);
			_mainLine.SetInstanceShaderParameter("gradient_end_distance", _gradientEndDistance);
		}

		// Knob at the mainline endpoint. Same gradient origin/range as the
		// mainline so the knob fades in over the first few meters identically.
		if (_endKnob != null)
		{
			_endKnob.Visible = true;
			_endKnob.Position = ToLocal(mainEnd);
			_endKnob.SetInstanceShaderParameter("gradient_origin_world", chestWorld);
			_endKnob.SetInstanceShaderParameter("gradient_start_distance", _gradientStartDistance);
			_endKnob.SetInstanceShaderParameter("gradient_end_distance", _gradientEndDistance);
		}

		// Spread offset at the actual aim distance. tan(halfAngle) * range
		// gives the lateral cone radius at the endpoint; the parallel markers
		// land there so the visible width matches the cone width at where the
		// shot would actually hit. When fully accurate (offset ≈ 0) the
		// markers collapse onto the main line, so we hide them instead.
		float spread01 = ComputeSpread01();
		float halfAngle = ItemEventHandlers.MAX_SPREAD_HALF_ANGLE * spread01;
		float spreadOffset = Mathf.Tan(halfAngle) * lineLength;
		bool showSpread = spreadOffset > 1e-3f;
		if (_spreadLineLeft != null) { _spreadLineLeft.Visible = showSpread; }
		if (_spreadLineRight != null) { _spreadLineRight.Visible = showSpread; }
		if (showSpread)
		{
			PlaceSpreadLine(_spreadLineLeft, chestWorld, forward, right, lineLength, +spreadOffset);
			PlaceSpreadLine(_spreadLineRight, chestWorld, forward, right, lineLength, -spreadOffset);
		}
	}

	// Rebuilds the arc-preview ribbon (a connected triangle strip of camera-facing
	// quads) along _arcPoints into its ImmediateMesh each frame. Each cross-pair is
	// offset by `right = tangent × toCamera`, so the strip rolls around the arc to
	// face the camera (the CPU analog of the billboard line shader). Verts are
	// authored in the node's local space. The ImmediateMesh is created in code (see
	// _Ready) — a .tscn-serialized ImmediateMesh silently fails to register its
	// geometry with the renderer, so nothing draws.
	void RenderArcRibbon(bool show)
	{
		// Dev visualizer (debug_aim_arc): draw the solved trajectory straight from
		// _arcPoints via DebugDraw, so the raw arc is visible independent of the ribbon.
		if (show && CVars.debugAimArc.Value && _arcPoints.Count >= 2)
		{
			DebugDraw.Lines(_arcPoints, new Color(0.2f, 1f, 0.4f, 1f));
			for (int i = 0; i < _arcPoints.Count; i++)
			{
				DebugDraw.Cross(_arcPoints[i], 0.15f, new Color(1f, 0.9f, 0.1f, 1f));
			}
		}

		if (_arcRibbon == null || _arcRibbonMesh == null)
		{
			return;
		}
		_arcRibbonMesh.ClearSurfaces();
		if (!show || _arcPoints.Count < 2)
		{
			_arcRibbon.Visible = false;
			return;
		}

		int n = _arcPoints.Count;
		Camera3D cam = GetViewport()?.GetCamera3D();
		Vector3 camPos = cam != null ? cam.GlobalPosition : _player.GlobalPosition + Vector3.Up * 8f;
		float halfWidth = Mathf.Max(0.005f, _arcRibbonWidth * 0.5f);

		// Total path length for the tail fade (distance-along-the-arc, so a path
		// that loops back near itself still fades only its true end).
		float totalLen = 0f;
		for (int i = 1; i < n; i++)
		{
			totalLen += _arcPoints[i].DistanceTo(_arcPoints[i - 1]);
		}
		float fadeInStart = _arcFadeInStart;
		float fadeInSpan = Mathf.Max(1e-3f, _arcFadeInEnd - _arcFadeInStart);
		float fadeOut = Mathf.Max(1e-3f, _arcFadeOutDistance);

		// Per-vertex alpha (carried in COLOR.a, which the shader multiplies in):
		// fade in from the launch over [fadeInStart, fadeInEnd] of arc length, fade
		// out over the last fadeOutDistance before the end.
		_arcRibbonMesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
		float cumLen = 0f;
		for (int i = 0; i < n; i++)
		{
			if (i > 0)
			{
				cumLen += _arcPoints[i].DistanceTo(_arcPoints[i - 1]);
			}
			Vector3 p = _arcPoints[i];
			Vector3 tangent;
			if (i == 0)
			{
				tangent = _arcPoints[1] - _arcPoints[0];
			}
			else if (i == n - 1)
			{
				tangent = _arcPoints[i] - _arcPoints[i - 1];
			}
			else
			{
				tangent = _arcPoints[i + 1] - _arcPoints[i - 1];
			}
			if (tangent.LengthSquared() < 1e-8f)
			{
				tangent = Vector3.Forward;
			}
			tangent = tangent.Normalized();
			Vector3 right = tangent.Cross(camPos - p);
			if (right.LengthSquared() < 1e-10f)
			{
				right = tangent.Cross(Vector3.Up);
			}
			if (right.LengthSquared() < 1e-10f)
			{
				right = Vector3.Right;
			}
			right = right.Normalized() * halfWidth;

			float aIn = Mathf.Clamp((cumLen - fadeInStart) / fadeInSpan, 0f, 1f);
			float aOut = Mathf.Clamp((totalLen - cumLen) / fadeOut, 0f, 1f);
			Color c = new Color(1f, 1f, 1f, aIn * aOut);
			// Verts in the node's local space (the ribbon isn't top_level, so
			// MODEL_MATRIX carries the AimingReticle transform).
			_arcRibbonMesh.SurfaceSetColor(c);
			_arcRibbonMesh.SurfaceAddVertex(_arcRibbon.ToLocal(p - right));
			_arcRibbonMesh.SurfaceSetColor(c);
			_arcRibbonMesh.SurfaceAddVertex(_arcRibbon.ToLocal(p + right));
		}
		_arcRibbonMesh.SurfaceEnd();
		_arcRibbon.Visible = true;
	}

	// Resolves the voxel-surface height under the ground ring into a small grid
	// and hands it to the shader, which displaces the (subdivided) ring mesh's
	// vertices to drape it over terrain. Centered on `cursorWorld`, covering an
	// `_undulationPatchWorldSize` square. The whole feature is gated on
	// reticle_undulate — when there's no voxel world (or it's disabled) we leave
	// the shader on its flat path so nothing regresses.
	void UpdateGroundUndulation(Vector3 cursorWorld)
	{
		Sim sim = Sim.Current;
		WorldState voxels = sim?.WorldState;
		if (!_undulationEnabled || _groundMaterial == null || voxels == null || sim.player == null)
		{
			_groundMaterial?.SetShaderParameter("reticle_undulate", 0f);
			_undulationValid = false;
			return;
		}

		float size = _undulationPatchWorldSize;
		float step = UndulationPatchRes > 1 ? size / (UndulationPatchRes - 1) : 1f;
		// Snap the sample grid to fixed world positions (multiples of `step`) so
		// the sampled height field is a stable function of world XZ. Without this
		// the columns move with the cursor and the ring pops every time a sample
		// crosses a voxel boundary; with it, sub-cell cursor motion changes
		// nothing and a cell-crossing is a seamless window slide over the same
		// world columns.
		float originX = Mathf.Floor((cursorWorld.X - size * 0.5f) / step) * step;
		float originZ = Mathf.Floor((cursorWorld.Z - size * 0.5f) / step) * step;
		// Anchor the per-column surface search at the player's level so a hill
		// above the player resolves to its top (scan up) and a cave floor / valley
		// below resolves to the floor (scan down) — a single top-down query can't
		// do both (it would catch a cave roof). Targeting refinement comes later;
		// this is purely the ring's vertical profile.
		float anchorY = sim.player.GlobalPosition.Y;
		int anchorVoxelY = Mathf.FloorToInt(anchorY);

		// The patch mapping is world-stable, so always keep the shader pointed at
		// the current snapped window even when we skip the (static-terrain) rescan.
		_groundCircle.SetInstanceShaderParameter("reticle_patch_origin", new Vector2(originX, originZ));
		_groundCircle.SetInstanceShaderParameter("reticle_patch_size", size);
		_groundMaterial.SetShaderParameter("reticle_undulate", 1f);

		// Re-scan only when the snapped grid or the player's voxel level changed —
		// the voxel terrain is static, so the height field is otherwise identical.
		if (_undulationValid
			&& originX == _lastPatchOriginX
			&& originZ == _lastPatchOriginZ
			&& anchorVoxelY == _lastAnchorVoxelY)
		{
			return;
		}
		_lastPatchOriginX = originX;
		_lastPatchOriginZ = originZ;
		_lastAnchorVoxelY = anchorVoxelY;
		_undulationValid = true;

		for (int cz = 0; cz < UndulationPatchRes; cz++)
		{
			int vz = Mathf.FloorToInt(originZ + cz * step);
			for (int cx = 0; cx < UndulationPatchRes; cx++)
			{
				int vx = Mathf.FloorToInt(originX + cx * step);
				_undulationHeights[cz * UndulationPatchRes + cx] =
					ScanColumnSurface(voxels, vx, vz, anchorY, cursorWorld.Y);
			}
		}
		_groundMaterial.SetShaderParameter("reticle_heights", _undulationHeights);
	}

	// World Y of the ground surface at a voxel column, anchored near `anchorY`.
	// If the anchor voxel is solid we scan UP to the top of that terrain; if it's
	// air we scan DOWN to the floor below. Returns `fallback` when no surface is
	// found within the scan window (e.g. an unloaded chunk).
	float ScanColumnSurface(WorldState voxels, int vx, int vz, float anchorY, float fallback)
	{
		int ay = Mathf.FloorToInt(anchorY);
		int maxScan = Mathf.Max(1, _undulationMaxScanVoxels);
		if (VoxelTypeInfo.IsSolid(voxels.GetVoxelWorld(vx, ay, vz)))
		{
			// Inside terrain — climb to the first air voxel; its base is the
			// surface (top of the solid below it).
			for (int k = 1; k <= maxScan; k++)
			{
				if (!VoxelTypeInfo.IsSolid(voxels.GetVoxelWorld(vx, ay + k, vz)))
				{
					return ay + k;
				}
			}
		}
		else
		{
			// In open air — drop to the first solid voxel; its top is the surface.
			for (int k = 1; k <= maxScan; k++)
			{
				if (VoxelTypeInfo.IsSolid(voxels.GetVoxelWorld(vx, ay - k, vz)))
				{
					return ay - k + 1;
				}
			}
		}
		return fallback;
	}

	// Writes the fade alpha into every reticle mesh's alpha_multiplier so
	// the shader can scale ALPHA at fragment exit. Called every frame the
	// reticle is at all visible (alpha > 0).
	void ApplyAlpha(float alpha)
	{
		SetMeshAlpha(_mainLine, alpha);
		SetMeshAlpha(_spreadLineLeft, alpha);
		SetMeshAlpha(_spreadLineRight, alpha);
		// Ground ring's alpha scale depends on what it's currently
		// representing: a Positional AoE footprint (loud — the ring is the
		// only telegraph), a Directional mob lock (full alpha), or an
		// unlocked Directional reticle (quieter dim). `_lastAimType` is
		// held across fade-out so the styling stays consistent as alpha
		// drops to zero.
		float groundScale;
		if (_lastMobTargeted)
		{
			// A mob lock (directional or a snapped positional/arced target) reads at
			// full alpha — it's the strongest UI state.
			groundScale = 1f;
		}
		else if (_lastAimType == EAimType.Positional || _lastAimType == EAimType.Arced)
		{
			groundScale = _groundRingPositionalAlphaScale;
		}
		else
		{
			groundScale = _groundRingUnlockedAlphaScale;
		}
		SetMeshAlpha(_groundCircle, alpha * groundScale);
		SetMeshAlpha(_endKnob, alpha);
		SetMeshAlpha(_arcRibbon, alpha);
	}

	static void SetMeshAlpha(MeshInstance3D mesh, float alpha)
	{
		if (mesh == null) { return; }
		mesh.SetInstanceShaderParameter("alpha_multiplier", alpha);
	}

	void PlaceSpreadLine(MeshInstance3D line, Vector3 chestWorld, Vector3 forward, Vector3 right, float lineLength, float lateralOffset)
	{
		if (line == null) { return; }
		// Ribbon ends at the main line's endpoint and extends `spreadLineLength`
		// back toward the player. Capped by the main line's length so a wall
		// clip that shortens the beam doesn't leave the spread markers poking
		// out behind the chest. Lateral offset matches the cone radius at the
		// actual aim distance.
		float length = Mathf.Min(_spreadLineLength, lineLength);
		Vector3 endPoint = chestWorld + forward * lineLength + right * lateralOffset;
		// Share the beam's visually-smoothed tip Y so the markers rise with it.
		endPoint.Y = _smoothedEndY;
		Vector3 startPoint = endPoint - forward * length;
		SetLineEndpoints(line, startPoint, endPoint);
	}

	static void SetLineEndpoints(MeshInstance3D mesh, Vector3 startWorld, Vector3 endWorld)
	{
		if (mesh == null) { return; }
		mesh.SetInstanceShaderParameter("line_start_world", startWorld);
		mesh.SetInstanceShaderParameter("line_end_world", endWorld);
	}

	// Mirrors DoHitscan's two-pass clip so the reticle ends where the actual
	// shot would land: environment first (bodies) for terrain, then hurtboxes
	// (areas) up to the env hit for mobs / destructible props. Whichever is
	// closer wins. `hitMob` is set when the closer hit was a mob hurtbox,
	// letting the caller swap the endpoint for the mob's body center.
	bool TryRaycastForward(Vector3 from, Vector3 dir, float distance, WeaponData weapon, out Vector3 hitWorld, out Mob hitMob)
	{
		hitWorld = default;
		hitMob = null;
		World3D world3D = GetWorld3D();
		if (world3D == null || distance <= 0f)
		{
			return false;
		}
		Vector3 to = from + dir * distance;
		var spaceState = world3D.DirectSpaceState;

		Godot.Collections.Array<Rid> bodyExclude = new();
		if (_player != null)
		{
			bodyExclude.Add(_player.GetRid());
		}
		using var envQuery = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Solid);
		envQuery.CollideWithBodies = true;
		envQuery.CollideWithAreas = false;
		envQuery.Exclude = bodyExclude;
		var envResult = spaceState.IntersectRay(envQuery);

		Vector3 envEnd = to;
		bool clipped = false;
		if (envResult.Count > 0)
		{
			envEnd = (Vector3)envResult["position"];
			clipped = true;
		}

		using var hurtQuery = PhysicsRayQueryParameters3D.Create(from, envEnd, (uint)ECollisionLayer.HurtBox);
		hurtQuery.CollideWithBodies = false;
		hurtQuery.CollideWithAreas = true;
		Rid? selfHurtBox = _player?.SelfHurtBoxRid;
		if (selfHurtBox.HasValue)
		{
			hurtQuery.Exclude = new Godot.Collections.Array<Rid> { selfHurtBox.Value };
		}
		var hurtResult = spaceState.IntersectRay(hurtQuery);
		if (hurtResult.Count > 0)
		{
			Mob mob = null;
			if (hurtResult["collider"].Obj is HurtBox hurtBox)
			{
				mob = ItemEventHandlers.FindOwningMob(hurtBox);
			}
			// Share Mob.CanTarget with UpdateAimAssist so the ring's mob-lock
			// styling never disagrees with whether the assist would acquire
			// this mob. Falls through to the env-clip return so the line still
			// terminates on any wall behind the mob. Direct hits remain
			// possible — only the visual telegraph is suppressed. Non-mob
			// hurtboxes (destructible props) keep the existing
			// clip-at-hurtbox behavior.
			if (mob == null || mob.CanTarget(weapon))
			{
				hitWorld = (Vector3)hurtResult["position"];
				hitMob = mob;
				return true;
			}
		}

		if (clipped)
		{
			hitWorld = envEnd;
			return true;
		}
		return false;
	}

	bool TryRaycastDown(Vector3 from, float distance, out Vector3 hitWorld)
	{
		hitWorld = default;
		World3D world3D = GetWorld3D();
		if (world3D == null)
		{
			return false;
		}
		Vector3 to = from + Vector3.Down * distance;
		// Water | Environment so the dot lands on the water surface over a
		// lake instead of punching through to the lake floor. Water lives on
		// an Area3D (WaterTrigger), so CollideWithAreas must be on; the layer
		// mask filters out other Area3Ds (interactives, hurtboxes).
		using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)(ECollisionLayer.Solid | ECollisionLayer.Water));
		query.CollideWithBodies = true;
		query.CollideWithAreas = true;
		// Exclude the player's own body — when the cursor sweeps on top of the
		// player, a drop ray from chest height would otherwise hit the player's
		// collider and snap the cursor Y up to the body, popping it vertically.
		if (_player != null)
		{
			query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
		}
		var result = world3D.DirectSpaceState.IntersectRay(query);
		if (result.Count == 0)
		{
			return false;
		}
		hitWorld = (Vector3)result["position"];
		return true;
	}

	// Resolve the right-hand weapon's currently-relevant tier — the in-flight
	// selected tier during a Charging phase, otherwise tier 0 (what an
	// immediate fire would produce). Returns null when no profile is
	// equipped. Shared by ComputeSpread01 and ResolveActiveAimType so spread
	// sampling and aim-mode resolution always agree on which tier is "current".
	ItemAction ResolveActiveTier(out float chargeT)
	{
		chargeT = 0f;
		WeaponState weapon = _player?.Inventory?.GetWeapon(EInventorySlot.WeaponRanged);
		ItemActionProfile profile = weapon?.data?.actionProfile;
		if (profile?.chargedActions == null || profile.chargedActions.Count == 0)
		{
			return null;
		}
		ActionRunner runner = _player.Runner;
		if (runner != null
			&& runner.Phase == EActionPhase.Charging
			&& runner.Current.context.sourceSlot == EInventorySlot.WeaponRanged)
		{
			chargeT = runner.CurrentChargeT;
			return runner.Current.selectedTier ?? profile.chargedActions[0];
		}
		return profile.chargedActions[0];
	}

	// Spread fraction in [0, 1] for the right-hand ranged weapon. While
	// charging that slot, samples the live charge fraction; otherwise samples
	// the snap tier at chargeT=0 — what would happen on an immediate fire.
	float ComputeSpread01()
	{
		ItemAction tier = ResolveActiveTier(out float chargeT);
		if (tier == null) { return 0f; }
		return ItemAction.SampleAccuracySpread(tier, chargeT);
	}

	// Per-tier aim mode for the right-hand weapon. Mirrors the tier
	// selection in ResolveActiveTier so the reticle picks Directional /
	// Positional from the same authority that drives spread / range.
	// Falls back to Directional when no weapon / profile is equipped so
	// the reticle's pre-existing aim path keeps running.
	EAimType ResolveActiveAimType()
	{
		ItemAction tier = ResolveActiveTier(out _);
		return tier?.aimType ?? EAimType.Directional;
	}

	// Reach of the active tier in world meters — the disk radius for
	// Positional aim and the directional raycast cap for Directional aim.
	// Positional tiers author their own `positionalRange` (independent of
	// weapon hitscan/projectile reach), so a charged AoE on a bow can target
	// closer than the bow's arrows fly. Directional tiers defer to
	// Player.GetWeaponRange so the reticle line stays in sync with the
	// shot's actual range / charge ramp.
	float ResolveActiveAimRange()
	{
		ItemAction tier = ResolveActiveTier(out float chargeT);
		if (tier == null) { return 0f; }
		if (tier.aimType == EAimType.Arced)
		{
			// Arced reach = the throw's horizontal range (off the event) scaled by
			// the tier's charge ramp — the throw distance (and with it the lob's
			// horizontal launch speed, reach / fuse) grows as the hold charges.
			ItemEvent arc = FindArcEvent();
			return arc != null ? arc.projectileMaxRange * ItemAction.SampleRangeScale(tier, chargeT) : tier.positionalRange;
		}
		if (tier.aimType == EAimType.Positional)
		{
			return tier.positionalRange;
		}
		return _player.GetWeaponRange(EInventorySlot.WeaponRanged);
	}
}
