using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Targeting;
using Eden.World.Docking;
using Eden.World.Exploration;
using Eden.World.Fittings;
using Eden.World.Inventory;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private void HackExplorationSite(ExplorationProgress exploration)
    {
        var world = _operation.World;
        var catalog = _operation.Catalog;
        var attempt = hacking.Pending(world, pilotId);
        var target = PirateExplorationStore
            .AvailableContainers(world, catalog, pilotId, exploration.SignalId, Pilot.SystemId, Now)
            .Where(body => !exploration.VisitedContainers.Contains(body.Id))
            .OrderByDescending(body => body.Id == exploration.ContainerId)
            .ThenBy(body => Pilot.Position!.DistanceTo(body.Position))
            .ThenBy(body => body.Id)
            .FirstOrDefault();
        if (target is null)
        {
            ReturnFromExploration(exploration);
            return;
        }
        if (target.Id != exploration.ContainerId)
        {
            hacking.Cancel(world, catalog, pilotId, Now);
            attempt = null;
            exploration = exploration with { ContainerId = target.Id, ReadyTicks = 0 };
            _work = Work with { Exploration = exploration };
        }
        double distance = Pilot.Position!.DistanceTo(target.Position) - Pilot.Flight.ShipRadius - target.Radius;
        if (distance > HackingRules.LootRange && attempt is null)
        {
            _navigation.Follow(
                Pilot.ShipId,
                Work.Stamp,
                Pilot.Position,
                Pilot.Flight.ShipRadius,
                target,
                HackingRules.LootRange / 2,
                false,
                true
            );
            var moved = _navigation.Advance(
                Pilot.ShipId,
                Work.Stamp,
                Pilot.Flight,
                Pilot.Position,
                id => id == target.Id ? target : null,
                _obstacles
            );
            _pilot = Pilot with { Position = moved.Position };
            SqliteWorld.SetPosition(world, catalog, Pilot.ShipId, moved.Position, Pilot.SystemId, Now);
            return;
        }
        _navigation.SetAtRest();
        _work = Work with { LastMotionStamp = Work.Stamp };
        if (target.HackingState == 2)
        {
            LootExplorationContainer(exploration, target.Id);
            return;
        }
        if (attempt is not null)
        {
            if (
                attempt.Board.Turn >= CapsuleerExplorationRules.MaximumHackTurns
                || CapsuleerExplorationRules.Hack(attempt.Board) is not { } move
            )
            {
                hacking.Cancel(world, catalog, pilotId, Now);
                FinishExplorationContainer(exploration);
                return;
            }
            // Let the scheduler roll back this action if native settlement fails.
            var turn = hacking.Act(world, catalog, pilotId, move, Now);
            if (turn.Current.Outcome == HackingOutcome.Lost)
            {
                _work = Work with { Exploration = exploration with { ReadyTicks = 0 } };
            }
            return;
        }
        StartExplorationHack(exploration, target, distance);
    }

    private void StartExplorationHack(ExplorationProgress exploration, StaticSpaceObject target, double distance)
    {
        var world = _operation.World;
        var catalog = _operation.Catalog;
        int kind = world.Set<Eden.Data.World.Exploration.SavedExplorationSite>().Find(exploration.SignalId)!.Kind;
        int analyzerType = kind == (int)ExplorationKind.Relic ? CapsuleerIndustry.RelicAnalyzer : CapsuleerIndustry.DataAnalyzer;
        var analyzer = ExplorationModules(world).FirstOrDefault(item => item.TypeId == analyzerType);
        if (analyzer is null)
        {
            ReturnFromExploration(exploration);
            return;
        }
        if (world.Set<ModuleCooldown>().Find(analyzer.Id)?.ReadyAt > Now)
        {
            return;
        }
        if (exploration.ReadyTicks == 0)
        {
            var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
            double signature = PilotFittingStore.SignatureRadius(world, catalog, target.Id, target.TypeId);
            double lockSeconds = TargetingDefinitions.LockSeconds(attributes.GetValueOrDefault(DogmaAttributeId.ScanResolution), signature);
            if (
                distance > attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange)
                || !double.IsFinite(lockSeconds)
                || lockSeconds <= 0
                || attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets) < 1
            )
            {
                FinishExplorationContainer(exploration);
                return;
            }
            _work = Work with
            {
                Exploration = exploration with
                {
                    ReadyTicks = checked(Work.ActiveTicks + (long)Math.Ceiling(lockSeconds * TimeSpan.TicksPerSecond)),
                },
            };
            return;
        }
        if (Work.ActiveTicks >= exploration.ReadyTicks)
        {
            hacking.Begin(world, catalog, pilotId, analyzer.Id, target.Id, new HashSet<long> { target.Id }, Now);
        }
    }

    private void LootExplorationContainer(ExplorationProgress exploration, long targetId)
    {
        var world = _operation.World;
        var catalog = _operation.Catalog;
        var items = world
            .Filter<OwnedItem>(item => item.LocationId == targetId)
            .Where(item => item.OwnerId == pilotId)
            .OrderBy(item => item.Id)
            .ToArray();
        var collected = exploration.LootIds.ToHashSet();
        double free =
            PilotInventoryStore.HoldCapacity(world, catalog, pilotId, Pilot.ShipId, (int)InventoryFlag.Cargo)
            - InventoryPersistence.UsedVolume(world, catalog, pilotId, Pilot.ShipId, (int)InventoryFlag.Cargo);
        foreach (var item in items)
        {
            double volume = PilotInventoryStore.Volume(catalog, item.TypeId);
            int quantity = volume <= 0 ? item.Quantity : (int)Math.Min(item.Quantity, Math.Floor(Math.Max(0, free) / volume));
            if (quantity <= 0)
            {
                continue;
            }
            var changes = hacking.Take(
                world,
                catalog,
                pilotId,
                Pilot.ShipId,
                new([item.Id], targetId, (int)InventoryFlag.Cargo, quantity),
                Now
            );
            collected.UnionWith(changes.Where(change => change.Item.LocationId == Pilot.ShipId).Select(change => change.Item.Id));
            free -= volume * quantity;
        }
        FinishExplorationContainer(exploration with { LootIds = collected.ToArray() });
    }

    private void FinishExplorationContainer(ExplorationProgress exploration) =>
        _work = Work with
        {
            Exploration = exploration with
            {
                VisitedContainers = exploration.VisitedContainers.Append(exploration.ContainerId).ToArray(),
                ContainerId = 0,
                ReadyTicks = 0,
            },
        };

    private void FinishExplorationAtDock(ExplorationProgress exploration)
    {
        var world = _operation.World;
        var cargo = exploration
            .LootIds.Where(id =>
                world.Set<OwnedItem>().Find(id) is { } item
                && item.OwnerId == pilotId
                && item.LocationId == Pilot.ShipId
                && item.FlagId == (int)InventoryFlag.Cargo
            )
            .ToArray();
        if (cargo.Length > 0)
        {
            var catalog = _operation.Catalog;
            var unloaded = PilotInventoryStore.Move(
                world,
                catalog,
                pilotId,
                Pilot.StationId,
                new(cargo, Pilot.ShipId, (int)InventoryFlag.Hangar, null),
                Now
            );
            _operation.MarketChanged |= ammunitionSupply.SellRecoveredExplorationLoot(world, catalog, Pilot, unloaded, Now);
        }
        _work = Work with { Exploration = null, ExplorationRetryAt = checked(Work.ActiveTicks + CapsuleerExplorationRules.RetryTicks) };
    }
}
