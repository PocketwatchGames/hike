using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public partial class EffectOneShot : Node3D
{
	List<CpuParticles3D> _particles = new List<CpuParticles3D>();

	public static EffectOneShot Create(PackedScene scene, Node parent, Vector3 position)
	{
		EffectOneShot effect = scene.Instantiate<EffectOneShot>();
		effect.Position = position;
		parent.AddChild(effect);
		return effect;
	}

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		foreach (var c in GetChildren())
		{
			if (c is CpuParticles3D p)
			{
				_particles.Add(p);
				p.Emitting = true;
			}
		}
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
		if (_particles.TrueForAll(p => !p.Emitting))
		{
			QueueFree();
		}
	}
}
