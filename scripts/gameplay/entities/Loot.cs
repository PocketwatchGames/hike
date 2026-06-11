using Godot;
using Godot.Collections;

// World pickup. The pickup model is decided at run time per (player,
// inventory) pair: if the player already has a same-kind non-full stack and
// the whole pile would top off into those existing stacks, walking near the
// pile is enough — InteractArea.BodyEntered fires and the loot deposits.
// Otherwise (fresh item, full stacks, non-stackable, or explicitly dropped by
// the player) the same area's interact-highlight path takes over and pickup
// runs through the action runner so the player has to press Interact. One
// Area3D drives both — the auto-pickup probe and the interact-highlight scan
// share the same volume so the two modes can't disagree on range.
[GlobalClass]
public partial class Loot : RigidBody3D, IInteractive, IWorldEntity
{
	[Export] private CollisionShape3D _collisionShape;
	[Export] private AnimationPlayer _animationPlayer;
	[Export] private Area3D _interactArea;
	[Export] private HurtBox _hurtBox;
	[Export] private Node3D _hudNode;
	[Export] private Sprite3D _sprite;
	// Anchor a LootData.worldModel is parented under when the dropped item
	// renders as a 3D mesh instead of the flat sprite (e.g. the elite crown).
	[Export] private Node3D _modelAnchor;
	[Export] private PackedScene _pickupEffectScene;
	[Export] private PackedScene _spawnEffectScene;
	// Played at the loot's position when it expires (LootData.removeTimeMs).
	// Same Fx.Create one-shot pattern as the pickup/spawn effects; null leaves
	// the despawn silent (e.g. test scenes that don't author a remove cue).
	[Export] private PackedScene _removeEffectScene;
	// How long after spawn an impulse-launched pickup becomes grabbable, even
	// if it's still moving. Lets arrows be recovered mid-flight after the
	// initial firing arc has cleared the shooter; loot that comes to rest
	// before this elapses unlocks pickup at rest via Settle() instead.
	[Export] private float _pickupReadyDelaySeconds = 0.6f;

	// Longest-side pixel budget for an auto-derived world pickup sprite. When
	// an item authors no worldSprite, its (full-res) inventory icon is
	// point-downsampled to this many chunky pixels (see GetChunkyPickupTexture)
	// so the dropped pickup reads at the same chunky scale as hand-authored
	// pickup art instead of rendering the big inventory icon 1:1.
	[Export] private int _pickupMaxChunkyPixels = 16;

	// Water physics tuning. Same shape as MobData / PlayerData — small items
	// bob more aggressively to the surface, ride currents readily, and don't
	// sink fast when displaced downward. Defaults chosen for typical
	// stackable loot; per-loot overrides can be authored on derived scenes
	// (e.g. a heavy iron ingot with low buoyancy that just sinks).
	[Export] private float _buoyancyAcceleration = 15f;
	[Export] private float _waterDrag = 5f;
	[Export] private float _waterSinkSpeed = 1f;
	[Export] private float _waterSurfaceOffset = 0.3f;
	[Export] private float _waterCurrentDrag = 3f;

	// Authored interaction list. The first entry's events should include an
	// OpenInteractive event that triggers Complete() — that's how the runner
	// signals "the loot has been collected." Break / Examine can be authored
	// later as the design evolves.
	[Export] private Array<InteractiveAction> _actions = new();

	private LootSimState _simState;
	private bool _pickedUp;
	private bool _removed;
	private World _world;
	private Vector3 _initialImpulse;
	private Player _picker;
	private bool _playSpawnEffects;
	// Elapsed time the pickup has been in the world, in seconds. Drives both
	// the settle-timeout fallback (force pickup-ready if physics never rests)
	// and the LootData.removeTimeMs despawn. Local to the live instance —
	// re-enters at 0 if the chunk unloads and re-streams the loot.
	private float _ageSeconds;
	// Water-physics state. _swimming flips when the loot's feet voxel is
	// water; _gravityScaleSwimActive tracks whether engine gravity has been
	// zeroed so ApplyWaterPhysics alone controls vertical motion (mirrors
	// Mob's pattern). _gravityScaleAuthored captures the scene-authored
	// gravity_scale once so the value is restored verbatim on water exit.
	private bool _swimming;
	private float _waterSurfaceY;
	private float _gravityScaleAuthored;
	private bool _gravityScaleCaptured;
	private bool _gravityScaleSwimActive;

