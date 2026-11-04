using System;
using System.IO;
using System.Text;

namespace CupheadCoop
{
    public enum CoopRole : byte
    {
        None = 0,
        Host = 1,
        Client = 2
    }

    public enum PacketType : byte
    {
        Handshake = 1,
        Ping = 2,
        Pong = 3,
        PlayerState = 4,
        WeaponAction = 5,
        LevelEvent = 6,
        SceneSync = 7,
        AchievementSync = 8,
        SaveProgressSync = 9,
        LoadoutSync = 10,
        InventorySync = 11,
        HandshakeReject = 12,
        SceneRequest = 13,
        WorldSaveMirrorChunk = 14,
        UdpAck = 250
    }

    public enum HandshakeStage : byte
    {
        Hello = 0,
        Welcome = 1,
        Ready = 2
    }

    public enum SceneRequestKind : byte
    {
        LoadScene = 1,
        LoadLevel = 2,
        ReloadLevel = 3,
        LoadLastMap = 4
    }

    public enum WeaponActionType : byte
    {
        BasicStart = 1,
        BasicEnd = 2,
        ExStart = 3,
        SuperStart = 4,
        SwitchWeapon = 5
    }

    public enum LevelEventType : byte
    {
        PlayerDeath = 1,
        PlayerRevive = 2,
        BossDamage = 3,
        BossPhaseChange = 4,
        LevelWin = 5,
        LevelLose = 6,
        PlayerJoined = 7,
        PlayerStealHealthRevive = 8,
        LevelReload = 9,
        KingDiceRoll = 10,
        KingDiceBoardSync = 11,
        CoinCollected = 12,
        DjimmiActivated = 13,
        LevelReady = 14,
        PauseSync = 15
    }

    public enum AchievementSyncKind : byte
    {
        UnlockAchievement = 1,
        SetStatInt = 2,
        SetStatFloat = 3,
        IncrementStatInt = 4,
        SyncStats = 5
    }

    [Flags]
    public enum PlayerStateFlags : ushort
    {
        None = 0,
        Grounded = 1 << 0,
        Ducking = 1 << 1,
        Dashing = 1 << 2,
        Parrying = 1 << 3,
        Locked = 1 << 4,
        Shooting = 1 << 5,
        UsingSuperOrEx = 1 << 6,
        IsDead = 1 << 7,
        IsChalice = 1 << 8,
        PlaneShrunk = 1 << 9,
        MapScene = 1 << 10,
        PlaneScene = 1 << 11
    }

    public struct PlayerStatePacket
    {
        public uint Tick;
        public byte PlayerIdByte;
        public float PosX;
        public float PosY;
        public float VelX;
        public float VelY;
        public sbyte LookX;
        public sbyte LookY;
        public sbyte MoveX;
        public sbyte MoveY;
        public PlayerStateFlags Flags;
        public byte Health;
        public byte HealthMax;
        public float SuperMeter;
        public int CurrentWeaponId;

