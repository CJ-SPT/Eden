using Eden.Data.Catalog.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
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
            AddSupport(catalog, equipment);
            return new(0, "", 0, DateTimeOffset.UnixEpoch, 0, 0, TrafficSecurityBand.Null, Merlin, equipment.ToArray());
        }
    );

    private static void AddSupport(CatalogDbContext catalog, List<TrafficEquipment> equipment)
    {
        // These optional starter modules are present in the full pinned catalog. Reduced catalogs
        // retain the existing valid core fit; normal blueprint validation still owns skills and resources.
        foreach (
            var module in new[]
            {
                new TrafficEquipment((int)StarterMarketType.Afterburner, (int)InventoryFlag.MediumSlot0 + 1, 1),
                new TrafficEquipment((int)StarterMarketType.SmallShieldBooster, (int)InventoryFlag.MediumSlot0 + 2, 1),
                new TrafficEquipment((int)StarterMarketType.DamageControl, (int)InventoryFlag.LowSlot0, 1),
            }
        )
        {
            if (catalog.FittingType(module.TypeId) is not null)
            {
                equipment.Add(module);
            }
        }
    }

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

    private const int LightMissileLauncher = 499;
    private const int ScourgeLightMissile = 210;
    private const int Railgun125 = 10678;
    private const int WarpDisruptor = 3242;
    private const int TurretRounds = 40;
    private const int LauncherRounds = 30;
    private static readonly GangFrigate[] s_frigates = Enum.GetValues<GangFrigate>();
    private static readonly CatalogDefinition<TrafficProfile?> s_frigate = new(
        "gang-frigate",
        (catalog, hull) =>
        {
            var frigate = (GangFrigate)hull;
            var (weapon, ammo, count, rounds) = frigate switch
            {
                GangFrigate.Stiletto => (GatlingAutocannon, EmpSmall, 2, TurretRounds),
                GangFrigate.Crow => (LightMissileLauncher, ScourgeLightMissile, 3, LauncherRounds),
                GangFrigate.Wolf => (GatlingAutocannon, EmpSmall, 4, TurretRounds),
                GangFrigate.Harpy => (Railgun125, IronCharge, 4, TurretRounds),
                _ => throw new ArgumentOutOfRangeException(nameof(hull)),
            };
            int point = frigate is GangFrigate.Stiletto or GangFrigate.Crow ? WarpDisruptor : Scrambler;
            List<TrafficEquipment> equipment =
            [
                new(point, (int)InventoryFlag.MediumSlot0, 1),
                new((int)StarterMarketType.Afterburner, (int)InventoryFlag.MediumSlot0 + 1, 1),
                new((int)StarterMarketType.DamageControl, (int)InventoryFlag.LowSlot0, 1),
            ];
            equipment.Add(
                frigate == GangFrigate.Wolf
                    ? new((int)StarterMarketType.SmallArmorRepairer, (int)InventoryFlag.LowSlot0 + 1, 1)
                    : new((int)StarterMarketType.SmallShieldBooster, (int)InventoryFlag.MediumSlot0 + 2, 1)
            );
            for (int slot = 0; slot < count; slot++)
            {
                int flag = (int)InventoryFlag.HighSlot0 + slot;
                equipment.Add(new(weapon, flag, 1));
                equipment.Add(new(ammo, flag, rounds));
            }
            if (catalog.FittingType(hull) is null || equipment.Any(item => catalog.FittingType(item.TypeId) is null))
            {
                return null;
            }
            return new(0, "", 0, DateTimeOffset.UnixEpoch, 0, 0, TrafficSecurityBand.Null, hull, equipment.ToArray());
        }
    );

    internal static TrafficProfile? Select(
        CatalogDbContext catalog,
        CapsuleerPopulationPolicy policy,
        long seed,
        int system,
        int slot,
        Func<TrafficProfile?>? imported = null
    )
    {
        if ((!policy.RoamingPvp && !policy.GateCamps) || !PatrolRules.Choose(seed ^ system, slot, policy.GeneratedCombatPercent))
        {
            return imported?.Invoke();
        }
        double security = catalog.Definition(CapsuleerProvisioningCatalog.Security, system);
        if (security > 0 && !(policy.GateCamps && policy.LowsecGateCamps && TrafficRules.SecurityBand(security) == TrafficSecurityBand.Low))
        {
            return imported?.Invoke();
        }
        var interdictor =
            policy.GateCamps && policy.CampBubbles && security <= 0 && PatrolRules.Choose(seed ^ system, ~slot, policy.InterdictorPercent)
                ? catalog.Definition(s_interdictor, 0)
                : null;
        // Half the non-bubble combat draws get a purpose-fitted frigate even when imported identities are available.
        // The other half use the existing imported pilot or generated Merlin fallback.
        int draw = TrafficRules.Start(seed, system, slot, s_frigates.Length * 2);
        if (interdictor is null && draw < s_frigates.Length && catalog.Definition(s_frigate, (int)s_frigates[draw]) is { } frigate)
        {
            return frigate;
        }
        return imported?.Invoke() ?? interdictor ?? catalog.Definition(s_fit, 0);
    }
}