	public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

	public override void _Ready()
	{
		if (_interactArea != null)
		{
			_interactArea.BodyEntered += OnInteractAreaBodyEntered;
		}

		if (_hurtBox != null)
		{
			_hurtBox.OnHit = OnHurtBoxHit;
			_hurtBox.GetHitType = _ => EHitResult.Object;
		}

		if (_initialImpulse != Vector3.Zero)
		{
			CanSleep = false;
			ContactMonitor = true;
			MaxContactsReported = 1;
			ApplyCentralImpulse(_initialImpulse);
		}
		else
		{
			Settle();
		}
	}

	public override void _Process(double delta)
	{
		if (_pickedUp || _removed)
		{
			return;
		}
		_ageSeconds += (float)delta;
		// Enable the interact area once the firing arc has cleared without
		// freezing the body — pickup remains available even if the loot is
		// still tumbling. Settle() (called from _IntegrateForces on rest)
		// also sets Monitoring=true, so whichever path fires first wins.
		if (_interactArea != null && !_interactArea.Monitoring
			&& _pickupReadyDelaySeconds > 0f && _ageSeconds >= _pickupReadyDelaySeconds)
		{
			_interactArea.Monitoring = true;
		}
		ItemData data = _simState?.Item?.data ?? _simState?.Data;
		if (data is LootData lootData && lootData.removeTimeMs > 0 && _ageSeconds * 1000f >= lootData.removeTimeMs)
		{
			Expire();
		}
	}

	// Fraction in [0, 1] of the way to the LootData.removeTimeMs expiry.
	// 0 when no timeout is authored or removal has already started. Used
	// by ArrowLootSimState to surface "time until ammo replenishes" on
	// the weapon HUD.
	public float GetReplenishProgress()
	{
		if (_pickedUp || _removed)
		{
			return 0f;
		}
		ItemData data = _simState?.Item?.data ?? _simState?.Data;
		if (data is not LootData lootData || lootData.removeTimeMs <= 0)
		{
			return 0f;
		}
		return Mathf.Clamp(_ageSeconds * 1000f / lootData.removeTimeMs, 0f, 1f);
	}

	private void Expire()
	{
		if (_removed || _pickedUp)
		{
			return;
		}
		_removed = true;
		// Reuse the PickedUp latch on the sim state — the only thing that
		// flag gates is LootSimState.CreateEntity returning null, which is
		// exactly the behavior we want for expired loot (don't respawn it
		// when the chunk re-streams).
		if (_simState != null)
		{
			_simState.PickedUp = true;
			_simState.OnRemovedFromWorld();
		}
		if (_removeEffectScene != null)
		{
			Fx.Create(_removeEffectScene, GetParent(), Position);
		}
		if (_collisionShape != null)
		{
			_collisionShape.Disabled = true;
		}
		if (_interactArea != null)
		{
			_interactArea.Monitoring = false;
		}
		_world?.RemoveEntity(this);
		// Play the PickedUp shrink/lift animation as the despawn outro — same
		// visual outro as a player pickup, so an arrow timing out reads as
		// "vanished" rather than "popped out of existence." OnPickedUpFinished
		// runs QueueFree once the animation ends. No animation player → free
		// immediately, matching the pre-animation behavior.
		if (_animationPlayer != null)
		{
			_animationPlayer.AnimationFinished += OnPickedUpFinished;
			_animationPlayer.Play("PickedUp");
		}
		else
		{
			QueueFree();
		}
	}

