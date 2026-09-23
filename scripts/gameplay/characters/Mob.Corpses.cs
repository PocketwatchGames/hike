using System.Collections.Generic;
using Godot;

// What a mob has noticed about a dead body it has not finished reacting to.
// Facts only — how long to stare at each leg is the behavior's authored tuning
// (CorpseInspectBehaviorData), not part of the stimulus.
public struct CorpseSighting
{
    // Instance id of the corpse, so the reaction can be remembered per body
    // (the Mob node itself may be freed while a sighting is still in flight).
    public ulong corpseId;
    public Vector3 position;
    // Where the killing damage came from — the attacker's position at the moment
    // of death. Null when there was no attacker to look for (a trap, a hazard,
    // a fall) or when the body was simply come across later.
    public Vector3? damageOrigin;
    // False when the sighting itself says not to go over there: a trap kill
    // reads as a place to keep away from, not a scene to inspect.
    public bool approach;
    // True when this mob watched the death happen, as opposed to coming across
    // the body afterwards — a trap kill counts. Decides which suspicion level
    // the reaction holds (see BehaviorInspectCorpse).
    public bool witnessed;
}

public partial class Mob
{
    // Generous spatial bound for the death broadcast — the exact per-witness
    // gate is that witness's own visionRange plus a sightline, inside
    // BroadcastCorpseSighting. Mirrors MaxListenerHearingRange: it has to be
    // generous, not tuned.
    private const float MaxWitnessVisionRange = 40f;
    // How many bodies a mob remembers having reacted to. Small on purpose — a
    // forgotten corpse costs one extra glance, and this is per resident mob.
    private const int MaxRememberedCorpses = 16;

    private static readonly List<Mob> _corpseWitnesses = new();

    public bool IsAirborne => _simState.Airborne;

    public CorpseSighting? CorpseSighting
    {
        get => _simState.CorpseSighting;
        set => _simState.CorpseSighting = value;
    }

    // True when this mob's brain wires a corpse-inspect node AND the species
    // notices bodies at all — resolved once in InitBehaviors so the discovery
    // scan and the death broadcast can skip every mob that never reacts.
    public bool ReactsToCorpses => _corpseInspectData != null;

    // The tuning of this mob's own corpse-inspect node, resolved once in
    // InitBehaviors. Also the ReactsToCorpses flag: null means either the brain
    // wires no such node or the species declines to notice bodies.
    private CorpseInspectBehaviorData _corpseInspectData;

    private void ResolveReactsToCorpses()
    {
        _corpseInspectData = null;
        if (mobData?.noticesCorpses != true)
        {
            return;
        }
        foreach (BehaviorBase behavior in _behaviors.Values)
        {
            if (behavior is BehaviorInspectCorpse inspect)
            {
                _corpseInspectData = inspect.Data;
                return;
            }
        }
    }

    // Called from Die(). Hands every mob that can SEE this death a sighting
    // carrying where the killer was, which is what gives a witness the
    // look-toward-the-killer leg that a body found later cannot have. "Can see"
    // is the witness's own visionRange plus a clear sightline — deliberately no
    // facing cone, since a death is loud enough to turn a head.
    private void BroadcastCorpseSighting()
    {
        if (_world?.MobSpatialHash == null)
        {
            return;
        }
        // Only a creature's kill gives a direction to look in. A trap, a hazard
        // zone or a fall is a place, not an attacker — witnesses stare at the
        // body instead, and do not walk over to the spot that just killed someone.
        bool fromActor = _simState.LastDamageFromActor;
        Vector3? damageOrigin = fromActor ? _simState.LastDamageSourcePosition : null;
        ulong corpseId = GetInstanceId();
        using (Profiler.Sample("Mob.CorpseBroadcast"))
        {
            _corpseWitnesses.Clear();
            _world.MobSpatialHash.QueryRadius(GlobalPosition, MaxWitnessVisionRange, _corpseWitnesses, exclude: this);
            Vector3 corpsePos = GlobalPosition;
            PhysicsDirectSpaceState3D space = GetWorld3D().DirectSpaceState;
            foreach (Mob witness in _corpseWitnesses)
            {
                if (witness == null || !witness.alive || !witness.ReactsToCorpses || witness.mobData == null)
                {
                    continue;
                }
                // The killer watched it happen from the inside — remember the
                // body on it so it cannot come back later to inspect its own kill.
                if (witness.GetInstanceId() == _simState.LastDamageSourceId)
                {
                    witness.RememberCorpse(corpseId);
                    continue;
                }
                float range = witness.mobData.visionRange;
                if (range <= 0f || witness.GlobalPosition.DistanceSquaredTo(corpsePos) > range * range)
                {
                    continue;
                }
                if (witness.HasSeenCorpse(corpseId))
                {
                    continue;
                }
                if (!witness.IsInVisionCone(corpsePos)
                    || !Sightline.IsClear(space, witness.GlobalPosition, corpsePos))
                {
                    continue;
                }
                witness.NoticeCorpse(corpseId, corpsePos, damageOrigin, approach: fromActor, witnessed: true);
            }
            _corpseWitnesses.Clear();
        }
    }

