using System.Collections.Generic;
using Godot;

// Drives the player's worn-armor visuals on the shared polysplit rig. Each
// equipped ArmorData names the MeshInstance3D parts it shows (wornMeshNames);
// this composites those with the always-on base (head + face) and the per-slot
// "bare body" defaults for empty slots, then hands the union to the model
// animator, which toggles visibility on the one shared skeleton — no reload,
// no skin rebind. Recomputed on every armor slot change and once at spawn.
//
// Placeholder scope: the base / bare-default mesh names below are the
// BasicHero_F rig's body parts. Head-slot coverage is wired but no head armor
// exists yet. When richer authoring lands, only the body-rig constants here
// need revisiting — the per-piece meshes are already data (ArmorData).
public partial class Player : CharacterBody3D
{
	// Always visible regardless of equipment: head shell + facial features.
	static readonly string[] ArmorBaseMeshes =
	{
		"F_Head", "F_eyes0", "F_eyebrows0", "F_mouth0",
	};

	// Per-slot fallbacks shown when that slot is empty (or its armor authored
	// no meshes): bare hair for the head, bare torso + legs for the body.
	static readonly string[] ArmorBareHead = { "F_hair_1" };
	static readonly string[] ArmorBareBody = { "F_TopBody", "F_BottomBody" };

	// Recompose the visible mesh set from equipped armor and push it to the
	// model. No-op on the sprite player (no model animator) or before the
	// inventory exists.
	private void UpdateArmorVisual()
	{
		if (_modelAnimator == null || _inventory == null)
		{
			return;
		}
		List<string> visible = new(ArmorBaseMeshes);
		AppendSlotMeshes(EInventorySlot.ArmorHead, ArmorBareHead, visible);
		AppendSlotMeshes(EInventorySlot.ArmorBody, ArmorBareBody, visible);
		_modelAnimator.SetVisibleMeshes(visible.ToArray());
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