	public override void _IntegrateForces(PhysicsDirectBodyState3D state)
	{
		if (_pickedUp || Freeze)
		{
			return;
		}

		// Don't settle (and freeze) while in water — buoyancy and currents
		// need to keep acting on the body so it bobs and drifts. The
		// _PhysicsProcess water path will handle wake-up if a settled item
		// later gets submerged (e.g. tide rises into a stash).
		if (_swimming)
		{
			return;
		}

		if (state.LinearVelocity.LengthSquared() < 0.25f && state.GetContactCount() > 0)
		{
			Settle();
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_pickedUp || _removed || _world == null)
		{
			return;
		}

		if (!_gravityScaleCaptured)
		{
			_gravityScaleAuthored = GravityScale;
			_gravityScaleCaptured = true;
		}

		UpdateWaterState();

		if (_swimming)
		{
			if (Freeze)
			{
				// Wake the body so buoyancy / currents can move it. Switch
				// off the sprite Bob animation — the rigidbody itself now
				// owns the visible vertical motion, and a sprite-Y bob on
				// top would compete with it.
				Freeze = false;
				_animationPlayer?.Play("Idle");
			}
			if (!_gravityScaleSwimActive)
			{
				GravityScale = 0f;
				_gravityScaleSwimActive = true;
			}
			ApplyWaterPhysics((float)delta);
		}
		else if (_gravityScaleSwimActive)
		{
			GravityScale = _gravityScaleAuthored;
			_gravityScaleSwimActive = false;
		}
	}

	// Mirrors Mob.UpdateWaterState minus the swimDepthThreshold gate — small
	// loot floats in puddles too, so any voxel of water at the body's feet
	// counts as "swimming." _waterSurfaceY is the Y of the first non-water
	// voxel above, used as the buoyancy target.
	private void UpdateWaterState()
	{
		WorldState ws = _world.WorldState;
		Vector3 pos = GlobalPosition;
		int fx = Mathf.FloorToInt(pos.X);
		int fy = Mathf.FloorToInt(pos.Y);
		int fz = Mathf.FloorToInt(pos.Z);
		if (ws.GetVoxelWorld(fx, fy, fz) != VoxelType.Water)
		{
			_swimming = false;
			return;
		}
		int topY = fy;
		while (ws.GetVoxelWorld(fx, topY + 1, fz) == VoxelType.Water)
		{
			topY++;
		}
		_swimming = true;
		_waterSurfaceY = topY + 1;
	}

	// Mirrors Mob.ApplyWaterPhysics: depth-scaled upward buoyancy toward the
	// surface, vertical drag damping the resulting oscillation, and an XZ
	// impulse that drags horizontal velocity toward the local water current
	// so the item rides the river instead of sitting still in the flow.
	private void ApplyWaterPhysics(float dt)
	{
		Vector3 pos = GlobalPosition;
		Vector3 vel = LinearVelocity;
		Vector3 deltaVel = Vector3.Zero;

		float targetY = _waterSurfaceY - _waterSurfaceOffset;
		float depthBelowSurface = targetY - pos.Y;
		if (depthBelowSurface > 0f)
		{
			deltaVel.Y += Mathf.Min(depthBelowSurface, 1f) * _buoyancyAcceleration * dt;
		}
		else
		{
			deltaVel.Y -= _buoyancyAcceleration * 0.5f * dt;
		}

		deltaVel.Y -= vel.Y * _waterDrag * dt;

		Vector3 current = _world.WorldState.SampleWaterCurrent(pos);
		deltaVel.X += (current.X - vel.X) * _waterCurrentDrag * dt;
		deltaVel.Z += (current.Z - vel.Z) * _waterCurrentDrag * dt;

		ApplyImpulse(deltaVel * Mass);

		if (LinearVelocity.Y < -_waterSinkSpeed)
		{
			Vector3 v = LinearVelocity;
			LinearVelocity = new Vector3(v.X, -_waterSinkSpeed, v.Z);
		}
	}

	private void Settle()
	{
		Freeze = true;
		// Enable monitoring after settle so the area only starts probing once
		// the loot is at rest — avoids spurious BodyEntered events from
		// graze-collisions during the post-spawn flight arc.
		if (_interactArea != null)
		{
			_interactArea.Monitoring = true;
		}
		// Loot that flew in (chest emission, player drop) bobs to read as
		// freshly arrived; loot that was already in the world at spawn (world
		// gen, LootSpawnEntry) sits idle so the chunk doesn't pulse.
		_animationPlayer?.Play(_initialImpulse != Vector3.Zero ? "Bob" : "Idle");
	}

