using Godot;

// The grass's memory of being walked through and struck. A grid of 2D damped
// springs around the controlled player — one horizontal lean (m at 1 m of
// blade height) per cell — that every grounded party member, and every mob
// the player can currently see, presses as they walk and that swings and blasts
// kick. Pressed cells hold their lean; released
// ones spring back with an overshoot, so a walker leaves a wake that wobbles
// upright behind them. detail_sprite.gdshader reads it as the `trample_field`
// global and bends each sprite by the lean under its root.
//
// Storage is TOROIDAL: world cell (x, z) always lives in slot (x mod N, z mod N),
// so the window following the player never shifts data — only the strip it
// scrolls onto is cleared. The shader samples with repeat and masks to the
// window. Purely presentational (wall clock); the texture is re-uploaded only on
// frames where an encoded value moved.
[GlobalClass]
public partial class TrampleField : Node
{
	// Cells per side. Locked at _Ready (the texture is allocated once).
	[Export(PropertyHint.Range, "16,256,1")] public int resolution = 64;
	// Side of one cell (m). resolution * cellSize is the window the field covers.
	[Export(PropertyHint.Range, "0.1,1,0.05")] public float cellSize = 0.25f;
	// Largest lean the texture can encode (m at 1 m blade height); also the clamp.
	[Export(PropertyHint.Range, "0.05,2,0.05")] public float maxLean = 0.6f;

	[ExportGroup("Walkers")]
	// Radius (m) of the patch a party member presses. A mob presses its
	// MobData.groundShadowRadius — the footprint its blob shadow already draws.
	[Export(PropertyHint.Range, "0.1,2,0.05")] public float footRadius = 0.5f;
	// Lean (m) pushed radially away from a walker standing still.
	[Export(PropertyHint.Range, "0,1,0.01")] public float standingLean = 0.2f;
	// Extra lean (m) along the direction of travel at fullSpeed.
	[Export(PropertyHint.Range, "0,1,0.01")] public float movingLean = 0.35f;
	[Export(PropertyHint.Range, "0.5,12,0.1")] public float fullSpeed = 4f;

	[ExportGroup("Spring")]
	// Stiffness (1/s²) and damping (1/s). sqrt(stiffness) / TAU is the wobble
	// frequency in Hz; damping below 2 * sqrt(stiffness) overshoots.
	[Export(PropertyHint.Range, "1,400,1")] public float stiffness = 45f;
	[Export(PropertyHint.Range, "0,40,0.1")] public float damping = 4f;

	[ExportGroup("Strikes")]
	// Lean velocity (m/s) a melee swing / an area blast throws the grass at.
	[Export(PropertyHint.Range, "0,20,0.1")] public float meleeKick = 3f;
	[Export(PropertyHint.Range, "0,20,0.1")] public float burstKick = 5f;

	private const string FieldGlobal = "trample_field";
	private const string WindowGlobal = "trample_window";
	// RG8 encodes a lean of 0 as 128 so rest is exact: byte = 128 + 127 * lean/maxLean.
	private const int ZeroByte = 128;
	private const float ByteScale = 127f;
	private const float RestEpsilon = 0.0005f;

	private int _n;
	private float[] _leanX, _leanZ, _velX, _velZ, _targetX, _targetZ;
	private byte[] _bytes;
	private Image _image;
	private ImageTexture _texture;
	private Sim _sim;
	// World cell at the window's min corner; int.MinValue = no window yet.
	private int _originX = int.MinValue;
	private int _originZ = int.MinValue;
	private bool _awake;
	// Bytes changed outside the spring loop (a scroll cleared a live slot).
	private bool _dirty;
	private bool _pressed;
	// Cached so the per-frame party walk doesn't allocate a closure.
	private System.Action<Player> _pressMember;

	private struct QueuedPress
	{
		public Vector3 position;
		public Vector3 velocity;
		public float radius;
		public float strength;
	}
	// Presses reported since the last tick (mobs, from their own _Process). Node
	// order between a mob and this field is arbitrary, so a press made after this
	// frame's tick lands on the next one.
	private readonly System.Collections.Generic.List<QueuedPress> _queued = new();

