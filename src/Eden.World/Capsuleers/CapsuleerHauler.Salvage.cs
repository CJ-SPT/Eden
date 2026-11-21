using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Targeting;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Information;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    // Salvager I in the build 3662168 catalog.
    private const int SalvagerOne = 25861;

    private void PrepareSalvager(EconomyTransaction world, IReadOnlyDictionary<DogmaAttributeId, double> ship)
    {
        var catalog = _operation.Catalog;
        if (Pilot.InStructure || !InformationCatalog.Supported(catalog, SalvagerOne, (int)InformationGroup.Salvager))
        {
            return;
        }
        var highs = world
            .Filter<OwnedItem>(item => item.LocationId == Pilot.ShipId)
            .Where(item => InventoryDefinitions.IsHighSlot(item.FlagId))
            .ToArray();
        if (highs.Length >= ship.GetValueOrDefault(DogmaAttributeId.HighSlots) || highs.Any(item => IsSalvager(catalog, item.TypeId)))
        {
            return;
        }
        var module = catalog.FittingAttributes(SalvagerOne);
        if (
            ship.GetValueOrDefault(DogmaAttributeId.CpuLoad) + module.GetValueOrDefault(DogmaAttributeId.Cpu)
                > ship.GetValueOrDefault(DogmaAttributeId.CpuOutput)
            || ship.GetValueOrDefault(DogmaAttributeId.PowerLoad) + module.GetValueOrDefault(DogmaAttributeId.Power)
                > ship.GetValueOrDefault(DogmaAttributeId.PowerOutput)
        )
        {
            return;
        }
        var owned = world
            .Filter<OwnedItem>(item => item.OwnerId == pilotId)
            .FirstOrDefault(item =>
                item.LocationId == Pilot.StationId && item.FlagId == (int)InventoryFlag.Hangar && item.TypeId == SalvagerOne
            );
        var offer = owned is null ? ammunitionSupply.AffordableSalvager(world, catalog, Pilot, SalvagerOne) : null;
        if (owned is null && offer is null)
        {
            return;
        }
        if (!PilotSkillReader.UsableTypes(world, catalog, pilotId, [SalvagerOne]).Contains(SalvagerOne))
        {
            _operation.MarketChanged |= careerTraining.TrainForModule(world, catalog, Pilot, SalvagerOne, Now);
            return;
        }
        long itemId = owned?.Id ?? ammunitionSupply.BuySalvager(world, catalog, Pilot, offer!);
        _operation.MarketChanged |= owned is null;
        var fitted = PilotFittingStore
            .Fit(world, catalog, pilotId, [itemId], Pilot.StationId, (int)InventoryFlag.AutoFit, Now)
            .Single(change => change.Item.LocationId == Pilot.ShipId);
        PilotFittingStore.SetOnline(world, catalog, pilotId, Pilot.ShipId, fitted.Item.Id, true, Now);
        _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
    }

    private static bool IsSalvager(CatalogDbContext catalog, int type) =>
        catalog.FittingType(type)?.GroupId == (int)InformationGroup.Salvager
        && InformationCatalog.Supported(catalog, type, (int)InformationGroup.Salvager);

    private InformationModule? Salvager(WorldAction world, CatalogDbContext catalog)
    {
        if (!database.Settings.Capsuleers.WreckSalvage || world is not EconomyTransaction)
        {
            return null;
        }
        var item = world
            .Filter<OwnedItem>(item => item.LocationId == Pilot.ShipId)
            .Where(item => item.OwnerId == pilotId && item.Singleton && item.Online && InventoryDefinitions.IsHighSlot(item.FlagId))
            .OrderBy(item => item.FlagId)
            .FirstOrDefault(item => IsSalvager(catalog, item.TypeId));
        return item is null
            ? null
            : InformationRules.Define(
                item.Id,
                item.TypeId,
                DogmaEffect.Salvaging,
                PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, item))
            );
    }

    private static bool CanSalvage(WorldAction world, CatalogDbContext catalog, long wreckId, InformationModule? module) =>
        module is not null
        && world.Set<WorldWreck>().Find(wreckId) is { SalvagedAt: null, SalvageJson: not null } wreck
        && InformationRules.SalvageChance(
            module.AccessBonus,
            catalog.FittingAttributes(wreck.TypeId).GetValueOrDefault(DogmaAttributeId.AccessDifficulty)
        ) > 0;

    private bool RecoverSalvage(WreckContact wreck, InformationModule module)
    {
        _navigation.SetAtRest();
        var activity = Work.Ratting!;
        var world = _operation.World;
        var catalog = _operation.Catalog;
        if (activity.Salvage is not { } progress)
        {
            var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
            double distance = Pilot.Position!.DistanceTo(wreck.Position) - Pilot.Flight.ShipRadius - wreck.Radius;
            double signature = PilotFittingStore.SignatureRadius(world, catalog, wreck.Id, wreck.TypeId);
            double seconds = TargetingDefinitions.LockSeconds(ship.GetValueOrDefault(DogmaAttributeId.ScanResolution), signature);
            if (
                distance > Math.Min(ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange), module.Range)
                || ship.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets) < 1
                || !double.IsFinite(seconds)
                || seconds <= 0
            )
            {
                return false;
            }
            long due = checked(Work.ActiveTicks + (long)Math.Ceiling(seconds * TimeSpan.TicksPerSecond));
            _work = Work with { Ratting = activity with { Salvage = new(wreck.Id, module.ItemId, due) } };
            return true;
        }
        if (Work.ActiveTicks < progress.DueTicks || Now < progress.CycleReadyAt)
        {
            return true;
        }
        try
        {
            if (progress.CycleReadyAt == 0)
            {
                var activation = information.Begin(
                    world,
                    catalog,
                    pilotId,
                    module.ItemId,
                    module.Effect,
                    wreck.Id,
                    new HashSet<long> { wreck.Id },
                    Now
                );
                var cycle = activation.Cycle;
                _work = Work with
                {
                    Ratting = activity with
                    {
                        Salvage = progress with
                        {
                            DueTicks = checked(Work.ActiveTicks + cycle.ReadyAt - cycle.StartedAt),
                            CycleReadyAt = cycle.ReadyAt,
                        },
                    },
                };
                return true;
            }
            var result = information.Complete(world, catalog, pilotId, module.ItemId, progress.CycleReadyAt, Now).Salvage!;
            _work = Work with
            {
                Ratting = activity with
                {
                    LootIds = activity.LootIds.Concat(result.Cargo.Select(change => change.Item.Id)).Distinct().ToArray(),
                    Salvage = result.Success ? null : progress with { CycleReadyAt = 0 },
                },
            };
            return !result.Success;
        }
        catch (Exception error) when (error is InvalidDataException or CargoCapacityException)
        {
            StopRattingSalvage(progress);
            return false;
        }
    }

    private void StopRattingSalvage(RattingSalvage progress)
    {
        if (progress.CycleReadyAt > 0)
        {
            PilotInformationStore.Stop(_operation.World, pilotId, progress.ModuleId, progress.CycleReadyAt);
        }
        _work = Work with { Ratting = Work.Ratting! with { Salvage = null } };
    }
}