	private void OnHurtBoxHit(HitInfo hit)
	{
	}

	private void OnInteractAreaBodyEntered(Node body)
	{
		if (_pickedUp || body is not Player player)
		{
			return;
		}
		// Body entry only acts when the inventory state allows auto-pickup.
		// Otherwise the same area's interact-highlight path is what the
		// player uses, via the action runner.
		if (!CanAutoPickup(player))
		{
			return;
		}
		_picker = player;
		FinalizePickup();
	}

	// Auto-pickup only fires when the entire pile would top off into existing
	// same-kind stacks. Fresh items (player has none of this kind) and items
	// that would need a new backpack slot fall through to the interactive
	// path so the player chooses to commit to a new slot.
	private bool CanAutoPickup(Player player)
	{
		if (_simState == null || _simState.RequireInteract)
		{
			return false;
		}
		if (player?.Inventory == null)
		{
			return false;
		}

		// Per-loot-kind gate (e.g. arrow bound to a specific weapon) — must
		// pass before any auto-pickup path. Without this, a loose arrow's
		// pickup would trigger regardless of which player walks over it.
		if (!_simState.CanPickup(player))
		{
			return false;
		}

		// Loot that doesn't deposit into the inventory (arrows return ammo
		// to the source weapon) auto-picks up on collision unconditionally —
		// no stack-space search needed since nothing is being added to a
		// slot. CanPickup above already vetted the binding.
		if (!_simState.ShouldDepositToInventory())
		{
			return true;
		}

		ItemData data = _simState.Item?.data ?? _simState.Data;
		if (data == null || !data.IsStackable)
		{
			return false;
		}

		int needed = _simState.Item?.stackCount ?? 1;
		int avail = 0;
		foreach (ItemState s in player.Inventory.EnumerateAll())
		{
			if (s.data != data)
			{
				continue;
			}
			avail += s.RemainingStackSpace();
			if (avail >= needed)
			{
				return true;
			}
		}
		return false;
	}

	public bool CanInteract() => !_pickedUp;
	public bool CanActorInteract(Player player)
	{
		if (_pickedUp || player?.Inventory == null)
		{
			return false;
		}
		// Per-loot-kind gate — arrow drops bound to a specific weapon refuse
		// pickup unless that weapon is still equipped.
		if (_simState != null && !_simState.CanPickup(player))
		{
			return false;
		}
		// Auto-pickup loot suppresses its own interact highlight — body entry
		// will commit the pickup on the next physics frame, so showing the
		// "press to interact" affordance would just flicker.
		if (CanAutoPickup(player))
		{
			return false;
		}
		// Backpack-fit gate only applies to loot that actually deposits into
		// the inventory. Arrows return to the source weapon's ammo pool and
		// don't take a slot, so a full backpack must not block recovering them.
		if (_simState != null && !_simState.ShouldDepositToInventory())
		{
			return true;
		}
		// If the loot carries an item, only allow interact when there's
		// space; otherwise the action would run to completion and silently
		// fail. Armor/weapons can land directly in an empty equip slot, so a
		// full backpack only blocks pickup when there's no slot to equip into.
		if (_simState?.Item != null && player.Inventory.BackpackCount >= player.Inventory.BackpackCapacity)
		{
			ItemData data = _simState.Item.data ?? _simState.Data;
			if (!HasEmptyEquipSlot(player.Inventory, data))
			{
				return false;
			}
		}
		return true;
	}

	private static bool HasEmptyEquipSlot(Inventory inv, ItemData data)
	{
		if (inv == null || data == null)
		{
			return false;
		}
		switch (data)
		{
			case ArmorData armor:
				return inv.GetEquipped(armor.armorSlot) == null;
			case WeaponData weapon:
				return inv.GetEquipped(weapon.CanonicalSlot) == null;
			default:
				return false;
		}
	}

	public Array<InteractiveAction> GetActions(Player player)
	{
		if (_actions == null || _actions.Count == 0)
		{
			return null;
		}
		_picker = player;
		return _actions;
	}