	public override void _Ready()
	{
		_n = Mathf.Max(16, resolution);
		int cells = _n * _n;
		_leanX = new float[cells];
		_leanZ = new float[cells];
		_velX = new float[cells];
		_velZ = new float[cells];
		_targetX = new float[cells];
		_targetZ = new float[cells];
		_bytes = new byte[cells * 2];
		System.Array.Fill(_bytes, (byte)ZeroByte);
		_image = Image.CreateFromData(_n, _n, false, Image.Format.Rg8, _bytes);
		_texture = ImageTexture.CreateFromImage(_image);
		_pressMember = PressMember;
		// Declared in project.godot with a placeholder so detail_sprite compiles
		// in the editor; this swaps the live texture in. The window's w (maxLean)
		// stays 0 until the first tick, so nothing bends off a stale window.
		ShaderGlobals.Register(FieldGlobal, RenderingServer.GlobalShaderParameterType.Sampler2D, _texture);
		ShaderGlobals.Register(WindowGlobal, RenderingServer.GlobalShaderParameterType.Vec4, Vector4.Zero);
	}

	public override void _ExitTree()
	{
		ShaderGlobals.ResetToProjectDefault(FieldGlobal);
		RenderingServer.GlobalShaderParameterSet(WindowGlobal, Vector4.Zero);
	}

	public void Initialize(Sim sim)
	{
		_sim = sim;
	}

	// Throws the grass within `radius` of `center` at `speed` (m/s of lean):
	// along `direction` if it has any horizontal length, else radially outward.
	public void Kick(Vector3 center, float radius, Vector3 direction, float speed)
	{
		if (_originX == int.MinValue || radius <= 0f || speed <= 0f)
		{
			return;
		}
		direction.Y = 0f;
		bool radial = direction.LengthSquared() < 1e-6f;
		if (!radial)
		{
			direction = direction.Normalized();
		}
		ForCellsInDisk(center, radius, (slot, dx, dz, dist, falloff) =>
		{
			float ux = direction.X;
			float uz = direction.Z;
			if (radial)
			{
				if (dist < 1e-4f)
				{
					return;
				}
				ux = dx / dist;
				uz = dz / dist;
			}
			_velX[slot] += ux * speed * falloff;
			_velZ[slot] += uz * speed * falloff;
		});
		_awake = true;
	}

	public void KickMelee(Vector3 center, float radius, Vector3 forward) => Kick(center, radius, forward, meleeKick);

	public void KickBurst(Vector3 center, float radius) => Kick(center, radius, Vector3.Zero, burstKick);

	// A walker other than the party presses the grass under it this frame. The
	// caller decides whether the player may see it do so — a hidden mob parting
	// the grass would give itself away. `strength` scales the lean (0..1).
	public void QueuePress(Vector3 position, Vector3 velocity, float radius, float strength)
	{
		if (_originX == int.MinValue || radius <= 0f || strength <= 0f)
		{
			return;
		}
		float minX = _originX * cellSize - radius;
		float minZ = _originZ * cellSize - radius;
		float size = _n * cellSize + 2f * radius;
		if (position.X < minX || position.Z < minZ || position.X > minX + size || position.Z > minZ + size)
		{
			return;
		}
		_queued.Add(new QueuedPress { position = position, velocity = velocity, radius = radius, strength = strength });
	}

