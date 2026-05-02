using Godot;
using System.Collections.Generic;

public partial class Fx : Node3D
{
	// When false (default), the node frees itself once every child particle
	// system has stopped emitting AND every child audio player has stopped —
	// the canonical fire-and-forget puff. When true, audio re-plays on each
	// Finished signal so the wrapped AudioStreamRandomizer keeps picking new
	// variants, and particles run continuously; the node only frees after
	// Stop() is called and the trailing audio + particles wind down.
	[Export] private bool _loop;

	readonly List<GpuParticles3D> _particles = new();
	readonly List<AudioStreamPlayer3D> _audio = new();
	bool _stopping;
	// Wall-clock time at which Stop() was called, used to defer free until the
	// longest particle Lifetime has elapsed. Existing particles continue to
	// render after Emitting flips false, and Godot exposes no "any particles
	// still alive" query for continuous emitters, so we gate on lifetime.
	ulong _stopTimeMs;

	public static Fx Create(PackedScene scene, Node parent, Vector3 position)
	{
		Fx effect = scene.Instantiate<Fx>();
		effect.Position = position;
		parent.AddChild(effect);
		// Godot rejects AddChild when the parent is mid-setup (data.blocked > 0)
		// — this happens when an Fx is spawned from a sibling's _Ready while
		// the common ancestor's add_child is still on the stack (e.g.
		// CarrierLight._Ready firing while Mob.Initialize is adding the mob
		// to the world). The native call prints an error and silently no-ops,
		// so we detect the failure via GetParent() and retry on the next idle
		// frame. Particles / audio stay dormant until the deferred AddChild
		// fires _Ready.
		if (effect.GetParent() == null)
		{
			parent.CallDeferred(Node.MethodName.AddChild, effect);
		}
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
			else if (c is AudioStreamPlayer3D a)
			{
				_audio.Add(a);
				if (_loop)
				{
					AudioStreamPlayer3D captured = a;
					a.Finished += () => OnAudioFinished(captured);
				}
				a.Play();
				if (CVars.audioLog.Value)
				{
					string streamPath = a.Stream?.ResourcePath ?? "<inline>";
					GD.Print($"[audio] t={Time.GetTicksMsec()}ms scene={Name} stream={streamPath}");
				}
			}
		}
	}

	// Loop-mode chain: re-Play the same player so any wrapped
	// AudioStreamRandomizer rolls a fresh variant. Once Stop() has flipped
	// _stopping the chain breaks and the loop winds down.
	private void OnAudioFinished(AudioStreamPlayer3D a)
	{
		if (_stopping || !IsInsideTree())
		{
			return;
		}
		a.Play();
	}

	// Owner calls this when the loop should end. Particles stop spawning new
	// puffs (existing ones fade naturally) and audio is halted immediately.
	// The hard audio stop is required because loop scenes often wrap an
	// intrinsically-looping stream (any `*_lp.wav` with loop_mode=1 in its
	// .import) — for those, Finished is never emitted and the chain handler
	// can't break naturally; without an explicit Stop() the node would sit
	// here with Playing=true forever and never free. The trade-off for
	// chain-mode loops (multi-sample randomizers) is a clipped trailing
	// sample, which is barely audible and worth the cross-cutting fix.
	public void Stop()
	{
		if (_stopping)
		{
			return;
		}
		_stopping = true;
		_stopTimeMs = Time.GetTicksMsec();
		foreach (var p in _particles)
		{
			p.Emitting = false;
		}
		foreach (var a in _audio)
		{
			a.Stop();
		}
	}

	public override void _Process(double delta)
	{
		if (_loop)
		{
			if (!_stopping)
			{
				return;
			}
			// Wait for audio to wind down AND for the longest particle
			// Lifetime to have elapsed since Stop(). Without the lifetime
			// gate, free can fire while the trailing burst is still on
			// screen — Emitting reads false the instant we set it, but
			// already-spawned particles keep rendering for `Lifetime` more
			// seconds.
			ulong now = Time.GetTicksMsec();
			foreach (var p in _particles)
			{
				ulong needed = (ulong)(p.Lifetime * 1000.0);
				if (now - _stopTimeMs < needed)
				{
					return;
				}
			}
			if (_audio.TrueForAll(a => !a.Playing))
			{
				QueueFree();
			}
			return;
		}
		if (_particles.TrueForAll(p => !p.Emitting) && _audio.TrueForAll(a => !a.Playing))
		{
			QueueFree();
		}
	}
}
