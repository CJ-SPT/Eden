using System.Security.Cryptography;
using Eden.Infrastructure.Registration;

namespace Eden.World.Combat;

internal interface IShipLootRoll
{
    bool Survives();
}

[Injectable]
internal sealed class ShipLootRoll : IShipLootRoll
{
    // Eden uses the normal 50% baseline described by CCP, independently for
    // each supported module or cargo stack. Seasonal modifiers are not modeled.
    private enum Outcome
    {
        Destroyed,
        Survived,
        Count,
    }

    public bool Survives() => (Outcome)RandomNumberGenerator.GetInt32((int)Outcome.Count) == Outcome.Survived;
}