    // Walking onto a body nobody saw die. Runs from the throttled perception
    // tick over Sim's corpse list (a handful of bodies at most), so it costs a
    // count check in the overwhelmingly common case of no corpses loaded.
    private void ScanForCorpses()
    {
        if (!alive || !ReactsToCorpses || _simState.CorpseSighting.HasValue || mobData == null)
        {
            return;
        }
        // A mob already in a fight has no attention to spare, and the reaction
        // would be interrupted by its own aggro transition on the first tick.
        if (IsTriggered)
        {
            return;
        }
        IReadOnlyList<Mob> corpses = _world?.Corpses;
        if (corpses == null || corpses.Count == 0)
        {
            return;
        }
        float range = mobData.visionRange;
        if (range <= 0f)
        {
            return;
        }
        Vector3 myPos = GlobalPosition;
        float rangeSq = range * range;
        PhysicsDirectSpaceState3D space = null;
        for (int i = 0; i < corpses.Count; i++)
        {
            Mob corpse = corpses[i];
            if (corpse == null || !GodotObject.IsInstanceValid(corpse) || corpse == this)
            {
                continue;
            }
            ulong corpseId = corpse.GetInstanceId();
            Vector3 corpsePos = corpse.GlobalPosition;
            if (myPos.DistanceSquaredTo(corpsePos) > rangeSq || HasSeenCorpse(corpseId))
            {
                continue;
            }
            if (!IsInVisionCone(corpsePos))
            {
                continue;
            }
            // Resolved lazily: the space state is a native crossing, and most
            // scans never get as far as a candidate worth raycasting.
            space ??= GetWorld3D().DirectSpaceState;
            if (!Sightline.IsClear(space, myPos, corpsePos))
            {
                continue;
            }
            // Nobody saw it die, so there is no direction the damage came from.
            NoticeCorpse(corpseId, corpsePos, damageOrigin: null, approach: true, witnessed: false);
            return;
        }
    }

    // Post a sighting and remember the body in the same breath. Remembering
    // happens HERE rather than when the reaction finishes, so an aggro interrupt
    // does not replay the whole thing once the fight ends.
    private void NoticeCorpse(ulong corpseId, Vector3 position, Vector3? damageOrigin, bool approach, bool witnessed)
    {
        RememberCorpse(corpseId);
        // Seeing it happen rattles the mob more than finding what is left.
        RaiseSuspicion(witnessed
            ? _corpseInspectData.witnessedDeathSuspicion
            : _corpseInspectData.foundCorpseSuspicion);
        _simState.CorpseSighting = new CorpseSighting
        {
            corpseId = corpseId,
            position = position,
            damageOrigin = damageOrigin,
            approach = approach,
            witnessed = witnessed,
        };
    }

    public bool HasSeenCorpse(ulong corpseId)
    {
        List<ulong> seen = _simState.SeenCorpses;
        for (int i = 0; i < seen.Count; i++)
        {
            if (seen[i] == corpseId)
            {
                return true;
            }
        }
        return false;
    }

    private void RememberCorpse(ulong corpseId)
    {
        if (HasSeenCorpse(corpseId))
        {
            return;
        }
        List<ulong> seen = _simState.SeenCorpses;
        if (seen.Count >= MaxRememberedCorpses)
        {
            seen.RemoveAt(0);
        }
        seen.Add(corpseId);
    }
}
