# Spawn Entries & Lists (`scripts/data/spawn/`, `resources/data/world_authoring/spawn_entries/`)


**What a thing is and how a list uses it are separate resources.** A
`SpawnEntryData` (`MobSpawnEntry`, `ForageSpawnEntry`, `ChestSpawnEntry`, …) is a
SHARED asset in `resources/data/world_authoring/spawn_entries/` holding only what is true of the
thing wherever it appears — its descriptor or item, its scene, its placement
gates. A **row** names one of those and adds what THIS container says about it,
so a zone's entity list reads as a list of named files with a number each:

| Row type | Container | Adds |
|---|---|---|
| `SpawnRow` (base) | — | `entry`, `spawnConditions` |
| `SpawnListRow` | `SpawnListData.rows` | `squareMetersPerSpawn` (per-column area rate) |
| `SpawnGroupRow` | `SpawnGroupData.rows` | `countMin`/`countMax`, `placeAtAnchor` |

The two containers ask different questions — a list wants a rate per area, a
cluster wants a count and a position within itself — and neither can act on the
other's answer, so they are separate types. One shared row type carrying all of
it put three dead fields on every list row, and a field that cannot affect its
container is worse than a missing one: it invites tuning that does nothing.

- **Per-container values must not migrate onto the entry.** They genuinely differ
  per list, which is the whole reason for the split: the mountain goblin is
  night-only on the surface and any-time in a cave, and a camp holds 2–3 of a
  goblin the surface scan places one of. Authored the other way round, each list
  had to embed its own copy of every entry and one well was re-authored in three
  files. `spawnConditions` is on the shared base because it is the one question
  BOTH containers ask.
- **`spawnConditions` reaches `Spawn` on the `SpawnContext`**, stamped by the row
  immediately before each spawn. `Spawn` is overridden by ~20 entry types and only
  three (mob, npc, chest) have a sim state that can defer on a condition, so
  widening every signature for it is the worse trade. Nothing clears the field —
  every caller sets it for the row it is about to place.
- **An entry stays embedded when it is genuinely one-of-a-kind**: the villagers in
  a house list (`NpcSpawnEntry` — each carries its own conversation, outfit and
  palette), a campfire fixture with authored text. Hoisting a single-use entry
  into a shared file buys nothing.
- **`spawn_check`** (`--headless -- "spawn_check 1"`, ~4s) dumps every list's
  resolved rows with every stored property. There is no runtime error mode here —
  a dropped rate or condition just quietly stops placing something — so a diff of
  this output is how an edit to these files is proved to have changed nothing else.
