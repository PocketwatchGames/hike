using System;
using Godot;

public partial class ChunkMesh : Node3D
{
    public bool CollisionReady { get; private set; }

    // When non-null, only this chunk coord builds geometry. Set via CVar
    // `debug_only_chunk` for isolating chunks while debugging.
    public static Vector3I? OnlyChunkFilter;

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
        if (OnlyChunkFilter.HasValue && data.ChunkCoord != OnlyChunkFilter.Value)
        {
            CollisionReady = true;
            return;
        }

        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;

        // Terrain (Dual Contouring)
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        st.SetMaterial(SharedMaterial);

        ChunkMesherDC.Build(data, getVoxel, st, chunkWorldX, chunkWorldY, chunkWorldZ, out bool hasAnyFace);

        // Water (axis-aligned cubic faces)
        var stWater = new SurfaceTool();
        stWater.Begin(Mesh.PrimitiveType.Triangles);
        stWater.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        stWater.SetMaterial(WaterMaterial);

        WaterMesher.Build(data, getVoxel, stWater, chunkWorldX, chunkWorldY, chunkWorldZ, out bool hasAnyWaterFace);

        if (!hasAnyFace && !hasAnyWaterFace)
        {
            CollisionReady = true;
            return;
        }

        if (hasAnyFace)
        {
            // DC winding is driven by sign of the edge's corner densities, but
            // the mesher relies on a post-pass for vertex normals — cheap and
            // avoids per-vertex gradient sampling in the mesher itself.
            st.GenerateNormals();
            ArrayMesh mesh = st.Commit();

            if (ChunkMesherDC.DebugLog)
            {
                Aabb aabb = mesh.GetAabb();
                int vertCount = 0;
                if (mesh.GetSurfaceCount() > 0)
                {
                    var arrays = mesh.SurfaceGetArrays(0);
                    var verts = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
                    vertCount = verts?.Length ?? 0;
                }
                GD.Print($"[DC] chunk {data.ChunkCoord} verts={vertCount} aabb={aabb.Position}..{aabb.End} nodePos={Position}");
            }

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

            var waterTrigger = new WaterTrigger();
            var waterShape = waterMesh.CreateTrimeshShape();
            var waterCollision = new CollisionShape3D();
            waterCollision.Shape = waterShape;
            waterTrigger.AddChild(waterCollision);
            AddChild(waterTrigger);
        }

        CollisionReady = true;
    }
}
