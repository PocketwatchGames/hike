using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// Type-tagged binary serialization for EntitySimState subclasses. Tags are
// stable wire values — append new ones, never reuse old numbers, so old world
// files keep loading after new entity types are added.
public static class EntitySerializer
{
    private enum Tag : byte
    {
        Prop = 1,
        Mob = 2,
        Door = 3,
        Torch = 4,
        Chest = 5,
        Trap = 6,
        Signpost = 7,
        FireTrap = 8,
        BerryTree = 9,
    }

    public static void WriteList(BinaryWriter w, List<EntitySimState> entities)
    {
        if (entities == null)
        {
            w.Write((uint)0);
            return;
        }

        w.Write((uint)entities.Count);
        foreach (EntitySimState e in entities)
        {
            WriteOne(w, e);
        }
    }

    public static List<EntitySimState> ReadList(BinaryReader r)
    {
        uint count = r.ReadUInt32();
        var list = new List<EntitySimState>((int)count);
        for (uint i = 0; i < count; i++)
        {
            list.Add(ReadOne(r));
        }
        return list;
    }

    private static void WriteOne(BinaryWriter w, EntitySimState e)
    {
        switch (e)
        {
            case PropSimState prop:
                w.Write((byte)Tag.Prop);
                WriteVec3(w, prop.WorldPosition);
                WriteScene(w, prop.Scene);
                w.Write((byte)prop.Type);
                w.Write(prop.PickedUp);
                break;

            case MobSimState mob:
                w.Write((byte)Tag.Mob);
                WriteVec3(w, mob.WorldPosition);
                WriteScene(w, mob.Scene);
                WriteResource(w, mob.MobData);
                w.Write(mob.RotationY);
                WriteVec3(w, mob.SpawnPosition);
                w.Write(mob.SpawnRotationY);
                w.Write(mob.Alive);
                w.Write(mob.Burrowed);
                w.Write(mob.Burrowing);
                w.Write(mob.BurrowTimeMs);
                w.Write(mob.MaxHealth);
                w.Write(mob.Health);
                w.Write(mob.PerceptionTargets[0].perception);
                w.Write(mob.PerceptionTargets[0].triggered);
                w.Write(mob.PlayerPerception);
                w.Write(mob.MemoryTimeMs);
                w.Write((byte)mob.DiscoveryState);
                w.Write(mob.InitialBehavior != null ? mob.InitialBehavior.ToString() : "");
                w.Write(mob.SpawnAtNight);
                break;

            case DoorSimState door:
                w.Write((byte)Tag.Door);
                WriteVec3(w, door.WorldPosition);
                WriteScene(w, door.Scene);
                w.Write(door.RotationY);
                w.Write(door.Active);
                break;

            case TorchSimState torch:
                w.Write((byte)Tag.Torch);
                WriteVec3(w, torch.WorldPosition);
                WriteScene(w, torch.Scene);
                w.Write(torch.Active);
                w.Write(torch.AutoLightAtNight);
                break;

            case ChestSimState chest:
                w.Write((byte)Tag.Chest);
                WriteVec3(w, chest.WorldPosition);
                WriteScene(w, chest.Scene);
                w.Write(chest.LootCount);
                WriteScene(w, chest.LootScene);
                w.Write(chest.Active);
                w.Write(chest.SpawnAtNight);
                break;

            case TrapSimState trap:
                w.Write((byte)Tag.Trap);
                WriteVec3(w, trap.WorldPosition);
                WriteScene(w, trap.Scene);
                w.Write(trap.Disarmed);
                break;

            case SignpostSimState signpost:
                w.Write((byte)Tag.Signpost);
                WriteVec3(w, signpost.WorldPosition);
                WriteScene(w, signpost.Scene);
                w.Write(signpost.Text ?? string.Empty);
                break;

            case FireTrapSimState fire:
                w.Write((byte)Tag.FireTrap);
                WriteVec3(w, fire.WorldPosition);
                WriteScene(w, fire.Scene);
                w.Write(fire.PhaseOffsetSeconds);
                break;

            case BerryTreeSimState berry:
                w.Write((byte)Tag.BerryTree);
                WriteVec3(w, berry.WorldPosition);
                WriteScene(w, berry.Scene);
                w.Write(berry.BerryCount);
                w.Write(berry.Picked);
                break;

            default:
                throw new InvalidOperationException($"EntitySerializer has no writer for {e.GetType().Name}");
        }
    }