	public override void _Process(double delta)
	{
		Player player = _sim?.player;
		if (player == null || !IsInstanceValid(player))
		{
			return;
		}
		float dt = Mathf.Min((float)delta, 1f / 20f);
		ScrollWindow(player.GlobalPosition);

		System.Array.Clear(_targetX);
		System.Array.Clear(_targetZ);
		_pressed = false;
		_sim.ForEachPartyNode(_pressMember);
		for (int i = 0; i < _queued.Count; i++)
		{
			QueuedPress q = _queued[i];
			Press(q.position, q.velocity, q.radius, q.strength);
			_pressed = true;
		}
		_queued.Clear();
		if (!_pressed && !_awake && !_dirty)
		{
			return;
		}

		bool changed = _dirty;
		_dirty = false;
		bool moving = false;
		float k = stiffness;
		float c = damping;
		float inv = ByteScale / maxLean;
		for (int i = 0; i < _leanX.Length; i++)
		{
			float tx = _targetX[i];
			float tz = _targetZ[i];
			float lx = _leanX[i];
			float lz = _leanZ[i];
			float vx = _velX[i];
			float vz = _velZ[i];
			if (tx == 0f && tz == 0f && lx == 0f && lz == 0f && vx == 0f && vz == 0f)
			{
				continue;
			}
			// Semi-implicit Euler; dt is clamped so a hitch can't blow it up.
			vx += ((tx - lx) * k - vx * c) * dt;
			vz += ((tz - lz) * k - vz * c) * dt;
			lx = Mathf.Clamp(lx + vx * dt, -maxLean, maxLean);
			lz = Mathf.Clamp(lz + vz * dt, -maxLean, maxLean);
			if (tx == 0f && tz == 0f
				&& Mathf.Abs(lx) < RestEpsilon && Mathf.Abs(lz) < RestEpsilon
				&& Mathf.Abs(vx) < RestEpsilon && Mathf.Abs(vz) < RestEpsilon)
			{
				lx = lz = vx = vz = 0f;
			}
			else
			{
				moving = true;
			}
			_leanX[i] = lx;
			_leanZ[i] = lz;
			_velX[i] = vx;
			_velZ[i] = vz;

			byte bx = (byte)(ZeroByte + Mathf.RoundToInt(lx * inv));
			byte bz = (byte)(ZeroByte + Mathf.RoundToInt(lz * inv));
			int b = i * 2;
			if (_bytes[b] != bx || _bytes[b + 1] != bz)
			{
				_bytes[b] = bx;
				_bytes[b + 1] = bz;
				changed = true;
			}
		}
		_awake = moving;

		if (changed)
		{
			_image.SetData(_n, _n, false, Image.Format.Rg8, _bytes);
			_texture.Update(_image);
		}
	}

	// Follows the controlled player, clearing whatever the window scrolled onto
	// (those slots last held cells from the far side of the old window).
	private void ScrollWindow(Vector3 center)
	{
		int half = _n / 2;
		int newX = Mathf.FloorToInt(center.X / cellSize) - half;
		int newZ = Mathf.FloorToInt(center.Z / cellSize) - half;
		if (newX == _originX && newZ == _originZ)
		{
			return;
		}
		int dx = newX - _originX;
		int dz = newZ - _originZ;
		if (_originX == int.MinValue || Mathf.Abs(dx) >= _n || Mathf.Abs(dz) >= _n)
		{
			ClearAll();
		}
		else
		{
			// Columns the window gained, then rows.
			int fromX = dx > 0 ? _originX + _n : newX;
			for (int i = 0; i < Mathf.Abs(dx); i++)
			{
				int sx = Mod(fromX + i, _n);
				for (int sz = 0; sz < _n; sz++)
				{
					ClearSlot(sz * _n + sx);
				}
			}
			int fromZ = dz > 0 ? _originZ + _n : newZ;
			for (int i = 0; i < Mathf.Abs(dz); i++)
			{
				int sz = Mod(fromZ + i, _n);
				for (int sx = 0; sx < _n; sx++)
				{
					ClearSlot(sz * _n + sx);
				}
			}
		}
		_originX = newX;
		_originZ = newZ;
		float size = _n * cellSize;
		RenderingServer.GlobalShaderParameterSet(WindowGlobal,
			new Vector4(newX * cellSize, newZ * cellSize, size, maxLean));
	}

	private void PressMember(Player member)
	{
		if (IsInstanceValid(member) && member.Visible && member.IsOnFloor())
		{
			Press(member.GlobalPosition, member.Velocity, footRadius, 1f);
			_pressed = true;
		}
	}

