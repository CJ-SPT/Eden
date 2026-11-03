namespace Eden.Protocol.Flight;

[Flags]
internal enum DestinyBallFlags : byte
{
    None = 0,
    Free = 1,
    Global = 2,
    Massive = 4,
    Interactive = 8,
}
