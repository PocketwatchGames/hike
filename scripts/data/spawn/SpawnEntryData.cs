using System;
using Godot;

// WHAT to place and how one lands: subclass per entity type (MobSpawnEntry,
// ChestSpawnEntry, ...) and override Spawn to construct the matching
// EntitySimState and add it to the world.
//
// An entry is a SHARED asset — one goblin.tres named by every list that wants
// goblins. So it holds only what is true of the thing wherever it appears. How
// densely a particular list sprinkles it, whether it is night-only there, and
// how many of it a camp holds belong to the SpawnRow that names it.
//
// Subclasses that need their own count parameter (chest loot count, berry
// count) declare a purpose-named field and roll inside Spawn.
[GlobalClass]
public partial class SpawnEntryData : Resource
{
    // Reject this entry's spawn position if any existing entity sits within
    // this radius. Prevents campfires inside trees, mobs inside chests, etc.
    // Set to 0 to disable the check (composite entries like SpawnGroupData
    // don't sit on a tile themselves — their anchor is just a scatter center,
    // so overlap at the group level is meaningless).
    [Export] public float minSpacing = 0.5f;

    // Does this property mean anything to a HAND-PLACED entity? An editor for
    // one hides the rest, because a control that cannot change the result is
    // worse than a missing one — it invites tuning that does nothing. Which
    // fields the placement path reads is this class's business, so the answer
    // lives here rather than in the UI.
    //
    // The scatter-only knobs a placement cannot use — rate, cluster count,
    // anchor pinning — are no longer on an entry at all; they are SpawnRow's,
    // and a hand placement has no row. What is left is minSpacing, which
    // TrySpawn skips for an authored position (the author put the mark exactly
    // there), and initialBehaviorChance, a POPULATION fraction with nothing to
    // be a fraction of when someone placed this one individually.
    public static bool IsHandPlacedProperty(StringName name)
    {
        return name != PropertyName.minSpacing
            && name != "initialBehaviorChance";
    }

    // Which palette family this entry belongs to, when that is not simply its own
    // file. Unset = the file, which is what almost every entry wants.
    //
    // It exists for the entry that cannot be folded into its family's shared
    // palette entry but still IS one of them: the leather merchant carries an `inventory` and
    // `loyaltyGifts` that no placement editor can author (both are arrays, which
    // still have no list editor), so it stays its own palette entry — and
    // selecting the npc palette entry must still light it up, because "show me
    // every NPC"
    // is the question being asked and the merchant is an NPC.
    //
    // A StringName the .tres never assigns arrives NULL, not empty, so every
    // read of it is guarded.
    [Export] public StringName family;

    // Does this property decide WHICH FAMILY this entry belongs to, rather than
    // which member of it this individual is? Only the family is the palette's to
    // choose: a fork keeps its palette file as its NAME, so a property that can
    // move an entry OUT of that family produces a placement that IS a drake
    // while the panel title, the hover readout, worldmap_check's listing and the
    // palette-match highlight all still call it npc_hermit.
    //
    // Which member is a per-placement choice and stays editable — that is the
    // whole point of a family entry. It is safe precisely because the candidates
    // are constrained to the family (ResourceCandidates), so no in-panel edit
    // can reach outside it and the fork's name stays true.
    //
    // `variants` and `appearances` are the family's own definition — what the
    // fields below MAY be set to — so they belong to whoever authors the palette
    // file, not to a placement. Shown, they would also be the one edit that can
    // widen a family from inside it.
    //
    // The raw appearance trio (scene / outfit / palette) stays hidden because it
    // is the WORLDGEN authoring path: the three must agree with each other (a
    // rig gender-matched to its outfit), which is a constraint no per-field row
    // can enforce. A hand placement varies its look through the bundled
    // NpcSpawnEntry.appearance instead, where a mismatch is unrepresentable.
    public static bool IsIdentityProperty(StringName name)
    {
        return name == PropertyName.family
            || name == "variants" || name == "appearances"
            || name == "scene" || name == "altScene"
            || name == "outfit" || name == "palette";
    }

