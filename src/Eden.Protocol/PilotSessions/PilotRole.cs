namespace Eden.Protocol.PilotSessions;

// Build 3662168 carbon/common/script/sys/serviceConst.pyj. These are account roles, not corporation roles.
[Flags]
public enum PilotRole : long
{
    WorldMod = 1L << 12,
    Programmer = 1L << 51,
    QualityAssurance = 1L << 52,
    GameMasterHigh = 1L << 53,
    GameMasterLow = 1L << 54,
    Player = 1L << 61,
    Developer = Player | WorldMod | Programmer | QualityAssurance | GameMasterHigh | GameMasterLow,
}
