using System.Collections.Frozen;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Configuration;
using Eden.Data.World.Docking;
using Eden.Data.World.Exploration;
using Eden.Data.World.Fittings;
using Eden.Data.World.Fleets;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Repair;
using Eden.Data.World.Skills;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Inventory;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime.Economy;

internal sealed partial class EconomyTransaction
{
    private static readonly FrozenSet<Type> s_fitInputs = new[]
    {
        typeof(OwnedItem),
        typeof(FlightPosition),
        typeof(SavedAbyssalRun),
        typeof(SavedAbyssalParticipant),
        typeof(WorldTackle),
        typeof(Ship),
        typeof(PilotSkill),
        typeof(Pilot),
        typeof(ActiveFittedModule),
        typeof(SavedMutatedItem),
        typeof(SavedModuleCondition),
        typeof(WorldCommandBurst),
        typeof(WorldRemoteAssistance),
        typeof(WorldNpcEwar),
        typeof(WorldPlayerEwar),
        typeof(SavedConfigurationState),
    }.ToFrozenSet();

    private bool DifferentWormholeEnvironment(
        Eden.Data.World.Docking.FlightPosition? previous,
        Eden.Data.World.Docking.FlightPosition? current
    )
    {
        if (previous?.SystemId == current?.SystemId || database is null)
        {
            return false;
        }
        using Eden.World.Catalog.CatalogDbContext catalog = new(database.CatalogPath);
        return Eden.World.Exploration.WormholeEnvironmentStore.At(catalog, previous)?.TypeId
            != Eden.World.Exploration.WormholeEnvironmentStore.At(catalog, current)?.TypeId;
    }

