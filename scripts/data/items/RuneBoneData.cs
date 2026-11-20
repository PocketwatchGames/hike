using Godot;

// An Ancient Bone etched with one of a species' secret runes — buried treasure,
// never rolled loot. WHICH rune is per bone (RuneBoneState), set by the
// placement that buries it (RuneBoneSpawnEntry), so one item serves every
// species. Read the moment it is picked up and spent.
// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class RuneBoneData : ItemData, IApplyOnPickup
{
	protected override EItemCategory ComputeCategory() => EItemCategory.Usable;

	public override ItemState CreateState() => new RuneBoneState(this);

	public bool Magnetized => false;

	// Teaches the bone's rune and shows it even when already known: the bone is
	// spent either way, and the player should see what was on it.
	public bool ApplyOnPickup(Player player, ItemState item)
	{
		var bone = item as RuneBoneState;
		RuneData rune = bone?.Rune;
		if (rune == null)
		{
			GD.PushError($"RuneBoneData: picked-up bone names no rune (species "
				+ $"'{bone?.species?.ResourcePath}', position {bone?.position}).");
			return true;
		}
		SimState sim = player?.Sim?.WorldState?.SimState;
		sim?.LearnRune(bone.species, bone.position);
		GameClient.Current?.AnnounceDiscovery(new Announcement
		{
			type = EAnnouncementType.RuneLearned,
			title = Loc.Get(Loc.Keys.rune_bone_etched),
			revealIcon = rune.icon,
		});
		// The species' page shows its rune sequence, so open it there — unless the
		// species is undiscovered, which would reveal it; then the banner alone.
		// The banner draws on its own canvas layer, above the almanac.
		if (sim != null && sim.IsSpeciesDiscovered(bone.species))
		{
			GameClient.Current?.OpenBestiary(bone.species);
		}
		return true;
	}
}
