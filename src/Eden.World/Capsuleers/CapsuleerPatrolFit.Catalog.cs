using Eden.Data.Catalog.Capsuleers;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;

namespace Eden.World.Capsuleers;

internal static partial class CapsuleerPatrolFit
{
    // Build 3662168 racial IDs and ordinary Tech I weapon/ammunition pairs, ordered small to large.
    private enum Empire
    {
        Caldari = 1,
        Minmatar = 2,
        Amarr = 4,
        Gallente = 8,
    }

    private sealed record Weapon(int Module, int Charge, int Rounds);

    private static readonly Weapon[] s_missiles =
    [
        new(LightMissileLauncher, ScourgeLightMissile, LauncherRounds),
        new(HeavyMissileLauncher, ScourgeHeavyMissile, LauncherRounds),
        new(CruiseMissileLauncher, ScourgeCruiseMissile, CruiseMissileRounds),
    ];
    private static readonly Weapon[] s_projectiles =
    [
        new(GatlingAutocannon, EmpSmall, TurretRounds),
        new(Dual180Autocannon, EmpMedium, TurretRounds),
        new(494, 201, TurretRounds), // Dual 425mm AutoCannon I, EMP L
    ];
    private static readonly Weapon[] s_hybrids =
    [
        new(ElectronBlaster, AntimatterSmall, TurretRounds),
        new(HeavyElectronBlaster, AntimatterMedium, TurretRounds),
        new(ElectronBlasterCannon, AntimatterLarge, TurretRounds),
    ];
    private static readonly Weapon[] s_rails =
    [
        new(Railgun125, IronCharge, TurretRounds),
        new(Dual150Railgun, IronMedium, TurretRounds),
        new(572, 231, TurretRounds), // Dual 250mm Railgun I, Iron Charge L
    ];
    private static readonly Weapon[] s_lasers =
    [
        new(450, 246, 1), // Gatling Pulse Laser I, Multifrequency S
        new(456, 254, 1), // Focused Medium Pulse Laser I, Multifrequency M
        new(460, 262, 1), // Dual Heavy Pulse Laser I, Multifrequency L
    ];

    private static readonly CatalogDefinition<bool> s_combatHull = new(
        "capsuleer-combat-hull",
        (catalog, id) =>
        {
            var item = catalog.RequireItem(id);
            var hull = (HullClass)item.GroupId;
            // Assembly reads the client type's actual tech level. Some Tech II hulls carry Tech I dogma attribute 422.
            if (CapsuleerDefenseRules.PatrolWeaponSize(hull) is not null && HullCatalog.CanAssemble(catalog, item, item.CategoryId))
            {
                return true;
            }
            int tech = (int)catalog.FittingAttributes(id).GetValueOrDefault(DogmaAttributeId.TechLevel, 1);
            return CapsuleerDefenseRules.CombatShip(hull, tech, true, id);
        }
    );

    internal static bool CombatHull(CatalogDbContext catalog, int id) => catalog.Definition(s_combatHull, id);

    private static readonly CatalogDefinition<int[]> s_catalogHulls = new(
        "additional-patrol-hulls",
        CatalogHulls,
        Estimate: ids => 32L + ids.Length * sizeof(int)
    );

