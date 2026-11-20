using System;
using Godot;

// Who can occupy a trigger. The party bits are about which member it is; the
// mob bits are the mob's CURRENT team (Mob.ActorTeam), except that the tamed
// companion only ever counts as Companion.
//
// APPEND new members only — written into .tres and the entity wire format.
[Flags]
public enum ETriggerActors
{
    None = 0,
    Controlled = 1 << 0,
    PartyMember = 1 << 1,
    Companion = 1 << 2,
    HostileMob = 1 << 3,
    FriendlyMob = 1 << 4,
    NeutralMob = 1 << 5,
    PreyMob = 1 << 6,
}

// APPEND new members only — written into .tres and the entity wire format.
public enum ETriggerRepeat
{
    // Enter fires once per run, and exit once after it. Saved with the world.
    Once,
    // Every time the box goes from empty to occupied (enter) and back (exit).
    EveryEntry,
}

// An axis-aligned box that fires when the actors it admits move into it and
// out of it. Every trigger is its own box, so no palette file offers one — the
// painter's trigger tool authors the whole entry per placement (AuthoredPlacement
// with no `source`), the way the scene editor's roof tool does.
//
// `position` at spawn is the box's CENTRE in XZ, seated on the floor under that
// centre; the box reaches `heightAbove` up and `depthBelow` down from there, so
// one placed on a slope still covers the ground at its edges.
//
// Fires on OCCUPANCY, not per actor: enter when the first admitted actor steps
// in, exit when the last one leaves. A party walking in together is one event.
[GlobalClass]
public partial class TriggerSpawnEntry : SpawnEntryData
{
    [Export(PropertyHint.Flags)] public ETriggerActors actors = ETriggerActors.Controlled;
    [Export] public ETriggerRepeat repeat = ETriggerRepeat.Once;

    // Set true when the box becomes occupied. Blank = none.
    [Export] public StringName enterSetsVariable;
    // Run when the box becomes occupied, in order, with the actor that stepped
    // in as the context's player (or the controlled player, for a mob).
    [Export] public Godot.Collections.Array<ConversationAction> enterActions = new();

    [Export] public StringName exitSetsVariable;
    [Export] public Godot.Collections.Array<ConversationAction> exitActions = new();

    // Footprint in metres. Edited by dragging the box's edges in the painter.
    [Export(PropertyHint.Range, "1,256,1,or_greater")] public int sizeX = 4;
    [Export(PropertyHint.Range, "1,256,1,or_greater")] public int sizeZ = 4;
    [Export(PropertyHint.Range, "1,64,1,or_greater")] public int heightAbove = 4;
    [Export(PropertyHint.Range, "0,64,1,or_greater")] public int depthBelow = 2;

    public override StringName[] PropertyOrder => new StringName[]
    {
        PropertyName.actors, PropertyName.repeat,
        PropertyName.enterSetsVariable, PropertyName.enterActions,
        PropertyName.exitSetsVariable, PropertyName.exitActions,
        PropertyName.sizeX, PropertyName.sizeZ, PropertyName.heightAbove, PropertyName.depthBelow,
    };

    public override string[] NameCandidates(StringName property)
        => property == PropertyName.enterSetsVariable || property == PropertyName.exitSetsVariable
            ? DeclaredBoolVariables()
            : base.NameCandidates(property);

    // The world-XZ cells the box covers, given its min corner.
    public Rect2I Footprint(Vector2I minCorner) => new(minCorner, Mathf.Max(1, sizeX), Mathf.Max(1, sizeZ));

    // The cell the box is seated from: the one holding its centre.
    public Vector2I SeatCell(Vector2I minCorner)
        => minCorner + new Vector2I(Mathf.Max(1, sizeX) / 2, Mathf.Max(1, sizeZ) / 2);

    // The box's centre in world XZ, given its min corner — where the bake puts
    // `position`, so SpawnEntities can recover the box from it.
    public Vector2 CenterXZ(Vector2I minCorner)
        => new(minCorner.X + Mathf.Max(1, sizeX) * 0.5f, minCorner.Y + Mathf.Max(1, sizeZ) * 0.5f);

    // Does this trigger do anything at all? One with no effect and no name for a
    // script to react to is a box that silently does nothing.
    public bool HasEffect(bool named)
    {
        return named
            || !IsBlank(enterSetsVariable) || !IsBlank(exitSetsVariable)
            || (enterActions?.Count ?? 0) > 0 || (exitActions?.Count ?? 0) > 0;
    }

    private static bool IsBlank(StringName name) => name is null || name.IsEmpty;

    protected override void SpawnEntities(WorldState ws, Vector3 position, Random rng, SpawnContext context)
    {
        if (actors == ETriggerActors.None)
        {
            GD.PushWarning($"TriggerSpawnEntry at {position}: admits no actors, so it can never fire. Not placed.");
            return;
        }
        int sx = Mathf.Max(1, sizeX);
        int sz = Mathf.Max(1, sizeZ);
        int below = Mathf.Max(0, depthBelow);
        int above = Mathf.Max(1, heightAbove);
        var box = new Aabb(
            new Vector3(position.X - sx * 0.5f, position.Y - below, position.Z - sz * 0.5f),
            new Vector3(sx, below + above, sz));
        // The box is axis-aligned whatever the facing; carried only so the
        // state's transform agrees with the placement's.
        ws.AddEntity(new TriggerSimState(position, box)
        {
            RotationY = FacingY(context),
            Actors = actors,
            Repeat = repeat,
            EnterSetsVariable = IsBlank(enterSetsVariable) ? null : enterSetsVariable,
            ExitSetsVariable = IsBlank(exitSetsVariable) ? null : exitSetsVariable,
            EnterActions = ToArray(enterActions),
            ExitActions = ToArray(exitActions),
        });
    }

    private static ConversationAction[] ToArray(Godot.Collections.Array<ConversationAction> list)
    {
        if (list == null || list.Count == 0)
        {
            return Array.Empty<ConversationAction>();
        }
        int count = list.Count;
        var result = new System.Collections.Generic.List<ConversationAction>(count);
        for (int i = 0; i < count; i++)
        {
            if (list[i] != null)
            {
                result.Add(list[i]);
            }
        }
        return result.ToArray();
    }
}
