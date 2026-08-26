using System.Collections.Generic;
using Godot;

// Managed vertex accumulation for one chunk surface — what the meshers write
// into instead of a SurfaceTool.
//
// SurfaceTool is a native object, so every SetNormal / SetColor / SetCustom /
// AddVertex is a managed->native marshal, and the DC mesher issued EIGHTEEN of
// them per triangle. That was most of what building a chunk cost. This holds
// plain C# lists and hands the renderer one array per channel at the end.
//
// The second reason matters more: with no Godot object in the loop the meshers
// are pure C#, so a chunk can be built on a worker thread. Only ToArrayMesh
// touches the rendering server, and it is the one call that must stay on the
// main thread.
//
// Triangle soup, never indexed — SurfaceTool was not indexing either (nothing
// called Index()), and the DC mesher's per-corner CUSTOM channels mean adjacent
// triangles rarely share a vertex anyway.
public sealed class MeshBuffer
{
    // 4 floats per vertex per channel, matching ArrayCustomFormat.RgbaFloat.
    private const int CUSTOM_FLOATS = 4;

    private readonly int _customChannels;
    private readonly List<Vector3> _vertices = new();
    private readonly List<Vector3> _normals = new();
    private readonly List<Color> _colors = new();
    private readonly List<float>[] _custom;

    public MeshBuffer(int customChannels)
    {
        _customChannels = customChannels;
        _custom = new List<float>[customChannels];
        for (int i = 0; i < customChannels; i++)
        {
            _custom[i] = new List<float>();
        }
    }

    public int VertexCount => _vertices.Count;
    public bool IsEmpty => _vertices.Count == 0;

    // One custom channel (the water mesher).
    public void Add(Vector3 position, Vector3 normal, Color color, Color custom0)
    {
        _vertices.Add(position);
        _normals.Add(normal);
        _colors.Add(color);
        AddCustom(0, custom0);
    }

    // Four custom channels (the terrain mesher).
    public void Add(Vector3 position, Vector3 normal, Color color, Color custom0, Color custom1, Color custom2, Color custom3)
    {
        _vertices.Add(position);
        _normals.Add(normal);
        _colors.Add(color);
        AddCustom(0, custom0);
        AddCustom(1, custom1);
        AddCustom(2, custom2);
        AddCustom(3, custom3);
    }

    private void AddCustom(int channel, Color value)
    {
        List<float> c = _custom[channel];
        c.Add(value.R);
        c.Add(value.G);
        c.Add(value.B);
        c.Add(value.A);
    }

    // MAIN THREAD ONLY — this is the rendering-server call the whole split
    // exists to isolate. Returns null for an empty buffer.
    public ArrayMesh ToArrayMesh(Material material)
    {
        if (IsEmpty)
        {
            return null;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();

        ulong format = 0;
        for (int i = 0; i < _customChannels; i++)
        {
            arrays[(int)Mesh.ArrayType.Custom0 + i] = _custom[i].ToArray();
            // Each channel's element format is 3 bits at its own shift; without
            // these the surface is built with the channels absent and every
            // shader reading CUSTOMn gets zeros.
            int shift = (int)Mesh.ArrayFormat.FormatCustom0Shift + i * (int)Mesh.ArrayFormat.FormatCustomBits;
            format |= (ulong)Mesh.ArrayCustomFormat.RgbaFloat << shift;
        }

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, (Mesh.ArrayFormat)format);
        if (material != null)
        {
            mesh.SurfaceSetMaterial(0, material);
        }
        return mesh;
    }
}
