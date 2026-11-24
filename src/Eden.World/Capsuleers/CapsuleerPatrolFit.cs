using Eden.Data.Catalog.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Capsuleers;

internal static partial class CapsuleerPatrolFit
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
    internal const int Cormorant = 16238;
    internal const int Catalyst = 16240;
    internal const int Thrasher = 16242;
    internal const int Corax = 32876;
    private const int ElectronBlaster = 562;
    private const int AntimatterSmall = 222;

    internal const int Caracal = 621;
    internal const int Stabber = 622;
    internal const int Moa = 623;
    internal const int Thorax = 627;
    private const int RapidLightLauncher = 1875;
    private const int RapidLightRounds = 19;
    private const int Dual180Autocannon = 489;
    private const int EmpMedium = 193;
    private const int Dual150Railgun = 567;
    private const int IronMedium = 223;
    private const int HeavyElectronBlaster = 566;
    private const int AntimatterMedium = 230;
    private const int Afterburner10Mn = 12056;
    private const int MediumArmorRepairer = 3528;
    private const int MediumShieldBooster = 10836;
    internal const int Drake = 24698;
    internal const int Hurricane = 24702;
    internal const int Raven = 638;
    internal const int Megathron = 641;
    private const int HeavyMissileLauncher = 501;
    private const int ScourgeHeavyMissile = 209;
    private const int VulcanAutocannon = 490;
    private const int CruiseMissileLauncher = 13320;
    private const int ScourgeCruiseMissile = 203;
    private const int CruiseMissileRounds = 20;
    private const int ElectronBlasterCannon = 571;
    private const int AntimatterLarge = 238;
    private const int Afterburner100Mn = 12066;
    private const int LargeArmorRepairer = 3538;
    private const int LargeShieldBooster = 10838;
    private const int SnapshotDraws = 4;

    private sealed record PatrolLoadout(
        int Hull,
        int Weapon,
        int Ammo,
        int WeaponCount,
        int Rounds,
        int Point,
        int Repair = (int)StarterMarketType.SmallShieldBooster,
        int Afterburner = (int)StarterMarketType.Afterburner
    );

    // Authored draws share the selection with additional catalog hulls. Four draws preserve the snapshot fallback.
    private static readonly PatrolLoadout[] s_patrols =
    [
        new((int)GangFrigate.Crow, LightMissileLauncher, ScourgeLightMissile, 3, LauncherRounds, WarpDisruptor),
        new((int)GangFrigate.Stiletto, GatlingAutocannon, EmpSmall, 2, TurretRounds, WarpDisruptor),
        new((int)GangFrigate.Wolf, GatlingAutocannon, EmpSmall, 4, TurretRounds, Scrambler, (int)StarterMarketType.SmallArmorRepairer),
        new((int)GangFrigate.Harpy, Railgun125, IronCharge, 4, TurretRounds, Scrambler),
        new(Cormorant, Railgun125, IronCharge, 6, TurretRounds, WarpDisruptor),
        new(Catalyst, ElectronBlaster, AntimatterSmall, 8, TurretRounds, Scrambler, (int)StarterMarketType.SmallArmorRepairer),
        new(Thrasher, GatlingAutocannon, EmpSmall, 7, TurretRounds, Scrambler),
        new(Corax, LightMissileLauncher, ScourgeLightMissile, 5, LauncherRounds, WarpDisruptor),
        new(Caracal, RapidLightLauncher, ScourgeLightMissile, 5, RapidLightRounds, WarpDisruptor, MediumShieldBooster, Afterburner10Mn),
        new(Stabber, Dual180Autocannon, EmpMedium, 4, TurretRounds, Scrambler, MediumShieldBooster, Afterburner10Mn),
        new(Moa, Dual150Railgun, IronMedium, 5, TurretRounds, WarpDisruptor, MediumShieldBooster, Afterburner10Mn),
        new(Thorax, HeavyElectronBlaster, AntimatterMedium, 5, TurretRounds, Scrambler, MediumArmorRepairer, Afterburner10Mn),
        new(Drake, HeavyMissileLauncher, ScourgeHeavyMissile, 6, LauncherRounds, WarpDisruptor, LargeShieldBooster, Afterburner10Mn),
        new(Hurricane, VulcanAutocannon, EmpMedium, 6, TurretRounds, Scrambler, MediumArmorRepairer, Afterburner10Mn),
        new(
            Raven,
            CruiseMissileLauncher,
            ScourgeCruiseMissile,
            6,
            CruiseMissileRounds,
            WarpDisruptor,
            LargeShieldBooster,
            Afterburner100Mn
        ),
        new(Megathron, ElectronBlasterCannon, AntimatterLarge, 7, TurretRounds, Scrambler, LargeArmorRepairer, Afterburner100Mn),
    ];
    private static readonly CatalogDefinition<TrafficProfile?> s_patrol = new(
        "armed-patrol",
        (catalog, index) =>
        {
            var fit = s_patrols[index];
            List<TrafficEquipment> equipment =
            [
                new(fit.Point, (int)InventoryFlag.MediumSlot0, 1),
                new(fit.Afterburner, (int)InventoryFlag.MediumSlot0 + 1, 1),
                new((int)StarterMarketType.DamageControl, (int)InventoryFlag.LowSlot0, 1),
            ];
            int repairSlot = fit.Repair is LargeArmorRepairer or MediumArmorRepairer or (int)StarterMarketType.SmallArmorRepairer
                ? (int)InventoryFlag.LowSlot0 + 1
                : (int)InventoryFlag.MediumSlot0 + 2;
            equipment.Add(new(fit.Repair, repairSlot, 1));
            for (int slot = 0; slot < fit.WeaponCount; slot++)
            {
                int flag = (int)InventoryFlag.HighSlot0 + slot;
                equipment.Add(new(fit.Weapon, flag, 1));
                equipment.Add(new(fit.Ammo, flag, fit.Rounds));
            }
            if (catalog.FittingType(fit.Hull) is null || equipment.Any(item => catalog.FittingType(item.TypeId) is null))
            {
                return null;
            }
            return new(0, "", 0, DateTimeOffset.UnixEpoch, 0, 0, TrafficSecurityBand.Null, fit.Hull, equipment.ToArray());
        }
    );

    internal static TrafficProfile? Starter(CatalogDbContext catalog) => catalog.Definition(s_fit, 0);

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
        int draws = s_patrols.Length + SnapshotDraws;
        ulong roll = CapsuleerProvisioningRules.Seed(seed, system, CapsuleerCareer.Hauling, slot);
        int draw = (int)(roll % (uint)draws);
        if (interdictor is null && draw < s_patrols.Length)
        {
            // Independent digits of the saved slot seed keep the full catalog reachable at any pool size.
            ulong variety = roll / (uint)draws;
            if (variety % 2 != 0 && catalog.Definition(s_catalogHulls, 0) is { Length: > 0 } hulls)
            {
                int hull = hulls[(int)(variety / 2 % (uint)hulls.Length)];
                if (catalog.Definition(s_catalogFit, hull) is { } fit)
                {
                    return fit;
                }
            }
            if (catalog.Definition(s_patrol, draw) is { } patrol)
            {
                return patrol;
            }
        }
        return imported?.Invoke() is { } profile && RoamingFit(catalog, profile, policy.RoamingGangs)
            ? profile
            : interdictor ?? Starter(catalog);
    }

    private static bool RoamingFit(CatalogDbContext catalog, TrafficProfile profile, bool allowFleetRepair)
    {
        if (!CombatHull(catalog, profile.HullTypeId))
        {
            return false;
        }
        bool contributes = false;
        foreach (var item in profile.Equipment.Where(item => InventoryDefinitions.IsHighSlot(item.Flag)))
        {
            var effect = FittingCatalog.DefaultEffect(catalog, item.TypeId);
            if (effect is DogmaEffect.MiningLaser or DogmaEffect.MiningClouds)
            {
                return false;
            }
            contributes |=
                (
                    effect is DogmaEffect.LaserFired or DogmaEffect.ProjectileFired or DogmaEffect.UseMissiles
                    || allowFleetRepair && effect is { } active && PvpSupport.RemoteRepair(active)
                )
                && catalog.FittingType(item.TypeId)?.GroupId != InterdictionRules.LauncherGroup;
        }
        return contributes;
    }
}