    internal IEnumerable<CombatRowChange> FitChanges(object? before, object? after)
    {
        if ((before ?? after) is not { } changedRow || !s_fitInputs.Contains(changedRow.GetType()))
        {
            return [];
        }
        HashSet<long> pilots = [];
        HashSet<long> drones = [];
        long? environmentRun = null;
        if (before is OwnedItem oldItem || after is OwnedItem)
        {
            var previous = before as OwnedItem;
            var current = after as OwnedItem;
            bool membership =
                previous is null
                || current is null
                || previous.TypeId != current.TypeId
                || previous.OwnerId != current.OwnerId
                || previous.LocationId != current.LocationId
                || previous.FlagId != current.FlagId
                || previous.Singleton != current.Singleton
                || previous.Online != current.Online;
            if (membership)
            {
                Item(previous);
                Item(current);
            }
        }
        else if (before is Eden.Data.World.Docking.FlightPosition || after is Eden.Data.World.Docking.FlightPosition)
        {
            var previous = before as Eden.Data.World.Docking.FlightPosition;
            var current = after as Eden.Data.World.Docking.FlightPosition;
            if (
                Eden.World.Exploration.AbyssalEnvironmentStore.At(this, previous)
                    != Eden.World.Exploration.AbyssalEnvironmentStore.At(this, current)
                || DifferentWormholeEnvironment(previous, current)
            )
            {
                Ship((current ?? previous)!.ShipId);
            }
        }
        else if (before is Eden.Data.World.Exploration.SavedAbyssalRun || after is Eden.Data.World.Exploration.SavedAbyssalRun)
        {
            var previous = before as Eden.Data.World.Exploration.SavedAbyssalRun;
            var current = after as Eden.Data.World.Exploration.SavedAbyssalRun;
            if (
                previous?.Weather != current?.Weather
                || previous?.Seed != current?.Seed
                || previous?.Room != current?.Room
                || previous?.Status != current?.Status
                || previous?.DisabledFields != current?.DisabledFields
            )
            {
                long run = (current ?? previous)!.Id;
                environmentRun = run;
                pilots.UnionWith(Filter<Eden.Data.World.Exploration.SavedAbyssalParticipant>(p => p.RunId == run).Select(p => p.PilotId));
            }
        }
        else if (
            before is Eden.Data.World.Exploration.SavedAbyssalParticipant
            || after is Eden.Data.World.Exploration.SavedAbyssalParticipant
        )
        {
            if (before is Eden.Data.World.Exploration.SavedAbyssalParticipant previous)
            {
                pilots.Add(previous.PilotId);
            }
            if (after is Eden.Data.World.Exploration.SavedAbyssalParticipant current)
            {
                pilots.Add(current.PilotId);
            }
        }
        else if (
            before is WorldTackle oldTackle
            && after is WorldTackle newTackle
            && oldTackle.TargetId == newTackle.TargetId
            && oldTackle.TargetKind == newTackle.TargetKind
            && oldTackle.Module() == newTackle.Module()
        )
        {
            // Extending the same paid tackle changes the deadline. The fitted graph consumes only its target and modifiers.
            return [];
        }
        else if (before is Ship || after is Ship)
        {
            var previous = before as Ship;
            var current = after as Ship;
            if (previous is null || current is null || previous.TypeId != current.TypeId || previous.OwnerId != current.OwnerId)
            {
                if (previous is not null)
                {
                    pilots.Add(previous.OwnerId);
                }
                if (current is not null)
                {
                    pilots.Add(current.OwnerId);
                }
            }
        }
        else
        {
            Row(before);
            Row(after);
        }
        var changes = pilots
            .Where(store.IsPilot)
            .Select(pilot => new CombatRowChange("WorldFitRevision", pilot, Resident: true))
            .Concat(drones.Select(drone => new CombatRowChange("WorldDroneElectronicWarfare", drone, Resident: true)));
        if (environmentRun is long environment)
        {
            changes = changes.Append(new("AbyssalEnvironment", environment, Resident: true));
        }
        return (before ?? after) is Eden.Data.World.Fleets.WorldCommandBurst burst
            ? [.. changes, new("CommandBurstRecipient", burst.ShipId, Resident: true)]
            : changes.ToArray();

        void Item(OwnedItem? item)
        {
            if (item is null)
            {
                return;
            }
            var parent = Set<OwnedItem>().Find(item.LocationId) ?? store.Peek<OwnedItem>(item.LocationId);
            if (Fitted(item.FlagId) || parent is not null && Fitted(parent.FlagId))
            {
                pilots.Add(item.OwnerId);
            }
        }

        static bool Fitted(int flag) =>
            InventoryDefinitions.IsLowSlot(flag)
            || InventoryDefinitions.IsMediumSlot(flag)
            || InventoryDefinitions.IsHighSlot(flag)
            || InventoryDefinitions.IsRigSlot(flag);

        void Ship(long id)
        {
            var ship = Ships.Find(id) ?? store.Peek<Eden.Data.World.Inventory.Ship>(id);
            if (ship is not null)
            {
                pilots.Add(ship.OwnerId);
            }
        }

        void Module(long id)
        {
            var item = Set<OwnedItem>().Find(id) ?? store.Peek<OwnedItem>(id);
            if (item is not null)
            {
                pilots.Add(item.OwnerId);
            }
        }

        void Row(object? row)
        {
            switch (row)
            {
                case PilotSkill skill:
                    pilots.Add(skill.PilotId);
                    break;
                case Pilot pilot:
                    pilots.Add(pilot.Id);
                    break;
                case ActiveFittedModule active:
                    Ship(active.ShipId);
                    break;
                case SavedMutatedItem mutation:
                    Module(mutation.ItemId);
                    break;
                case SavedModuleCondition condition:
                    Module(condition.ModuleId);
                    break;
                case WorldTackle tackle:
                    Ship(tackle.TargetId);
                    break;
                case Eden.Data.World.Fleets.WorldCommandBurst burst:
                    Ship(burst.ShipId);
                    break;
                case WorldRemoteAssistance assistance:
                    Ship(assistance.TargetId);
                    break;
                case WorldNpcEwar effect:
                    Ship(effect.TargetShipId);
                    break;
                case Eden.Data.World.Combat.WorldPlayerEwar effect:
                    if (effect.TargetKind == Eden.Gameplay.Propulsion.TackleTargetKind.Drone)
                    {
                        drones.Add(effect.TargetId);
                    }
                    Ship(effect.TargetId);
                    break;
            }
        }
    }
}