    // Does this property get a row in a placement editor at all? Two independent
    // reasons not to, kept as separate questions because they mean different
    // things: the value cannot reach a hand placement (IsHandPlacedProperty), or
    // it is implicit in the palette entry that was chosen (IsIdentityProperty).
    //
    // What is deliberately still SHOWN is the third case — a property that would
    // vary per placement and simply has no editor yet (a chest's lootItems, an
    // NPC's inventory / loyaltyGifts / itemPreferences, all of which need list
    // editing). Those are marked as such rather than hidden, so the panel never
    // implies an entry holds less than it does.
    public static bool ShowsInPlacementEditor(StringName name)
    {
        return IsHandPlacedProperty(name) && !IsIdentityProperty(name);
    }

    // The same question, asked of THIS entry — the one the panel and the check
    // actually call. Virtual because what a property means depends on the entry
    // type: an NPC's `descriptor` picks between two species that resolve to the
    // same MobData and differ only in a bestiary displayName, so a row for it is
    // a control that cannot meaningfully change the result.
    public virtual bool ShowsProperty(StringName name)
    {
        return ShowsInPlacementEditor(name);
    }

    // The rows this entry wants FIRST, in this order; anything not named follows
    // in declaration order. Declaration order is the C# field order across a
    // class hierarchy, which puts the base class's bookkeeping above the fields
    // an author actually came to set — so the order a panel reads well in is a
    // statement the entry type makes, not an accident of inheritance.
    public virtual StringName[] PropertyOrder => null;

    // The values a string/StringName property may take, or null for "anything"
    // — which keeps it a free-text box. Overridden where the answer is derivable
    // from what the entry already names (a brain's behaviour nodes, a rig's
    // animation clips), so the editor offers a list instead of asking an author
    // to remember an identifier that fails SILENTLY when mistyped: a bad
    // behaviour name falls through to the species default and a bad clip name
    // fails ModelAnimator.HasAnimation, and neither says anything.
    //
    // An answer is advisory, not a constraint. Whatever the property currently
    // holds is offered too even when it is not in the list, so a value authored
    // against a different rig survives being looked at.
    public virtual string[] NameCandidates(StringName property) => null;

    // The resources an Object-typed property may be set to, or null for "every
    // authored .tres of that type" — which is what the panel's project-wide scan
    // gives a field like `conversation`, where any authored file is a valid
    // answer.
    //
    // Overridden where the valid set is a FAMILY the entry itself defines: the
    // goblin palette entry names its own goblin descriptors, so the row that
    // picks a biome variant cannot reach a spider. That constraint is what makes
    // the row safe to show at all — see IsIdentityProperty. Authored rather than
    // derived, because neither of the derivable answers is right: grouping by
    // SpeciesData is per-BIOME (finer than a family), and a filename prefix
    // makes a naming rule load-bearing with nothing enforcing it.
    //
    // Advisory in the same sense NameCandidates is: whatever the property
    // already holds is offered even when the list does not contain it, so a
    // value authored before the family was retuned survives being looked at.
    public virtual Resource[] ResourceCandidates(StringName property) => null;

    // Is this entry a private copy belonging to one placement, rather than the
    // shared palette file every placement of its kind points at?
    //
    // TWO shapes, and missing the second is a live bug: a fresh fork has no path
    // at all, but one that has been SAVED and loaded back carries the
    // sub-resource path Godot gives an embedded resource
    // ("res://…/placements.tres::Resource_abc"). That is not a palette file, and
    // treating it as shared makes the next edit fork the fork — renaming it after
    // the file it was embedded in and dropping it out of every by-entry answer.
    public static bool IsOwnedCopy(SpawnEntryData entry)
    {
        return entry != null
            && (string.IsNullOrEmpty(entry.ResourcePath) || entry.ResourcePath.Contains("::"));
    }

    // Which palette FILE this entry belongs to — its family, and the identity
    // everything matching a placement against the palette keys on. A fork has
    // that file only as its resource NAME, which is all that is left saying
    // where it came from.
    //
    // Deliberately NOT DisplayName: that one now decorates a family entry with
    // the variant this individual is, and a match must not depend on a
    // decoration — comparing display strings would stop every customized NPC
    // matching the npc entry the moment its appearance was picked.
    public static string FamilyName(SpawnEntryData entry)
    {
        if (entry == null)
        {
            return "";
        }
        // An explicit family outranks the file, and it survives a fork for free:
        // the duplicate carries the exported value, where ResourceName is engine
        // bookkeeping the fork has to be told to set.
        if (entry.family is not null && !entry.family.IsEmpty)
        {
            return entry.family.ToString();
        }
        return IsOwnedCopy(entry)
            ? entry.ResourceName ?? ""
            : entry.ResourcePath.GetFile().GetBaseName();
    }

