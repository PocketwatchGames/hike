# Item Composition & Weapon Status-Effect Mods

Covers `scripts/data/items/` (authored item data + the descriptor wrappers), `scripts/gameplay/items/` (runtime `ItemState`/`WeaponState`, `WeaponNameGenerator`), and the weapon-mod payload under `scripts/data/effects/payloads/WeaponModData.cs`.

Items are customized by **composition, not new `ItemData`**. A spawn source (a `WorldGenData` loadout, a loot table, a chest) holds an `ItemDescriptor` — an `ItemData` paired with a list of permanent **status-effect mods** — instead of a bare `ItemData`. `ItemCount` / `LootSpawnEntry` HAS-A descriptor (neither IS one); `ItemDescriptor.CreateState()` builds the runtime `ItemState` and composes the mods onto its item-side `statusEffects` controller. This is how one "Fragile" bomb and one "Piercing" bow exist without authoring a unique `WeaponData` per permutation. **Expect to add many of these — prefer a new mod over a new item variant.**

## The mod is a `duration = 0` `StatusEffectData` with a `weaponMod` payload

The mod itself is an ordinary `StatusEffectData` authored with `duration = 0` (a permanent passive), carrying a **`weaponMod` sub-resource** (`WeaponModData`, null on non-weapon effects) that holds the weapon-modifier **payload** fields — `projectilesDetonateOnContact`, `projectilePierceCount` (creatures a shot passes *through* before stopping; 0 = stops on the first), and future ones. The payload says *what* the mod does; it is intrinsic to the effect. `StatusEffectData`'s feature-specific fields are grouped into optional sub-resources this way (`dot`, `attackImpact`, `dashBurst`, `trail`, `instant`, `weaponMod`) so the core stays small and each cluster lives in its own `*Data` class under `scripts/data/effects/payloads/`; null = feature absent. Each `ItemState` owns a `StatusEffectController` (`statusEffects`) constructed with null actor/world/health, so item-side mods are passive modifiers — no fx, no DoT — that travel with the object (a dropped enchanted sword keeps its enchant).

## Scope is a per-composition concern, NOT a property of the effect

