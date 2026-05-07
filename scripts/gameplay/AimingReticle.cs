using Godot;

// Aiming overlay attached to the player. Forward line, vertical drop line,
// ground ring, and the two parallel spread lines all share
// aiming_reticle.gdshader. Per-frame work: a forward raycast (clamps the line
// at walls), a down raycast (places drop / circle), and the transform / shader
// uniform writes that fall out of those distances.
[GlobalClass]
public partial class AimingReticle : Node3D
{
	// Main forward beam. Mesh authored as a unit-Z box; scale.z + position.z
	// per frame so the line ends at the wall hit (or _maxLineLength if clear).
	// Alpha ramps from 1m→3m via gradient instance uniforms.
	[Export] private MeshInstance3D _mainLine;
	// Two parallel spread markers, fixed length, positioned at the line
	// endpoint with X offsets of ±tan(halfAngle) * lineLength.
	[Export] private MeshInstance3D _spreadLineLeft;
	[Export] private MeshInstance3D _spreadLineRight;
	// Vertical drop from line endpoint to ground. Y scale = drop distance.
	[Export] private MeshInstance3D _dropLine;
	// Always-visible ring on the ground beneath the drop line.
	[Export] private MeshInstance3D _groundCircle;

	// Maximum forward extent of the aim line. The line clips short whenever
	// the forward raycast hits an environment surface.
	[Export] private float _maxLineLength = 5f;
	// Vertical offset of the chest pivot above the player's feet.
	[Export] private float _aimHeight = 1f;
	// World distance from the chest pivot at which the main line's alpha ramp
	// starts (transparent) and finishes (max_alpha).
	[Export] private float _gradientStartDistance = 1f;
	[Export] private float _gradientEndDistance = 3f;
	// Max distance below the line endpoint to search for ground when placing
	// the drop line + ground circle. Misses hide both for that frame.
	[Export] private float _maxGroundDropDistance = 30f;
	// Fixed length of each parallel spread marker.
	[Export] private float _spreadLineLength = 1f;
	// When the forward raycast hits a wall, the drop raycast backs off this
	// far along -forward so it starts in open air rather than coplanar with
	// (or fractionally inside) the wall surface. Without it the drop ray can
	// self-hit or pass through the wall on the first cell, missing the floor
	// entirely and rendering the drop line at max length.
	[Export] private float _wallBackoff = 0.05f;
	// Ground ring geometry. Outer radius matches the source PlaneMesh extent
	// (size 2 * outer); inner radius is the inside of the band. Both are set
	// once on the ground circle's instance uniforms so the shader's discard
	// path produces a clean annulus.
	[Export] private float _groundRingOuterRadius = 0.2f;
	[Export] private float _groundRingInnerRadius = 0.16f;
	// Half-thickness of each line type. Must match the BoxMesh size in the
	// .tscn for the shader's L∞ AA to land on the visible silhouette. Lines
	// run along Z (main, spread) or Y (drop) — line_axis is set per mesh.
	[Export] private float _mainLineRadius = 0.06f;
	[Export] private float _spreadLineRadius = 0.05f;
	[Export] private float _dropLineRadius = 0.05f;

	Player _player;

	const float LineAxisX = 0f;
	const float LineAxisY = 1f;
	const float LineAxisZ = 2f;

	public void Initialize(Player player)
	{
		_player = player;
	}

	public override void _Ready()
	{
		if (_groundCircle != null)
		{
			_groundCircle.SetInstanceShaderParameter("ring_outer_radius", _groundRingOuterRadius);
			_groundCircle.SetInstanceShaderParameter("ring_inner_radius", _groundRingInnerRadius);
		}
		ConfigureLineMesh(_mainLine, LineAxisZ, _mainLineRadius);
		ConfigureLineMesh(_spreadLineLeft, LineAxisZ, _spreadLineRadius);
		ConfigureLineMesh(_spreadLineRight, LineAxisZ, _spreadLineRadius);
		ConfigureLineMesh(_dropLine, LineAxisY, _dropLineRadius);
	}

	static void ConfigureLineMesh(MeshInstance3D mesh, float axis, float radius)
	{
		if (mesh == null)
		{
			return;
		}
		mesh.SetInstanceShaderParameter("line_axis", axis);
		mesh.SetInstanceShaderParameter("line_radius", radius);
	}