        public byte[] Serialize()
        {
            using (MemoryStream ms = new MemoryStream(48))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)PacketType.PlayerState);
                bw.Write(Tick);
                bw.Write(PlayerIdByte);
                bw.Write(PosX);
                bw.Write(PosY);
                bw.Write(VelX);
                bw.Write(VelY);
                bw.Write(LookX);
                bw.Write(LookY);
                bw.Write(MoveX);
                bw.Write(MoveY);
                bw.Write((ushort)Flags);
                bw.Write(Health);
                bw.Write(HealthMax);
                bw.Write(SuperMeter);
                bw.Write(CurrentWeaponId);
                return ms.ToArray();
            }
        }

        public static PlayerStatePacket Deserialize(BinaryReader br)
        {
            PlayerStatePacket pkt = new PlayerStatePacket();
            pkt.Tick = br.ReadUInt32();
            pkt.PlayerIdByte = br.ReadByte();
            pkt.PosX = br.ReadSingle();
            pkt.PosY = br.ReadSingle();
            pkt.VelX = br.ReadSingle();
            pkt.VelY = br.ReadSingle();
            pkt.LookX = br.ReadSByte();
            pkt.LookY = br.ReadSByte();
            pkt.MoveX = br.ReadSByte();
            pkt.MoveY = br.ReadSByte();
            pkt.Flags = (PlayerStateFlags)br.ReadUInt16();
            pkt.Health = br.ReadByte();
            pkt.HealthMax = br.ReadByte();
            pkt.SuperMeter = br.ReadSingle();
            pkt.CurrentWeaponId = br.ReadInt32();
            return pkt;
        }
    }

    public static class CoopProtocol
    {
        public const uint ProtocolVersion = 2;
        public const string ModVersion = "1.1.1";

        public static byte[] BuildHandshake(
            uint epoch,
            int rngSeed,
            int primaryWeapon,
            int secondaryWeapon,
            int superId,
            int charmId,
            bool hasDlc = true,
            HandshakeStage stage = HandshakeStage.Hello,
            string modVersion = ModVersion)
        {
            using (MemoryStream ms = new MemoryStream(48))
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write((byte)PacketType.Handshake);
                bw.Write(ProtocolVersion);
                bw.Write(epoch);
                bw.Write(rngSeed);
                bw.Write(primaryWeapon);
                bw.Write(secondaryWeapon);
                bw.Write(superId);
                bw.Write(charmId);
                bw.Write(hasDlc);
                bw.Write((byte)stage);
                bw.Write(modVersion ?? ModVersion);
                return ms.ToArray();
            }
        }

        public static byte[] BuildHandshakeReject(uint localProto, string localModVersion, string reason)
        {
            using (MemoryStream ms = new MemoryStream(96))
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write((byte)PacketType.HandshakeReject);
                bw.Write(localProto);
                bw.Write(localModVersion ?? ModVersion);
                bw.Write(reason ?? "Incompatible mod version.");
                return ms.ToArray();
            }
        }

        public static byte[] BuildSceneRequest(SceneRequestKind kind, string sceneName, int levelId, int difficultyMode)
        {
            using (MemoryStream ms = new MemoryStream(64))
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write((byte)PacketType.SceneRequest);
                bw.Write((byte)kind);
                bw.Write(sceneName ?? string.Empty);
                bw.Write(levelId);
                bw.Write(difficultyMode);
                return ms.ToArray();
            }
        }

        public static byte[] BuildWorldSaveMirrorChunk(ushort mirrorId, byte slotIndex, ushort chunkIndex, ushort totalChunks, byte[] chunkData, int offset, int count)
        {
            using (MemoryStream ms = new MemoryStream(count + 16))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)PacketType.WorldSaveMirrorChunk);
                bw.Write(mirrorId);
                bw.Write(slotIndex);
                bw.Write(chunkIndex);
                bw.Write(totalChunks);
                bw.Write((ushort)count);
                bw.Write(chunkData, offset, count);
                return ms.ToArray();
            }
        }

        public static byte[] BuildPingPong(PacketType type, long timestampTicks)
        {
            using (MemoryStream ms = new MemoryStream(12))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)type);
                bw.Write(timestampTicks);
                return ms.ToArray();
            }
        }

        public static byte[] BuildWeaponAction(byte playerId, WeaponActionType action, int weaponId, float aimX, float aimY)
        {
            using (MemoryStream ms = new MemoryStream(20))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)PacketType.WeaponAction);
                bw.Write(playerId);
                bw.Write((byte)action);
                bw.Write(weaponId);
                bw.Write(aimX);
                bw.Write(aimY);
                return ms.ToArray();
            }
        }

        public static byte[] BuildLevelEvent(LevelEventType evtType, byte playerId, float valueA, float valueB, int intVal)
        {
            using (MemoryStream ms = new MemoryStream(20))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)PacketType.LevelEvent);
                bw.Write((byte)evtType);
                bw.Write(playerId);
                bw.Write(valueA);
                bw.Write(valueB);
                bw.Write(intVal);
                return ms.ToArray();
            }
        }

        public static byte[] BuildSceneSync(string sceneName, int levelId, int difficultyMode, int rngSeed, int loadSequence = 0)
        {
            using (MemoryStream ms = new MemoryStream(68))
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write((byte)PacketType.SceneSync);
                bw.Write(sceneName ?? string.Empty);
                bw.Write(levelId);
                bw.Write(difficultyMode);
                bw.Write(rngSeed);
                bw.Write(loadSequence);
                return ms.ToArray();
            }
        }

        public static byte[] BuildLoadoutSync(byte playerId, int primaryWeapon, int secondaryWeapon, int superId, int charmId)
        {
            using (MemoryStream ms = new MemoryStream(24))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)PacketType.LoadoutSync);
                bw.Write(playerId);
                bw.Write(primaryWeapon);
                bw.Write(secondaryWeapon);
                bw.Write(superId);
                bw.Write(charmId);
                return ms.ToArray();
            }
        }

        public static byte[] BuildInventorySync(int[] weapons, int[] supers, int[] charms, int coinsP1, int coinsP2)
        {
            using (MemoryStream ms = new MemoryStream(128))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)PacketType.InventorySync);
                bw.Write(coinsP1);
                bw.Write(coinsP2);

                int wLen = weapons != null ? weapons.Length : 0;
                bw.Write((ushort)wLen);
                for (int i = 0; i < wLen; i++) bw.Write(weapons[i]);

                int sLen = supers != null ? supers.Length : 0;
                bw.Write((ushort)sLen);
                for (int i = 0; i < sLen; i++) bw.Write(supers[i]);

                int cLen = charms != null ? charms.Length : 0;
                bw.Write((ushort)cLen);
                for (int i = 0; i < cLen; i++) bw.Write(charms[i]);

                return ms.ToArray();
            }
        }

        public static byte[] BuildAchievementSync(AchievementSyncKind kind, string id, int intValue, float floatValue)
        {
            using (MemoryStream ms = new MemoryStream(64))
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write((byte)PacketType.AchievementSync);
                bw.Write((byte)kind);
                bw.Write(id ?? string.Empty);
                bw.Write(intValue);
                bw.Write(floatValue);
                return ms.ToArray();
            }
        }

        public static byte[] BuildSaveProgressSync(
            int levelId,
            bool completed,
            int grade,
            int difficultyBeaten,
            float bestTime,
            bool chaliceP1,
            bool chaliceP2,
            bool curseP1,
            bool curseP2,
            bool hardModeBase,
            bool hardModeDlc,
            int coinsP1,
            int coinsP2)
        {
            using (MemoryStream ms = new MemoryStream(48))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)PacketType.SaveProgressSync);
                bw.Write(levelId);
                bw.Write(completed);
                bw.Write(grade);
                bw.Write(difficultyBeaten);
                bw.Write(bestTime);
                bw.Write(chaliceP1);
                bw.Write(chaliceP2);
                bw.Write(curseP1);
                bw.Write(curseP2);
                bw.Write(hardModeBase);
                bw.Write(hardModeDlc);
                bw.Write(coinsP1);
                bw.Write(coinsP2);
                return ms.ToArray();
            }
        }
    }
}
