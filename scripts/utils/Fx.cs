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

	// Live counts of Fx, AudioStreamPlayer3D, and GpuParticles3D currently in
	// the world. Surface as Godot custom monitors below ("hike/fx/active",
	// "hike/fx/active_audio", "hike/fx/active_particles") so the F3 overlay
	// and the editor's Monitors tab show how many are alive at any moment.
	// At 30fps with a footstep / idle-sound suspicion the question is
	// usually "are we leaking" or "is the steady-state count higher than
	// expected" — these monitors answer it directly.
	private static int _activeFx;
	private static int _activeAudio;
	private static int _activeParticles;
	private static bool _monitorsRegistered;
	public static int ActiveCount => _activeFx;
	public static int ActiveAudioCount => _activeAudio;
	public static int ActiveParticlesCount => _activeParticles;
	// Wall-clock time at which Stop() was called, used to defer free until the
	// longest particle Lifetime has elapsed. Existing particles continue to
	// render after Emitting flips false, and Godot exposes no "any particles
	// still alive" query for continuous emitters, so we gate on lifetime.
	ulong _stopTimeMs;

	public static Fx Create(PackedScene scene, Node parent, Vector3 position)
	{
		using var _prof = Profiler.Sample("Fx.Create");
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
		using var _prof = Profiler.Sample("Fx.Ready");
		EnsureMonitorsRegistered();
		bool audioEnabled = CVars.fxAudio.Value;
		bool particlesEnabled = CVars.fxParticles.Value;
		foreach (var c in GetChildren())
		{
			if (c is GpuParticles3D p)
			{
				_particles.Add(p);
				_activeParticles++;
				p.Emitting = particlesEnabled;
			}
			else if (c is AudioStreamPlayer3D a)
			{
				_audio.Add(a);
				_activeAudio++;
				if (_loop)
				{
					AudioStreamPlayer3D captured = a;
					a.Finished += () => OnAudioFinished(captured);
				}
				if (audioEnabled)
				{
					a.Play();
				}
				if (CVars.audioLog.Value)
				{
					string streamPath = a.Stream?.ResourcePath ?? "<inline>";
					GD.Print($"[audio] t={Time.GetTicksMsec()}ms scene={Name} stream={streamPath}");
				}
			}
		}
		_activeFx++;
	}

	public override void _ExitTree()
	{
		_activeFx--;
		_activeAudio -= _audio.Count;
		_activeParticles -= _particles.Count;
	}

	// Lazy registration so the C# side is the source of truth for the int
	// fields — the editor polls these via the Callable each frame. Skipped
	// in shipping builds where the monitors would just churn the remote
	// debugger; the F3 overlay can still show live counts via the same
	// Performance.GetCustomMonitor lookup.
	private static void EnsureMonitorsRegistered()
	{
		if (_monitorsRegistered)
		{
			return;
		}
		_monitorsRegistered = true;
		Performance.AddCustomMonitor("hike/fx/active", Callable.From(() => (double)_activeFx));
		Performance.AddCustomMonitor("hike/fx/active_audio", Callable.From(() => (double)_activeAudio));
		Performance.AddCustomMonitor("hike/fx/active_particles", Callable.From(() => (double)_activeParticles));
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
