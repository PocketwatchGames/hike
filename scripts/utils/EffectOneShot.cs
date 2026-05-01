using Godot;
using System.Collections.Generic;

public partial class EffectOneShot : Node3D
{
	readonly List<GpuParticles3D> _particles = new();

	public static EffectOneShot Create(PackedScene scene, Node parent, Vector3 position)
	{
		EffectOneShot effect = scene.Instantiate<EffectOneShot>();
		effect.Position = position;
		parent.AddChild(effect);
		return effect;
	}

	public override void _Ready()
	{
		foreach (var c in GetChildren())
		{
			if (c is GpuParticles3D p)
			{
				_particles.Add(p);
				p.Emitting = true;
			}
		}
	}

	public override void _Process(double delta)
	{
		if (_particles.TrueForAll(p => !p.Emitting))
		{
			QueueFree();
		}
	}
}
