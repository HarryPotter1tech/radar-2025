using radar.data;

namespace radar.serial.package
{
    public static class Constants
    {
        public const int FrameDataMaxLength = 512;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct FrameHeader
    {
        public byte SOF;
        public ushort DataLength;
        public byte Sequence;
        public byte Crc8;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct FrameBody
    {
        public ushort CommandId;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = Constants.FrameDataMaxLength)]
        public byte[] Data;
        public ushort Crc16;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct Frame
    {
        public FrameHeader Header;
        public FrameBody Body;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct GameStatus
    {
        public byte GameTypeAndStage; // Combine game_type (4 bits) and game_stage (4 bits)
        public ushort StageRemainTime;
        public ulong SyncTimestamp;

        public int GameType => GameTypeAndStage & 0x0F;
        public GameStage Stage => (GameStage)((GameTypeAndStage >> 4) & 0x0F);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct GameRobotHp
    {
        public ushort Red1;
        public ushort Red2;
        public ushort Red3;
        public ushort Red4;
        public ushort Red5;
        public ushort Red7;
        public ushort RedOutpost;
        public ushort RedBase;
        public ushort Blue1;
        public ushort Blue2;
        public ushort Blue3;
        public ushort Blue4;
        public ushort Blue5;
        public ushort Blue7;
        public ushort BlueOutpost;
        public ushort BlueBase;
    }


    /*
        CMD: 0x020E 1 byte 雷达自主决策信息同步，固定以1Hz频率发送

        bit 0-1：雷达是否拥有触发双倍易伤的机会，开局为 0，数值为雷达拥有触发双倍易伤的机会，至多为 2
        bit 2：对方是否正在被触发双倍易伤
        - 0：对方未被触发双倍易伤
        - 1：对方正在被触发双倍易伤
        bit 3-7：保留
    */
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct RadarInfo
    {
        public byte RadarInfoData;

        public int DoubleDebuffChances => RadarInfoData & 0x03;
        public bool IsDoubleDebuffAble => ((RadarInfoData >> 2 )& 0x01 )== 0x01;
        public int EncryptionRank => (RadarInfoData>>3 )&0x03;
        public bool IsModifyKeyAble =>((RadarInfoData >>5)&0x01 )== 0x01; 
    }

    /* 
        CMD: 0x0101  4 bytes 场地事件数据，固定以 1Hz 频率发送

        0：未占领/未激活
        1：已占领/已激活

        bit 0：己方补给区占领状态，1 为己占领
        bit 1：保留位
        bit 2：己方补给区占领状态，1 为己占领（仅 RMUL 适用）
        bit 3-4：己方小能量机关激活状态，0 未激活，1 已激活，2 正在激活
        bit 5-6：己方大能量机关激活状态，0 未激活，1 已激活，2 正在激活
        bit 7-8：己方中央高地占领状态，1 为己方占领，2 为对方占领
        bit 9-10：己方梯形高地占领状态，1 为己占领
        bit 11-19：对方飞镖最后一次击中己方前哨站或基地的时间（0-420）
        bit 20-22：对方飞镖最后一次击中的具体目标：
                  0 默认，1 前哨站，2 基地固定目标，3 基地随机固定目标，
                  4 基地随机移动目标，5 基地末端移动目标
        bit 23-24：中心增益点占领状态（仅 RMUL）：
                  0 未占领，1 己方占领，2 对方占领，3 双方占领
        bit 25-26：己方堡垒增益点占领状态：
                  0 未占领，1 己方占领，2 对方占领，3 双方占领
        bit 27-28：己方前哨站增益点占领状态：
                  0 未占领，1 己方占领，2 对方占领
        bit 29：己方基地增益点占领状态，1 为己占领
        bit 30-31：保留位
    */
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct EventData
    {
        public uint EventDataValue;

        public bool IsSupplyAreaOccupied => (EventDataValue & 0x01) == 0x01;
        public bool IsReservedBit1Set => (EventDataValue & 0x02) == 0x02;
        public bool IsSupplyAreaOccupied3 => (EventDataValue & 0x04) == 0x04;
        public int LittleEnergyOrganStatus => (int)((EventDataValue >> 3) & 0x03);
        public int BigEnergyOrganStatus => (int)((EventDataValue >> 5) & 0x03);
        public int CentralHighlandStatus => (int)((EventDataValue >> 7) & 0x03);
        public int TrapezoidalHighlandStatus => (int)((EventDataValue >> 9) & 0x03);
        public int EnemyDartHitTime => (int)((EventDataValue >> 11) & 0x1FF);
        public int EnemyDartHitTarget => (int)((EventDataValue >> 20) & 0x07);
        public int CenterGainPointStatus => (int)((EventDataValue >> 23) & 0x03);
        public int FortressGainPointStatus => (int)((EventDataValue >> 25) & 0x03);
        public int OutpostGainPointStatus => (int)((EventDataValue >> 27) & 0x03);
        public bool IsBaseGainPointOccupied => (EventDataValue & (1u << 29)) != 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct RadarMarkProgress
    {
        public byte MarkHeroProgress;
        public byte MarkEngineerProgress;
        public byte MarkStandard3Progress;
        public byte MarkStandard4Progress;
        public byte MarkStandard5Progress;
        public byte MarkSentryProgress;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct DartInfo
    {
        public byte DartRemainingTime;
        public ushort DartInfoData;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct MapRobotData
    {
        public ushort HeroPositionX;
        public ushort HeroPositionY;
        public ushort EngineerPositionX;
        public ushort EngineerPositionY;
        public ushort Infantry3PositionX;
        public ushort Infantry3PositionY;
        public ushort Infantry4PositionX;
        public ushort Infantry4PositionY;
        public ushort Infantry5PositionX;
        public ushort Infantry5PositionY;
        public ushort SentryPositionX;
        public ushort SentryPositionY;
    }
    /*
        CMD: 0x0121 雷达自主决策指令 data，长度 8 bytes
        byte0: radar_cmd，雷达确认触发双倍易伤计数（开局0，合法增量+1，最大2）
        byte1: password_cmd，密钥相关命令字节
        byte2-7: password_1..password_6，密钥数据
    */
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct RadarDecisionCmd0121
    {
        public byte radar_cmd;
        public byte password_cmd;
        public byte password_1;
        public byte password_2;
        public byte password_3;
        public byte password_4;
        public byte password_5;
        public byte password_6;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct RobotInteraction_Radar
    {
        public ushort dataCmdId;
        public ushort senderId;
        public ushort receiverId;
        public RadarDecisionCmd0121 data;
    }

}