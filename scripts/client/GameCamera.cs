using Godot;

public partial class GameCamera : Camera3D
{
	[Export] public float pitchDegrees = -65;
	[Export] public float distance = 80;
	[Export] public float rotationTime = 0.5f;

	private const float CLIP_EPSILON = 0.1f;
	private const float CAP_PLANE_Y_BIAS = 0.5f;
	private const float EYE_HEIGHT = 2f;
	private const float PLATEAU_STEP = 4f;
	// Duration of the dithered fade between cutaway elevations. While
	// blending, ceiling-discard shaders stipple the transition band via
	// camera_clip_prev / camera_clip_blend (see clip_dither.gdshaderinc).
	private const float CLIP_FADE_TIME = 0.1f;

	private float _pitchRadians => Mathf.DegToRad(pitchDegrees);
	private float _clip = float.PositiveInfinity;
	// Source Y for the in-progress fade. While `_clipBlend` < 1, shaders
	// blend between this and `_clip`. Equal to `_clip` when idle.
	private float _clipPrev = float.PositiveInfinity;
	private float _clipBlend = 1f;
	// At most one transition can be queued behind the current fade. A
	// second incoming change while a fade is running overwrites it; the
	// queued target is consumed when blend reaches 1. NaN sentinel = none.
	private float _pendingClip = float.NaN;
	private Vector3 _pendingCenter;
	// Yaw is stored in RADIANS (consistent with Q/E rotations that use
	// DegToRad(90)). Initial value = 45° → DegToRad(45). Previously was
	// raw `45` which normalized to ~58.3° via 45 mod 2π, throwing off
	// reflection-sun alignment expectations.
	private float _yaw = Mathf.Pi / 4f;
	private float _destYaw = Mathf.Pi / 4f;
	private bool _clipAlways = false;
	private MeshInstance3D _clipCapPlane;
	private MeshInstance3D _waterCapPlane;
	private SubViewport _capMaskViewport;
	private Camera3D _capMaskCamera;
	private CanvasLayer _capMaskDebugLayer;
	private TextureRect _capMaskDebugRect;
	// Visibility layers — main scene meshes default to bit 0 (layers = 1),
	// cap-mask geometry (added per-chunk in ChunkMesh) is on bit 1
	// (layers = 2). The main camera's cull_mask excludes bit 1 so it
	// doesn't see the mask geometry; the SubViewport camera's cull_mask
	// is bit 1 ONLY so it sees nothing else.
	public const uint MainSceneLayer = 1u << 0;
	public const uint CapMaskLayer = 1u << 1;

	public float Clip => _clip;
	public float Yaw => _yaw;
	public MeshInstance3D WaterCapPlane => _waterCapPlane;
	public bool ManualClipMode { get; set; } = false;

	// Perspective FOV (degrees) that yields the same vertical view extent as
	// the orthographic Size at distance = 80. tan(FOV/2) = Size / (2 * dist)
	// → FOV = 2 * atan(20 / 160) ≈ 14.25°. Narrow enough that perspective
	// distortion is barely noticeable while still letting Godot's volumetric
	// fog froxel pipeline (which assumes perspective) render.
	private const float PERSPECTIVE_FOV_FOR_ORTHO_MATCH = 14.25f;

	public void ApplyProjection(bool perspective)
	{
		if (perspective)
		{
			Projection = ProjectionType.Perspective;
			Fov = PERSPECTIVE_FOV_FOR_ORTHO_MATCH;
		}
		else
		{
			Projection = ProjectionType.Orthogonal;
		}
	}

