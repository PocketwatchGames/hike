using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class GameClient : Node3D
{
	[Export] public Camera3D camera;
	[Export] public Hud hud;
	[Export] public Node2D worldHUD;
	[Export] public PackedScene hudTextScene;
	float _cameraPitchRadians => Mathf.DegToRad(cameraPitchDegrees);
	[Export] public float cameraPitchDegrees = -65;
	[Export] public float cameraDistance = 20;
	[Export] public float cameraRotationTime = 0.5f;


	private const float CAMERA_CLIP_EPSILON = 0.1f;
	private const float CAP_PLANE_Y_BIAS = 0.5f;
	private const float CAMERA_CLIP_ALWAYS_HEIGHT = 3f;
	private float _cameraClip = float.PositiveInfinity;

	public Action onInit;
	public Action<Vector3, string, ulong, float, Color> onHudText;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;
	

	public bool paused { get; private set; } = false;

	private static readonly double[] timeScales = { 1.0, 2.0, 4.0 };
	private int timeScaleIndex = 0;

	Vector2 _inputDir = Vector2.Zero;
	float _cameraYaw = 45;
	float _destYaw = 45;
	bool _cameraClipAlways = false;
	Player _player;
	VoxelWorld _voxelWorld;
	MeshInstance3D _clipCapPlane;

	public async void Init(Vector3 playerPosition, PackedScene playerScene)
	{
		onHudText += OnHudTextRequested;
		onInit?.Invoke();

		var worldData = new WorldData();

		_voxelWorld = new VoxelWorld();
		AddChild(_voxelWorld);
		_voxelWorld.SetCamera(camera);
		_voxelWorld.Initialize(worldData, playerPosition);

		while (!_voxelWorld.IsSpawnChunkReady(playerPosition))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}

		_player = playerScene.Instantiate<Player>();
		AddChild(_player);
		_player.GlobalPosition = playerPosition;
		_player.GlobalRotation = Vector3.Zero;

		_voxelWorld.SetPlayerPositionSource(() => _player.GlobalPosition);

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
		AddChild(_clipCapPlane);

		camera.GlobalRotation = new Vector3(_cameraPitchRadians, _cameraYaw, 0);
		_destYaw = camera.GlobalRotation.Y;
		_cameraYaw = _destYaw;
		camera.GlobalPosition = _player.GlobalPosition + camera.GlobalTransform.Basis.Z * cameraDistance;
	}

	public override void _Process(double deltaTime)
	{
		if (_player == null || ConsoleUI.IsOpen || paused)
		{
			return;
		}
		Vector2 inputDir = Vector2.Zero;
		inputDir.X -= Input.GetActionStrength("MoveLeft");
		inputDir.X += Input.GetActionStrength("MoveRight");
		inputDir.Y -= Input.GetActionStrength("MoveUp");
		inputDir.Y += Input.GetActionStrength("MoveDown");
		_inputDir = inputDir.LengthSquared() > 1 ? inputDir.Normalized() : inputDir;

		float t = 1f - Mathf.Pow(0.01f, (float)deltaTime / cameraRotationTime);
		_cameraYaw = Mathf.LerpAngle(_cameraYaw, _destYaw, t);

		camera.GlobalRotation = new Vector3(_cameraPitchRadians, _cameraYaw, 0);
		camera.GlobalPosition = _player.GlobalPosition + camera.GlobalTransform.Basis.Z * cameraDistance;

		UpdateCameraClip();
		_voxelWorld.CullProps(_cameraClip);
	}

	private void UpdateCameraClip()
	{
		Vector3 playerPos = _player.GlobalPosition;
		float cameraY = camera.GlobalPosition.Y;
		Vector3 rayFrom = playerPos;
		Vector3 rayTo = new Vector3(playerPos.X, cameraY, playerPos.Z);

		var spaceState = GetWorld3D().DirectSpaceState;
		var query = PhysicsRayQueryParameters3D.Create(rayFrom, rayTo);
		query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
		var result = spaceState.IntersectRay(query);

		float alwaysClip = playerPos.Y + CAMERA_CLIP_ALWAYS_HEIGHT - CAMERA_CLIP_EPSILON;

		if (result.Count > 0)
		{
			Vector3 hitPosition = (Vector3)result["position"];
			float ceilingClip = hitPosition.Y - CAMERA_CLIP_EPSILON;
			_cameraClip = _cameraClipAlways ? Mathf.Min(ceilingClip, alwaysClip) : ceilingClip;
			_clipCapPlane.Visible = CVars.ceilingCap.Value;
			_clipCapPlane.GlobalPosition = new Vector3(playerPos.X, _cameraClip - CAP_PLANE_Y_BIAS, playerPos.Z);
		}
		else if (_cameraClipAlways)
		{
			_cameraClip = alwaysClip;
			_clipCapPlane.Visible = CVars.ceilingCap.Value;
			_clipCapPlane.GlobalPosition = new Vector3(playerPos.X, _cameraClip - CAP_PLANE_Y_BIAS, playerPos.Z);
		}
		else
		{
			_cameraClip = float.PositiveInfinity;
			_clipCapPlane.Visible = false;
		}

		RenderingServer.GlobalShaderParameterSet("camera_clip", _cameraClip);
	}

	public override void _PhysicsProcess(double delta)
	{
		base._PhysicsProcess(delta);
		if (_player != null && !paused)
		{
			_player.InputDir = _inputDir;
			_player.CameraYaw = _cameraYaw;
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		base._UnhandledInput(e);

		if (e.IsActionPressed("TogglePause"))
		{
			TogglePause();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (paused)
		{
			return;
		}

		if (e.IsActionPressed("CameraLeft"))
		{
			_destYaw += Mathf.DegToRad(90);
		}

		if (e.IsActionPressed("CameraRight"))
		{
			_destYaw -= Mathf.DegToRad(90);
		}

		if (e.IsActionPressed("CameraDown"))
		{
			_cameraClipAlways = !_cameraClipAlways;
		}

	}

	void OnHudTextRequested(Vector3 position, string text, ulong fadeMs, float verticalMovement, Color color)
	{
		HudText.Create(hudTextScene, camera, position, text, fadeMs, verticalMovement, color, this);
	}

	public void TogglePause()
	{
		paused = !paused;
		onPauseToggled?.Invoke(paused);
	}

	public void Save()
	{
		SaveGame.Save(CVars.savePath.Value);
	}

	public void QuitToMenu()
	{
		onQuitToMenu?.Invoke();
	}

}
