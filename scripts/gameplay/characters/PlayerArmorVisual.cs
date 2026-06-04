using System.Collections.Generic;
using Godot;

// Drives the player's worn-armor visuals on the shared polysplit rig. Each
// equipped ArmorData names the MeshInstance3D parts it shows (wornMeshNames);
// this composites those with the always-on base (head + face) and the per-slot
// "bare body" defaults for empty slots, then hands the union to the model
// animator, which toggles visibility on the one shared skeleton — no reload,
// no skin rebind. Recomputed on every armor slot change and once at spawn.
//
// Targets the live gender model (_activeModelAnimator), so it composites onto
// whichever body type the player spawned as.
//
// Placeholder scope: the base / bare-default mesh names below are the
// BasicHero_F (Female) rig's body parts. Head-slot coverage is wired but no
// head armor exists yet. When a second body type (EGender.Male) and richer
// authoring land, these base/bare constants become per-gender (the male rig
// names its parts M_*); the per-piece meshes are already data (ArmorData).
public partial class Player : CharacterBody3D
{
	// Always visible regardless of equipment: head shell + facial features.
	static readonly string[] ArmorBaseMeshes =
	{
		"F_Head", "F_eyes0", "F_eyebrows0", "F_mouth0",
	};

	// Bare-body fallback shown when the body slot is empty (or its armor
	// authored no meshes): bare torso + legs.
	static readonly string[] ArmorBareBody = { "F_TopBody", "F_BottomBody" };

	// Skin meshes recolored by the chosen skin tone — face shell + bare body.
	// (The bare body only shows when no body armor is worn; recoloring it while
	// hidden is harmless.) Same female-rig placeholder caveat as the constants
	// above; becomes per-gender with the male rig.
	static readonly string[] SkinMeshNames = { "F_Head", "F_TopBody", "F_BottomBody" };

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
		if (_activeModelAnimator == null || data == null)
		{
			return;
		}
		string hairMesh = data.GetHairStyleMesh(spawnData?.hairStyle ?? 0);
		_hairStyleMeshes = hairMesh != null ? new[] { hairMesh } : System.Array.Empty<string>();

		_activeModelAnimator.SetMeshRecolor(SkinMeshNames, data.GetSkinTone(spawnData?.skinTone ?? 0));
		_activeModelAnimator.SetMeshRecolor(_hairStyleMeshes, data.GetHairColor(spawnData?.hairColor ?? 0));
	}

	// Recompose the visible mesh set from equipped armor and push it to the
	// model. No-op on the sprite player (no model animator) or before the
	// inventory exists.
	private void UpdateArmorVisual()
	{
		if (_activeModelAnimator == null || _inventory == null)
		{
			return;
		}
		List<string> visible = new(ArmorBaseMeshes);
		AppendSlotMeshes(EInventorySlot.ArmorHead, _hairStyleMeshes, visible);
		AppendSlotMeshes(EInventorySlot.ArmorBody, ArmorBareBody, visible);
		_activeModelAnimator.SetVisibleMeshes(visible.ToArray());
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
