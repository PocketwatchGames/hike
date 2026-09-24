using System;
using Godot;

// One hand-placed prop — a tree, a boulder, a barrel.
//
// ONE palette entry for the whole library rather than a .tres per prop, and the
// library entry is the per-placement variant. Exactly MobSpawnEntry's shape and
// for the same reason: sixty files whose only difference is which scene they
// name would be sixty registrations to keep in step, and a tool that wants to
// show them as sixty buttons can already do that by expanding `variants`.
//
// The library stays a PropLibraryData because it carries what a bare scene
// cannot — the icon a palette button shows, the category it files under, and
// the PropType the placed state gets, which is a placement class (canopy vs
// ground cover) and not derivable from the scene.
[GlobalClass]
public partial class PropSpawnEntry : SpawnEntryData
{
    // Which prop this individual is. Null places nothing.
    [Export] public PropLibraryEntry prop;

    // The library this entry offers. The group's own definition — what `prop`
    // may be set to — so it is authored here and never per placement.
    [Export] public PropLibraryData library;

    public override StringName VariantProperty => PropertyName.prop;

    // The library is what `prop` MAY be set to, so it belongs to whoever authors
    // the palette file and not to a placement — the same reason `variants` and
    // `appearances` are hidden. Shown, it would be the one edit that can widen
    // the group from inside it: re-point it and this placement's prop is no
    // longer in the family the panel still calls it a member of.
    public override bool ShowsProperty(StringName name)
        => name != PropertyName.library && base.ShowsProperty(name);

    public override string VariantName()
    {
        if (prop == null)
        {
            return null;
        }
        return string.IsNullOrEmpty(prop.displayName)
            ? prop.scene?.ResourcePath.GetFile().GetBaseName()
            : prop.displayName;
    }

    public override Texture2D PaletteIcon => prop?.icon;

    public override PackedScene PaletteScene => prop?.scene;

    // Constrained to the library, so no in-panel edit can reach a scene the
    // author never put in it.
    public override Resource[] ResourceCandidates(StringName property)
    {
        if (property != PropertyName.prop || library?.entries == null)
        {
            return base.ResourceCandidates(property);
        }
        return library.entries;
    }

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (prop?.scene == null)
        {
            return;
        }
        ws.AddEntity(new PropSimState(prop.propType, position, prop.scene)
        {
            RotationY = FacingY(context),
        });
    }
}