	// Called from the action's OpenInteractive event handler at the
	// authored t=N moment. Deposits the carried item into the picker's
	// inventory (if any) and removes the loot from the world.
	public void Complete(int actionIndex)
	{
		if (_pickedUp || _picker == null)
		{
			return;
		}
		FinalizePickup();
	}

	private void FinalizePickup()
	{
		if (_pickedUp)
		{
			return;
		}
		if (!TryDepositItem(_picker))
		{
			return;
		}

		_pickedUp = true;
		if (_simState != null)
		{
			_simState.PickedUp = true;
			_simState.OnRemovedFromWorld();
		}
		if (_pickupEffectScene != null)
		{
			Fx.Create(_pickupEffectScene, GetParent(), Position);
		}
		_collisionShape.Disabled = true;
		_world?.RemoveEntity(this);
		if (_animationPlayer != null)
		{
			_animationPlayer.AnimationFinished += OnPickedUpFinished;
			_animationPlayer.Play("PickedUp");
		}
		else
		{
			QueueFree();
		}
	}

	// Returns true if the pickup should proceed. World-spawned loot with no
	// attached Item synthesizes a fresh ItemState from Data; legacy entries
	// with neither Data nor Item still pick up cleanly (deposit nothing).
	private bool TryDepositItem(Player player)
	{
		if (_simState == null)
		{
			return true;
		}
		// Per-loot-kind gate — refuse the commit so the runner's
		// completionEvents pass turns into a no-op (e.g. an arrow whose
		// source weapon was dropped between highlight and action commit).
		if (!_simState.CanPickup(player))
		{
			return false;
		}
		// Pickup with no inventory deposit — arrow ammo returns to the
		// source weapon via OnRemovedFromWorld; the Loot still despawns
		// normally.
		if (!_simState.ShouldDepositToInventory())
		{
			return true;
		}
		ItemState toAdd = _simState.Item ?? _simState.Data?.CreateState();
		if (toAdd == null)
		{
			return true;
		}
		if (player?.Inventory == null)
		{
			return false;
		}
		Inventory inv = player.Inventory;

		// Armor/weapons land directly in an empty equip slot — bypasses the
		// backpack so the player can grab an obvious upgrade even when the
		// backpack is full.
		if (TryEquipToEmptySlot(inv, toAdd))
		{
			return true;
		}

		int initial = toAdd.stackCount;
		int added = inv.TryAdd(toAdd);
		if (added < initial)
		{
			return false;
		}

		// Consumables promote from the backpack into the first empty hotbar
		// slot. No-op when the item fully merged into an existing stack (the
		// move requires the item to be present in the backpack).
		if (toAdd.data is ConsumableData)
		{
			inv.TryMoveToConsumableSlot(toAdd);
		}
		return true;
	}

	private static bool TryEquipToEmptySlot(Inventory inv, ItemState item)
	{
		if (item?.data == null)
		{
			return false;
		}
		switch (item.data)
		{
			case ArmorData armor:
				if (inv.GetEquipped(armor.armorSlot) == null)
				{
					return inv.TryEquip(item, armor.armorSlot);
				}
				return false;
			case WeaponData weapon:
				EInventorySlot weaponSlot = weapon.CanonicalSlot;
				if (inv.GetEquipped(weaponSlot) == null)
				{
					return inv.TryEquip(item, weaponSlot);
				}
				return false;
			default:
				return false;
		}
	}

	private void OnPickedUpFinished(StringName animName)
	{
		QueueFree();
	}

	public void OnSpawned(World world)
	{
		if (_playSpawnEffects && _spawnEffectScene != null)
		{
			Fx.Create(_spawnEffectScene, GetParent(), Position);
		}
	}

