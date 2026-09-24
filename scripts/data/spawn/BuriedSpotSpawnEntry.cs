using System;
using Godot;

// Something buried in the ground: WHAT is buried (an item, or an entity such as
// a chest or an ambush) and what kind of spot it is (its style — the tell above
// ground, the mound it leaves).
//
// The contents are on the ENTRY, not the style, because they are what varies
// per spot: a hand-placed treasure forks this entry and picks its item in the
// painter's property panel, and a scatter list names a shared one (a carrot
// patch) once.
//
// A NAMED spot is a treasure. The name arrives on the SpawnContext (a named
// painter placement, a zone's treasureName) and is registered in
// WorldState.TreasureSpots, which the .hike header carries — that is what a
// treasure map points at.
//
// Remember-vs-forget is decided by WHERE a spot is placed, not by a flag: one in
// a baked world keeps its Excavated state for the session, one scattered by a
// regenerating chunk is simply re-rolled.
[GlobalClass]
public partial class BuriedSpotSpawnEntry : SpawnEntryData
{
    // Shared buried_spot.tscn (carries the BuriedSpot script + model anchor).
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    // The item sprayed out of the hole when it is dug, `count` of it as one
    // pile.
    [Export] public ItemData item;
    [Export(PropertyHint.Range, "1,99,1,or_greater")] public int count = 1;

    // An ENTITY buried here — a chest, a mob that bursts out — spawned through
    // the normal spawn path at dig time, so its contents stay sealed until then.
    // May accompany `item`: both fire.
    [Export] public SpawnEntryData payload;

    // How the spot reads and feels. Required — a spot with no style has no
    // mound and no dig feedback to give.
    [Export] public BuriedSpotStyleData style;

    // Restrict SCATTERED placement to flat patches (the column and its 8
    // neighbours share a height) so the hint and mound sit level and the dug-up
    // payload doesn't tumble down a slope. A hand-placed spot skips the gate.
    [Export] public bool requireFlat = true;

    public override bool RequireFlatTerrain => requireFlat;

    private static readonly StringName[] Order =
    {
        PropertyName.item, PropertyName.count, PropertyName.payload, PropertyName.style,
    };

    public override StringName[] PropertyOrder => Order;

    // `requireFlat` gates only a scattered spot (TrySpawn skips it for an
    // authored position), so on a placement it is a control that changes
    // nothing.
    public override bool ShowsProperty(StringName name)
    {
        return name != PropertyName.requireFlat && base.ShowsProperty(name);
    }

    // What is buried names the individual: "buried_spot: scroll_vyeshal_vocab2".
    public override string VariantName()
    {
        if (item != null && !string.IsNullOrEmpty(item.ResourcePath))
        {
            return item.ResourcePath.GetFile().GetBaseName();
        }
        return payload != null ? PaletteName(payload) : null;
    }

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null || style == null)
        {
            return;
        }
        if (item == null && payload == null)
        {
            GD.PushError($"BuriedSpotSpawnEntry at {position}: nothing is buried — pick an item or a "
                + "payload (on the placement, in the painter's property panel). Not placed.");
            return;
        }
        var state = new BuriedSpotSimState(position, scene, style)
        {
            Item = item,
            Count = Math.Max(1, count),
            Payload = payload,
            RotationY = FacingY(context),
        };
        string name = context?.AuthoredName;
        if (!string.IsNullOrEmpty(name))
        {
            if (ws.TreasureSpots.ContainsKey(name))
            {
                // A map names ONE place, so a second spot under the same name
                // would be undiggable-by-map at best. The first claim wins.
                GD.PushError($"BuriedSpotSpawnEntry at {position}: treasure name '{name}' is already "
                    + $"buried at {ws.TreasureSpots[name]} — this spot is placed unnamed.");
            }
            else
            {
                state.TreasureName = name;
                ws.TreasureSpots[name] = position;
            }
        }
        ws.AddEntity(state);
    }
}
