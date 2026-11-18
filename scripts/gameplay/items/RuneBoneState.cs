using System.IO;

// One rune bone: the species and the position (0-based) of the rune etched on
// it. Set by RuneBoneSpawnEntry when the bone is buried.
public class RuneBoneState : ItemState
{
	public SpeciesData species;
	public int position;

	public RuneBoneState(RuneBoneData d) : base(d)
	{
	}

	// Null when the species has no rune at `position`.
	public RuneData Rune
	{
		get
		{
			RuneData[] sequence = species?.RunesFlat;
			return sequence != null && position >= 0 && position < sequence.Length ? sequence[position] : null;
		}
	}

	// Inside an EntitySerializer item write (refs resolve against its table).
	public override void WriteSubclassState(BinaryWriter w)
	{
		EntitySerializer.WriteRef(w, species);
		w.Write(position);
	}

	public override void ReadSubclassState(BinaryReader r)
	{
		species = EntitySerializer.ReadRef<SpeciesData>(r);
		position = r.ReadInt32();
	}

	protected override void CopySubclassStateTo(ItemState dest)
	{
		var bone = (RuneBoneState)dest;
		bone.species = species;
		bone.position = position;
	}
}
