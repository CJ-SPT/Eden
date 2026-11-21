using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Flight;

public sealed record FighterBomb(BombProfile Payload, double DurationMilliseconds, int Charges, double RearmMilliseconds);
