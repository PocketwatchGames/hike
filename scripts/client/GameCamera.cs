using Godot;

public partial class GameCamera : Camera3D
{
	[Export] public float pitchDegrees = -65;
	[Export] public float distance = 80;
	[Export] public float rotationTime = 0.5f;

	private const float CLIP_EPSILON = 0.1f;
	private const float CAP_PLANE_Y_BIAS = 0.5f;
	private const float CLIP_ALWAYS_HEIGHT = 3f;

	private float _pitchRadians => Mathf.DegToRad(pitchDegrees);
	private float _clip = float.PositiveInfinity;
	private float _yaw = 45;
	private float _destYaw = 45;
	private bool _clipAlways = false;
	private MeshInstance3D _clipCapPlane;
	private MeshInstance3D _waterCapPlane;

	public float Clip => _clip;
	public float Yaw => _yaw;
	public MeshInstance3D WaterCapPlane => _waterCapPlane;
	public bool ManualClipMode { get; set; } = false;

	public void Init(Node parent)
	{
		var capShader = GD.Load<Shader>("res://shaders/clip_cap.gdshader");
		var capMaterial = new ShaderMaterial();
		capMaterial.Shader = capShader;
		capMaterial.RenderPriority = 1;

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

		_waterCapPlane = new MeshInstance3D();
		_waterCapPlane.Mesh = planeMesh;
		_waterCapPlane.MaterialOverride = waterCapMaterial;
		_waterCapPlane.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		_waterCapPlane.Visible = false;
		parent.AddChild(_waterCapPlane);

		GlobalRotation = new Vector3(_pitchRadians, _yaw, 0);
		_destYaw = GlobalRotation.Y;
		_yaw = _destYaw;
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
	}

	private void UpdateClip(Vector3 playerPos)
	{
		float cameraY = GlobalPosition.Y;
		Vector3 rayFrom = playerPos;
		Vector3 rayTo = new Vector3(playerPos.X, cameraY, playerPos.Z);

		var spaceState = GetWorld3D().DirectSpaceState;
		var query = PhysicsRayQueryParameters3D.Create(rayFrom, rayTo);
		query.CollisionMask = (uint)ECollisionLayer.Environment;
		var result = spaceState.IntersectRay(query);

		float alwaysClip = playerPos.Y + CLIP_ALWAYS_HEIGHT - CLIP_EPSILON;

		if (result.Count > 0)
		{
			Vector3 hitPosition = (Vector3)result["position"];
			float ceilingClip = hitPosition.Y - CLIP_EPSILON;
			_clip = _clipAlways ? Mathf.Min(ceilingClip, alwaysClip) : ceilingClip;
			_clipCapPlane.Visible = CVars.ceilingCap.Value;
			_clipCapPlane.GlobalPosition = new Vector3(playerPos.X, _clip - CAP_PLANE_Y_BIAS, playerPos.Z);
			_waterCapPlane.Visible = true;
			_waterCapPlane.GlobalPosition = new Vector3(playerPos.X, _clip - CAP_PLANE_Y_BIAS, playerPos.Z);
		}
		else if (_clipAlways)
		{
			_clip = alwaysClip;
			_clipCapPlane.Visible = CVars.ceilingCap.Value;
			_clipCapPlane.GlobalPosition = new Vector3(playerPos.X, _clip - CAP_PLANE_Y_BIAS, playerPos.Z);
			_waterCapPlane.Visible = true;
			_waterCapPlane.GlobalPosition = new Vector3(playerPos.X, _clip - CAP_PLANE_Y_BIAS, playerPos.Z);
		}
		else
		{
			_clip = float.PositiveInfinity;
			_clipCapPlane.Visible = false;
			_waterCapPlane.Visible = false;
		}

		RenderingServer.GlobalShaderParameterSet("camera_clip", _clip);
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
		_clip = clipY;
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
		RenderingServer.GlobalShaderParameterSet("camera_clip", _clip);
	}
}