	public void Init(Node parent)
	{
		ApplyProjection(CVars.cameraPerspective.Value);

		// Main camera only sees the main scene layer; the cap-mask geometry
		// (added per-chunk on CapMaskLayer) is invisible here.
		CullMask = MainSceneLayer;

		// Off-screen render target that builds a per-pixel mask of "where
		// the cap should draw." SubViewport shares the parent's World3D
		// (own_world_3d=false) so it sees the same chunk meshes without
		// us needing to mirror the scene tree, but its camera's cull_mask
		// is CapMaskLayer ONLY — it sees just the mask MeshInstance3Ds
		// added per-chunk in ChunkMesh, never the visible terrain or
		// sprites. Size is matched to the inner pre-upscale viewport in
		// SyncCapMaskCamera so SCREEN_UV in clip_cap maps 1:1.
		_capMaskViewport = new SubViewport();
		_capMaskViewport.OwnWorld3D = false;
		_capMaskViewport.HandleInputLocally = false;
		_capMaskViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		_capMaskViewport.RenderTargetClearMode = SubViewport.ClearMode.Always;
		_capMaskViewport.TransparentBg = false;
		_capMaskViewport.Disable3D = false;
		_capMaskViewport.Msaa3D = Viewport.Msaa.Disabled;
		_capMaskViewport.Size = new Vector2I(2, 2);
		parent.AddChild(_capMaskViewport);

		_capMaskCamera = new Camera3D();
		_capMaskCamera.CullMask = CapMaskLayer;
		_capMaskCamera.Current = true;
		// Clear to WHITE = "cap should draw here." The terrain mask material
		// renders BLACK over visible (below-clip) terrain so those pixels
		// fail the cap's `mask >= 0.5` test and the cap doesn't draw
		// there. Above-clip front-faces are discarded so the white clear
		// shows through. The back-face mask material then writes white
		// over any underground front-faces that painted black through
		// other clipped solids, restoring the cap mask in those zones.
		// Environment is stripped of every non-essential effect since the
		// mask render only needs raw albedo writes — no lighting, no
		// post-process, no auto-exposure.
		var maskEnv = new Environment();
		maskEnv.BackgroundMode = Environment.BGMode.Color;
		maskEnv.BackgroundColor = new Color(1, 1, 1, 1);
		maskEnv.AmbientLightSource = Environment.AmbientSource.Disabled;
		maskEnv.ReflectedLightSource = Environment.ReflectionSource.Disabled;
		maskEnv.TonemapMode = Environment.ToneMapper.Linear;
		_capMaskCamera.Environment = maskEnv;
		_capMaskViewport.AddChild(_capMaskCamera);

		// Debug overlay: drives the `cap_mask_debug` CVar. When toggled on,
		// draws the SubViewport's texture as a full-screen TextureRect so
		// the mask is directly visible on top of the game.
		_capMaskDebugLayer = new CanvasLayer();
		_capMaskDebugLayer.Layer = 100;
		parent.AddChild(_capMaskDebugLayer);
		_capMaskDebugRect = new TextureRect();
		_capMaskDebugRect.Texture = _capMaskViewport.GetTexture();
		_capMaskDebugRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_capMaskDebugRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
		_capMaskDebugRect.StretchMode = TextureRect.StretchModeEnum.Scale;
		_capMaskDebugRect.Visible = false;
		_capMaskDebugLayer.AddChild(_capMaskDebugRect);

		var capShader = GD.Load<Shader>("res://shaders/clip_cap.gdshader");
		var capMaterial = new ShaderMaterial();
		capMaterial.Shader = capShader;
		capMaterial.RenderPriority = 1;
		capMaterial.SetShaderParameter("cap_mask_tex", _capMaskViewport.GetTexture());

		var planeMesh = new PlaneMesh();
		planeMesh.Size = new Vector2(1000, 1000);

		_clipCapPlane = new MeshInstance3D();
		_clipCapPlane.Mesh = planeMesh;
		_clipCapPlane.MaterialOverride = capMaterial;
		_clipCapPlane.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		_clipCapPlane.Visible = false;
		parent.AddChild(_clipCapPlane);

		var waterCapShader = GD.Load<Shader>("res://shaders/water_clip_cap.gdshader");
		var waterCapMaterial = new ShaderMaterial();
		waterCapMaterial.Shader = waterCapShader;
		waterCapMaterial.RenderPriority = 2;
		// Same two ripple normal-map textures that voxel_water uses, so the
		// cap surface animates continuously with the water beneath it.
		var rippleA = GD.Load<Texture2D>("res://assets/textures/water_ripple_a.tres");
		var rippleB = GD.Load<Texture2D>("res://assets/textures/water_ripple_b.tres");
		waterCapMaterial.SetShaderParameter("ripple_tex_a", rippleA);
		waterCapMaterial.SetShaderParameter("ripple_tex_b", rippleB);

		_waterCapPlane = new MeshInstance3D();
		_waterCapPlane.Mesh = planeMesh;
		_waterCapPlane.MaterialOverride = waterCapMaterial;
		_waterCapPlane.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		_waterCapPlane.Visible = false;
		parent.AddChild(_waterCapPlane);

		GlobalRotation = new Vector3(_pitchRadians, _yaw, 0);
		_destYaw = GlobalRotation.Y;
		_yaw = _destYaw;

		PushClipGlobals();
	}

