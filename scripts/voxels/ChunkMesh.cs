using System;
using Godot;

public partial class ChunkMesh : Node3D
{
    public bool CollisionReady { get; private set; }


    private static readonly ShaderMaterial SharedMaterial;
    private static readonly ShaderMaterial BackfaceStencilMaterial;
    private static readonly ShaderMaterial WaterMaterial;
    private static readonly ShaderMaterial WaterBackfaceMaterial;

    static ChunkMesh()
    {
        var shader = GD.Load<Shader>("res://shaders/voxel_clip.gdshader");
        SharedMaterial = new ShaderMaterial();
        SharedMaterial.Shader = shader;
        var tileArray = GD.Load<TextureLayered>("res://assets/textures/voxels/voxel_tiles.png");
        SharedMaterial.SetShaderParameter("tile_array", tileArray);

        var backfaceShader = GD.Load<Shader>("res://shaders/voxel_backface_stencil.gdshader");
        BackfaceStencilMaterial = new ShaderMaterial();
        BackfaceStencilMaterial.Shader = backfaceShader;
        BackfaceStencilMaterial.RenderPriority = 0;

        var waterShader = GD.Load<Shader>("res://shaders/voxel_water.gdshader");
        WaterMaterial = new ShaderMaterial();
        WaterMaterial.Shader = waterShader;

        var waterBackfaceShader = GD.Load<Shader>("res://shaders/voxel_water_backface.gdshader");
        WaterBackfaceMaterial = new ShaderMaterial();
        WaterBackfaceMaterial.Shader = waterBackfaceShader;
        WaterBackfaceMaterial.RenderPriority = -1;
    }

    // Face index constants
    private const int FACE_TOP = 0;
    private const int FACE_BOTTOM = 1;
    private const int FACE_NORTH = 2;
    private const int FACE_SOUTH = 3;
    private const int FACE_WEST = 4;
    private const int FACE_EAST = 5;

    // Face definitions: each face is 4 vertices (2 triangles) with indices 0-1-2, 0-2-3
    // Vertices are in counter-clockwise order when viewed from outside the cube.

