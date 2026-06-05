using System.Collections.Generic;
using Godot;

// Drives the player's worn-armor visuals on the shared polysplit rig. Each
// equipped ArmorData names the MeshInstance3D parts it shows (wornMeshNames);
// this composites those with the always-on base (head + face) and the per-slot
// "bare body" defaults for empty slots, then hands the union to the model
// animator, which toggles visibility on the one shared skeleton — no reload,
// no skin rebind. Recomputed on every armor slot change and once at spawn.
//
// Targets the live gender model (_animator), so it composites onto
// whichever body type the player spawned as. The rig-specific part names (head
// shell, bare body, skin meshes, hair menu) are NOT hardcoded here — they live
// on each gender's ModelAnimator (baseMeshNames / bareBodyMeshNames /
// skinMeshNames / hairStyleMeshNames), authored per package scene, because the
// Female rig prefixes its parts F_ and the Male rig M_. Head-slot coverage is
// wired but no head armor exists yet; the per-piece worn meshes are data
// (ArmorData.wornMeshNames).
public partial class Player : CharacterBody3D
{
	// The chosen hair-style mesh(es), shown in the head slot when no head armor
	// is worn — the bare-head fallback, resolved per-spawn from the appearance
	// palette (ApplyAppearance). Empty = bald (no hair mesh). Set before the
	// first UpdateArmorVisual so the composite picks up the styled hair.
	private string[] _hairStyleMeshes = System.Array.Empty<string>();

	// Apply the spawned modular appearance to the live model: a flat skin tone
	// on the face / bare-body skin meshes and the hair color on the chosen
	// hair-style mesh. Resolved from PlayerData's palettes by the indices on
	// PlayerSpawnData and applied once at spawn — the `recolor` instance uniforms
	// persist across animation and visibility changes, so hair keeps its color
	// even while hidden under a helmet. Must run before UpdateArmorVisual so the
	// hair-style mesh is known when the visible set is composited.
	private void ApplyAppearance(PlayerSpawnData spawnData)
	{
		if (_animator == null || data == null)
		{
			return;
		}
		string hairMesh = _animator.GetHairStyleMesh(spawnData?.hairStyle ?? 0);
		_hairStyleMeshes = hairMesh != null ? new[] { hairMesh } : System.Array.Empty<string>();

		_animator.SetMeshRecolor(_animator.skinMeshNames, data.GetSkinTone(spawnData?.skinTone ?? 0));
		_animator.SetMeshRecolor(_hairStyleMeshes, data.GetHairColor(spawnData?.hairColor ?? 0));
	}

	// Recompose the visible mesh set from equipped armor and push it to the
	// model. No-op before the model animator or inventory exists.
	private void UpdateArmorVisual()
	{
		if (_animator == null || _inventory == null)
		{
			return;
		}
		List<string> visible = new(_animator.baseMeshNames);
		AppendSlotMeshes(EInventorySlot.ArmorHead, _hairStyleMeshes, visible);
		AppendSlotMeshes(EInventorySlot.ArmorBody, _animator.bareBodyMeshNames, visible);
		_animator.SetVisibleMeshes(visible.ToArray());
	}

	// Append the equipped armor's worn meshes for `slot`, or the bare-body
	// fallback when nothing (or armor with no authored mesh) is equipped there.
	private void AppendSlotMeshes(EInventorySlot slot, string[] bareFallback, List<string> dst)
	{
		if (_inventory.GetEquipped(slot) is ArmorState armor
			&& armor.data is ArmorData armorData
			&& armorData.wornMeshNames is { Length: > 0 })
		{
			dst.AddRange(armorData.wornMeshNames);
		}
		else
		{
			dst.AddRange(bareFallback);
		}
	}
}
