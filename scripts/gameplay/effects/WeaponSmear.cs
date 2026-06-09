using Godot;

// A flat, animated swing smear that fades across the far edge of a weapon's
// damage fan. Built as an Fx scene so it spawns through the existing
// releaseEffect path (ItemEventHandlers.SpawnOnActor parents it to the actor,
// so the generated mesh inherits the actor's forward facing). Author one
// scene per swing flavor: a Node3D root with this script, an optional Audio
// child (the swoosh, played by the base Fx), and a MeshInstance3D child wired
// to `_meshInstance` carrying the weapon_smear material.
//
// The mesh is an annular sector in the actor's forward plane (forward = +Z),
// spanning `arcDegrees` and reaching from `nearRange` to `range`. Per-vertex
// UV2.x carries the normalized angular position from the entry side (0) to the
// exit side (1) — `clockwise` chooses which angular end is the entry side. The
// weapon_smear shader uses that coordinate plus the per-instance timing
// uniforms to drive the reveal / erase wipes:
//   - entryTime == 0: the smear shows instantly, then erases from the back
//     (entry) side between exitStartTime and exitTime.
//   - entryTime  > 0: the smear wipes on from the entry side toward the exit
//     side over [0, entryTime], then erases over [exitStartTime, exitTime].
[GlobalClass]
public partial class WeaponSmear : Fx
{
	[Export] private MeshInstance3D _meshInstance;

	// Geometry is driven by the attack shape through Initialize (so status
	// effects that resize the swing resize the smear), not authored per scene.
	// The defaults below only apply if the scene is used standalone without an
	// Initialize call (e.g. previewing in the editor).
	private float _range = 4f;
	private float _nearRange = 1.5f;
	private float _arcDegrees = 120f;
	// Sweep direction — flips which angular end is the entry side.
	private bool _clockwise = true;

	// Height above the actor's feet that the flat smear sits at — a visual
	// offset, independent of the attack's collision height, so it stays
	// authored per scene.
	[Export(PropertyHint.Range, "0,3,0.05")] private float _height = 0.3f;

	// Smallest / largest sweep the derived arc is clamped to, so an extreme
	// attack shape (e.g. a far disk that engulfs the actor) still reads sanely.
	private const float MinArcDegrees = 20f;
	private const float MaxArcDegrees = 200f;

	// Timing, in seconds from spawn. See the class comment for the two modes.
	[Export(PropertyHint.Range, "0,2,0.01")] private float _entryTime;
	[Export(PropertyHint.Range, "0,2,0.01")] private float _exitStartTime = 0.08f;
	[Export(PropertyHint.Range, "0,2,0.01")] private float _exitTime = 0.3f;
	// Extra seconds to keep the (now invisible) node alive after exitTime
	// before freeing — a small cushion so the final erased frame isn't clipped.
	[Export(PropertyHint.Range, "0,1,0.01")] private float _freeGrace = 0.05f;

	// Mesh resolution. Angular segments control how smooth the arc reads;
	// radial segments rarely need more than 1.
	[Export(PropertyHint.Range, "1,64,1")] private int _arcSegments = 24;
	[Export(PropertyHint.Range, "1,16,1")] private int _radialSegments = 1;

	private float _elapsed;

	// Size the smear to a melee attack's shape. Call BEFORE adding the node to
	// the tree so the values are in place when _Ready builds the mesh. The
	// outer radius is the attack's reach; the quads begin at the near disk
	// edge; the swept arc matches the angular width of the far disk as seen from
	// the actor, so a wider far cylinder (or a buff that widens it) opens the
	// sweep. `range`/`nearWidth`/`farWidth` are the same scalars DoMelee feeds
	// the damage query.
	public void Initialize(float range, float nearWidth, float farWidth, bool clockwise)
	{
		_range = Mathf.Max(0.01f, range);
		_nearRange = Mathf.Clamp(nearWidth * 0.5f, 0f, _range - 0.01f);
		float farRadius = farWidth * 0.5f;
		float farCenterDist = Mathf.Max(_range - farRadius, 0.01f);
		float halfAngle = Mathf.Atan2(farRadius, farCenterDist);
		_arcDegrees = Mathf.Clamp(Mathf.RadToDeg(halfAngle) * 2f, MinArcDegrees, MaxArcDegrees);
		_clockwise = clockwise;
	}

