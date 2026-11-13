using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
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
    internal IEnumerable<CombatRowChange> FitChanges(object? before, object? after)
    {
        HashSet<long> pilots = [];
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
        var changes = pilots.Where(store.IsPilot).Select(pilot => new CombatRowChange("WorldFitRevision", pilot, Resident: true));
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
                    Ship(effect.TargetId);
                    break;
            }
        }
    }
}
