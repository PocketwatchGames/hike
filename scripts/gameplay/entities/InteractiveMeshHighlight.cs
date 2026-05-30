using System.Collections.Generic;
using Godot;

// The 3D-mesh counterpart of the sprite selection outline. Toggles the
// inverted-hull outline (mesh_outline.gdshader) on a set of MeshInstance3Ds so a
// SOLID interactive (statue, sign, chest, the climbable tree's ladder rungs)
// highlights the same way sprite interactives do when targeted — but on real
// geometry instead of a billboard overlay.
//
// Outline only: there is NO through-cover X-ray on mesh interactives. The outline
// is driven entirely by GameClient.ApplyHighlight/RemoveHighlight via SetSelected
// when this interactive is the player's current highlight target — exactly when
// the sprite outline overlay would appear. No per-frame probe, no proximity logic.
//
// Wiring: drop one as a direct child of the interactive root. Point `_meshes` at
// the meshes that should highlight, and/or set `_collectFrom` to a node whose
// MeshInstance3D descendants are gathered (used for instanced-FBX models and the
// ladder's authored rung nodes). The push targets a per-instance shader uniform,
// so the materials stay shared and batchable.
[GlobalClass]
public partial class InteractiveMeshHighlight : Node3D
{
    // Explicitly authored highlight meshes (e.g. a statue body MeshInstance3D).
    // Left empty when using _collectFrom for instanced-FBX / multi-mesh sets.
    [Export] private Godot.Collections.Array<MeshInstance3D> _meshes = new();
    // Optional: gather every MeshInstance3D under this node into the highlight
    // set. Used for instanced FBX models (meshes live in a sub-scene) and the
    // climbable tree's Ladder node (authored rung children).
    [Export] private Node3D _collectFrom;

    private bool _selected;
    private bool _collected;
    private readonly List<MeshInstance3D> _targets = new();

    public override void _Ready()
    {
        EnsureCollected();
    }

    // Gather the highlight meshes once: the explicit list plus, if set, every
    // MeshInstance3D under _collectFrom. Retried from SetSelected if the set is
    // still empty (a sub-scene that built after our _Ready).
    private void EnsureCollected()
    {
        if (_collected)
        {
            return;
        }
        _collected = true;
        _targets.Clear();
        foreach (MeshInstance3D m in _meshes)
        {
            if (m != null)
            {
                _targets.Add(m);
            }
        }
        if (_collectFrom != null)
        {
            CollectMeshes(_collectFrom);
        }
    }

    private void CollectMeshes(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MeshInstance3D mi)
            {
                _targets.Add(mi);
            }
            CollectMeshes(child);
        }
    }

    // Toggle the inverted-hull outline. Called by GameClient when this
    // interactive becomes / stops being the player's highlight target.
    public void SetSelected(bool selected)
    {
        if (_selected == selected)
        {
            return;
        }
        _selected = selected;
        // Re-collect if the first pass found nothing (generated/instanced meshes
        // may not have existed at our _Ready).
        if (_targets.Count == 0 && _collectFrom != null)
        {
            _collected = false;
            EnsureCollected();
        }
        float v = selected ? 1f : 0f;
        foreach (MeshInstance3D m in _targets)
        {
            if (m != null)
            {
                Rid rid = m.GetInstance();
                if (rid.IsValid)
                {
                    RenderingServer.InstanceGeometrySetShaderParameter(rid, "selected", v);
                }
            }
        }
    }
}
