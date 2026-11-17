namespace Eden.Protocol.Flight;

[Flags]
internal enum DestinyBallFlags : byte
{
    None = 0,
    Free = 1,
    Global = 2,
    Massive = 4,
    Interactive = 8,

    // Carbon Destiny Thunkers.cpp appends local compound geometry after the rigid formation ID.
    HasMiniBalls = 0x40,
    HasMiniCapsules = 0x80,
}