	public void SetCapMaskDebugVisible(bool visible)
	{
		if (_capMaskDebugRect != null)
		{
			_capMaskDebugRect.Visible = visible;
		}
	}

	public void SetInitialPosition(Vector3 playerPosition)
	{
		GlobalPosition = playerPosition + GlobalTransform.Basis.Z * distance;
	}

	public void UpdateCamera(double deltaTime, Vector3 playerPosition)
	{
		float t = 1f - Mathf.Pow(0.01f, (float)deltaTime / rotationTime);
		_yaw = Mathf.LerpAngle(_yaw, _destYaw, t);

		GlobalRotation = new Vector3(_pitchRadians, _yaw, 0);
		GlobalPosition = playerPosition + GlobalTransform.Basis.Z * distance;

		if (!ManualClipMode)
		{
			UpdateClip(playerPosition);
		}

		AdvanceClipFade((float)deltaTime);
	}

	// Mirrors the main camera's pose and projection into the off-screen
	// cap-mask camera. Must be called AFTER GameClient's chunky-pixel
	// camera snap so the mask renders with the same snapped pose as the
	// visible scene — otherwise the mask is sub-texel offset from the
	// main render and the cap edges shimmer. Also resizes the mask
	// SubViewport to exactly match the inner pre-upscale viewport so
	// SCREEN_UV samples line up 1:1 with the chunky pixel grid.
	public void SyncCapMaskCamera(Vector2I innerViewportSize)
	{
		// Same init-order guard ApplyClipPlanes uses: GameClient._Process
		// can call this before the cap-mask camera + viewport are wired
		// during init. Skip until they exist; the cap-mask render is
		// purely a visual augmentation and missing one frame on startup
		// is harmless.
		if (_capMaskCamera == null || _capMaskViewport == null)
		{
			return;
		}
		_capMaskCamera.GlobalTransform = GlobalTransform;
		if (Projection == ProjectionType.Perspective)
		{
			_capMaskCamera.Projection = ProjectionType.Perspective;
			_capMaskCamera.Fov = Fov;
		}
		else
		{
			_capMaskCamera.Projection = ProjectionType.Orthogonal;
			_capMaskCamera.Size = Size;
		}
		_capMaskCamera.Near = Near;
		_capMaskCamera.Far = Far;

		var targetSize = new Vector2I(Mathf.Max(1, innerViewportSize.X), Mathf.Max(1, innerViewportSize.Y));
		if (_capMaskViewport.Size != targetSize)
		{
			_capMaskViewport.Size = targetSize;
		}
	}

	private void UpdateClip(Vector3 playerPos)
	{
		float cameraY = GlobalPosition.Y;
		Vector3 rayFrom = playerPos;
		Vector3 rayTo = new Vector3(playerPos.X, cameraY, playerPos.Z);

		var spaceState = GetWorld3D().DirectSpaceState;
		using var query = PhysicsRayQueryParameters3D.Create(rayFrom, rayTo);
		query.CollisionMask = (uint)ECollisionLayer.Environment;
		var result = spaceState.IntersectRay(query);

		float eyeY = playerPos.Y + EYE_HEIGHT;
		float alwaysClip = Mathf.Ceil(eyeY / PLATEAU_STEP) * PLATEAU_STEP - CLIP_EPSILON;

		float targetClip;
		if (result.Count > 0)
		{
			Vector3 hitPosition = (Vector3)result["position"];
			float ceilingClip = hitPosition.Y - CLIP_EPSILON;
			targetClip = _clipAlways ? Mathf.Min(ceilingClip, alwaysClip) : ceilingClip;
		}
		else if (_clipAlways)
		{
			targetClip = alwaysClip;
		}
		else
		{
			targetClip = float.PositiveInfinity;
		}

		RequestClip(targetClip, playerPos);
	}

