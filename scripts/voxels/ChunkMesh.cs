using System;
using Godot;

public partial class ChunkMesh : Node3D
{
    public bool CollisionReady { get; private set; }

    // Minecraft-style face brightness multipliers for pseudo-3D depth
    private const float FACE_SHADE_TOP = 1.0f;
    private const float FACE_SHADE_BOTTOM = 0.5f;
    private const float FACE_SHADE_NORTH_SOUTH = 0.8f;
    private const float FACE_SHADE_EAST_WEST = 0.6f;
    private const float MIN_AMBIENT_LIGHT = 0.06f;

    private static readonly float[] FaceShading =
    {
        FACE_SHADE_TOP,         // Top
        FACE_SHADE_BOTTOM,      // Bottom
        FACE_SHADE_NORTH_SOUTH, // North
        FACE_SHADE_NORTH_SOUTH, // South
        FACE_SHADE_EAST_WEST,   // West
        FACE_SHADE_EAST_WEST,   // East
    };

    private static readonly ShaderMaterial SharedMaterial;

    static ChunkMesh()
    {
        var shader = GD.Load<Shader>("res://shaders/voxel_clip.gdshader");
        SharedMaterial = new ShaderMaterial();
        SharedMaterial.Shader = shader;
    }

    // Face definitions: each face is 4 vertices (2 triangles) with indices 0-1-2, 0-2-3
    // Vertices are in counter-clockwise order when viewed from outside the cube.
    private static readonly Vector3[] TopFace =
    {
        new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0),
    };

    private static readonly Vector3[] BottomFace =
    {
        new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1),
    };

    private static readonly Vector3[] NorthFace = // -Z
    {
        new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0),
    };

    private static readonly Vector3[] SouthFace = // +Z
    {
        new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1),
    };

    private static readonly Vector3[] WestFace = // -X
    {
        new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0),
    };

    private static readonly Vector3[] EastFace = // +X
    {
        new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1),
    };

    private struct FaceDefinition
    {
        public Vector3[] Vertices;
        public Vector3 Normal;
        public Vector3I NeighborOffset;
    }

    private static readonly FaceDefinition[] Faces =
    {
        new() { Vertices = TopFace, Normal = Vector3.Up, NeighborOffset = new Vector3I(0, 1, 0) },
        new() { Vertices = BottomFace, Normal = Vector3.Down, NeighborOffset = new Vector3I(0, -1, 0) },
        new() { Vertices = NorthFace, Normal = new Vector3(0, 0, -1), NeighborOffset = new Vector3I(0, 0, -1) },
        new() { Vertices = SouthFace, Normal = new Vector3(0, 0, 1), NeighborOffset = new Vector3I(0, 0, 1) },
        new() { Vertices = WestFace, Normal = Vector3.Left, NeighborOffset = new Vector3I(-1, 0, 0) },
        new() { Vertices = EastFace, Normal = Vector3.Right, NeighborOffset = new Vector3I(1, 0, 0) },
    };

    public static ChunkMesh Create(ChunkData data, Func<int, int, int, int> getLightLevel)
    {
        var chunk = new ChunkMesh();
        chunk.Position = new Vector3(
            data.ChunkCoord.X * ChunkData.SIZE,
            data.ChunkCoord.Y * ChunkData.SIZE,
            data.ChunkCoord.Z * ChunkData.SIZE
        );
        chunk.BuildMesh(data, getLightLevel);
        return chunk;
    }

    private void BuildMesh(ChunkData data, Func<int, int, int, int> getLightLevel)
    {
        if (IsAllAir(data))
        {
            CollisionReady = true;
            return;
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetMaterial(SharedMaterial);

        int chunkWorldX = data.ChunkCoord.X * ChunkData.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkData.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkData.SIZE;

        bool hasAnyFace = false;

        for (int x = 0; x < ChunkData.SIZE; x++)
        {
            for (int y = 0; y < ChunkData.SIZE; y++)
            {
                for (int z = 0; z < ChunkData.SIZE; z++)
                {
                    VoxelType type = data.Voxels[x, y, z];
                    if (!VoxelTypeInfo.IsSolid(type))
                    {
                        continue;
                    }

                    Color baseColor = VoxelTypeInfo.Colors[type];
                    Vector3 offset = new(x, y, z);

                    for (int faceIndex = 0; faceIndex < Faces.Length; faceIndex++)
                    {
                        FaceDefinition face = Faces[faceIndex];
                        int nx = x + face.NeighborOffset.X;
                        int ny = y + face.NeighborOffset.Y;
                        int nz = z + face.NeighborOffset.Z;

                        if (VoxelTypeInfo.IsSolid(data.GetVoxel(nx, ny, nz)))
                        {
                            continue;
                        }

                        // Look up light level at the air voxel adjacent to this face
                        int worldNx = chunkWorldX + nx;
                        int worldNy = chunkWorldY + ny;
                        int worldNz = chunkWorldZ + nz;
                        int lightLevel = getLightLevel(worldNx, worldNy, worldNz);

                        // Combine light level with face-direction shading
                        float lightFactor = Math.Max(lightLevel / (float)LightEngine.MAX_LIGHT, MIN_AMBIENT_LIGHT);
                        float shade = FaceShading[faceIndex] * lightFactor;
                        Color color = new Color(baseColor.R * shade, baseColor.G * shade, baseColor.B * shade);

                        st.SetNormal(face.Normal);
                        st.SetColor(color);

                        // Triangle 1: 0-2-1
                        st.AddVertex(face.Vertices[0] + offset);
                        st.SetNormal(face.Normal);
                        st.SetColor(color);
                        st.AddVertex(face.Vertices[2] + offset);
                        st.SetNormal(face.Normal);
                        st.SetColor(color);
                        st.AddVertex(face.Vertices[1] + offset);

                        // Triangle 2: 0-3-2
                        st.SetNormal(face.Normal);
                        st.SetColor(color);
                        st.AddVertex(face.Vertices[0] + offset);
                        st.SetNormal(face.Normal);
                        st.SetColor(color);
                        st.AddVertex(face.Vertices[3] + offset);
                        st.SetNormal(face.Normal);
                        st.SetColor(color);
                        st.AddVertex(face.Vertices[2] + offset);

                        hasAnyFace = true;
                    }
                }
            }
        }

        if (!hasAnyFace)
        {
            CollisionReady = true;
            return;
        }

        ArrayMesh mesh = st.Commit();

        var visual = new MeshInstance3D();
        visual.Mesh = mesh;
        visual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(visual);

        visual.CreateTrimeshCollision();
        CollisionReady = true;
    }

    private static bool IsAllAir(ChunkData data)
    {
        for (int x = 0; x < ChunkData.SIZE; x++)
        {
            for (int y = 0; y < ChunkData.SIZE; y++)
            {
                for (int z = 0; z < ChunkData.SIZE; z++)
                {
                    if (data.Voxels[x, y, z] != VoxelType.Air)
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }
}
