namespace Eden.Protocol.Flight;

internal enum DestinyBallMode : byte
{
    Goto = 0,
    Follow = 1,
    Stop = 2,
    Warp = 3,
    Rigid = 11,
}

[Flags]
internal enum DestinyBallFlags : byte
{
    None = 0,
    Free = 1,
    Global = 2,
    Massive = 4,
    Interactive = 8,
}