    // Which member of its family this individual is — the biome variant of a
    // goblin, the rig and outfit of a villager — or null for an entry whose
    // family has only the one member. Overridden by the entry types that carry a
    // per-placement choice.
    //
    // It exists because collapsing a palette to families costs the map its
    // names: with one npc entry, every NPC hovers as "npc" and the elder is not
    // distinguishable from the archer. The family answers WHICH HIGHLIGHT, this
    // answers WHICH ONE IS IT, and the UI wants both.
    public virtual string VariantName() => null;

    // What to call this entry in the authoring UI: its family, plus the variant
    // this individual is, plus a mark when it is a placement's own customized
    // copy. One answer, because the tool row, the hover readout and the property
    // panel all name the same thing and a name that differs between them reads
    // as two different entries.
    public static string DisplayName(SpawnEntryData entry)
    {
        if (entry == null)
        {
            return "";
        }
        string family = FamilyName(entry);
        if (string.IsNullOrEmpty(family))
        {
            family = entry.GetType().Name;
        }
        string variant = entry.VariantName();
        string name = string.IsNullOrEmpty(variant) ? family : $"{family}: {variant}";
        return IsOwnedCopy(entry) ? $"{name} *" : name;
    }

    // True iff this entry requires a flat patch — the column and all 8
    // surrounding columns must share the same surface height. Subclasses
    // override to opt in; defaults to false so existing entry types
    // (loot, torches, fire traps, berry trees, ...) keep their current
    // placement domain. Mobs and campfires opt in to stop placements at
    // step edges and ramp adjacencies where physics can knock them off.
    public virtual bool RequireFlatTerrain => false;

    // True iff this entry needs air at the 4 lateral neighbors over a
    // 2-voxel body height. Catches mobs spawned against tunnel walls (the
    // cave-pocket pre-validation only checks the column itself, so a wall-
    // adjacent column passes — and a mob hitbox slightly wider than 0.5m
    // can clip in). Redundant with RequireFlatTerrain on the surface pass
    // (flat patch guarantees lateral air) — useful primarily inside caves.
    public virtual bool RequireLateralClearance => false;

    // True iff this entry spawns a mob. Mob entries are kept out of hazard
    // danger zones at spawn time (see TrySpawn). Defaults false; MobSpawnEntry
    // overrides.
    public virtual bool IsMobEntry => false;

    // True iff this entry resolves its own final position from the anchor it's
    // handed, ignoring the calling pass's column-validity sampler. A
    // SpawnGroupData calls Spawn directly on the anchor for these (no scatter,
    // no grassy-column gate) — e.g. a boat that must ring-scan for water, which
    // the grassy surface sampler would otherwise reject. Default false.
    public virtual bool SelfPlaces => false;

    // Radius (meters) of the damaging danger zone this entry's entity projects
    // — set by hazard entries (fire trap, campfire, spike trap). 0 = harmless.
    // Drives both the spawn keep-out (mobs won't spawn within it, and the
    // hazard won't spawn onto an existing mob) and the runtime hazard grid
    // (wander/normal pathing routes around it). Authored as a per-type
    // [Export] on the hazard subclasses so it's designer-tunable.
    public virtual float HazardSpawnRadius => 0f;

    // Final standability gate, evaluated against the same navigation
    // walkability sampler the mob navigator uses at runtime — so an entity
    // only spawns where its profile could actually stand and path. Default
    // true (the voxel air-over-solid + flat/lateral gates suffice for static
    // props); MobSpawnEntry overrides to require a navgrid-walkable column.
    // Runs at worldgen with no Sim node, so path-blocker cells aren't
    // consulted here (entity overlap is already covered by MinSpacing).
    public virtual bool IsSpawnPositionWalkable(WorldState ws, Vector3 position) => true;

