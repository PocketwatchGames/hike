using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class GameClient : Node3D
{
	public static GameClient Current { get; private set; }

	[Export] public GameCamera camera;
	[Export] public Hud hud;
	[Export] public Node worldHUD;
	[Export] public SubViewport sceneViewport;
	[Export] public MeshInstance3D bloomQuad;
	[Export] public ShaderMaterial upscaleMaterial;
	[Export] public PackedScene hudTextScene;
	[Export] public PackedScene interactHudScene;
	[Export] public ShaderMaterial outlineMaterial;
	[Export] public ShaderMaterial postProcessMaterial;

	public Action onInit;
	public Action<Vector3, string, ulong, float, Color> onHudText;
	public Action<bool> onPauseToggled;
	public Action onQuitToMenu;

	public bool paused { get; private set; } = false;

	Player _player;
	World _world;
	Vector2 _mousePosition;
	Sprite3D _highlightOverlay;
	InteractHUD _interactHUD;
	Vector2 _subpixelTexelOffset;

	public int PixelScale => Math.Max(1, CVars.pixelScale.Value);

	public Vector2 ProjectToScreen(Vector3 worldPos)
	{
		// The upscale shader flips V (sample at 1 - inner_uv.y) to
		// compensate for Godot's Y-up viewport texture storage. That flip
		// inverts the direction of uv_offset.y relative to uv_offset.x, so
		// the Y correction here adds the subpixel offset where X subtracts.
		Vector2 innerPx = camera.UnprojectPosition(worldPos);
		return new Vector2(
			(innerPx.X - _subpixelTexelOffset.X) * PixelScale,
			(innerPx.Y + _subpixelTexelOffset.Y) * PixelScale);
	}

	public override void _Ready()
	{
		Current = this;
		_highlightOverlay = new Sprite3D();
		_highlightOverlay.Name = "HighlightOverlay";
		_highlightOverlay.MaterialOverride = outlineMaterial;
		_highlightOverlay.AlphaCut = SpriteBase3D.AlphaCutMode.Disabled;
		_highlightOverlay.Visible = false;
		sceneViewport.AddChild(_highlightOverlay);

		GetTree().Root.SizeChanged += UpdateViewportSize;
		UpdateViewportSize();

		if (upscaleMaterial != null)
		{
			upscaleMaterial.SetShaderParameter("inner_tex", sceneViewport.GetTexture());
		}
	}

	public async void Init(Vector3 playerPosition, PackedScene playerScene, PlayerSpawnData playerSpawnData, WorldState worldState)
	{
		onHudText += OnHudTextRequested;
		onInit?.Invoke();

		_world = new World();
		_world.onMobSpawned += OnMobSpawned;
		_world.onMobRemoved += OnMobRemoved;
		sceneViewport.AddChild(_world);
		_world.Initialize(worldState, playerPosition, camera, () => _player?.GlobalPosition ?? playerPosition);

		while (!_world.IsSpawnChunkReady(playerPosition))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}

		_player = playerScene.Instantiate<Player>();
		_player.onHighlightChanged += OnPlayerHighlightChanged;
		_player.onInteractChanged += OnPlayerInteractChanged;
		sceneViewport.AddChild(_player);
		_player.Initialize(_world, playerSpawnData, playerPosition, Vector3.Zero);

		_world.SetPlayer(_player);

		camera.Init(sceneViewport);
		camera.SetInitialPosition(_player.GlobalPosition);

		if (camera.WaterCapPlane.MaterialOverride is ShaderMaterial waterCapMat)
		{
			_world.SetLightMapUniforms(waterCapMat);
		}
	}

	public override void _Process(double deltaTime)
	{
		if (_player == null || ConsoleUI.IsOpen || paused)
		{
			return;
		}
		_world.Tick(deltaTime);
		_player.ProcessInput(camera.Yaw);

		camera.UpdateCamera(deltaTime, _player.GlobalPosition);
		SnapCameraAndUpdateUpscale();
		CullProps(camera.Clip);
		UpdatePostProcess();
	}

	void UpdateViewportSize()
	{
		if (sceneViewport == null)
		{
			return;
		}
		Vector2I screenSize = GetTree().Root.Size;
		int scale = Math.Max(1, CVars.pixelScale.Value);
		// +1 pixel padding on each axis for subpixel camera offset.
		int innerW = (screenSize.X + scale - 1) / scale + 1;
		int innerH = (screenSize.Y + scale - 1) / scale + 1;
		sceneViewport.Size = new Vector2I(innerW, innerH);

		if (upscaleMaterial != null)
		{
			Vector2 uvScale = new Vector2(
				(float)screenSize.X / (scale * innerW),
				(float)screenSize.Y / (scale * innerH));
			upscaleMaterial.SetShaderParameter("uv_scale", uvScale);
		}
	}

	void SnapCameraAndUpdateUpscale()
	{
		if (sceneViewport == null || upscaleMaterial == null)
		{
			return;
		}

		int scale = Math.Max(1, CVars.pixelScale.Value);
		Vector2I screenSize = GetTree().Root.Size;
		Vector2I innerSize = sceneViewport.Size;

		// World units per inner-viewport texel. Orthographic camera.Size is
		// the vertical world extent mapped across innerSize.Y texels (Godot
		// derives horizontal size from viewport aspect, so texel width in
		// world equals this too). The camera must snap in multiples of this
		// so every voxel edge projects to the same sub-texel offset frame
		// to frame — otherwise wall pixels crawl within each chunky block.
		float chunky = camera.Size / Mathf.Max(1, innerSize.Y);
		RenderingServer.GlobalShaderParameterSet("sprite_chunky", chunky);

		Vector3 pos = camera.GlobalPosition;
		Basis basis = camera.GlobalBasis;
		Vector3 right = basis.X;
		Vector3 up = basis.Y;
		Vector3 forward = basis.Z;

		float rx = right.Dot(pos);
		float ry = up.Dot(pos);
		float rz = forward.Dot(pos);

		float sx = Mathf.Floor(rx / chunky) * chunky;
		float sy = Mathf.Floor(ry / chunky) * chunky;
		float fracX = rx - sx;
		float fracY = ry - sy;

		camera.GlobalPosition = sx * right + sy * up + rz * forward;

		// fracX/fracY in [0, chunky); convert to texel units (in [0,1) of a
		// single inner texel) and then to UV.
		float texFracX = fracX / chunky;
		float texFracY = fracY / chunky;
		Vector2 uvOffset = new Vector2(texFracX / innerSize.X, texFracY / innerSize.Y);
		_subpixelTexelOffset = new Vector2(texFracX, texFracY);

		upscaleMaterial.SetShaderParameter("uv_offset", uvOffset);
		// uv_scale may drift if pixel_scale is changed at runtime without a
		// window resize; refresh it every frame so the CVar toggle works live.
		Vector2 uvScale = new Vector2(
			(float)screenSize.X / (scale * innerSize.X),
			(float)screenSize.Y / (scale * innerSize.Y));
		upscaleMaterial.SetShaderParameter("uv_scale", uvScale);

		if (sceneViewport.Size.X != innerSize.X || sceneViewport.Size.Y != innerSize.Y)
		{
			UpdateViewportSize();
		}
	}

	void UpdatePostProcess()
	{
		if (postProcessMaterial != null)
		{
			postProcessMaterial.SetShaderParameter("vignette_radius", CVars.vignetteRadius.Value);
			postProcessMaterial.SetShaderParameter("vignette_softness", CVars.vignetteSoftness.Value);
			postProcessMaterial.SetShaderParameter("vignette_strength", CVars.vignetteStrength.Value);
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		base._PhysicsProcess(delta);
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

		if (e is InputEventMouseMotion mouseMotion)
		{
			if (_player != null)
			{
				_mousePosition += mouseMotion.Relative;
				float mouseSensitivity = 0.1f;
				if (_mousePosition.LengthSquared() > 1.0f / (mouseSensitivity * mouseSensitivity)) // Prevent overflow from large mouse movements
				{
					_mousePosition = _mousePosition.Normalized() / mouseSensitivity;
				}
				_player.ProcessMouseMotion(_mousePosition, camera.Yaw);
			}
		}

		if (e.IsActionPressed("CameraLeft"))
		{
			camera.RotateLeft();
		}

		if (e.IsActionPressed("CameraRight"))
		{
			camera.RotateRight();
		}

		if (e.IsActionPressed("CameraDown"))
		{
			camera.ToggleClipAlways();
		}

	}

	void CullProps(float cameraClip)
	{
		foreach (List<Node3D> entities in _world.ActiveEntities.Values)
		{
			foreach (Node3D entity in entities)
			{
				entity.Visible = entity.GlobalPosition.Y < cameraClip;
			}
		}
	}

	void OnPlayerHighlightChanged(Node3D node)
	{
		RemoveHighlight();
		if (node != null)
		{
			ApplyHighlight(node);
		}
	}

	void ApplyHighlight(Node3D node)
	{
		Sprite3D source = FindChildSprite(node);
		if (source == null || !source.Visible)
		{
			return;
		}

		_highlightOverlay.Texture = source.Texture;
		_highlightOverlay.Transform = source.Transform;
		_highlightOverlay.Offset = source.Offset;
		_highlightOverlay.PixelSize = source.PixelSize;
		_highlightOverlay.Billboard = source.Billboard;
		_highlightOverlay.TextureFilter = source.TextureFilter;
		outlineMaterial.SetShaderParameter("texture_albedo", source.Texture);
		_highlightOverlay.Reparent(node, false);
		_highlightOverlay.Visible = true;
	}

	void RemoveHighlight()
	{
		_highlightOverlay.Visible = false;
		_highlightOverlay.Reparent(sceneViewport, false);
	}

	static Sprite3D FindChildSprite(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child is Sprite3D sprite && sprite.Visible)
			{
				return sprite;
			}
		}
		return null;
	}

	void OnHudTextRequested(Vector3 position, string text, ulong fadeMs, float verticalMovement, Color color)
	{
		HudText.Create(hudTextScene, _world, camera, position, text, fadeMs, verticalMovement, color, this);
	}

	void OnPlayerInteractChanged(IInteractive interactive)
	{
		if (_interactHUD != null)
		{
			_interactHUD.QueueFree();
			_interactHUD = null;
		}
		if (interactive != null && interactHudScene != null)
		{
			_interactHUD = InteractHUD.Create(interactHudScene, camera, _player, interactive, worldHUD);
		}
	}

	void OnMobSpawned(Mob mob)
	{
		if (mob.HudScene != null)
		{
			MobHUD.Create(mob.HudScene, camera, mob, worldHUD);
		}
	}

	void OnMobRemoved(Mob mob)
	{
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