    private static EntitySimState ReadOne(BinaryReader r)
    {
        var tag = (Tag)r.ReadByte();
        switch (tag)
        {
            case Tag.Prop:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                var type = (PropType)r.ReadByte();
                bool pickedUp = r.ReadBoolean();
                var prop = new PropSimState(type, pos, scene);
                prop.PickedUp = pickedUp;
                return prop;
            }
            case Tag.Mob:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                var mobData = ReadResource<MobData>(r);
                float rotationY = r.ReadSingle();
                Vector3 spawnPos = ReadVec3(r);
                float spawnRotationY = r.ReadSingle();
                bool alive = r.ReadBoolean();
                bool burrowed = r.ReadBoolean();
                bool burrowing = r.ReadBoolean();
                ulong burrowTimeMs = r.ReadUInt64();
                float maxHealth = r.ReadSingle();
                float health = r.ReadSingle();
                float targetPerception = r.ReadSingle();
                bool targetTriggered = r.ReadBoolean();
                float playerPerception = r.ReadSingle();
                ulong memoryTimeMs = r.ReadUInt64();
                var perceptionState = (EPlayerPerceptionState)r.ReadByte();
                string initialBehavior = r.ReadString();
                bool spawnAtNight = r.ReadBoolean();

                var mob = new MobSimState(pos, rotationY, spawnPos, spawnRotationY, scene, mobData);
                if (!string.IsNullOrEmpty(initialBehavior))
                {
                    mob.InitialBehavior = initialBehavior;
                }
                mob.SpawnAtNight = spawnAtNight;
                mob.Alive = alive;
                mob.Burrowed = burrowed;
                mob.Burrowing = burrowing;
                mob.BurrowTimeMs = burrowTimeMs;
                mob.MaxHealth = maxHealth;
                mob.Health = health;
                mob.PerceptionTargets[0].perception = targetPerception;
                mob.PerceptionTargets[0].triggered = targetTriggered;
                mob.PerceptionTargets[0].aggro = targetPerception;
                mob.PlayerPerception = playerPerception;
                mob.MemoryTimeMs = memoryTimeMs;
                mob.DiscoveryState = perceptionState;
                return mob;
            }
            case Tag.Door:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                float rotationY = r.ReadSingle();
                bool active = r.ReadBoolean();
                var door = new DoorSimState(pos, rotationY, scene);
                door.Active = active;
                return door;
            }
            case Tag.Torch:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                bool active = r.ReadBoolean();
                bool autoLightAtNight = r.ReadBoolean();
                var torch = new TorchSimState(pos, scene);
                torch.Active = active;
                torch.AutoLightAtNight = autoLightAtNight;
                return torch;
            }
            case Tag.Chest:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                int lootCount = r.ReadInt32();
                PackedScene lootScene = ReadScene(r);
                bool active = r.ReadBoolean();
                bool spawnAtNight = r.ReadBoolean();
                var chest = new ChestSimState(pos, scene, lootCount, lootScene);
                chest.Active = active;
                chest.SpawnAtNight = spawnAtNight;
                return chest;
            }
            case Tag.Trap:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                bool disarmed = r.ReadBoolean();
                var trap = new TrapSimState(pos, scene);
                trap.Disarmed = disarmed;
                return trap;
            }
            case Tag.Signpost:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                string text = r.ReadString();
                return new SignpostSimState(pos, scene, text);
            }
            case Tag.FireTrap:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                float phaseOffset = r.ReadSingle();
                var fire = new FireTrapSimState(pos, scene);
                fire.PhaseOffsetSeconds = phaseOffset;
                return fire;
            }
            case Tag.BerryTree:
            {
                Vector3 pos = ReadVec3(r);
                PackedScene scene = ReadScene(r);
                int berryCount = r.ReadInt32();
                bool picked = r.ReadBoolean();
                var berry = new BerryTreeSimState(pos, scene, berryCount);
                berry.Picked = picked;
                return berry;
            }
            default:
                throw new InvalidOperationException($"Unknown entity tag {(byte)tag}");
        }
    }

    private static void WriteVec3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 ReadVec3(BinaryReader r)
    {
        float x = r.ReadSingle();
        float y = r.ReadSingle();
        float z = r.ReadSingle();
        return new Vector3(x, y, z);
    }

    private static void WriteScene(BinaryWriter w, PackedScene scene)
    {
        w.Write(scene != null ? scene.ResourcePath : "");
    }

    private static PackedScene ReadScene(BinaryReader r)
    {
        string path = r.ReadString();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        return GD.Load<PackedScene>(path);
    }

    private static void WriteResource(BinaryWriter w, Resource resource)
    {
        w.Write(resource != null ? resource.ResourcePath : "");
    }

    private static T ReadResource<T>(BinaryReader r) where T : Resource
    {
        string path = r.ReadString();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        return GD.Load<T>(path);
    }
}