    // Run the entry-specific placement gates (flat-terrain check, overlap
    // check) and dispatch to Spawn on success. Returns false if the spot
    // was rejected — caller skips the instance. SpawnGroupData's scatter
    // path bypasses this wrapper because TryPickInRadius does the same
    // checks inside its rejection-sampling loop.
    public bool TrySpawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (RequireFlatTerrain && context?.IsFlatColumn != null)
        {
            int wx = Mathf.FloorToInt(position.X);
            int wz = Mathf.FloorToInt(position.Z);
            if (!context.IsFlatColumn(wx, wz))
            {
                return false;
            }
        }
        // Skipped for a hand-authored position — see SpawnContext.AuthoredPosition.
        if (RequireLateralClearance && context?.AuthoredPosition != true
            && !HasLateralClearance(ws, position))
        {
            return false;
        }
        // Skipped for a hand-authored position, like the lateral clearance
        // above it: the author put the mark exactly there, and every other mark
        // is drawn on the same map. Nothing authors this away from its 0.5m
        // default except the SCATTER lists and two worldgen fixtures, which is
        // the path it exists for — a rejection radius is a statement about how
        // densely a pass may sprinkle something, not about a spot someone chose.
        if (minSpacing > 0f && context?.AuthoredPosition != true
            && ws.HasEntityWithinRadius(position, minSpacing))
        {
            return false;
        }
        // Hazard keep-out, enforced symmetrically so it's order-independent: a
        // mob never spawns inside a hazard's danger zone, and a hazard never
        // drops onto an already-placed mob. Either way the two end up at least
        // the hazard's radius apart. (Runtime attack pathing still lures mobs
        // in — this gate is spawn-time only.)
        if (IsMobEntry && ws.HasHazardSpawnConflict(position))
        {
            return false;
        }
        if (HazardSpawnRadius > 0f && ws.HasMobWithinRadius(position, HazardSpawnRadius))
        {
            return false;
        }
        if (!IsSpawnPositionWalkable(ws, position))
        {
            return false;
        }
        Spawn(ws, position, rng, context);
        return true;
    }

    // 4-connected air check over a 2-voxel body height around the spawn
    // anchor's voxel. Conservative — rejects 1-voxel-wide tunnels (mobs
    // would barely fit and be hard to navigate around anyway).
    private static bool HasLateralClearance(WorldState ws, Vector3 position)
    {
        int vx = Mathf.FloorToInt(position.X);
        int vy = Mathf.FloorToInt(position.Y);
        int vz = Mathf.FloorToInt(position.Z);
        const int BodyHeight = 2;
        for (int h = 0; h < BodyHeight; h++)
        {
            if (ws.GetBlockWorld(vx + 1, vy + h, vz) != Blocks.AirId) { return false; }
            if (ws.GetBlockWorld(vx - 1, vy + h, vz) != Blocks.AirId) { return false; }
            if (ws.GetBlockWorld(vx, vy + h, vz + 1) != Blocks.AirId) { return false; }
            if (ws.GetBlockWorld(vx, vy + h, vz - 1) != Blocks.AirId) { return false; }
        }
        return true;
    }

    // `position` is the GROUND TOP (top face of the solid voxel below the
    // entity), unified across both the surface and cave passes so subclasses
    // are pass-agnostic. Subclasses consume it as-is — every entity sits
    // with its scene root on this anchor, so the scene itself is the right
    // place to author any internal Y offset (a campfire bowl raised slightly
    // off the floor, a sprite stem lifted to avoid z-fighting, etc.). No
    // per-entry spawn-time lift; doing one here adds an in-air drop on
    // first physics tick, which can tunnel mobs through the floor when the
    // chunk's trimesh collider isn't registered yet.
    //
    // SpawnContext lets composite entries (SpawnGroupData) scatter sub-
    // entries within the placement domain of the calling pass. Leaf entries
    // (MobSpawnEntry, LootSpawnEntry, ...) ignore it. May be null when the
    // caller has no scatter sampler to provide (e.g. cave-pocket pass —
    // cells are pre-validated, no rejection needed).
    public virtual void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        GD.PushError($"SpawnEntryData subclass '{GetType().Name}' did not override Spawn");
    }
}
