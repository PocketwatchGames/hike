using Godot;
using System;
using System.Collections.Generic;

public partial class Player : CharacterBody3D
{
	// Board a rideable. Called from the vehicle's IInteractive.Complete (the
	// Board action's OpenInteractive event) — i.e. from inside the runner tick
	// during _PhysicsProcess, so the actual reparent (a physics-tree mutation)
	// is deferred to the idle frame boundary via AttachToMount.
	public void Mount(IRideable vehicle)
	{
		if (vehicle == null || _mount != null)
		{
			return;
		}
		_mount = vehicle;
		_preMountParent = GetParent();
		// Drop transient locomotion so dismount resumes from a clean slate.
		Velocity = Vector3.Zero;
		_dashTimeRemaining = 0f;
		_skating = false;
		_skidding = false;
		_sneaking = false;
		SetCurInteractive(null);
		_highlightInteractive = null;
		onHighlightChanged?.Invoke(null);
		vehicle.OnMounted(this);
		Callable.From(AttachToMount).CallDeferred();
	}

	private void AttachToMount()
	{
		if (_mount?.SeatAnchor == null)
		{
			return;
		}
		// keepGlobalTransform:false leaves the local transform as-is; we then
		// zero it so the rider sits exactly on the seat anchor and faces the
		// vehicle's forward.
		Reparent(_mount.SeatAnchor, keepGlobalTransform: false);
		Position = Vector3.Zero;
		Rotation = Vector3.Zero;
	}

	// Leave the current vehicle and drop onto the nearest shore. Called from
	// ProcessInput (which runs in _Process, not the physics flush), so the
	// reparent is safe to do inline here.
	public void Dismount()
	{
		if (_mount == null)
		{
			return;
		}
		IRideable vehicle = _mount;
		Vector3 dropPos = vehicle.GetDismountPosition();
		_mount = null;
		vehicle.OnDismounted(this);

		Node parent = (_preMountParent != null && IsInstanceValid(_preMountParent))
			? _preMountParent
			: GetParent();
		_preMountParent = null;
		if (parent != null && parent != GetParent())
		{
			Reparent(parent, keepGlobalTransform: false);
		}
		GlobalPosition = dropPos;
		Rotation = Vector3.Zero;
		Velocity = Vector3.Zero;
		_grounded = false;
	}

	// Emergency dismount used when the vehicle itself is being freed (its
	// origin chunk evicted out from under a long voyage) — hand the rider back
	// to the world so freeing the parented vehicle doesn't free the player too.
	// Mirrors Dismount minus the vehicle-side calls (the vehicle is mid-
	// teardown). Pure tree move + state reset — no spawning, safe from the
	// vehicle's _ExitTree.
	public void ForceDismount(Node fallbackParent, Vector3 pos)
	{
		if (_mount == null)
		{
			return;
		}
		_mount = null;
		Node parent = (_preMountParent != null && IsInstanceValid(_preMountParent) && _preMountParent.IsInsideTree())
			? _preMountParent
			: fallbackParent;
		_preMountParent = null;
		if (parent != null && IsInstanceValid(parent) && parent.IsInsideTree())
		{
			Reparent(parent, keepGlobalTransform: false);
			GlobalPosition = pos;
		}
		Rotation = Vector3.Zero;
		Velocity = Vector3.Zero;
		_grounded = false;
	}

	// Minimal per-frame upkeep while mounted: keep status effects ticking and
	// drive the seated animation loop. All locomotion, gravity, water, and
	// collision are owned by the vehicle (the rider rides its transform).
	private void TickMounted(float dt)
	{
		_statusEffects?.Tick(dt);
		UpdateNightVisionShaderGlobal();
		UpdateAnimation();
	}

	// Drop any highlighted or current interactive without going through the
	// proximity-detect path. Modal screens (merchant, etc.) that take focus
	// while the player is still standing next to the NPC call this so the
	// interact HUD and highlight overlay don't persist underneath the modal.
	// ProcessInput is gated off by GameClient.InputSuppressed while the modal
	// is open, so UpdateHighlightInteractive doesn't re-detect underneath;
	// the next physics frame after close re-evaluates from scratch.
	public void ClearInteractive()
	{
		if (_curInteractive != null)
		{
			SetCurInteractive(null);
		}
		if (_highlightInteractive != null)
		{
			_highlightInteractive = null;
			onHighlightChanged?.Invoke(null);
		}
	}

