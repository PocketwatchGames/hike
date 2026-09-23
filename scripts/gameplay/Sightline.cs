using Godot;

// The one "can this creature see that point" test: an eye-height ray against
// solid world geometry and props. Perception, threat scans and the
// look/investigate/corpse behaviors all ask through here so they can't drift
// apart on what counts as visible.
public static class Sightline
{
    // Eye / nose height both ends of the ray are lifted to.
    public const float EyeHeight = 1.5f;

    // Convenience form: resolves the observer's position and space state. Use
    // the explicit overload inside a loop — both of those are native crossings.
    public static bool IsClear(Node3D observer, Vector3 target)
    {
        return IsClear(observer.GetWorld3D().DirectSpaceState, observer.GlobalPosition, target);
    }

    // `from` / `to` are body positions; the ray is lifted to eye height at both ends.
    public static bool IsClear(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
    {
        Vector3 rayStart = from + new Vector3(0f, EyeHeight, 0f);
        Vector3 rayEnd = to + new Vector3(0f, EyeHeight, 0f);
        using var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, (uint)ECollisionLayer.Solid);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        return space.IntersectRay(query).Count == 0;
    }
}
