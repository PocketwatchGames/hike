using Godot;

[GlobalClass]
public partial class WorldGenData : Resource
{
    [Export] public int SizeX = 8;
    [Export] public int SizeY = 3;
    [Export] public int SizeZ = 8;

    [Export] public int TerrainNoiseSeed = 12345;
    [Export] public int CaveNoiseSeed = 67890;
    [Export] public int GrassNoiseSeed = 31415;

    [Export] public float ElevationMultiplier = 20;
    [Export] public float CaveThreshold = 0.4f;
    [Export] public float GrassThreshold = 0.3f;
    [Export] public int DirtDepth = 3;

    [Export] public int TreesPerChunkMin = 0;
    [Export] public int TreesPerChunkMax = 4;
    [Export] public int BuildingHeight = 4;

    [Export] public int TorchesPerHouseMin = 1;
    [Export] public int TorchesPerHouseMax = 3;

    [Export] public int SpawnBuildingWidth = 20;
    [Export] public int SpawnBuildingDepth = 16;
    [Export] public int SpawnBuildingOriginX = -10;
    [Export] public int SpawnBuildingOriginZ = -5;
    [Export] public int SpawnFlatPadding = 4;

    [Export] public PackedScene TreeScene = GD.Load<PackedScene>("res://scenes/game/tree.tscn");
    [Export] public PackedScene TallGrassScene = GD.Load<PackedScene>("res://scenes/game/tall_grass.tscn");
    [Export] public PackedScene DoorScene = GD.Load<PackedScene>("res://scenes/game/door.tscn");
    [Export] public PackedScene TorchScene = GD.Load<PackedScene>("res://scenes/game/torch.tscn");

    [Export] public float LootChance = 0.005f;
    [Export] public PackedScene LootScene = GD.Load<PackedScene>("res://scenes/game/loot.tscn");
}
