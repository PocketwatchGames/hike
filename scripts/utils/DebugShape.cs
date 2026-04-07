using Godot;

[GlobalClass]
public abstract partial class DebugShape : Node3D
{
	protected StandardMaterial3D _material;
	float _fadeTime;
	float _elapsed;

	protected void Init(Color color, float fadeTime, Vector3 position)
	{
		_fadeTime = fadeTime;
		Position = position;
		TopLevel = true;

		_material = new StandardMaterial3D();
		_material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		_material.AlbedoColor = color;
		_material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
	}

	public override void _Process(double delta)
	{
		_elapsed += (float)delta;
		if (_elapsed >= _fadeTime)
		{
			QueueFree();
		}
	}
}