	public override void _Ready()
	{
		// Base Fx plays any Audio child and registers the active-count monitors.
		base._Ready();
		if (_meshInstance == null)
		{
			GD.PushWarning("WeaponSmear: _meshInstance is not assigned; no smear will render.");
			return;
		}
		_meshInstance.Mesh = BuildMesh();
		_meshInstance.SetInstanceShaderParameter("entry_time", _entryTime);
		_meshInstance.SetInstanceShaderParameter("exit_start_time", _exitStartTime);
		_meshInstance.SetInstanceShaderParameter("exit_time", _exitTime);
		_meshInstance.SetInstanceShaderParameter("time", 0f);
	}

	// Drive the smear's own clock and free deterministically once the exit wipe
	// has finished. Deliberately does NOT chain to base Fx._Process: a one-shot
	// Fx with no particles / playing audio frees itself on the first frame,
	// which would kill the smear before it animates.
	public override void _Process(double delta)
	{
		_elapsed += (float)delta;
		_meshInstance?.SetInstanceShaderParameter("time", _elapsed);
		if (_elapsed >= _exitTime + _freeGrace)
		{
			QueueFree();
		}
	}

	private ArrayMesh BuildMesh()
	{
		int arcSeg = Mathf.Max(1, _arcSegments);
		int radSeg = Mathf.Max(1, _radialSegments);
		int cols = arcSeg + 1;
		int rows = radSeg + 1;
		float half = Mathf.DegToRad(_arcDegrees) * 0.5f;

		int vertCount = cols * rows;
		var verts = new Vector3[vertCount];
		var normals = new Vector3[vertCount];
		var uvs = new Vector2[vertCount];
		var uv2s = new Vector2[vertCount];

		for (int i = 0; i < cols; i++)
		{
			float u = (float)i / arcSeg;
			float angle = Mathf.Lerp(-half, half, u);
			float sinA = Mathf.Sin(angle);
			float cosA = Mathf.Cos(angle);
			// Entry side depends on the sweep direction.
			float s = _clockwise ? 1f - u : u;
			for (int j = 0; j < rows; j++)
			{
				float v = (float)j / radSeg;
				float radius = Mathf.Lerp(_nearRange, _range, v);
				int idx = i * rows + j;
				// Forward is +Z; rotating +Z by `angle` about +Y gives this.
				verts[idx] = new Vector3(sinA * radius, _height, cosA * radius);
				normals[idx] = Vector3.Up;
				uvs[idx] = new Vector2(u, v);
				uv2s[idx] = new Vector2(s, 0f);
			}
		}

		var indices = new int[arcSeg * radSeg * 6];
		int t = 0;
		for (int i = 0; i < arcSeg; i++)
		{
			for (int j = 0; j < radSeg; j++)
			{
				int i0 = i * rows + j;
				int i1 = i * rows + (j + 1);
				int i2 = (i + 1) * rows + j;
				int i3 = (i + 1) * rows + (j + 1);
				indices[t++] = i0;
				indices[t++] = i2;
				indices[t++] = i1;
				indices[t++] = i1;
				indices[t++] = i2;
				indices[t++] = i3;
			}
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = verts;
		arrays[(int)Mesh.ArrayType.Normal] = normals;
		arrays[(int)Mesh.ArrayType.TexUV] = uvs;
		arrays[(int)Mesh.ArrayType.TexUV2] = uv2s;
		arrays[(int)Mesh.ArrayType.Index] = indices;

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}
}