    // Full block faces
    private static readonly Vector3[][] FullBlockFaces =
    {
        new Vector3[] { new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0) },       // Top
        new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1) },       // Bottom
        new Vector3[] { new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0) },       // North (-Z)
        new Vector3[] { new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1) },       // South (+Z)
        new Vector3[] { new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0) },       // West (-X)
        new Vector3[] { new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1) },       // East (+X)
    };

    // Bottom slab faces (y = 0 to 0.5)
    private static readonly Vector3[][] BottomSlabFaces =
    {
        new Vector3[] { new(0, 0.5f, 0), new(0, 0.5f, 1), new(1, 0.5f, 1), new(1, 0.5f, 0) }, // Top
        new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1) },              // Bottom
        new Vector3[] { new(0, 0, 0), new(0, 0.5f, 0), new(1, 0.5f, 0), new(1, 0, 0) },        // North
        new Vector3[] { new(0, 0, 1), new(1, 0, 1), new(1, 0.5f, 1), new(0, 0.5f, 1) },        // South
        new Vector3[] { new(0, 0, 0), new(0, 0, 1), new(0, 0.5f, 1), new(0, 0.5f, 0) },        // West
        new Vector3[] { new(1, 0, 0), new(1, 0.5f, 0), new(1, 0.5f, 1), new(1, 0, 1) },        // East
    };

    // Top slab faces (y = 0.5 to 1)
    private static readonly Vector3[][] TopSlabFaces =
    {
        new Vector3[] { new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0) },              // Top
        new Vector3[] { new(0, 0.5f, 0), new(1, 0.5f, 0), new(1, 0.5f, 1), new(0, 0.5f, 1) },  // Bottom
        new Vector3[] { new(0, 0.5f, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0.5f, 0) },        // North
        new Vector3[] { new(0, 0.5f, 1), new(1, 0.5f, 1), new(1, 1, 1), new(0, 1, 1) },        // South
        new Vector3[] { new(0, 0.5f, 0), new(0, 0.5f, 1), new(0, 1, 1), new(0, 1, 0) },        // West
        new Vector3[] { new(1, 0.5f, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0.5f, 1) },        // East
    };

    private static readonly Vector3[] FaceNormals =
    {
        Vector3.Up,
        Vector3.Down,
        new Vector3(0, 0, -1),
        new Vector3(0, 0, 1),
        Vector3.Left,
        Vector3.Right,
    };

    private static readonly Vector3I[] FaceNeighborOffsets =
    {
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, -1),
        new(0, 0, 1),
        new(-1, 0, 0),
        new(1, 0, 0),
    };

    private static Vector3[][] GetFaceSet(VoxelType type)
    {
        if (VoxelTypeInfo.IsBottomSlab(type))
        {
            return BottomSlabFaces;
        }
        if (VoxelTypeInfo.IsTopSlab(type))
        {
            return TopSlabFaces;
        }
        return FullBlockFaces;
    }

    /// <summary>
    /// Determines whether a face of the current voxel is fully occluded by its neighbor.
    /// A face is occluded only if the neighbor completely covers it.
    /// </summary>
    private static bool FaceIsOccluded(VoxelType self, int faceIndex, VoxelType neighbor)
    {
        if (!VoxelTypeInfo.IsSolid(neighbor) || neighbor == VoxelType.Barrier)
        {
            // Transparent voxels don't occlude opaque faces
            // But transparent faces are occluded by same-type transparent neighbors
            if (VoxelTypeInfo.IsTransparent(self) && self == neighbor)
            {
                return true;
            }
            return false;
        }

        // Solid neighbors never occlude transparent voxels — their faces must
        // remain so the transparent geometry is visible against the terrain
        if (VoxelTypeInfo.IsTransparent(self))
        {
            return false;
        }

        bool selfIsSlab = VoxelTypeInfo.IsSlab(self);
        bool neighborIsSlab = VoxelTypeInfo.IsSlab(neighbor);

        // Full block neighbor covers the entire cell
        if (!neighborIsSlab)
        {
            if (!selfIsSlab)
            {
                return true;
            }
            // Slab interior faces are not at the cell boundary, so they can't be occluded
            if (VoxelTypeInfo.IsBottomSlab(self) && faceIndex == FACE_TOP)
            {
                return false;
            }
            if (VoxelTypeInfo.IsTopSlab(self) && faceIndex == FACE_BOTTOM)
            {
                return false;
            }
            return true;
        }

        // Neighbor is a slab — it only covers half the cell
        if (!selfIsSlab)
        {
            // Full block top face: occluded if neighbor above is a bottom slab (covers y=0 boundary)
            if (faceIndex == FACE_TOP)
            {
                return VoxelTypeInfo.IsBottomSlab(neighbor);
            }
            // Full block bottom face: occluded if neighbor below is a top slab (covers y=1 boundary)
            if (faceIndex == FACE_BOTTOM)
            {
                return VoxelTypeInfo.IsTopSlab(neighbor);
            }
            // Full block side: slab never fully covers a full-height side
            return false;
        }

        // Both are slabs
        bool selfIsBottom = VoxelTypeInfo.IsBottomSlab(self);

        if (faceIndex == FACE_TOP)
        {
            // Bottom slab top face is interior — never occluded
            return !selfIsBottom && VoxelTypeInfo.IsBottomSlab(neighbor);
        }
        if (faceIndex == FACE_BOTTOM)
        {
            // Top slab bottom face is interior — never occluded
            return selfIsBottom && VoxelTypeInfo.IsTopSlab(neighbor);
        }
        // Side faces: occluded only if slabs share the same vertical range
        return selfIsBottom == VoxelTypeInfo.IsBottomSlab(neighbor);
    }

    public static ChunkMesh Create(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        LightMap lightMap,
        Texture2D shadowMap)
    {
        var chunk = new ChunkMesh();
        chunk.Position = new Vector3(
            data.ChunkCoord.X * ChunkState.SIZE,
            data.ChunkCoord.Y * ChunkState.SIZE,
            data.ChunkCoord.Z * ChunkState.SIZE
        );
        chunk.BuildMesh(data, getVoxel, lightMap, shadowMap);
        return chunk;
    }

    private void BuildMesh(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        LightMap lightMap,
        Texture2D shadowMap)
    {
        if (IsAllAir(data))
        {
            CollisionReady = true;
            return;
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetMaterial(SharedMaterial);

        var stWater = new SurfaceTool();
        stWater.Begin(Mesh.PrimitiveType.Triangles);
        stWater.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        stWater.SetMaterial(WaterMaterial);

        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;

        bool hasAnyFace = false;
        bool hasAnyWaterFace = false;

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
                {
                    VoxelType type = data.Voxels[x, y, z];
                    if (type == VoxelType.Air || type == VoxelType.Barrier)
                    {
                        continue;
                    }

                    bool isTransparent = VoxelTypeInfo.IsTransparent(type);
                    SurfaceTool target = isTransparent ? stWater : st;

                    Color baseColor = VoxelTypeInfo.Colors[type];
                    Vector3 offset = new(x, y, z);

                    Vector3[][] faceSet = GetFaceSet(type);

                    for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                    {
                        Vector3I neighborOffset = FaceNeighborOffsets[faceIndex];
                        int worldNx = chunkWorldX + x + neighborOffset.X;
                        int worldNy = chunkWorldY + y + neighborOffset.Y;
                        int worldNz = chunkWorldZ + z + neighborOffset.Z;

                        VoxelType neighbor = getVoxel(worldNx, worldNy, worldNz);
                        if (FaceIsOccluded(type, faceIndex, neighbor))
                        {
                            continue;
                        }

                        Vector3[] verts = faceSet[faceIndex];
                        Vector3 normal = FaceNormals[faceIndex];

                        // CUSTOM0 holds the world-space position for light map sampling.
                        // Opaque voxels sample from the adjacent air voxel.
                        // Transparent voxels (water) sample from themselves, since light
                        // propagates through them and the neighbor may be solid (light=0).
                        Vector3 lightSamplePos;
                        if (isTransparent)
                        {
                            lightSamplePos = new Vector3(
                                chunkWorldX + x + 0.5f,
                                chunkWorldY + y + 0.5f,
                                chunkWorldZ + z + 0.5f
                            );
                        }
                        else
                        {
                            lightSamplePos = new Vector3(
                                worldNx + 0.5f,
                                worldNy + 0.5f,
                                worldNz + 0.5f
                            );
                        }
                        int tileId = VoxelTypeInfo.GetTileForFace(type, faceIndex);
                        Color custom0 = new Color(lightSamplePos.X, lightSamplePos.Y, lightSamplePos.Z, tileId);

                        // Triangle 1: 0-2-1
                        target.SetNormal(normal);
                        target.SetColor(baseColor);
                        target.SetCustom(0, custom0);
                        target.AddVertex(verts[0] + offset);
                        target.SetNormal(normal);
                        target.SetColor(baseColor);
                        target.SetCustom(0, custom0);
                        target.AddVertex(verts[2] + offset);
                        target.SetNormal(normal);
                        target.SetColor(baseColor);
                        target.SetCustom(0, custom0);
                        target.AddVertex(verts[1] + offset);

                        // Triangle 2: 0-3-2
                        target.SetNormal(normal);
                        target.SetColor(baseColor);
                        target.SetCustom(0, custom0);
                        target.AddVertex(verts[0] + offset);
                        target.SetNormal(normal);
                        target.SetColor(baseColor);
                        target.SetCustom(0, custom0);
                        target.AddVertex(verts[3] + offset);
                        target.SetNormal(normal);
                        target.SetColor(baseColor);
                        target.SetCustom(0, custom0);
                        target.AddVertex(verts[2] + offset);

                        if (isTransparent)
                        {
                            hasAnyWaterFace = true;
                        }
                        else
                        {
                            hasAnyFace = true;
                        }
                    }
                }
            }
        }

        if (!hasAnyFace && !hasAnyWaterFace)
        {
            CollisionReady = true;
            return;
        }

        if (hasAnyFace)
        {
            ArrayMesh mesh = st.Commit();

            var visual = new MeshInstance3D();
            visual.Mesh = mesh;
            visual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

            var mat = SharedMaterial.Duplicate() as ShaderMaterial;
            mat.SetShaderParameter("light_map", lightMap.Texture);
            mat.SetShaderParameter("light_map_origin", lightMap.Origin);
            mat.SetShaderParameter("light_map_inv_size", Vector3.One / lightMap.Size);
            mat.SetShaderParameter("shadow_map", shadowMap);
            visual.MaterialOverride = mat;
            AddChild(visual);

            var backface = new MeshInstance3D();
            backface.Mesh = mesh;
            backface.MaterialOverride = BackfaceStencilMaterial;
            backface.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(backface);

            visual.CreateTrimeshCollision();
        }

        if (hasAnyWaterFace)
        {
            ArrayMesh waterMesh = stWater.Commit();

            var waterVisual = new MeshInstance3D();
            waterVisual.Mesh = waterMesh;
            waterVisual.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

            var waterMat = WaterMaterial.Duplicate() as ShaderMaterial;
            waterMat.SetShaderParameter("light_map", lightMap.Texture);
            waterMat.SetShaderParameter("light_map_origin", lightMap.Origin);
            waterMat.SetShaderParameter("light_map_inv_size", Vector3.One / lightMap.Size);
            waterVisual.MaterialOverride = waterMat;
            AddChild(waterVisual);

            var waterBackface = new MeshInstance3D();
            waterBackface.Mesh = waterMesh;
            waterBackface.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            waterBackface.MaterialOverride = WaterBackfaceMaterial;
            AddChild(waterBackface);

            // Non-blocking trigger volume for water enter/exit events
            var waterTrigger = new WaterTrigger();
            var waterShape = waterMesh.CreateTrimeshShape();
            var waterCollision = new CollisionShape3D();
            waterCollision.Shape = waterShape;
            waterTrigger.AddChild(waterCollision);
            AddChild(waterTrigger);
        }

        CollisionReady = true;
    }

    private static bool IsAllAir(ChunkState data)
    {
        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int y = 0; y < ChunkState.SIZE; y++)
            {
                for (int z = 0; z < ChunkState.SIZE; z++)
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
