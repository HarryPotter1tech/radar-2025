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

    /*
        CMD: 0x0200 最多112 bytes 多机通信数据，固定以 10Hz 频率发送
        byte0-1: hero_position_x 英雄x位置
        byte2-3: hero_position_y 英雄y位置
        byte4-5: engineer_position_x 工程x位置
        byte6-7: engineer_position_y 工程y位置
        byte8-9: soldier3_position_x 步兵3x位置
        byte10-11: soldier3_position_y 步兵3y位置
        byte12-13: soldier4_position_x 步兵4x位置
        byte14-15: soldier4_position_y 步兵4y位置
        byte16-17: drone_position_x 无人机x位置
        byte18-19: drone_position_y 无人机y位置
        byte20-21: sentry_position_x 哨兵x位置
        byte22-23: sentry_position_y 哨兵y位置
        byte24: hero_hp 英雄血量
        byte25: engineer_hp 工程血量
        byte26: soldier3_hp 步兵3血量
        byte27: soldier4_hp 步兵4血量
        byte28: drone_hp 无人机血量
        byte29: sentry_hp 哨兵血量
        byte30: hero_defense_gain 英雄防御增益
        byte31: engineer_defense_gain 工程防御增益
        byte32: soldier3_defense_gain 步兵3防御增益
        byte33: soldier4_defense_gain 步兵4防御增益
        byte34: sentry_defense_gain 哨兵防御增益
    */
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct MultiRobotCommData0200
    {
        public ushort HeroPositionX;
        public ushort HeroPositionY;
        public ushort EngineerPositionX;
        public ushort EngineerPositionY;
        public ushort Soldier3PositionX;
        public ushort Soldier3PositionY;
        public ushort Soldier4PositionX;
        public ushort Soldier4PositionY;
        public ushort DronePositionX;
        public ushort DronePositionY;
        public ushort SentryPositionX;
        public ushort SentryPositionY;
        public byte HeroHp;
        public byte EngineerHp;
        public byte Soldier3Hp;
        public byte Soldier4Hp;
        public byte SentryHp;
        public byte HeroDefenseGain;
        public byte EngineerDefenseGain;
        public byte Soldier3DefenseGain;
        public byte Soldier4DefenseGain;
        public byte SentryDefenseGain;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct RobotInteraction_Radar
    {
        public ushort dataCmdId;
        public ushort senderId;
        public ushort receiverId;
        public RadarDecisionCmd0121 data;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct RobotInteraction_MultiRobot
    {
        public ushort dataCmdId;
        public ushort senderId;
        public ushort receiverId;
        public MultiRobotCommData0200 data;
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
        public ushort MarkProgress;

        public bool IsOpponentHeroDebuffed => (MarkProgress & (1 << 0)) != 0;
        public bool IsOpponentEngineerDebuffed => (MarkProgress & (1 << 1)) != 0;
        public bool IsOpponentInfantry3Debuffed => (MarkProgress & (1 << 2)) != 0;
        public bool IsOpponentInfantry4Debuffed => (MarkProgress & (1 << 3)) != 0;
        public bool IsOpponentAerialMarked => (MarkProgress & (1 << 4)) != 0;
        public bool IsOpponentSentryDebuffed => (MarkProgress & (1 << 5)) != 0;
        public bool IsAllyHeroMarked => (MarkProgress & (1 << 6)) != 0;
        public bool IsAllyEngineerMarked => (MarkProgress & (1 << 7)) != 0;
        public bool IsAllyInfantry3Marked => (MarkProgress & (1 << 8)) != 0;
        public bool IsAllyInfantry4Marked => (MarkProgress & (1 << 9)) != 0;
        public bool IsAllyAerialMarked => (MarkProgress & (1 << 10)) != 0;
        public bool IsAllySentryMarked => (MarkProgress & (1 << 11)) != 0;
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
        public ushort OpponentHeroPositionX;
        public ushort OpponentHeroPositionY;
        public ushort OpponentEngineerPositionX;
        public ushort OpponentEngineerPositionY;
        public ushort OpponentInfantry3PositionX;
        public ushort OpponentInfantry3PositionY;
        public ushort OpponentInfantry4PositionX;
        public ushort OpponentInfantry4PositionY;
        public ushort OpponentAerialPositionX;
        public ushort OpponentAerialPositionY;
        public ushort OpponentSentryPositionX;
        public ushort OpponentSentryPositionY;
        public ushort AllyHeroPositionX;
        public ushort AllyHeroPositionY;
        public ushort AllyEngineerPositionX;
        public ushort AllyEngineerPositionY;
        public ushort AllyInfantry3PositionX;
        public ushort AllyInfantry3PositionY;
        public ushort AllyInfantry4PositionX;
        public ushort AllyInfantry4PositionY;
        public ushort AllyAerialPositionX;
        public ushort AllyAerialPositionY;
        public ushort AllySentryPositionX;
        public ushort AllySentryPositionY;
    }
    

}