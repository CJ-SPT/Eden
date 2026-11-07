using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SmallHullBonusTests
{
    private sealed record Probe(int ModuleType, int? ChargeType = null, int? DroneType = null, bool Character = false);

    [Theory]
    [MemberData(nameof(SmallHullTests.Hulls), MemberType = typeof(SmallHullTests))]
    public void EveryOperationalBonusChangesItsCatalogTargetByItsEarnedSkillAndRoleAmount(int type)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadSmallHull(f.CatalogPath, type);
        VerifyBonuses(f, type);
    }

    internal static void VerifyBonuses(Fixture f, int type)
    {
        PassiveCatalogFixture.LoadPlayerEwar(
            f.CatalogPath,
            Enum.GetValues<ElectronicWarfareSkill>().Select(s => (int)s).Concat(new[] { 1948, 1968, 2108, 37543, 12709 }).ToHashSet()
        );
        PassiveCatalogFixture.LoadRemoteRepairs(f.CatalogPath);
        long ship = SmallHullTests.Board(f, type);
        IReadOnlyList<PassiveModifier> modifiers;
        Dictionary<DogmaAttributeId, double> sources;
        bool stripMiner;
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            stripMiner = catalog.Set<ItemType>().Find(type)!.GroupId == (int)HullClass.MiningBarge;
            modifiers = HullCatalog.Modifiers(catalog, type);
            sources = PilotFittingStore.ReadAttributes(catalog, type).ToDictionary();
            foreach (int skill in HullCatalog.Skills(catalog, type))
            {
                var skillAttributes = PilotFittingStore.ReadAttributes(catalog, skill).ToDictionary();
                skillAttributes[DogmaAttributeId.SkillLevel] = 5;
                var skillModifiers = PassiveCatalog.Modifiers(catalog, skill);
                foreach (
                    var modifier in skillModifiers.Where(m =>
                        m.Domain == ModifierDomain.Item
                        && m.Selector == ModifierSelector.Item
                        && m.SourceAttribute == DogmaAttributeId.SkillLevel
                    )
                )
                {
                    skillAttributes[modifier.Attribute] = Apply(
                        skillAttributes.GetValueOrDefault(modifier.Attribute),
                        modifier.Operation,
                        5
                    );
                }
                foreach (var modifier in skillModifiers.Where(m => m.Domain == ModifierDomain.Ship && m.Selector == ModifierSelector.Item))
                {
                    sources[modifier.Attribute] = Apply(
                        sources.GetValueOrDefault(modifier.Attribute),
                        modifier.Operation,
                        skillAttributes.GetValueOrDefault(modifier.SourceAttribute)
                    );
                }
            }
        }
        if (
            modifiers.Any(modifier =>
                modifier.Selector == ModifierSelector.OwnerRequiredSkill && DroneRules.SupportsOwnerSkill(modifier.SelectionId!.Value)
            )
        )
        {
            PassiveCatalogFixture.LoadCombatDrones(f.CatalogPath);
        }
        foreach (var probe in modifiers.Select(modifier => ProbeFor(modifier, stripMiner)).Distinct())
        {
            InventoryItem? module = null;
            long target =
                probe.DroneType is not null ? FittingCalculation.OwnerTargetId
                : probe.Character ? f.PilotId
                : ship;
            if (probe.ModuleType != 0)
            {
                long fitted = f.Fit(probe.ModuleType, online: false);
                module = Assert.Single(f.Inventory.List(f.PilotId, ship), item => item.Id == fitted);
                target = fitted;
                if (probe.ChargeType is int chargeType)
                {
                    long charge = f.Grant(chargeType, 10);
                    f.Db.Services.GetRequiredService<IPilotAmmunition>()
                        .Load(f.PilotId, ship, [fitted], [charge], f.StationId, f.Clock.Now);
                    using WorldDbContext world = new(f.Db.Path);
                    target = Assert.Single(world.Set<OwnedItem>(), item => item.LocationId == fitted).Id;
                }
            }
            var enhanced = Attributes(f, target, probe.DroneType);
            string definition;
            using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
            {
                var source = catalog.Set<SourceRecord>().Find("client/typedogma", PassiveCatalog.Key(type))!;
                definition = source.Data;
                string empty = "{\"dogmaEffects\":[]}";
                string key = PassiveCatalog.Key(type);
                catalog.Database.ExecuteSql($"UPDATE SourceRecord SET Data={empty} WHERE Dataset='client/typedogma' AND Key={key}");
            }
            var baseline = Attributes(f, target, probe.DroneType);
            using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
            {
                string key = PassiveCatalog.Key(type);
                catalog.Database.ExecuteSql($"UPDATE SourceRecord SET Data={definition} WHERE Dataset='client/typedogma' AND Key={key}");
            }
            foreach (var group in modifiers.Where(m => Matches(f, m, probe)).GroupBy(m => m.Attribute))
            {
                double expected = baseline.GetValueOrDefault(group.Key);
                foreach (var modifier in group.OrderBy(m => (int)m.Operation))
                {
                    double amount = sources.GetValueOrDefault(modifier.SourceAttribute);
                    expected = modifier.Operation switch
                    {
                        DogmaOperation.PreMultiply or DogmaOperation.PostMultiply => expected * amount,
                        DogmaOperation.Add => expected + amount,
                        DogmaOperation.PostPercent => expected * (1 + amount / 100),
                        _ => throw new InvalidDataException("Unexpected operational hull operator."),
                    };
                }
                // Native dogma exposes fitting CPU/grid with two decimals.
                if (
                    group.Key
                    is DogmaAttributeId.Cpu
                        or DogmaAttributeId.Power
                        or DogmaAttributeId.CpuOutput
                        or DogmaAttributeId.PowerOutput
                )
                {
                    expected = Math.Round(expected, 2);
                }
                Assert.True(
                    Math.Abs(expected - enhanced.GetValueOrDefault(group.Key)) <= Math.Max(1, Math.Abs(expected)) * 1e-9,
                    $"Hull {type}, module {probe.ModuleType}, charge {probe.ChargeType}, {group.Key}: "
                        + $"expected {expected}, actual {enhanced.GetValueOrDefault(group.Key)}"
                );
            }
            if (module is not null)
            {
                f.Inventory.Move(f.PilotId, [module.Id], ship, f.StationId, (int)InventoryFlag.Hangar, null);
            }
        }
    }

    private static double Apply(double basis, DogmaOperation operation, double amount) =>
        operation switch
        {
            DogmaOperation.PreMultiply or DogmaOperation.PostMultiply => basis * amount,
            DogmaOperation.Add => basis + amount,
            DogmaOperation.PostPercent => basis * (1 + amount / 100),
            _ => throw new InvalidDataException("Unexpected hull-source skill operation."),
        };

    private static Dictionary<DogmaAttributeId, double> Attributes(Fixture f, long target, int? droneType = null)
    {
        using CatalogDbContext catalog = new(f.CatalogPath);
        using WorldDbContext world = new(f.Db.Path);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
        return FittingCalculation.Read(world, catalog, pilot, ownerTypeId: droneType).Graph.Attributes(target);
    }

    private static bool Matches(Fixture f, PassiveModifier modifier, Probe probe)
    {
        if (modifier.Selector == ModifierSelector.Item)
        {
            return probe.ModuleType == 0 && probe.DroneType is null && (modifier.Domain == ModifierDomain.Character) == probe.Character;
        }
        if (probe.ModuleType == 0 && probe.DroneType is null)
        {
            return false;
        }
        if ((modifier.Selector == ModifierSelector.OwnerRequiredSkill) != (probe.ChargeType.HasValue || probe.DroneType.HasValue))
        {
            return false;
        }
        using CatalogDbContext catalog = new(f.CatalogPath);
        int type = probe.DroneType ?? probe.ChargeType ?? probe.ModuleType;
        if (modifier.Selector == ModifierSelector.Group)
        {
            return catalog.Set<ItemType>().Find(type)!.GroupId == modifier.SelectionId;
        }
        var attributes = PilotFittingStore.ReadAttributes(catalog, type);
        return InventoryDefinitions.RequiredSkills.Any(pair => attributes.GetValueOrDefault(pair.Skill) == modifier.SelectionId);
    }

    private static Probe ProbeFor(PassiveModifier modifier, bool stripMiner) =>
        modifier.Selector switch
        {
            ModifierSelector.Item => new(0, Character: modifier.Domain == ModifierDomain.Character),
            ModifierSelector.OwnerRequiredSkill when DroneRules.SupportsOwnerSkill(modifier.SelectionId!.Value) => new(
                0,
                DroneType: (DroneSkill)modifier.SelectionId.Value switch
                {
                    DroneSkill.MediumDroneOperation => 2185,
                    DroneSkill.HeavyDroneOperation => 2476,
                    DroneSkill.SentryDroneInterfacing => 23561,
                    _ => 2488,
                }
            ),
            ModifierSelector.OwnerRequiredSkill => (MissileSkill?)modifier.SelectionId switch
            {
                MissileSkill.MissileLauncherOperation or MissileSkill.LightMissiles => new(499, 210),
                MissileSkill.HeavyMissiles => new(501, 209),
                MissileSkill.HeavyAssaultMissiles => new(20138, 20307),
                MissileSkill.Torpedoes => new(503, 267),
                MissileSkill.CruiseMissiles => new(13320, 203),
                _ => new(10629, 266),
            },
            ModifierSelector.Group => (HullBonusEquipmentGroup?)modifier.SelectionId switch
            {
                HullBonusEquipmentGroup.ProjectileWeapon => new(484),
                HullBonusEquipmentGroup.WarpScrambler => new(447),
                HullBonusEquipmentGroup.StasisWeb => new(527),
                HullBonusEquipmentGroup.RemoteShieldBooster => new(3596),
                HullBonusEquipmentGroup.RemoteArmorRepairer => new(11357),
                HullBonusEquipmentGroup.Ecm => new(1948),
                HullBonusEquipmentGroup.SensorDampener => new(1968),
                HullBonusEquipmentGroup.TargetPainter => new(12709),
                HullBonusEquipmentGroup.ArmorPlate => new(11293),
                HullBonusEquipmentGroup.ShieldExtender => new(377),
                HullBonusEquipmentGroup.ReinforcedBulkhead => new(1333),
                HullBonusEquipmentGroup.CruiseMissileLauncher => new(13320),
                HullBonusEquipmentGroup.TorpedoLauncher => new(503),
                HullBonusEquipmentGroup.HeavyMissileLauncher => new(501),
                HullBonusEquipmentGroup.RapidLightMissileLauncher => new(1875),
                HullBonusEquipmentGroup.HeavyAssaultMissileLauncher => new(20138),
                HullBonusEquipmentGroup.RapidHeavyMissileLauncher => new(33448),
                _ => throw new InvalidDataException($"No operational group probe for {modifier.SelectionId}."),
            },
            ModifierSelector.RequiredSkill => (HullBonusSkill?)modifier.SelectionId switch
            {
                HullBonusSkill.SmallHybridTurret => new(561),
                HullBonusSkill.SmallProjectileTurret => new(484),
                HullBonusSkill.SmallEnergyTurret => new(450),
                HullBonusSkill.MediumHybridTurret => new(567),
                HullBonusSkill.MediumProjectileTurret => new(490),
                HullBonusSkill.MediumEnergyTurret => new(456),
                HullBonusSkill.LargeHybridTurret => new(572),
                HullBonusSkill.LargeProjectileTurret => new(494),
                HullBonusSkill.LargeEnergyTurret => new(460),
                HullBonusSkill.MissileLauncherOperation => new(499),
                HullBonusSkill.Mining => new(stripMiner ? 17482 : 482),
                HullBonusSkill.RepairSystems => new(523),
                HullBonusSkill.ShieldOperation => new(399),
                HullBonusSkill.PropulsionJamming or HullBonusSkill.Navigation => new(447),
                HullBonusSkill.Afterburner => new(439),
                HullBonusSkill.HighSpeedManeuvering => new(434),
                HullBonusSkill.ShieldEmissionSystems => new(3586),
                HullBonusSkill.RemoteArmorRepairSystems => new(11355),
                HullBonusSkill.CapitalRemoteArmorRepairSystems => new(3536),
                HullBonusSkill.ElectronicWarfare => new(1948),
                HullBonusSkill.TargetPainting => new(12709),
                HullBonusSkill.WeaponDisruption => new(
                    modifier.Attribute
                        is DogmaAttributeId.MissileVelocityBonus
                            or DogmaAttributeId.MissileLifetimeBonus
                            or DogmaAttributeId.ExplosionRadiusBonus
                            or DogmaAttributeId.ExplosionVelocityBonus
                        ? 37543
                        : 2108
                ),
                _ => throw new InvalidDataException($"No operational skill probe for {modifier.SelectionId}."),
            },
            _ => throw new InvalidDataException("No operational hull probe."),
        };
}