	public void CloseInteractMenu()
	{
		InteractMenuOpen = false;
		InteractHoldProgress = 0f;
		_interactPressActive = false;
	}
	// HUD progress fill while the runner is driving an interactive action.
	// Reads directly off the in-flight PlayerAction so the bar reflects what
	// the runner is actually doing — no separate timer to keep in sync.
	public float ClientInteractProgress
	{
		get
		{
			if (_runner == null || !_runner.IsBusy)
			{
				return 0f;
			}
			ref readonly PlayerAction action = ref _runner.Current;
			if (action.interactiveAction == null || _world == null)
			{
				return 0f;
			}
			ulong total = action.endMs > action.activateMs ? action.endMs - action.activateMs : 0;
			if (total == 0)
			{
				return 0f;
			}
			ulong now = _world.GameTimeMs;
			ulong elapsed = now > action.activateMs ? now - action.activateMs : 0;
			return Mathf.Clamp((float)elapsed / total, 0f, 1f);
		}
	}

	void SetCurInteractive(IInteractive value, int actionIndex = 0)
	{
		if (_curInteractive != value || _curInteractiveActionIndex != actionIndex)
		{
			_curInteractive = value;
			_curInteractiveActionIndex = value != null ? actionIndex : 0;
			onInteractChanged?.Invoke(value);
		}
	}

	private void UpdateHighlightInteractive()
	{
		// Bird's-eye and camp both suppress interactive highlighting entirely — no
		// outline, no interact prompt. Clear any target held when the state began
		// so GameClient drops the outline + interact HUD; this also runs every
		// physics tick, so it keeps the campfire (which the player is standing in)
		// from re-highlighting itself underneath the camp screen.
		if (_birdsEye || _camping)
		{
			if (_highlightInteractive != null)
			{
				_highlightInteractive = null;
				onHighlightChanged?.Invoke(null);
			}
			return;
		}

		if (_curInteractive != null)
		{
			return;
		}

		IInteractive prevHighlight = _highlightInteractive;

		if (_interactiveCollisions.Count == 0)
		{
			_highlightInteractive = null;
		}
		else
		{
			IInteractive closest = null;
			float closestDist = float.MaxValue;
			foreach (IInteractive interactive in _interactiveCollisions)
			{
				if (interactive is Node3D node && interactive.CanActorInteract(this))
				{
					float dist = GlobalPosition.DistanceSquaredTo(node.GlobalPosition);
					if (dist < closestDist)
					{
						closestDist = dist;
						closest = interactive;
					}
				}
			}
			_highlightInteractive = closest;
		}

		if (_highlightInteractive != prevHighlight)
		{
			onHighlightChanged?.Invoke(_highlightInteractive as Node3D);
		}
	}

	private void OnInteractAreaEntered(Area3D area)
	{
		if (area is InteractiveBox box && box.Interactive != null)
		{
			_interactiveCollisions.Add(box.Interactive);
		}
	}

	private void OnInteractAreaExited(Area3D area)
	{
		if (area is InteractiveBox box && box.Interactive != null)
		{
			_interactiveCollisions.Remove(box.Interactive);
		}
	}

	// The pickup-attract sphere overlaps Loot rigid bodies (ECollisionLayer.Passive).
	// Hand each one this player as its magnet attractor on enter and drop it on
	// exit; the Loot itself gates on material/eligibility + line of sight and
	// drives the flight.
	private void OnPickupAttractBodyEntered(Node body)
	{
		if (body is Loot loot)
		{
			loot.OnEnterAttractRange(this);
		}
	}

	private void OnPickupAttractBodyExited(Node body)
	{
		if (body is Loot loot)
		{
			loot.OnExitAttractRange(this);
		}
	}
}