Each entry in `ItemDescriptor.statusEffects` is a `StatusEffectDescriptor` = `{ StatusEffectData effect, EWeaponModScope scope, int chargeIndex }`. `EWeaponModScope` is `AllAttacks` (default; weapon-global, e.g. Fragile) or `SpecificCharge` (only the `chargeIndex`'th tier of the weapon's `ItemActionProfile.chargedActions`, e.g. Charged Pierce on the bow's heavy shot = index 1). Keeping scope on the descriptor — not on `StatusEffectData` — lets the same effect be all-attacks on one weapon and charge-scoped on another. `ItemDescriptor.ApplyTo` composes via `StatusEffectController.AddWeaponMod(effect, scope, chargeIndex)`, which stamps `weaponModScope`/`weaponModChargeIndex` onto the live `StatusEffectState`.

The consuming gameplay code reads the composed value off the firing weapon's controller **filtered by the firing charge tier** — e.g. `ItemEventHandlers.DoProjectile` calls `weapon.statusEffects.ProjectilePierceCount(chargeIndex)` / `ProjectilesDetonateOnContact(chargeIndex)`, where a mod "reaches" the shot when it's `AllAttacks` or its `SpecificCharge` index matches (`FindChargeIndex` resolves the firing `ItemAction`'s position in the profile). These queries iterate the controller in a single pass like every other accumulator there.

## Adding a new weapon mod

1. Add the payload field(s) to `WeaponModData` (`scripts/data/effects/payloads/`), not to `StatusEffectData` itself (and if you add a brand-new payload `*Data` class, mark it `[Tool]` — see Key Conventions in the root CLAUDE.md, or the editor binds it as a base `Resource` and the field reads empty).
2. In the consuming code, read them off `WeaponState.statusEffects` via a small scope-filtered query method (mirror `ProjectilePierceCount(int)`, which dereferences `data.weaponMod`).
3. Author a `duration = 0` `.tres` under `resources/data/status_effects/` with a `weaponMod` sub-resource.
4. Compose it onto the item with a `StatusEffectDescriptor` (scope + chargeIndex) in the spawn/loot `.tres`.

Never bake a mod into the shared `WeaponData` or onto an `ItemAction`.

## Composed weapon names

A modded item's display name is generated, not authored: `WeaponNameGenerator.Compose` (`scripts/gameplay/items/`) wraps the item's base `displayName` with each weapon mod's name affix → "Fragile bomb of Piercing". The affix lives on `WeaponModData` (`affix` + `affixPosition` Prefix/Suffix); an empty `affix` falls back to the effect's `displayName`, so an ordinary adjective mod names the weapon with zero extra authoring, and you set `affix` explicitly only for a different surface form (a suffix `"of Lightning"` whose effect name is just "Lightning"). **Grammar is per-language, not in C#:** word order and connectors live in the `weapon_name_prefix` / `weapon_name_suffix` loc templates (`%0`/`%1`), so a language with post-nominal adjectives reorders the placeholders in its own `.tsv` rather than touching the generator. Read-side only and identification-gated — `WorldSimState.GetItemDisplayName(ItemState)` withholds affixes until the item is identified; the bare `GetItemDisplayName(ItemData)` overload still returns the plain noun for call sites that only have the data.

## Attacking mobs wield real `WeaponData` weapons — just like the player

Each attacking species authors one or more `WeaponData` `.tres` files **next to its mob data resource** (e.g. `resources/data/characters/goblin/goblin_claw.tres`, `goblin_battle_cry.tres`, `goblin_torch.tres`; `dog/dog_bite.tres`). A mob's loadout is a **per-variant species trait** — it lives on `SpeciesData.weapons`, *not* on the base `MobData` and *not* on the brain. Each weapon holds its own `actionProfile`, `damageProfiles` (a fiery weapon just adds `status_burning` to its `DamageData.statusEffects`), the in-hand `heldModel`, **and** its mob-AI engagement tuning in the `WeaponData` `AI` group (`priority` / `maxAttackRange` / `maxVerticalAttackRange` / `desiredAttackRange` / `cooldownSeconds` / `minAllies` / `allyRange`); `MobData` carries no weapons or damage data, and `AttackBehaviorData` carries no weapon ref — it holds only behavior-level positioning (`approachRange`, `encircleDistance`, `encircleMinSpacing`, `encircleOverflowGap`).

**Loadout lives on the species — fork the species for a loadout variant.** Because weapons are a `SpeciesData` trait, a loadout variant is its own `SpeciesData` over the shared `MobData`/`mobScene` (and thus its own bestiary row): `goblin_desert.tres` / `goblin_swamp.tres` carry `[battle_cry, claw]` (loose surface goblins), while `goblin_desert_torchbearer.tres` / `goblin_swamp_torchbearer.tres` carry `[battle_cry, goblin_torch]` (campfire & cave goblins wielding a burning torch in place of the claw). `SpeciesData.CreateState` stamps `species.weapons` onto `MobSimState.Weapons`, which `Mob.Weapons` reads; the loadout persists via `EntitySerializer` (a list of `WeaponData` paths). Every spawn path that needs weapons names a species — `MobSpawnEntry.species` (and its `NpcSpawnEntry` subclass, e.g. the companion in the hub zone's fixtures) — so the companion dog (`dog_subspecies` → `[dog_bite]`) gets its loadout the same way. Since weapons drive the held prop, a loadout swap reskins the mob's hand for free.

At attack time `BehaviorAttack` reads `me.Weapons` and fires the **highest-`priority` weapon whose gates pass this tick** (in range, seen, off its own per-weapon cooldown, ally-count satisfied) — so a higher-priority gated special (the goblin's battle cry: `priority = 1`, `minAllies = 2`, long `cooldownSeconds`) is chosen when its conditions hold and otherwise falls through to the always-available basic attack (claw or torch, `priority = 0`). It sets that weapon's `WeaponState` (`Mob.GetWeapon(WeaponData)`, lazily created + cached, per-weapon cooldown tracked in `BehaviorAttack`) as the action's `primaryItem` — so damage resolution (`ItemEventHandlers.ResolveHit` / `DoProjectile` read `primaryItem.data.GetDamage`) and the **same** item-side weapon-mod path the player uses (`TriggerWeaponModChains`, `ProjectilePierceCount`, on-hit enchants, knockback) fire for mobs identically. The mob's visible in-hand prop is the `heldModel` of its highest-`priority` weapon that defines one (`Mob.PrimaryHeldWeapon`). A `HeldTorch` heldModel with `startLit = true` (the goblin torch) renders burning **and casts world light**: `HeldItemVisual` lights it via `SetLit(true, carrierRoot)` when the weapon prop is instanced, where `carrierRoot` is the nearest `PhysicsBody3D` ancestor (the mob body) so the deposit tracks the body, not the swinging hand. (Mobs no longer have the old conditional "light a torch when it's dark" feature — `MobData.heldTorchScene`, `Mob.ShouldUseTorch`, and `AIOutput.useTorch` were removed; a mob that should glow simply wields a `startLit` torch weapon.)

**An elite is a property of the SPAWN, not of the species, and it is a mob-mod rather than a body status effect:** a spawn entry turns its mob elite by pointing `MobSpawnEntry.elite` at a shared `EliteData` signature (`resources/data/characters/elites/`, carrying `statusEffects` + a `badge` icon + an optional `crownScene`), and the elite entry is wired into spawn lists as a rarer separate row over the SAME `SpeciesData` — so an elite goblin stays one bestiary row, discovery and kill-quest target with the plain one, and no species is forked to make an elite of it. There is no runtime elite roll or zone signature pool. At spawn (`Mob.Initialize`) each `statusEffects` entry (the species' own, composed with the elite signature's) is routed by `ApplySpawnStatusEffect`: one whose effect has a `weaponMod` payload (e.g. `status_elite_weapon_lightning`) is captured as `Mob._eliteWeaponMod` and composed (`AllAttacks`) onto every weapon as `GetWeapon` creates it; one with no `weaponMod` (a plain body buff) is added to the mob's own controller instead. The HUD badge reads `MobSimState.Badge`; the effects list + badge persist via `EntitySerializer` (weapons themselves are runtime-only, rebuilt on first attack). Chain lightning therefore rides the weapon for player and elite alike; the actor-side `StatusEffectController.TriggerAttackImpact` fires only `attackImpact` body auras (no chain branch). Keep mob attack tiers at the per-tier `ItemAction.cooldownSeconds = 0` (the weapon's own `cooldownSeconds` governs cadence) — with `primaryItem` set, the `ActionRunner` also tracks the mob weapon's per-tier cooldown.

## Gear durability (`GearData` / `GearState`)

Weapons, armor and shields derive from `GearData` (`weight`, `maxDurability`) and
their states from `GearState` (`durability`). `maxDurability = 0` never wears —
mob weapons and the bare-hands fallbacks stay that way. **Only the player wears
gear**, at the event's own site — weapons by uses, armor and shields by damage (`maxDurability` is in those units):

| Gear | Wears on | Broken |
|---|---|---|
| melee weapon | its events' authored costs (below) | stays equipped, but the attack swings bare hands (`PlayerData.unarmedWeapon`, via `Player.GetMeleeWeaponOrUnarmed`) |
| ranged weapon (`rightHandSlot`: bow, guns, blowgun, sling) | every shot — its shot events carry `UseDurability` | cannot fire at all (`WeaponState.CanFire`) |
| thrown weapon (`WeaponData.thrown`: tomahawk, bomb) | never — each shot spends a unit of the stack outright | — |
| shield | damage absorbed: the guard a block consumes, the whole blow a parry negates | no guard, no parry, no bare-hands fallback |
| armor | damage the pool soaks (DoT chips included), split across worn pieces by their share of the pool | the pool stops recharging |

- **Weapon wear is authored on the `ItemEvent`, like ammo.** `durabilityCostOnHit`
  (any Melee / Hitscan / Projectile event) is spent when the attack FIRST strikes
  a MOB — breakables and props never wear a weapon — a swing's first creature, a
  hitscan hit, a projectile's first creature
  (`ItemEventHandlers.WearOnStrike`); the `UseDurability` flag spends
  `durabilityCost` as the event fires, hit or not (`DoUseDurability`). A ranged
  weapon wears this way, per shot, never on the strike. A weapon-mod missile
  carries no cost.
- **All wear goes through `Player.WearGear`**, which plays the cues: warning once
  on crossing `SimData.durabilityWarningFraction`, broken once at zero. The HUD
  overlays read the same `GearState.Condition`.
- **A broken weapon is an empty slot to gameplay** — no reticle, no draw,
  no fire, no aim assist, and the melee slot falls back to bare hands exactly as
  if it were empty — through `Player.GetUsableWeapon`. The HUD still reads the
  equipped item, so it shows broken.
- **Wear rate is a per-ITEM mod**: an `EStat.DurabilityWear` `StatModifier` on a
  `StatusEffectData` composed through `ItemDescriptor.statusEffects`
  (`status_gear_sturdy` 0.5, `status_gear_brittle` 2.0). It is folded off the
  item's own controller (`GearState.ComposeWearMultiplier`), never the wearer's.
  Those mods carry an empty `weaponMod` only so the name affix applies.

## Ammo (`AmmoData`, `WeaponData.ammoType`)

**A ranged weapon has no magazine. Its rounds are items** — an `AmmoData` of the
weapon's `EAmmoType` (Arrow, Ball, Shot, Dart), carried in the grids like any
stackable. `ammoType = None` needs no rounds (the sling, wands, every mob weapon).

- **What a weapon fires is the member's LOADED kind of its type**
  (`Inventory.LoadedAmmo`), a selection per type, not an equip slot: "Equip" on an
  ammo stack loads it, and only while the right-hand weapon fires that type. Shots
  draw from every carried stack of the loaded kind, and the HUD counter is that
  total (`Inventory.RoundsFor`). The selection is per type, so swapping bow →
  musket → bow keeps the arrow choice.
- **Running out falls back on its own** (`Inventory.ReconcileLoadedAmmo`, run on
  every change): the type's `isStandard` round, else the first carried kind of the
  type. At most one standard per type (`resource_check`).
- **A shot spends `ItemEvent.ammoCost` rounds on its `UseAmmo` event** (default 1),
  however many projectiles it fires — the blunderbuss's fan is one shell. Spending
  happens at the top of `DoProjectile` / `DoHitscan` (`SpendShotRounds`), so the
  round's `AmmoData.mods` reach the shot: they are folded into the weapon's mod
  reads through `StatusEffectController.SetLoadedRoundSource`, like a forge
  upgrade. A fire arrow is a new `AmmoData` with the flaming mod, not a new bow.
- **A landed round is an ordinary pickup of its own item**, rolled against
  `AmmoData.breakChance` per round where it lands (`Sim.LandRecoverableRound`,
  which floats "<name> broken!" on a break). It embeds in a living mob it struck
  (`embedsInTargets`, `ArrowStuck`) and drops loose when that mob dies; a mob that
  unloads alive takes it along. Nothing binds a round to the weapon that fired it,
  nothing times it out, and like all dropped loot the next rest sweeps it. It
  is collected on contact even when none of its kind is left in the pack
  (`LootSimState.FiredRound`, set by `Sim.EjectFiredRound`).
- **A thrown weapon is its own round** (`WeaponData.thrown`): a stackable weapon
  whose shots spend units of the equipped stack, landing as a unit of itself
  (`thrownBreakChance`). The last unit leaves the slot and the next carried stack
  of the same weapon is equipped; picking one up tops up the equipped stack
  (`Inventory.Acquire`). It never wears (`resource_check`).

## Sacks (`SackData` / `SackState`)

A sack is an item that owns an `ItemGrid` (`SackState.contents`) and takes only the
`EItemCategory`s in `SackData.accepts` — never another sack, whatever it lists.
Its contents ride in its subclass state, so a dropped, stashed, death-sacked or
saved sack keeps them with no help from the container. A dropped sack is exempt
from the rest sweep (`ResetSpawns`).

- **A carried sack's slots are carried slots.** `Inventory` keeps one
  `CarriedGrid` per sack in the belt or backpack (`Inventory.Sacks`, rebuilt by
  `SyncSacks`), and every owned-item query walks all of them. **A new reader of
  what a member carries goes through `EnumerateAll` / `Contains` / the carried
  grids — never `Belt` and `Backpack` alone**, or sack contents silently drop out
  of it. `ItemGrid.EnumerateAll` / `SpendMaterial` / `PruneExpired` recurse into
  sacks, so a sack in the stash still spoils and still cooks.
- **Placement asks the grid** (`IItemGrid.Accepts`): `PlaceAt`, `Add`, `Move` and
  `ItemTransfer` refuse what a sack won't take, both sides of a swap included.
- **Pickup order** (`Inventory.PlacementOrder`): top up existing stacks — sacks
  first — then a sack's empty slot, then the backpack / belt.
- A screen addresses a member's slot as (`CarriedGrid`, index) through
  `InventoryPanel.onGridSlot*`, and a sack that leaves detaches its grid (reads
  empty, refuses everything), so a held reference can't reach a sack no longer
  carried.

## Salvage (`ItemData.salvage` / `ItemTransfer.SalvageInto`)

**An item breaks into what its `salvage` list names** — `MaterialCount` rows of a
plain `ItemData` and a count, certain rather than rolled (the bench previews the
yields to check they fit), and no `ItemDescriptor` because a material carries no
mods. Empty = not salvageable. The materials live in `resources/data/items/materials/`.

- **Salvage is a crafting-bench action only** (`CraftingScreen`). The inventory
  and stash screens don't offer it. Butchering a meat IS salvaging it — there is
  no separate action.
- **Yields go into the stash, all or nothing**: `SalvageInto` refuses, changing
  nothing, when the stash hasn't room for every yield.
- A filled sack is refused (`CanSalvage`): its contents would leave with it.
- **A salvage can need a tool** (`ItemData.salvageTool`, an `EToolKind`): it is
  offered only while one is carried, and each unit wears it.

## Tools (`EToolKind`, `ItemData.toolUses`)

**What an item can do besides its main use is a row on the item; what an action
needs is a requirement naming the kind.** A knife and a sword are Blades, the
shovel is a Digging tool. Carrying one anywhere counts — equipped, belt, backpack
or a sack.

- **`Inventory.FindTool(kind)` is the one search**, broken gear skipped,
  non-allocating (prompts ask it every frame). **`Player.UseTool(kind)` /
  `WearTool(item, kind)` is the one wear path**, by the row's `wear`; an item
  that isn't `GearData` never wears.
- **An action gates on `CarriesToolRequirement`**, marked
  `hidesActionWhenUnmet` so no tool means no prompt at all
  (`InteractiveAction.IsOfferedTo`); a gate without the flag refuses at the press
  with its message instead. An interactive whose offered set can change filters
  its `GetActions` by it — safe because `IInteractive.Complete` receives the
  action that ran, never an index into a list that may since have changed.
- **Tool work is not done in combat** — digging, and a salvage that
  needs a tool. Combat is `CombatTracker` (a perceived dangerous enemy attacking,
  what the combat music follows), NOT `NoDangerRequirement`'s merely-nearby
  hostile: `NotInCombatRequirement` (hiding) on the actions, `ItemData.CanSalvage`
  for salvage. In combat the prompts simply aren't there.
- **A kind has code behind it** (the dig path), which is why it is
  an enum and not a resource — APPEND-ONLY, stored as an int.
- **A tool has no use of its own.** `ToolData` (the shovel) is `GearData` in the
  `Tool` category: it rides in the backpack, never the hotbar, and wears only
  through the interactions it enables. Digging is ONLY the `BuriedSpot`
  interaction — a Dig prompt within 1 m with a shovel carried, and for a
  treasure spot only while its map is held.
- **A burrowed mob is dug out the same way** (`SimData.digUpAction`, the same
  Dig): offered only while it is fully burrowed AND shows its dirt mound, so the
  prompt never reveals a mob that left no trace. Like a corpse it is targeted by
  its hurtbox (`ECollisionLayer.BurrowedHurtBox`, in the player's interact mask).

## Meat (`MobData.meat`)

**Every death drops exactly one piece of its body's meat** (`MobData.meat`), on
top of the variant's `SpeciesData.loot` and any zone loot. It is a trait of the
body, shared by every variant; null for a body with none (slimes, fairies, the
dog, villagers). A corpse itself offers nothing.

- **Meat never stacks** (maxStack 1) — each piece is one animal's worth, and
  `resource_check` refuses a `MobData.meat` that stacks. It still spoils like
  any perishable (one cohort per piece).
- **Butchering is the meat's salvage** — its `salvage` list is the hide, bone,
  sinew, feather or silk the body yields. The meat is used up, so the player
  chooses between cooking a piece and cutting it up.
