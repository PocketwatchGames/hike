using System.Collections.Generic;
using Godot;

// Tri-state result of the foliage-occlusion probe. Tight = at least
// one cluster overlaps the tight (visible) cutaway radius — player is
// actively obscured. Wide = nothing in the tight radius but something
// sits in the wider "neighborhood" radius — player just stepped into
// a small clearing inside the forest, hold cutaway at a small minimum
// so re-expansion is instant when they round another tree. None = no
// fading foliage anywhere nearby — drop the cutaway to zero hard.
// Ordered Tight > Wide > None so the probe can early-out on Tight and
// a "max so far" merge over multiple props is just Math.Max.
public enum FadeProbeResult
{
    None = 0,
    Wide = 1,
    Tight = 2,
}

// Per-frame CPU probe that classifies the camera→player capsule volume against
// nearby fade-eligible foliage clusters, driving the ceiling/canopy cutaway in
// GameClient. Lives apart from World — it's a camera/render concern that only
// needs read access to the entity buckets — and is owned by World (exposed as
// World.FadeProbe) so it shares the one live entity index.
public class FoliageCutawayProbe
{
    private readonly WorldState _worldState;

    public FoliageCutawayProbe(WorldState worldState)
    {
        _worldState = worldState;
    }

    // Classifies the camera→player capsule volume against nearby fade-eligible
    // foliage clusters. Returns BOTH a tier classification (Tight / Wide /
    // None) AND the count of unique PROPS with at least one fading cluster
    // inside the wider probe radius. GameClient uses the tier to pick a target
    // band (full / minimum / off) and the prop count to scale the full target
    // by local cover density — so one tree gives a small cutaway and a thicket
    // of trees gives a bigger one even when only one is directly behind the
    // player.
    //
    // The count is intentionally per-PROP, not per-cluster: every tree
    // has 3-6 authored clusters (trunk-base, mid-canopy, top, etc.) and
    // counting each as a separate hit would saturate the density scale
    // after just a single isolated tree. Counting props matches the
    // intuition "how many trees are nearby", which is what we actually
    // want to drive the cutaway size.
    //
    // The scan walks every entity bucket but early-outs per-prop on a
    // squared XZ distance gate. Within a prop, we early-break the cluster
    // loop on the first Tight hit (can't escalate further) but otherwise
    // scan all clusters to find one that might be tight even if an
    // earlier one was only wide.
    public FadeProbeResult Probe(Vector3 cameraPos, Vector3 capsuleFeet, Vector3 capsuleHead, float tightRadius, float wideRadius, float scanRange, out int nearbyPropCount)
    {
        nearbyPropCount = 0;
        Vector3 segDir = capsuleHead - cameraPos;
        float segLenSq = segDir.LengthSquared();
        if (segLenSq < 1e-4f)
        {
            return FadeProbeResult.None;
        }
        // Horizontal-only cull around the player's ground position — trees
        // sit at ground level (prop.WorldPosition.Y ≈ player feet Y), while
        // the camera→head midpoint floats meters up. A 3D-distance gate
        // there would burn most of the scan range on vertical separation
        // and reject trees that are right next to the player horizontally.
        // XZ distance to the player is the actual signal: any tree close
        // enough horizontally to plausibly intercept the segment gets the
        // per-cluster test.
        float scanRangeSq = scanRange * scanRange;

        Vector3 playerAxis = capsuleHead - capsuleFeet;
        float playerAxisLenSq = Mathf.Max(playerAxis.LengthSquared(), 1e-4f);

        // Horizontal half-space: anything past the player along the
        // camera→player ground vector is behind the player from the camera's
        // POV and doesn't obscure the silhouette. Mirrors the shader's
        // t_horiz <= 1.0 gate so probe + render judgments stay in sync.
        float camToPlayerXzX = capsuleFeet.X - cameraPos.X;
        float camToPlayerXzZ = capsuleFeet.Z - cameraPos.Z;
        float camToPlayerXzLenSq = Mathf.Max(camToPlayerXzX * camToPlayerXzX + camToPlayerXzZ * camToPlayerXzZ, 1e-4f);

        FadeProbeResult best = FadeProbeResult.None;
        foreach (List<EntitySimState> bucket in _worldState._entities.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i] is not PropSimState prop)
                {
                    continue;
                }
                float dx = prop.WorldPosition.X - capsuleFeet.X;
                float dz = prop.WorldPosition.Z - capsuleFeet.Z;
                if (dx * dx + dz * dz > scanRangeSq)
                {
                    continue;
                }
                FoliageOccluder[] occluders = FoliageOccluderCache.GetOccluders(prop.Scene);
                if (occluders.Length == 0)
                {
                    continue;
                }
                float cos = Mathf.Cos(prop.RotationY);
                float sin = Mathf.Sin(prop.RotationY);
                // Per-prop accumulators — a prop counts as ONE hit
                // regardless of how many of its clusters land in range.
                bool propHasTight = false;
                bool propHasWide = false;
                for (int o = 0; o < occluders.Length; o++)
                {
                    FoliageOccluder occ = occluders[o];
                    if (!occ.FadesWhenOccludingPlayer)
                    {
                        continue;
                    }
                    // Rotate occluder local pos around Y by prop's
                    // rotation, then translate to world — matches the
                    // FoliageStamper transform path so the test sees the
                    // same cluster center the renderer does.
                    float rx = cos * occ.CenterLocal.X + sin * occ.CenterLocal.Z;
                    float rz = -sin * occ.CenterLocal.X + cos * occ.CenterLocal.Z;
                    Vector3 centerWorld = new Vector3(
                        prop.WorldPosition.X + rx,
                        prop.WorldPosition.Y + occ.CenterLocal.Y,
                        prop.WorldPosition.Z + rz);

                    // Horizontal half-space test — skip clusters past the
                    // player along the camera→player ground vector.
                    float tHoriz = ((centerWorld.X - cameraPos.X) * camToPlayerXzX
                                  + (centerWorld.Z - cameraPos.Z) * camToPlayerXzZ) / camToPlayerXzLenSq;
                    if (tHoriz > 1f)
                    {
                        continue;
                    }

                    // Same geometry the shader runs: project onto player
                    // capsule axis, then test against the camera→that-axis-
                    // point segment. Keeps probe + shader judgments in sync.
                    float tAxis = Mathf.Clamp(
                        (centerWorld - capsuleFeet).Dot(playerAxis) / playerAxisLenSq, 0f, 1f);
                    Vector3 axisPt = capsuleFeet + playerAxis * tAxis;

                    Vector3 segToAxis = axisPt - cameraPos;
                    float segToAxisLenSq = Mathf.Max(segToAxis.LengthSquared(), 1e-4f);
                    float tSeg = Mathf.Clamp(
                        (centerWorld - cameraPos).Dot(segToAxis) / segToAxisLenSq, 0f, 1f);
                    Vector3 closest = cameraPos + segToAxis * tSeg;
                    float dist = (centerWorld - closest).Length();

                    float clusterMax = Mathf.Max(occ.Radii.X, Mathf.Max(occ.Radii.Y, occ.Radii.Z));
                    if (dist < clusterMax + tightRadius)
                    {
                        propHasTight = true;
                        propHasWide = true;
                        // Tight is the strongest classification this prop
                        // can hit — no point checking its other clusters.
                        break;
                    }
                    if (dist < clusterMax + wideRadius)
                    {
                        propHasWide = true;
                        // Keep scanning — a later cluster on the same prop
                        // could escalate the prop to Tight.
                    }
                }

                if (propHasTight)
                {
                    nearbyPropCount++;
                    best = FadeProbeResult.Tight;
                }
                else if (propHasWide)
                {
                    nearbyPropCount++;
                    if (best == FadeProbeResult.None)
                    {
                        best = FadeProbeResult.Wide;
                    }
                }
            }
        }
        return best;
    }
}