	public override void _Process(double delta)
	{
		if (_player == null)
		{
			return;
		}

		bool aiming = _player.IsAiming;

		// Aiming-only meshes follow IsAiming. Drop / circle visibility is
		// also gated below by the down raycast.
		if (_mainLine != null) { _mainLine.Visible = aiming; }
		if (_spreadLineLeft != null) { _spreadLineLeft.Visible = aiming; }
		if (_spreadLineRight != null) { _spreadLineRight.Visible = aiming; }

		Vector3 chestWorld = _player.GlobalPosition + Vector3.Up * _aimHeight;
		Vector3 forward = GlobalTransform.Basis.Z.Normalized();

		// Forward raycast: clamp the line at the first environment hit so it
		// doesn't bury into geometry. Without this clamp the drop raycast
		// could start inside a wall and miss the floor entirely, producing
		// the "infinite" drop line.
		float lineLength = _maxLineLength;
		bool clippedAtWall = false;
		if (TryRaycastForward(chestWorld, forward, _maxLineLength, out Vector3 forwardHit))
		{
			lineLength = (forwardHit - chestWorld).Length();
			clippedAtWall = true;
		}

		// Main line: unit-Z box scaled to lineLength, centered halfway along.
		if (_mainLine != null)
		{
			_mainLine.Position = new Vector3(0f, _aimHeight, lineLength * 0.5f);
			_mainLine.Scale = new Vector3(1f, 1f, lineLength);
			_mainLine.SetInstanceShaderParameter("gradient_origin_world", chestWorld);
			_mainLine.SetInstanceShaderParameter("gradient_start_distance", _gradientStartDistance);
			_mainLine.SetInstanceShaderParameter("gradient_end_distance", _gradientEndDistance);
		}

		// Spread offset at the actual aim distance. tan(halfAngle) * range
		// gives the lateral cone radius at the endpoint; the parallel markers
		// land there so the visible width matches the cone width at where the
		// shot would actually hit.
		float spread01 = ComputeSpread01();
		float halfAngle = ItemEventHandlers.MAX_SPREAD_HALF_ANGLE * spread01;
		float spreadOffset = Mathf.Tan(halfAngle) * lineLength;
		PlaceSpreadLine(_spreadLineLeft, +spreadOffset, lineLength);
		PlaceSpreadLine(_spreadLineRight, -spreadOffset, lineLength);

		// Drop line + ground circle anchor to the (possibly wall-clipped)
		// endpoint, not the max-range endpoint. When the line clipped on a
		// wall we back the drop start off the surface along -forward — the
		// few-cm offset is invisible at game scale and keeps the down ray
		// from starting flush with the wall it just hit.
		Vector3 dropOriginWorld = chestWorld + forward * lineLength;
		if (clippedAtWall)
		{
			dropOriginWorld -= forward * _wallBackoff;
		}
		if (TryRaycastDown(dropOriginWorld, _maxGroundDropDistance, out Vector3 hitWorld))
		{
			float dropHeight = dropOriginWorld.Y - hitWorld.Y;
			if (_dropLine != null)
			{
				bool show = aiming && dropHeight > 0.01f;
				_dropLine.Visible = show;
				if (show)
				{
					Vector3 topLocal = ToLocal(dropOriginWorld);
					_dropLine.Position = topLocal + Vector3.Down * (dropHeight * 0.5f);
					_dropLine.Scale = new Vector3(1f, dropHeight, 1f);
				}
			}
			if (_groundCircle != null)
			{
				_groundCircle.Visible = true;
				_groundCircle.Position = ToLocal(hitWorld);
			}
		}
		else
		{
			if (_dropLine != null) { _dropLine.Visible = false; }
			if (_groundCircle != null) { _groundCircle.Visible = false; }
		}
	}

	void PlaceSpreadLine(MeshInstance3D line, float xOffset, float lineLength)
	{
		if (line == null)
		{
			return;
		}
		// Centered on the line endpoint Z: a unit-Z box scaled to
		// _spreadLineLength straddles the endpoint, half ahead and half
		// behind, producing a clear "bullets land around here" indicator.
		line.Position = new Vector3(xOffset, _aimHeight, lineLength);
		line.Scale = new Vector3(1f, 1f, _spreadLineLength);
	}

	bool TryRaycastForward(Vector3 from, Vector3 dir, float distance, out Vector3 hitWorld)
	{
		hitWorld = default;
		World3D world3D = GetWorld3D();
		if (world3D == null)
		{
			return false;
		}
		Vector3 to = from + dir * distance;
		using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Environment);
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;
		// Exclude the player's own body — otherwise the chest origin sitting
		// inside the capsule self-hits at zero distance.
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

	bool TryRaycastDown(Vector3 from, float distance, out Vector3 hitWorld)
	{
		hitWorld = default;
		World3D world3D = GetWorld3D();
		if (world3D == null)
		{
			return false;
		}
		Vector3 to = from + Vector3.Down * distance;
		using var query = PhysicsRayQueryParameters3D.Create(from, to, (uint)ECollisionLayer.Environment);
		query.CollideWithBodies = true;
		query.CollideWithAreas = false;
		var result = world3D.DirectSpaceState.IntersectRay(query);
		if (result.Count == 0)
		{
			return false;
		}
		hitWorld = (Vector3)result["position"];
		return true;
	}

	// Spread fraction in [0, 1] for the right-hand ranged weapon. While
	// charging that slot, samples the live charge fraction; otherwise samples
	// the snap tier at chargeT=0 — what would happen on an immediate fire.
	float ComputeSpread01()
	{
		if (_player?.Inventory == null)
		{
			return 0f;
		}
		WeaponState weapon = _player.Inventory.GetWeapon(EInventorySlot.WeaponRight);
		ItemActionProfile profile = weapon?.data?.actionProfile;
		if (profile?.chargedActions == null || profile.chargedActions.Count == 0)
		{
			return 0f;
		}

		ItemAction tier;
		float chargeT;
		ActionRunner runner = _player.Runner;
		if (runner != null
			&& runner.Phase == EActionPhase.Charging
			&& runner.Current.context.sourceSlot == EInventorySlot.WeaponRight)
		{
			tier = runner.Current.selectedTier ?? profile.chargedActions[0];
			chargeT = runner.CurrentChargeT;
		}
		else
		{
			tier = profile.chargedActions[0];
			chargeT = 0f;
		}
		if (tier?.accuracyScaleCurve == null)
		{
			return 0f;
		}
		return tier.accuracyScaleCurve.Sample(Mathf.Clamp(chargeT, 0f, 1f));
	}
}
