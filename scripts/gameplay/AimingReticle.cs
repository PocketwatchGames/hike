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

	// Linear ease from current outer radius to the new target whenever the
	// target changes (lock on/off, or the targeted mob's clearance differs
	// from the previous one). Inner derives from outer so a single lerp
	// drives the whole ring.
	const float RingTransitionSeconds = 0.15f;

	// Fade duration when the ranged weapon becomes unavailable mid-aim
	// (cooldown after firing, ammo runs out, weapon swapped, etc.). Same
	// constant for fade-in so reticle pop-on is symmetric and not jarring.
	const float FadeDurationSeconds = 0.15f;

	// Positional aim cursor speed scalar. At full input deflection the
	// cursor sweeps `weaponRange * PositionalCursorRangeFractionPerSecond`
	// meters per second across the ground — value 1.0 means a full-
	// deflection sweep covers the disk's edge-to-center in one second,
	// so short-range and long-range positional tiers both feel like the
	// same edge-to-edge time regardless of physical range.
	const float PositionalCursorRangeFractionPerSecond = 1.0f;

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
	// Positional aim integrates Player.AimDeflection01 into this each frame.
	// Held continuous across mid-charge mode flips so the ground circle
	// glides instead of teleporting between modes. Exposed to downstream
	// consumers (positional fire handlers) via AimWorldPosition.
	Vector3 _cursorWorldPos;
	// Goes false on aim-off so the next aim session re-seeds rather than
	// reusing a stale cursor from minutes ago. Downstream consumers must
	// check HasAimWorldPosition before reading the position.
	bool _cursorValid;
	// Last frame's resolved aim type — used to detect Pos ↔ Dir transitions
	// so we can snap the player's facing toward the cursor on Pos → Dir
	// (the directional raycast THIS SAME FRAME picks up the new yaw).
	EAimType _lastAimType = EAimType.Directional;

	// World position currently being aimed at — the ground circle anchor.
	// Read by positional fire handlers (AoE drop target, throw destination)
	// at activation. Always check HasAimWorldPosition first; the value is
	// stale when false.
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

		bool active = _player.IsAiming && IsRangedWeaponAvailable();
		float dt = (float)delta;
		float step = dt / FadeDurationSeconds;

		if (active)
		{
			_currentAlpha = Mathf.Min(_currentAlpha + step, 1f);
			UpdateReticle(dt);
		}
		else
		{
			_currentAlpha = Mathf.Max(_currentAlpha - step, 0f);
			// Aim turned off — invalidate the cursor so the next aim session
			// re-seeds from a fresh raycast / positional default instead of
			// jumping back to a stale point from minutes ago. `_lastAimType`
			// stays at its last-active value so the fade-out renders with
			// the same ring radius / alpha scale the player was just seeing
			// (a Positional → fade-out doesn't snap the ring back to
			// directional defaults while alpha is dropping to zero).
			_cursorValid = false;
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
	}

	// Mirrors the gate Player uses in TryStartWeaponAction: a weapon must be
	// equipped with an action profile, have ammo if it consumes any, not be
	// on cooldown, and the action runner must not be running a DIFFERENT
	// slot's action (charging the bow itself is fine — that's the reticle's
	// whole point of existing during charge).
	bool IsRangedWeaponAvailable()
	{
		if (_player?.Inventory == null) { return false; }
		WeaponState weapon = _player.Inventory.GetWeapon(EInventorySlot.WeaponRight);
		if (weapon?.data?.actionProfile == null) { return false; }
		if (weapon.data.maxAmmo > 0 && weapon.ammo <= 0) { return false; }
		if (weapon.cooldownExpireMs > _player.GameTimeMs) { return false; }
		ActionRunner runner = _player.Runner;
		if (runner != null && runner.IsBusy && runner.Current.context.sourceSlot != EInventorySlot.WeaponRight)
		{
			return false;
		}
		return true;
	}

	// Fresh active update: resolve aim type → update cursor + cached state → render.
	// Handles the Pos ↔ Dir transition by snapping player yaw toward the cursor
	// on Pos → Dir BEFORE running the directional raycast, so the same-frame
	// forward fires through the previous positional cursor.
	void UpdateReticle(float dt)
	{
		EAimType aimType = ResolveActiveAimType();
		float maxRange = ResolveActiveAimRange();

		// Mode flip: on Pos → Dir the body needs to face the cursor first so
		// ActorForward this frame reflects the previously-aimed direction.
		// Dir → Pos needs no explicit seed — the cursor already sits at the
		// last directional ground point from the previous tick.
		if (_cursorValid && aimType == EAimType.Directional && _lastAimType == EAimType.Positional)
		{
			_player.SnapAimYawToward(_cursorWorldPos);
		}

		if (aimType == EAimType.Directional)
		{
			UpdateDirectional(maxRange, dt);
		}
		else
		{
			UpdatePositional(maxRange, dt);
		}
		_lastAimType = aimType;
	}

	void UpdateDirectional(float maxRange, float dt)
	{
		Vector3 chestWorld = _player.GlobalPosition + Vector3.Up * _aimHeight;
		// Use the player's pitched forward so the main beam, spread, and
		// ground circle anchor all follow the same elevation the next shot
		// will fire along (Player.ActorForward folds in the auto-aim pitch).
		Vector3 forward = _player.ActorForward.Normalized();

		float lineLength = maxRange;
		bool clippedAtSurface = false;
		bool mobTargeted = false;
		float mobTargetOuter = _groundRingOuterRadius;
		if (TryRaycastForward(chestWorld, forward, maxRange, out Vector3 forwardHit, out Mob hitMob))
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

	void UpdatePositional(float maxRange, float dt)
	{
		Vector3 playerPos = _player.GlobalPosition;
		Vector3 chestWorld = playerPos + Vector3.Up * _aimHeight;

		// First Positional frame in this aim session — seed from a forward
		// raycast so the cursor doesn't start at (0,0,0). Re-uses the
		// directional helper to keep "first seed" and "mid-charge Dir → Pos"
		// flip consistent (in the flip case the cursor was already set by
		// the previous directional frame and skips this branch).
		if (!_cursorValid)
		{
			Vector3 forward = _player.ActorForward.Normalized();
			if (TryRaycastForward(chestWorld, forward, maxRange, out Vector3 forwardHit, out Mob _))
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

		// Integrate per-frame deflection. AimDeflection01 is the player's
		// aim input pre-rotated by camera yaw and normalized to [0, 1]
		// magnitude, so the same code path works for gamepad and mouse.
		// Speed scales with weapon range so short-range and long-range
		// positional tiers both sweep edge-to-edge in the same wall time.
		Vector2 deflection = _player.AimDeflection01;
		float deflectionLenSq = deflection.X * deflection.X + deflection.Y * deflection.Y;
		if (deflectionLenSq > 0f && maxRange > 0f)
		{
			float metersPerSec = maxRange * PositionalCursorRangeFractionPerSecond;
			float scale = metersPerSec * dt;
			_cursorWorldPos.X += deflection.X * scale;
			_cursorWorldPos.Z += deflection.Y * scale;
		}

		// Clamp to a disk of radius=maxRange around the player. Re-applied
		// each frame so walking away from the cursor drags it along the
		// disk edge rather than orphaning it past the weapon's reach.
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

		// Drop Y to the ground at the (possibly clamped) cursor X/Z. Start
		// the drop from chest height so we don't miss surfaces that are
		// slightly above the player's feet. Misses leave Y at the last
		// valid value rather than snapping to the player's feet, so a
		// cursor swept briefly off a cliff edge doesn't jitter.
		Vector3 dropFrom = new(_cursorWorldPos.X, chestWorld.Y, _cursorWorldPos.Z);
		if (TryRaycastDown(dropFrom, _maxGroundDropDistance, out Vector3 groundHit))
		{
			_cursorWorldPos.Y = groundHit.Y;
		}

		// Face the player body toward the cursor so the sprite and
		// ActorForward both point at where the throw / drop will land.
		_player.SnapAimYawToward(_cursorWorldPos);

		// Resolve the AoE/footprint ring radius for the active tier — fed
		// to RenderReticle's existing ring-radius lerp so the change from
		// the default outer radius eases in over RingTransitionSeconds.
		// Cached so the fade-out path holds it through the alpha drop.
		ItemAction tier = ResolveActiveTier(out _);
		float positionalRadius = Mathf.Max(0f, tier?.positionalAreaRadius ?? _groundRingOuterRadius);
		_lastPositionalRadius = positionalRadius;

		// Positional has no concept of mob lock or aim distance line —
		// the cursor is a free ground point. Cached state still kept up
		// to date so the fade-out path renders without surprises if the
		// player switches back to a directional tier later.
		float lineLength = Mathf.Sqrt(dx * dx + dz * dz);
		_lastLineLength = lineLength;
		_lastMobTargeted = false;
		_lastMobTargetOuter = _groundRingOuterRadius;

		RenderReticle(EAimType.Positional, lineLength, clippedAtSurface: false, mobTargeted: false, mobTargetOuter: _groundRingOuterRadius, positionalRadius: positionalRadius, dt: dt);
	}

	// Render path — used by both the live update and the fade-out path. The
	// only state held across frames is the ground-ring radius lerp; the rest
	// (chest, forward, spread sampling) recomputes from current state so the
	// fading reticle still follows the player. `mobTargeted` gates the red
	// tint; `mobTargetOuter` is the outer radius to ease into. Positional
	// aim hides the forward beam / spread markers / knob entirely — the
	// ground circle alone communicates the throw / drop target. `_cursorWorldPos`
	// (written by UpdateDirectional / UpdatePositional) is the ground-circle
	// world position regardless of mode.
	void RenderReticle(EAimType aimType, float lineLength, bool clippedAtSurface, bool mobTargeted, float mobTargetOuter, float positionalRadius, float dt)
	{
		bool showForwardBeam = aimType == EAimType.Directional;

		// Ground ring lerp. New target whenever the active-tier footprint
		// or lock state changes — capture the current value as the source
		// and reset the elapsed clock so the next 0.15s plays out linearly
		// from here. Positional aim drives the ring to the tier's authored
		// AoE radius; Directional uses the locked-mob silhouette or the
		// default outer radius.
		float targetOuter;
		if (aimType == EAimType.Positional)
		{
			targetOuter = positionalRadius;
		}
		else if (mobTargeted)
		{
			targetOuter = mobTargetOuter;
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
			_groundCircle.Visible = true;
			Vector3 tint = mobTargeted
				? new Vector3(_groundRingLockedColor.R, _groundRingLockedColor.G, _groundRingLockedColor.B)
				: Vector3.One;
			_groundCircle.SetInstanceShaderParameter("ring_outer_radius", _currentOuterRadius);
			_groundCircle.SetInstanceShaderParameter("ring_inner_radius", currentInner);
			_groundCircle.SetInstanceShaderParameter("instance_color", tint);
			_groundCircle.Position = ToLocal(_cursorWorldPos);
		}

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
		if (_lastAimType == EAimType.Positional)
		{
			groundScale = _groundRingPositionalAlphaScale;
		}
		else if (_lastMobTargeted)
		{
			groundScale = 1f;
		}
		else
		{
			groundScale = _groundRingUnlockedAlphaScale;
		}
		SetMeshAlpha(_groundCircle, alpha * groundScale);
		SetMeshAlpha(_endKnob, alpha);
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
	bool TryRaycastForward(Vector3 from, Vector3 dir, float distance, out Vector3 hitWorld, out Mob hitMob)
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
		using var envQuery = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Environment);
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
			hitWorld = (Vector3)hurtResult["position"];
			if (hurtResult["collider"].Obj is HurtBox hurtBox)
			{
				hitMob = ItemEventHandlers.FindOwningMob(hurtBox);
			}
			return true;
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
		using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)(ECollisionLayer.Environment | ECollisionLayer.Water));
		query.CollideWithBodies = true;
		query.CollideWithAreas = true;
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
		WeaponState weapon = _player?.Inventory?.GetWeapon(EInventorySlot.WeaponRight);
		ItemActionProfile profile = weapon?.data?.actionProfile;
		if (profile?.chargedActions == null || profile.chargedActions.Count == 0)
		{
			return null;
		}
		ActionRunner runner = _player.Runner;
		if (runner != null
			&& runner.Phase == EActionPhase.Charging
			&& runner.Current.context.sourceSlot == EInventorySlot.WeaponRight)
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
		ItemAction tier = ResolveActiveTier(out _);
		if (tier == null) { return 0f; }
		if (tier.aimType == EAimType.Positional)
		{
			return tier.positionalRange;
		}
		return _player.GetWeaponRange(EInventorySlot.WeaponRight);
	}
}