	public static Loot Create(World world, LootSimState data, PackedScene scene, Vector3 impulse = default)
	{
		var instance = scene.Instantiate<Loot>();
		instance.Position = data.WorldPosition;
		instance._simState = data;
		instance._world = world;
		instance._initialImpulse = impulse;
		instance._playSpawnEffects = true;
		ItemData itemData = data.Item?.data ?? data.Data;
		PackedScene worldModel = (itemData as LootData)?.worldModel;
		if (worldModel != null && instance._modelAnchor != null)
		{
			// 3D-model loot (the elite crown halo) renders its authored mesh
			// instead of the flat sprite. The model brings its own material /
			// x-ray stack and idle spin/bob, so hide the sprite quad to avoid a
			// doubled visual rather than swap its texture.
			if (instance._sprite != null)
			{
				instance._sprite.Visible = false;
			}
			instance._modelAnchor.AddChild(worldModel.Instantiate<Node3D>());
		}
		else
		{
			// Swap the world-pickup sprite to the carried item's icon. Prefer an
			// authored worldSprite (already drawn at chunky-pixel resolution);
			// when none is set, point-downsample the full-res inventory icon to
			// the loot's chunky-pixel budget so it reads at the right size and
			// stays crisp (rather than rendering the big icon 1:1 — oversized —
			// or live-minifying it, which swims as the loot bobs). RegionEnabled
			// =false makes SpriteBase.Apply recompute the quad size + center
			// offset for whatever texture lands here.
			Texture2D texture = itemData?.worldSprite
				?? GetChunkyPickupTexture(itemData?.inventorySprite, instance._pickupMaxChunkyPixels);
			if (instance._sprite != null && texture != null)
			{
				instance._sprite.RegionEnabled = false;
				instance._sprite.Texture = texture;
			}
		}
		world.AddChild(instance);
		return instance;
	}

	// Cache of point-downsampled world-pickup textures, keyed by the source
	// (inventory) texture. Lets an item with no authored worldSprite render a
	// chunky-reduced copy of its inventory icon without hand-authoring a
	// separate small pickup PNG per item. Pure render derivation — the same
	// spirit as SpriteBase's shared-material cache, not authored game data — so
	// it lives in code rather than a .tres. Reduced once per icon per run.
	private static readonly System.Collections.Generic.Dictionary<Texture2D, Texture2D> _chunkyPickupCache = new();

	// Point-downsample `source` so its longest side is ~maxChunkyPixels,
	// snapping to an integer reduction factor so output texels map cleanly onto
	// the chunky-pixel grid the sprite shader fetches per source texel (a 1:1
	// fetch, so the baked-down icon is as shimmer-free as hand-authored pickup
	// art — no fractional live minification to swim as the loot bobs). Returns
	// `source` unchanged when it's already within budget or can't be decoded.
	private static Texture2D GetChunkyPickupTexture(Texture2D source, int maxChunkyPixels)
	{
		if (source == null || maxChunkyPixels <= 0)
		{
			return source;
		}
		if (_chunkyPickupCache.TryGetValue(source, out Texture2D cached))
		{
			return cached;
		}
		Image img = source.GetImage();
		if (img == null)
		{
			return source;
		}
		int longest = Mathf.Max(img.GetWidth(), img.GetHeight());
		if (longest <= maxChunkyPixels)
		{
			// Already chunky enough (also covers tiny placeholder art) — keep
			// the source so it shares the existing 1:1 fetch path untouched.
			_chunkyPickupCache[source] = source;
			return source;
		}
		// Resize needs an uncompressed image; lossless-imported icons decode
		// fine, but bail to the source if some format can't be decompressed.
		if (img.IsCompressed() && img.Decompress() != Error.Ok)
		{
			_chunkyPickupCache[source] = source;
			return source;
		}
		int factor = Mathf.Max(1, Mathf.RoundToInt((float)longest / maxChunkyPixels));
		int w = Mathf.Max(1, Mathf.RoundToInt(img.GetWidth() / (float)factor));
		int h = Mathf.Max(1, Mathf.RoundToInt(img.GetHeight() / (float)factor));
		img.Resize(w, h, Image.Interpolation.Nearest);
		Texture2D reduced = ImageTexture.CreateFromImage(img);
		_chunkyPickupCache[source] = reduced;
		return reduced;
	}

	public override void _ExitTree()
	{
		if (_simState != null && !_pickedUp)
		{
			_simState.WorldPosition = Position;
		}
		base._ExitTree();
	}
}