    private static int[] CatalogHulls(CatalogDbContext catalog, int _) =>
        catalog
            .Definitions.Items.Values.Where(hull =>
                hull.CategoryId == (int)InventoryCategory.Ship
                && CapsuleerDefenseRules.PatrolWeaponSize((HullClass)hull.GroupId) is not null
                && !s_patrols.Any(fit => fit.Hull == hull.Id)
                && HullCatalog.CanAssemble(catalog, hull, hull.CategoryId)
            )
            .Where(hull =>
            {
                var attributes = catalog.FittingAttributes(hull.Id);
                return attributes.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft) > 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.LauncherSlotsLeft) > 0;
            })
            .Select(hull => hull.Id)
            .Order()
            .ToArray();

    private static readonly CatalogDefinition<TrafficProfile?> s_catalogFit = new("catalog-patrol-fit", BuildCatalogFit);

    internal static TrafficProfile? CareerFit(CatalogDbContext catalog, int hull) =>
        hull == Merlin ? Starter(catalog)
        : CapsuleerDefenseRules.PatrolWeaponSize((HullClass)(catalog.FittingType(hull)?.GroupId ?? 0)) is not null
            ? catalog.Definition(s_catalogFit, hull)
        : null;

    private static TrafficProfile? BuildCatalogFit(CatalogDbContext catalog, int hull)
    {
        var type = catalog.RequireItem(hull);
        var hullClass = (HullClass)type.GroupId;
        var size = CapsuleerDefenseRules.PatrolWeaponSize(hullClass)!.Value;
        var empire = (Empire)(catalog.Read(CatalogData.Client.Types, (long)hull)?.RaceID ?? 0);
        var attributes = catalog.FittingAttributes(hull);
        int Slots(DogmaAttributeId attribute) => (int)attributes.GetValueOrDefault(attribute);
        int highs = Slots(DogmaAttributeId.HighSlots);
        int turrets = Math.Min(highs, Slots(DogmaAttributeId.TurretSlotsLeft));
        int launchers = Math.Min(highs, Slots(DogmaAttributeId.LauncherSlotsLeft));
        double cpu = attributes.GetValueOrDefault(DogmaAttributeId.CpuOutput);
        double grid = attributes.GetValueOrDefault(DogmaAttributeId.PowerOutput);
        List<TrafficEquipment> equipment = [];
        bool Mount(int module, int slot)
        {
            if (FittingCatalog.Definition(catalog, module) is null)
            {
                return false;
            }
            var needs = catalog.FittingAttributes(module);
            double useCpu = needs.GetValueOrDefault(DogmaAttributeId.Cpu);
            double useGrid = needs.GetValueOrDefault(DogmaAttributeId.Power);
            if (useCpu > cpu || useGrid > grid)
            {
                return false;
            }
            cpu -= useCpu;
            grid -= useGrid;
            equipment.Add(new(module, slot, 1));
            return true;
        }
        var racialTurrets = empire switch
        {
            Empire.Amarr => s_lasers,
            Empire.Caldari => s_rails,
            Empire.Gallente => s_hybrids,
            _ => s_projectiles,
        };
        bool preferMissiles = launchers > 0 && (launchers > turrets || launchers == turrets && empire == Empire.Caldari);
        var families = preferMissiles
            ? new[] { s_missiles, racialTurrets, s_projectiles }
            : new[] { racialTurrets, s_missiles, s_projectiles };
        Weapon? weapon = null;
        int hardpoints = 0;
        foreach (var family in families.Distinct())
        {
            hardpoints = family == s_missiles ? launchers : turrets;
            if (hardpoints == 0)
            {
                continue;
            }
            for (int tier = (int)size - 1; tier >= 0; tier--)
            {
                var candidate = family[tier];
                var charge = catalog.FittingType(candidate.Charge);
                if (charge?.CategoryId != (int)InventoryCategory.Charge)
                {
                    continue;
                }
                double volume = PilotInventoryStore.Volume(catalog, candidate.Charge);
                if (
                    volume <= 0
                    || volume * candidate.Rounds
                        > (catalog.FittingType(candidate.Module)?.Capacity ?? 0) + InventoryDefinitions.ResourceTolerance
                )
                {
                    continue;
                }
                if (Mount(candidate.Module, (int)InventoryFlag.HighSlot0))
                {
                    weapon = candidate;
                    break;
                }
            }
            if (weapon is not null)
            {
                break;
            }
        }
        if (weapon is null)
        {
            return null;
        }
        equipment.Add(new(weapon.Charge, (int)InventoryFlag.HighSlot0, weapon.Rounds));
        int mid = 0;
        int low = 0;
        void Support(int module, bool armorSlot = false)
        {
            ref int used = ref (armorSlot ? ref low : ref mid);
            int slots = Slots(armorSlot ? DogmaAttributeId.LowSlots : DogmaAttributeId.MediumSlots);
            if (used < slots && Mount(module, (int)(armorSlot ? InventoryFlag.LowSlot0 : InventoryFlag.MediumSlot0) + used))
            {
                used++;
            }
        }
        Support(Scrambler);
        // Attack battlecruisers mount battleship weapons but use cruiser propulsion and repair.
        var frame = hullClass == HullClass.AttackBattlecruiser ? ChargeSize.Medium : size;
        Support(
            frame switch
            {
                ChargeSize.Small => (int)StarterMarketType.Afterburner,
                ChargeSize.Medium => Afterburner10Mn,
                _ => Afterburner100Mn,
            }
        );
        bool armor =
            attributes.GetValueOrDefault(DogmaAttributeId.ArmorHitPoints) > attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity);
        int repair = (frame, armor) switch
        {
            (ChargeSize.Small, true) => (int)StarterMarketType.SmallArmorRepairer,
            (ChargeSize.Small, false) => (int)StarterMarketType.SmallShieldBooster,
            (ChargeSize.Medium, true) => MediumArmorRepairer,
            (ChargeSize.Medium, false) => MediumShieldBooster,
            (_, true) => LargeArmorRepairer,
            _ => LargeShieldBooster,
        };
        Support(repair, armor);
        Support((int)StarterMarketType.DamageControl, armorSlot: true);
        for (int slot = 1; slot < hardpoints; slot++)
        {
            int flag = (int)InventoryFlag.HighSlot0 + slot;
            if (!Mount(weapon.Module, flag))
            {
                break;
            }
            equipment.Add(new(weapon.Charge, flag, weapon.Rounds));
        }
        return new(0, "", 0, DateTimeOffset.UnixEpoch, 0, 0, TrafficSecurityBand.Null, hull, equipment.ToArray());
    }
}
