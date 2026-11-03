using Eden.Protocol.Serialization;

namespace Eden.Protocol.Combat;

public sealed record ShipDamageState(double ShieldFraction, double ShieldTimeConstant, double ArmorFraction, double StructureFraction)
    : IBlueWritable
{
    public object ToBlue() => new object?[] { new object?[] { ShieldFraction, ShieldTimeConstant }, ArmorFraction, StructureFraction };
}
