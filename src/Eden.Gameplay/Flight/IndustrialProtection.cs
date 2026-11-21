using Eden.Gameplay.Fleets;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Flight;

public sealed record IndustrialProtectionActivation(
    long PilotId,
    long ShipId,
    long ModuleId,
    long StartedAt,
    long EndsAt,
    InventoryChange Burnout,
    double Damage
);

public static class IndustrialProtectionRules
{
    // Pinned 3662168 type 42522, effect 6719 and dbuff/common/industrialInvulnConst.pyj.
    public const int Group = 588;
    public const int Module = 42522;
    public const int OperationSkill = 44067;
    public const int ShieldResistance = 8;
    public const int VelocityPenalty = 3;

    public static IReadOnlyList<CommandBurstValue> Buffs { get; } =
    [
        new(ShieldResistance, -99.99),
        new(9, -90),
        new(58, -100),
        new(59, 1),
        new(61, 1),
        new(56, 100),
        new(VelocityPenalty, -50),
        new(4, 100),
        new(55, -75),
        new(5, 1),
        new(6, 1),
        new(57, 1),
    ];

    public static bool Eligible(HullClass hull, int type) =>
        type == (int)TechOneHullType.Venture
        || hull
            is HullClass.Hauler
                or HullClass.DeepSpaceTransport
                or HullClass.MiningBarge
                or HullClass.Exhumer
                or HullClass.IndustrialCommand
                or HullClass.BlockadeRunner
                or HullClass.ExpeditionFrigate;
}
