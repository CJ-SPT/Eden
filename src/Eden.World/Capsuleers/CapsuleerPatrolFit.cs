using Eden.Data.Catalog.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;

namespace Eden.World.Capsuleers;

internal static class CapsuleerPatrolFit
{
    // Build 3662168. Skills, fitting resources and the loaded magazine go through the normal blueprint owner.
    private const int Merlin = 603;
    private const int GatlingRail = 561;
    private const int IronCharge = 215;
    private const int Scrambler = 447;
    private static readonly CatalogDefinition<TrafficProfile?> s_fit = new(
        "patrol-merlin",
        (catalog, _) =>
        {
            if (new[] { Merlin, GatlingRail, IronCharge }.Any(id => catalog.FittingType(id) is null))
            {
                return null;
            }
            List<TrafficEquipment> equipment =
            [
                new(GatlingRail, (int)InventoryFlag.HighSlot0, 1),
                new(IronCharge, (int)InventoryFlag.HighSlot0, 50),
                new(GatlingRail, (int)InventoryFlag.HighSlot1, 1),
                new(IronCharge, (int)InventoryFlag.HighSlot1, 50),
            ];
            if (catalog.FittingType(Scrambler) is not null)
            {
                equipment.Add(new(Scrambler, (int)InventoryFlag.MediumSlot0, 1));
            }
            return new(0, "", 0, DateTimeOffset.UnixEpoch, 0, 0, TrafficSecurityBand.Null, Merlin, equipment.ToArray());
        }
    );

    private const int Sabre = 22456;
    private const int SphereLauncher = 22782;
    private const int WarpDisruptProbe = 22778;
    private const int GatlingAutocannon = 484;
    private const int EmpSmall = 185;
    private static readonly CatalogDefinition<TrafficProfile?> s_interdictor = new(
        "patrol-sabre",
        (catalog, _) =>
        {
            if (
                new[] { Sabre, SphereLauncher, WarpDisruptProbe, GatlingAutocannon, EmpSmall, Scrambler }.Any(id =>
                    catalog.FittingType(id) is null
                )
            )
            {
                return null;
            }
            return new(
                0,
                "",
                0,
                DateTimeOffset.UnixEpoch,
                0,
                0,
                TrafficSecurityBand.Null,
                Sabre,
                [
                    new(GatlingAutocannon, (int)InventoryFlag.HighSlot0, 1),
                    new(EmpSmall, (int)InventoryFlag.HighSlot0, 40),
                    new(SphereLauncher, (int)InventoryFlag.HighSlot1, 1),
                    new(WarpDisruptProbe, (int)InventoryFlag.HighSlot1, 3),
                    new(Scrambler, (int)InventoryFlag.MediumSlot0, 1),
                ]
            );
        }
    );

    internal static TrafficProfile? Select(CatalogDbContext catalog, CapsuleerPopulationPolicy policy, long seed, int system, int slot)
    {
        if ((!policy.RoamingPvp && !policy.GateCamps) || !PatrolRules.Choose(seed ^ system, slot, policy.GeneratedCombatPercent))
        {
            return null;
        }
        double security = catalog.Definition(CapsuleerProvisioningCatalog.Security, system);
        if (security > 0 && !(policy.GateCamps && policy.LowsecGateCamps && TrafficRules.SecurityBand(security) == TrafficSecurityBand.Low))
        {
            return null;
        }
        var interdictor =
            policy.GateCamps && policy.CampBubbles && security <= 0 && PatrolRules.Choose(seed ^ system, ~slot, policy.InterdictorPercent)
                ? catalog.Definition(s_interdictor, 0)
                : null;
        return interdictor ?? catalog.Definition(s_fit, 0);
    }
}
