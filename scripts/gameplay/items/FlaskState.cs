using Godot;

// A carried flask's fill, in FlaskData.capacity's unit.
public class FlaskState : ItemState
{
	public override FlaskData data => _flaskData;
	private readonly FlaskData _flaskData;

	public float amount;

	public FlaskState(FlaskData d) : base(d)
	{
		_flaskData = d;
		amount = d.capacity;
	}

	public float Room => _flaskData.capacity - amount;

	// Pour in up to `offered`; returns what didn't fit.
	public float Fill(float offered)
	{
		float taken = Mathf.Clamp(offered, 0f, Room);
		amount += taken;
		return offered - taken;
	}

	// Draw up to `wanted`; returns what was short.
	public float Draw(float wanted)
	{
		float taken = Mathf.Clamp(wanted, 0f, amount);
		amount -= taken;
		return wanted - taken;
	}

	public override void WriteSubclassState(System.IO.BinaryWriter w)
	{
		w.Write(amount);
	}

	public override void ReadSubclassState(System.IO.BinaryReader r)
	{
		amount = Mathf.Clamp(r.ReadSingle(), 0f, _flaskData.capacity);
	}
}
