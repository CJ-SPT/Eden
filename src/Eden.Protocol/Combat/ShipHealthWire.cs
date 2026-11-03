using Eden.Gameplay.Combat;
using Eden.Gameplay.Ships;

namespace Eden.Protocol.Combat;

internal static class ShipHealthWire
{
    // damageStateValue.pyj accepts (shield fraction, tau in milliseconds), then
    // armor and structure fractions. dogma.const.dgmTauConstant is 10,000 ticks/ms.
    public static ShipDamageState DamageState(this ShipHealth health)
    {
        var fractions = health.Remaining.Fractions(health.Defense);
        return new(
            fractions.Shield,
            ChargedResource.TimeConstant(health.Defense.ShieldRechargeMilliseconds),
            fractions.Armor,
            fractions.Structure
        );
    }
}