	// Sets the held target under a walker: pushed out from their feet, plus
	// along their travel in proportion to speed.
	private void Press(Vector3 pos, Vector3 velocity, float radius, float strength)
	{
		velocity.Y = 0f;
		float speed = velocity.Length();
		float speed01 = Mathf.Clamp(speed / fullSpeed, 0f, 1f);
		float mx = speed > 1e-3f ? velocity.X / speed * movingLean * speed01 : 0f;
		float mz = speed > 1e-3f ? velocity.Z / speed * movingLean * speed01 : 0f;
		GetDiskBounds(pos, radius, out int minX, out int maxX, out int minZ, out int maxZ);
		for (int cz = minZ; cz <= maxZ; cz++)
		{
			float dz = (cz + 0.5f) * cellSize - pos.Z;
			for (int cx = minX; cx <= maxX; cx++)
			{
				float dx = (cx + 0.5f) * cellSize - pos.X;
				float dist = Mathf.Sqrt(dx * dx + dz * dz);
				if (dist > radius)
				{
					continue;
				}
				float falloff = (1f - Mathf.SmoothStep(0f, radius, dist)) * strength;
				float rx = dist > 1e-4f ? dx / dist * standingLean : 0f;
				float rz = dist > 1e-4f ? dz / dist * standingLean : 0f;
				float tx = (rx + mx) * falloff;
				float tz = (rz + mz) * falloff;
				int slot = Mod(cz, _n) * _n + Mod(cx, _n);
				// Two walkers on one cell: the harder press wins.
				if (tx * tx + tz * tz > _targetX[slot] * _targetX[slot] + _targetZ[slot] * _targetZ[slot])
				{
					_targetX[slot] = tx;
					_targetZ[slot] = tz;
				}
			}
		}
	}

	private delegate void CellVisitor(int slot, float dx, float dz, float dist, float falloff);

	// In-window world-cell bounds of the disk of `radius` around `center`.
	private void GetDiskBounds(Vector3 center, float radius, out int minX, out int maxX, out int minZ, out int maxZ)
	{
		minX = Mathf.Max(_originX, Mathf.FloorToInt((center.X - radius) / cellSize));
		maxX = Mathf.Min(_originX + _n - 1, Mathf.FloorToInt((center.X + radius) / cellSize));
		minZ = Mathf.Max(_originZ, Mathf.FloorToInt((center.Z - radius) / cellSize));
		maxZ = Mathf.Min(_originZ + _n - 1, Mathf.FloorToInt((center.Z + radius) / cellSize));
	}

	// Visits every in-window cell whose center is within `radius` of `center`,
	// with its offset from the center and a smooth 1 → 0 falloff to the rim.
	// Allocates a closure per call, so it is for the rare kicks, not per frame.
	private void ForCellsInDisk(Vector3 center, float radius, CellVisitor visit)
	{
		GetDiskBounds(center, radius, out int minX, out int maxX, out int minZ, out int maxZ);
		for (int cz = minZ; cz <= maxZ; cz++)
		{
			float dz = (cz + 0.5f) * cellSize - center.Z;
			for (int cx = minX; cx <= maxX; cx++)
			{
				float dx = (cx + 0.5f) * cellSize - center.X;
				float dist = Mathf.Sqrt(dx * dx + dz * dz);
				if (dist > radius)
				{
					continue;
				}
				float falloff = 1f - Mathf.SmoothStep(0f, radius, dist);
				visit(Mod(cz, _n) * _n + Mod(cx, _n), dx, dz, dist, falloff);
			}
		}
	}

	private void ClearAll()
	{
		System.Array.Clear(_leanX);
		System.Array.Clear(_leanZ);
		System.Array.Clear(_velX);
		System.Array.Clear(_velZ);
		System.Array.Fill(_bytes, (byte)ZeroByte);
		_image.SetData(_n, _n, false, Image.Format.Rg8, _bytes);
		_texture.Update(_image);
	}

	private void ClearSlot(int slot)
	{
		if (_bytes[slot * 2] != ZeroByte || _bytes[slot * 2 + 1] != ZeroByte)
		{
			_dirty = true;
		}
		_leanX[slot] = 0f;
		_leanZ[slot] = 0f;
		_velX[slot] = 0f;
		_velZ[slot] = 0f;
		_bytes[slot * 2] = ZeroByte;
		_bytes[slot * 2 + 1] = ZeroByte;
	}

	private static int Mod(int a, int n)
	{
		int r = a % n;
		return r < 0 ? r + n : r;
	}
}
