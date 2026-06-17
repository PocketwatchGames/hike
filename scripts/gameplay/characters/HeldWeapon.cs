using Godot;
using System.Collections.Generic;

// Root script for a wielded weapon's heldModel scene (club, bow, bomb, scroll,
// ...). Its job is to host the idle Fx a weapon mod attaches to the weapon in
// hand — a Flaming sword's flame, a glowing enchant aura. HeldItemVisual pushes
// the wielded weapon's composed idle-fx set here (via SetIdleFx) whenever the
// weapon is drawn, and the fx parents to idleFxAnchor so an authored scene can
// point the flame at a blade tip or a haft socket (its origin if unset). The fx
// are loop Fx — Stop()ped (so their trailing particles fade) when the set
// changes or clears; a weapon-model swap frees the whole instance and its fx
// with it.
[GlobalClass]
public partial class HeldWeapon : Node3D
{
	// Where idle mod Fx parent. Null = this node's origin. Point it at a blade
	// tip / haft so a Flaming enchant's flame sits where it reads best on this
	// particular weapon.
	[Export] public Node3D idleFxAnchor;

	private readonly List<Fx> _idleFx = new();
	// The scene set currently playing, so a repeated SetIdleFx with the same
	// scenes (the per-press call site re-pushes every swing) is a no-op rather
	// than a stop-and-respawn flicker.
	private readonly List<PackedScene> _idleScenes = new();

	// Replace the idle Fx playing on this weapon. No-op when the scene set is
	// unchanged. Null/empty clears all idle fx. Loops are Stop()ped so their
	// trailing particles fade rather than popping off.
	public void SetIdleFx(Godot.Collections.Array<PackedScene> scenes)
	{
		if (SameScenes(scenes))
		{
			return;
		}
		foreach (Fx fx in _idleFx)
		{
			if (GodotObject.IsInstanceValid(fx))
			{
				fx.Stop();
			}
		}
		_idleFx.Clear();
		_idleScenes.Clear();

		if (scenes == null)
		{
			return;
		}
		Node3D anchor = idleFxAnchor ?? this;
		for (int i = 0; i < scenes.Count; i++)
		{
			PackedScene scene = scenes[i];
			if (scene == null)
			{
				continue;
			}
			Fx fx = Fx.Create(scene, anchor, Vector3.Zero);
			if (fx != null)
			{
				_idleFx.Add(fx);
			}
			_idleScenes.Add(scene);
		}
	}

	private bool SameScenes(Godot.Collections.Array<PackedScene> scenes)
	{
		int count = scenes?.Count ?? 0;
		if (count != _idleScenes.Count)
		{
			return false;
		}
		for (int i = 0; i < count; i++)
		{
			if (scenes[i] != _idleScenes[i])
			{
				return false;
			}
		}
		return true;
	}
}