	// Routes a target clip Y through the fade state. If we're idle, kicks
	// off a fresh 0.1s blend from current → target. If a fade is already
	// running, stashes the target as the single pending slot (overwriting
	// any earlier pending) and consumes it once the current fade lands.
	private void RequestClip(float targetClip, Vector3 centerPos)
	{
		bool fading = _clipBlend < 1f;
		if (!fading)
		{
			if (targetClip != _clip)
			{
				StartClipFade(targetClip, centerPos);
			}
			else
			{
				ApplyClipPlanes(centerPos);
			}
		}
		else
		{
			if (targetClip == _clip)
			{
				_pendingClip = float.NaN;
			}
			else
			{
				_pendingClip = targetClip;
				_pendingCenter = centerPos;
			}
			ApplyClipPlanes(centerPos);
		}
	}

	private void StartClipFade(float targetClip, Vector3 centerPos)
	{
		_clipPrev = _clip;
		_clip = targetClip;
		_clipBlend = 0f;
		_pendingClip = float.NaN;
		ApplyClipPlanes(centerPos);
		PushClipGlobals();
	}

	private void AdvanceClipFade(float deltaTime)
	{
		if (_clipBlend >= 1f)
		{
			return;
		}
		_clipBlend = Mathf.Min(1f, _clipBlend + deltaTime / CLIP_FADE_TIME);
		if (_clipBlend >= 1f)
		{
			_clipPrev = _clip;
			if (!float.IsNaN(_pendingClip) && _pendingClip != _clip)
			{
				StartClipFade(_pendingClip, _pendingCenter);
				return;
			}
			_pendingClip = float.NaN;
		}
		PushClipGlobals();
	}

	private void ApplyClipPlanes(Vector3 centerPos)
	{
		// UpdateClip can fire from GameClient._Process before the cap-plane
		// nodes are wired (the clip-cap mesh + materials are created later
		// in init alongside the SubViewport for the cap mask). Skip in
		// that case; clip globals still propagate to shaders via the
		// PushClipGlobals path.
		if (_clipCapPlane == null || _waterCapPlane == null)
		{
			return;
		}
		if (_clip < float.PositiveInfinity)
		{
			_clipCapPlane.Visible = CVars.ceilingCap.Value;
			_clipCapPlane.GlobalPosition = new Vector3(centerPos.X, _clip - CAP_PLANE_Y_BIAS, centerPos.Z);
			_waterCapPlane.Visible = true;
			_waterCapPlane.GlobalPosition = new Vector3(centerPos.X, _clip - CAP_PLANE_Y_BIAS, centerPos.Z);
		}
		else
		{
			_clipCapPlane.Visible = false;
			_waterCapPlane.Visible = false;
		}
	}

	private void PushClipGlobals()
	{
		RenderingServer.GlobalShaderParameterSet("camera_clip", _clip);
		RenderingServer.GlobalShaderParameterSet("camera_clip_prev", _clipPrev);
		RenderingServer.GlobalShaderParameterSet("camera_clip_blend", _clipBlend);
	}

	public void RotateLeft()
	{
		_destYaw += Mathf.DegToRad(90);
	}

	public void RotateRight()
	{
		_destYaw -= Mathf.DegToRad(90);
	}

	public void ToggleClipAlways()
	{
		_clipAlways = !_clipAlways;
	}

	public void SetClip(float clipY, Vector3 centerPos)
	{
		RequestClip(clipY, centerPos);
	}
}
