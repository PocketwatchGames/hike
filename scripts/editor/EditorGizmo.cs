using Godot;
using System.Collections.Generic;

// Which gizmo handle the cursor is over, or is dragging.
public enum EGizmoHandle
{
    None,
    // Free-drag across the horizontal plane through the pivot.
    Ground,
    // Vertical arrow — moves the selection in Y only.
    Vertical,
    // Ring around Y — orbits the selection about the pivot and turns each
    // entity's facing by the same angle.
    Rotate,
}

// The editor's translate / rotate gizmo: geometry, hit-testing and drag math for
// the entity selection's pivot. It draws through DebugDraw (immediate mode,
// re-emitted every frame) instead of owning meshes, so there is nothing to keep
// in sync as the selection changes.
//
// A ground plane plus a Y arrow rather than three axis arrows: at this camera's
// fixed isometric angle a thin X or Z arrow is nearly edge-on and miserable to
// grab, while the horizontal plane is the one the author is working across
// anyway.
[GlobalClass]
public partial class EditorGizmo : Node
{
    [Export(PropertyHint.Range, "0.1,5,0.05")] public float groundHandleRadius = 1f;
    [Export(PropertyHint.Range, "0.5,10,0.1")] public float verticalHandleHeight = 3f;
    [Export(PropertyHint.Range, "0.5,10,0.1")] public float rotateRingRadius = 2.4f;
    // World-space slack around a handle's ideal geometry that still counts as a
    // grab. Generous on purpose — the scene renders at 480x270 before upscale,
    // so one screen pixel covers a lot of world.
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float grabTolerance = 0.45f;
    [Export(PropertyHint.Range, "8,64,1")] public int ringSegments = 32;

    [ExportGroup("Colors")]
    [Export] public Color groundColor = new Color(0.4f, 1f, 0.55f);
    [Export] public Color verticalColor = new Color(0.5f, 0.72f, 1f);
    [Export] public Color rotateColor = new Color(1f, 0.85f, 0.4f);
    // Whichever handle the cursor is over (or is dragging) draws in this instead.
    [Export] public Color hotColor = new Color(1f, 1f, 1f);

    // ----- Drawing ---------------------------------------------------------

    public void Draw(Vector3 pivot, EGizmoHandle hot)
    {
        DrawGroundHandle(pivot, hot == EGizmoHandle.Ground ? hotColor : groundColor);
        DrawVerticalHandle(pivot, hot == EGizmoHandle.Vertical ? hotColor : verticalColor);
        DrawRotateRing(pivot, hot == EGizmoHandle.Rotate ? hotColor : rotateColor);
    }

    private void DrawGroundHandle(Vector3 pivot, Color color)
    {
        // A cross inside its disc, so the pivot itself is readable even when the
        // disc sits over busy geometry.
        DrawRing(pivot, groundHandleRadius, color);
        DebugDraw.Line(pivot - new Vector3(groundHandleRadius, 0f, 0f), pivot + new Vector3(groundHandleRadius, 0f, 0f), color);
        DebugDraw.Line(pivot - new Vector3(0f, 0f, groundHandleRadius), pivot + new Vector3(0f, 0f, groundHandleRadius), color);
    }

    private void DrawVerticalHandle(Vector3 pivot, Color color)
    {
        DebugDraw.Arrow(pivot, pivot + Vector3.Up * verticalHandleHeight, color);
    }

    private void DrawRotateRing(Vector3 pivot, Color color)
    {
        DrawRing(pivot, rotateRingRadius, color);
    }

