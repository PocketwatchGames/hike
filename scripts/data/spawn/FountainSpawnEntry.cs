using System;
using Godot;

// Anything the player drinks from — a healing or mana fountain, a well, a
// cauldron; the look is the scene's (see Fountain). A kind whose drink is
// intrinsic (the healing fountain heals, the well hydrates) carries its effects
// on its shared palette entry. One that varies per placement (the cauldron)
// leaves them empty and the painter's fork supplies them. A fountain that does
// nothing is refused.
//
// Wants flat, grassy ground so the basin doesn't tilt off a step edge.
[GlobalClass]
public partial class FountainSpawnEntry : SpawnEntryData
{
    [Export] public PackedScene scene;

    public override PackedScene PaletteScene => scene;

    // Applied to the player on each drink. Files under resources/data/fountain_effects/,
    // so the painter can offer them.
    [Export] public ItemEffect[] effects = System.Array.Empty<ItemEffect>();
    // In-world days until it can be used again; 0 = any number of times.
    [Export(PropertyHint.Range, "0,7,1,or_greater")] public int cooldownDays;
    // Bool script variable that must be true for it to be usable (a quest's
    // QuestData.completedVariable, a conversation flag). Blank = always.
    [Export] public StringName enabledVariable;

    // Radius (meters) around the fountain where worldgen-painted detail sprites
    // are erased so scattered foliage doesn't share the station's footprint.
    [Export] public float detailSuppressionRadius = 2f;

    public override bool RequireFlatTerrain => true;

    // Offer every declared Bool variable for enabledVariable: a mistyped name
    // reads false forever and leaves the fountain silently cold. Registries
    // rather than ScriptVariableData files, because the generated npc_variables
    // registry embeds its declarations.
    public override string[] NameCandidates(StringName property)
    {
        if (property != PropertyName.enabledVariable)
        {
            return base.NameCandidates(property);
        }
        var names = new System.Collections.Generic.List<string>();
        foreach (string path in ResourceTypeIndex.Candidates(typeof(ScriptVariableRegistry), null))
        {
            if (ResourceLoader.Load(path) is not ScriptVariableRegistry registry)
            {
                continue;
            }
            foreach (ScriptVariableData variable in registry.variables)
            {
                if (variable?.id != null && !variable.id.IsEmpty && variable.type == EScriptVarType.Bool)
                {
                    names.Add(variable.id.ToString());
                }
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    public override void Spawn(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (scene == null)
        {
            return;
        }
        if (effects == null || effects.Length == 0)
        {
            GD.PushError($"FountainSpawnEntry '{ResourcePath}' at {position}: no effects — a drink "
                + "that does nothing. A cauldron's effect is authored on the PLACEMENT (edit it in "
                + "the painter). Not placed.");
            return;
        }
        ws.AddEntity(new FountainSimState(position, scene, effects, cooldownDays, enabledVariable)
        {
            RotationY = FacingY(context),
        });
        ws.ClearDetailVoxelsWithin(position, detailSuppressionRadius);
    }
}
