using Godot;

[GlobalClass]
public partial class WorldGenData : Resource
{
    [Export] public SimData SimData;

    [Export] public int SizeX = 8;
    [Export] public int SizeY = 3;
    [Export] public int SizeZ = 8;

    [Export] public int TerrainNoiseSeed = 12345;
    [Export] public int CaveNoiseSeed = 67890;
    [Export] public int GrassNoiseSeed = 31415;
    [Export] public int PathNoiseSeed = 24680;
    [Export] public int RiverNoiseSeed = 13579;

    [Export] public float ElevationMultiplier = 20;
    // Terrain is quantized to multiples of PlateauStep so the world reads as
    // tiered plateaus with cliffs between them. Where |path noise| exceeds
    // PathThreshold the height stays smooth (creating ramps/paths between
    // plateau levels). Path columns also use VoxelType.TerrainPath so the
    // shader paints them as dirt instead of grass.
    [Export] public float PlateauStep = 4f;
    [Export] public float PathThreshold = 0.1f;
    [Export] public float PathBlendBand = 0.05f;
    [Export] public float CaveThreshold = 0.1f;
    // Caves are carved as horizontal slabs at the bottom of each plateau step
    // (the lowest CaveLayerHeight voxels of each step boundary), gated by 3D
    // cave noise. This produces tiered cave systems whose floors line up with
    // plateau elevations.
    [Export] public int CaveLayerHeight = 3;
    // Rivers: where |riverNoise| < RiverThreshold and the underlying terrain
    // height is within RiverInfluenceMaxHeight of the water level, the column
    // is carved down to RiverDepth (below water level) so it floods.
    [Export] public float RiverThreshold = 0.05f;
    [Export] public float RiverBlendBand = 0.05f;
    [Export] public float RiverDepth = 3f;
    [Export] public float RiverInfluenceMaxHeight = 6f;
    [Export] public float GrassThreshold = 0.3f;
    [Export] public int DirtDepth = 3;

    [Export] public int TreesPerChunkMin = 0;
    [Export] public int TreesPerChunkMax = 4;

    // Pockets of thick forest. ForestNoise is a low-frequency noise; cells
    // where the noise exceeds ForestThreshold attempt a tree at every grid
    // position with probability ForestDensity, producing dense groves.
    [Export] public int ForestNoiseSeed = 54321;
    [Export] public float ForestNoiseFrequency = 0.05f;
    [Export] public float ForestThreshold = 0.01f;
    [Export] public float ForestDensity = 0.5f;
    [Export] public int BuildingHeight = 4;

    [Export] public int TorchesPerHouseMin = 1;
    [Export] public int TorchesPerHouseMax = 3;

    [Export] public int SpawnBuildingWidth = 20;
    [Export] public int SpawnBuildingDepth = 16;
    [Export] public int SpawnBuildingOriginX = 20;
    [Export] public int SpawnBuildingOriginZ = 20;
    [Export] public int SpawnFlatPadding = 4;

    [Export] public PackedScene TreeScene = GD.Load<PackedScene>("res://scenes/game/tree.tscn");
    [Export] public PackedScene TallGrassScene = GD.Load<PackedScene>("res://scenes/game/tall_grass.tscn");
    [Export] public PackedScene DoorScene = GD.Load<PackedScene>("res://scenes/game/door.tscn");
    [Export] public PackedScene TorchScene = GD.Load<PackedScene>("res://scenes/game/torch.tscn");
    [Export] public PackedScene GoblinScene = GD.Load<PackedScene>("res://scenes/game/goblin.tscn");
    [Export] public MobData GoblinData;
    [Export] public PackedScene KunKunScene = GD.Load<PackedScene>("res://scenes/game/kun_kun.tscn");
    [Export] public MobData KunKunData;

    [Export] public float LootChance = 0.005f;
    [Export] public PackedScene LootScene = GD.Load<PackedScene>("res://scenes/game/loot.tscn");

    [Export] public float GoblinChance = 0.005f;
    [Export] public float KunKunChance = 0.005f;
    [Export] public float ChestChance = 0.002f;
    [Export] public int ChestLootCountMin = 3;
    [Export] public int ChestLootCountMax = 6;
    [Export] public PackedScene ChestScene = GD.Load<PackedScene>("res://scenes/game/chest.tscn");
}
