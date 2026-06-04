# Rideable Vehicles

Covers `scripts/gameplay/vehicles/` (runtime) and `scripts/data/vehicles/` (authored tuning).

A rideable is anything the player boards and rides — `Boat` today, horses / other
mounts later. The shared shape is `IRideable`; the shared plumbing is
`RideableVehicle`; the per-vehicle physics lives in the concrete subclass.

## The handoff: how riding works

1. **Board** rides the normal interactive system. A `RideableVehicle` *is* an
   `IInteractive` (and `IWorldEntity`). Its authored `_actions` carry a single
   `InteractiveAction` with `verb = Mount` whose `completionEvents` fire
   `[OpenInteractive]`. `GetActions(player)` stashes the player in `_pendingRider`
   (mirrors `ClimbableTree._climber`); the runner's `OpenInteractive` handler
   calls `Complete()`, which calls `_pendingRider.Mount(this)`.
2. **Mount** (`Player.Mount`) latches `_mount`, zeroes transient locomotion,
   fires `vehicle.OnMounted` (which sets the vehicle's `_rider`), and defers the
   reparent via `Callable.From(AttachToMount).CallDeferred()` — Mount is called
   from inside the runner tick during `_PhysicsProcess`, and reparenting a
   physics body mid-step is unsafe. `AttachToMount` reparents the player under
   `SeatAnchor` and zeroes its local transform so it sits on the seat facing the
   vehicle's forward.
3. **While mounted**, `Player._PhysicsProcess` early-returns into `TickMounted`
   (status effects + seated anim only) — all on-foot locomotion, gravity, water,
   and `MoveAndSlide` are skipped. The vehicle owns the body transform; the rider
   rides it via the scene-tree parent. `Player.ProcessInput` still updates the
   steering vectors but drops every action press except Interact (dismount). The
   vehicle reads `Player.MountMoveInput` from its own `_PhysicsProcess`.
4. **Animation**: `UpdateAnimation` has a `_mount != null` branch that loops
   `IdleAnim` / `MoveAnim` (from `RideableData`) based on `IRideable.IsPropelling`.
5. **Dismount** (Interact press, handled in `ProcessInput` which runs from
   `_Process`): reparent back to the pre-mount parent, drop at
   `GetDismountPosition()`.

## Chunk-streaming safety net

The rider is a scene-tree child of the vehicle, so freeing the vehicle would free
the player. In practice the rider is *at* the vehicle, so the streaming radius
(centered on the player) keeps the vehicle's current chunk resident. The edge
case is the vehicle's **origin** chunk evicting after a long voyage — handled by
`RideableVehicle._ExitTree`, which calls `Player.ForceDismount` to hand the rider
back to the world before the vehicle frees. Pure tree move, no spawning (safe
from `_ExitTree`).

## Adding a new vehicle (e.g. Horse)

1. `HorseData : RideableData` with the locomotion tuning; author a `.tres`.
2. `Horse : RideableVehicle` overriding `RideData`, `IsPropelling`,
   `GetDismountPosition`, and `_PhysicsProcess` (its own ground-locomotion).
   Add a `Create(World, HorseSimState)` factory.
3. `HorseSimState : EntitySimState` → `CreateEntity` calls `Horse.Create`.
4. Append a `Tag.Horse` to `EntitySerializer` (never reuse numbers) with
   write/read cases.
5. Author the scene: `CharacterBody3D (Horse)` + hull collision + a visual model
   under a single `Node3D` child + a `ModelAnimator` (`visual` = that child,
   wired to `_modelAnimator` so it faceting/quantizes like the boat) + `SeatAnchor`
   + `HUDNode` + an `InteractiveBox` (layer Interactive, `_interactiveNode = ..`)
   + the Board `InteractiveAction`. Wire the data `.tres`.
6. Add `EAnimation` seated slots and map them in each rider's `PlayerData.animations`.

## Vehicle visual: yaw faceting + animation quantization

The hull reads like the game's pixel-art sprites the same way Player and model
mobs do — through a shared `ModelAnimator`. `RideableVehicle` carries an optional
`[Export] _modelAnimator` and calls `SetActive(true)` on it in `_Ready` (the
animator defaults inactive, since Player/Mob normally pick which of two visuals
is live; a vehicle always shows its model). `ModelAnimator` then runs its two
stylization passes on the visual node: camera-relative yaw snapped to 8 facets
and stepped (`quantizeFps`) playback. Only the **visual child** is faceted — the
`CharacterBody3D` root keeps its smooth physics yaw for travel/steering, exactly
like the player body vs. `PlayerModel`.

Scene shape: the hull meshes live under a single `BoatModel` `Node3D` (the
animator's `visual`); its parent is the `Boat` root, whose yaw is the "true"
heading the facet snaps relative to. The rider sits on `SeatAnchor` (also on the
smooth-yaw root), and the rider's own `ModelAnimator` facets against the same
root yaw and camera, so rider and hull snap to the same octant in lockstep.
The boat has no `AnimationPlayer` of its own yet (placeholder box), so the
quantization pass is dormant (no clip playing) until real boat art adds one —
the wiring is already in place for it.

## Boat physics (`Boat.cs`)

Water-locked momentum. Each `_PhysicsProcess`: find the water surface in the
hull column (`FindWaterSurfaceY`, mirrors `Player.UpdateWaterState`); accelerate
horizontal velocity toward `MountMoveInput * maxSpeed` (or bleed off via `drag`
when released) and swing the hull heading toward travel at `turnRateDegrees`;
settle Y onto the surface (+ a gentle bob) via a velocity so `MoveAndSlide` still
resolves banks. No water column => beached: propulsion refused, `beachedGravity`
settles it to the ground. `IsPropelling` (steer magnitude over
`propellingInputThreshold` while afloat) drives the rider's paddle animation.

## Known stub limitations (this pass is "framework + stub scene")

- `boat.tscn` uses a placeholder `BoxMesh` hull, not real art.
- `BoatIdle` / `BoatPaddle` (`EAnimation` 27 / 28) map to existing placeholder
  clips (`idle` / `using`) in `default_player.tres` — swap in real paddle clips
  by renaming those two `AnimationData` entries once the sprite/model frames exist.
- No mount/dismount fx or sound cues yet.
- Death-while-mounted isn't force-dismounted.
