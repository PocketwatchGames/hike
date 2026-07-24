using System.Collections.Generic;
using Godot;

// Drives the player's outfit visuals on the shared polysplit rig. The member's
// class outfit and any equipped ArmorData each name an entry in the central
// outfit registry (PlayerData.outfits) by key; this resolves those to the live
// gender's mesh sets, composites them with the always-on base (head + face) and
// the bare-body / hair defaults for uncovered slots, then hands the union to
// the model animator, which toggles visibility on the one shared skeleton — no
// reload, no skin rebind. Recomputed on every armor slot change and once at
// spawn.
//
// Targets the live gender model (_animator), so it composites onto
// whichever body type the player spawned as. The rig-specific part names (head
// shell, bare body, skin meshes, hair menu) are NOT hardcoded here — they live
// on each gender's ModelAnimator (baseMeshNames / bareBodyMeshNames /
// skinMeshNames / hairStyleMeshNames), authored per package scene, because the
// Female rig prefixes its parts F_ and the Male rig M_. Head-slot coverage is
// wired (an outfit's head meshes replace the hair fallback) but no current
// outfit authors head meshes.
public partial class Player : CharacterBody3D
{
	// The chosen hair-style mesh(es), shown in the head slot when no head armor
	// is worn — the bare-head fallback, resolved per-spawn from the appearance
	// palette (ApplyAppearance). Empty = bald (no hair mesh). Set before the
	// first UpdateArmorVisual so the composite picks up the styled hair.
	private string[] _hairStyleMeshes = System.Array.Empty<string>();

	// Apply the hosted member's modular appearance to the live model: a flat skin
	// tone on the face / bare-body skin meshes and the hair color on the chosen
	// hair-style mesh. Resolved from PlayerData's palettes by the indices on the
	// PlayerState and applied once at spawn — the `recolor` instance uniforms
	// persist across animation and visibility changes, so hair keeps its color
	// even while hidden under a helmet. Must run before UpdateArmorVisual so the
	// hair-style mesh is known when the visible set is composited.
	private void ApplyAppearance(PlayerState member)
	{
		if (_animator == null || data == null)
		{
			return;
		}
		string hairMesh = _animator.GetHairStyleMesh(member?.hairStyle ?? 0);
		_hairStyleMeshes = hairMesh != null ? new[] { hairMesh } : System.Array.Empty<string>();

		_animator.SetMeshRecolor(_animator.skinMeshNames, data.GetSkinTone(member?.skinTone ?? 0));
		_animator.SetMeshRecolor(_hairStyleMeshes, data.GetHairColor(member?.hairColor ?? 0));
	}

	// Recompose the visible mesh set from the class outfit and equipped armor,
	// and push it to the model. No-op before the model animator or inventory
	// exists. Per-slot precedence: equipped armor's outfit → class outfit →
	// bare head (styled hair) / bare body.
	private void UpdateArmorVisual()
	{
		if (_animator == null || _inventory == null)
		{
			return;
		}
		OutfitData classOutfit = data?.GetOutfit(Member?.outfit);
		List<string> visible = new(_animator.baseMeshNames);
		AppendSlotMeshes(EInventorySlot.Helmet, FirstNonEmpty(classOutfit?.GetHeadMeshNames(_gender), _hairStyleMeshes), visible);
		AppendSlotMeshes(EInventorySlot.Armor, FirstNonEmpty(classOutfit?.GetBodyMeshNames(_gender), _animator.bareBodyMeshNames), visible);
		_animator.SetVisibleMeshes(visible.ToArray());
	}

	// Append the equipped armor's outfit meshes for `slot`, or the fallback when
	// nothing is equipped there (or its outfit has no meshes for this slot and
	// gender).
	private void AppendSlotMeshes(EInventorySlot slot, string[] fallback, List<string> dst)
	{
		if (_inventory.GetEquipped(slot) is ArmorState armor
			&& armor.data is ArmorData armorData
			&& data?.GetOutfit(armorData.outfit) is OutfitData outfit
			&& SlotMeshes(outfit, slot) is { Length: > 0 } worn)
		{
			dst.AddRange(worn);
		}
		else
		{
			dst.AddRange(fallback);
		}
	}

	// The outfit mesh set an equip slot draws from: head pieces show the head
	// meshes, body pieces the body meshes.
	private string[] SlotMeshes(OutfitData outfit, EInventorySlot slot)
	{
		return slot == EInventorySlot.Helmet ? outfit.GetHeadMeshNames(_gender) : outfit.GetBodyMeshNames(_gender);
	}

	private static string[] FirstNonEmpty(string[] preferred, string[] fallback)
	{
		return preferred is { Length: > 0 } ? preferred : fallback;
	}
}