    private void DrawRing(Vector3 center, float radius, Color color)
    {
        var points = new List<Vector3>(ringSegments + 1);
        for (int i = 0; i <= ringSegments; i++)
        {
            float angle = Mathf.Tau * i / ringSegments;
            points.Add(center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius));
        }
        DebugDraw.Lines(points, color);
    }

    // ----- Hit testing -----------------------------------------------------

    // Handles are tested nearest-to-the-author first: the vertical arrow stands
    // clear of the plane so it wins outright, then the ring's rim, then the disc
    // at the middle. Without that order the disc would swallow the ring wherever
    // the two overlap on screen.
    public EGizmoHandle Pick(Vector3 pivot, Vector3 rayOrigin, Vector3 rayDir)
    {
        if (RayHitsVerticalHandle(pivot, rayOrigin, rayDir))
        {
            return EGizmoHandle.Vertical;
        }
        if (!RayPlaneY(rayOrigin, rayDir, pivot.Y, out Vector3 planeHit))
        {
            return EGizmoHandle.None;
        }
        float distance = HorizontalDistance(planeHit, pivot);
        if (Mathf.Abs(distance - rotateRingRadius) <= grabTolerance)
        {
            return EGizmoHandle.Rotate;
        }
        if (distance <= groundHandleRadius + grabTolerance)
        {
            return EGizmoHandle.Ground;
        }
        return EGizmoHandle.None;
    }

    private bool RayHitsVerticalHandle(Vector3 pivot, Vector3 rayOrigin, Vector3 rayDir)
    {
        ClosestPointOnVerticalAxis(pivot, rayOrigin, rayDir, out float axisOffset, out float distance);
        return axisOffset >= 0f && axisOffset <= verticalHandleHeight && distance <= grabTolerance;
    }

    // ----- Drag math -------------------------------------------------------

    // Where the cursor ray meets the horizontal plane through `planeY`. False
    // when the ray runs parallel to it or points away — the caller keeps the
    // drag's last good value rather than snapping the selection somewhere wild.
    public static bool RayPlaneY(Vector3 rayOrigin, Vector3 rayDir, float planeY, out Vector3 point)
    {
        point = default;
        if (Mathf.IsZeroApprox(rayDir.Y))
        {
            return false;
        }
        float t = (planeY - rayOrigin.Y) / rayDir.Y;
        if (t < 0f)
        {
            return false;
        }
        point = rayOrigin + rayDir * t;
        return true;
    }

    // Height the cursor ray picks out along the vertical axis through the pivot,
    // i.e. what a vertical drag should set the pivot's Y to.
    public bool TryVerticalY(Vector3 pivot, Vector3 rayOrigin, Vector3 rayDir, out float y)
    {
        ClosestPointOnVerticalAxis(pivot, rayOrigin, rayDir, out float axisOffset, out _);
        y = pivot.Y + axisOffset;
        // Degenerate only when the view is dead along the axis, which this
        // camera's fixed pitch never is.
        return !Mathf.IsZeroApprox(rayDir.X) || !Mathf.IsZeroApprox(rayDir.Z);
    }

    // Angle of the cursor around the pivot, measured in the horizontal plane.
    public bool TryRotateAngle(Vector3 pivot, Vector3 rayOrigin, Vector3 rayDir, out float angle)
    {
        angle = 0f;
        if (!RayPlaneY(rayOrigin, rayDir, pivot.Y, out Vector3 planeHit))
        {
            return false;
        }
        Vector3 offset = planeHit - pivot;
        if (Mathf.IsZeroApprox(offset.X) && Mathf.IsZeroApprox(offset.Z))
        {
            return false;
        }
        angle = Mathf.Atan2(offset.X, offset.Z);
        return true;
    }

    // Closest approach between the cursor ray and the infinite vertical line
    // through the pivot. `axisOffset` is how far up that line the closest point
    // sits (metres from the pivot); `distance` is the gap between the two lines.
    private static void ClosestPointOnVerticalAxis(Vector3 pivot, Vector3 rayOrigin, Vector3 rayDir, out float axisOffset, out float distance)
    {
        // Standard closest-points-between-two-lines, specialized to a vertical
        // second line so the dot products collapse to component reads.
        Vector3 between = pivot - rayOrigin;
        float rayDotAxis = rayDir.Y;
        float denominator = 1f - rayDotAxis * rayDotAxis;
        if (Mathf.IsZeroApprox(denominator))
        {
            // Ray is parallel to the axis — no meaningful point along it.
            axisOffset = 0f;
            distance = HorizontalDistance(rayOrigin, pivot);
            return;
        }
        float betweenDotRay = between.Dot(rayDir);
        float betweenDotAxis = between.Y;
        axisOffset = (betweenDotAxis - betweenDotRay * rayDotAxis) / denominator * -1f;
        float rayT = (betweenDotRay - betweenDotAxis * rayDotAxis) / denominator;
        Vector3 onRay = rayOrigin + rayDir * rayT;
        Vector3 onAxis = pivot + Vector3.Up * axisOffset;
        distance = onRay.DistanceTo(onAxis);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        return new Vector2(a.X - b.X, a.Z - b.Z).Length();
    }
}
