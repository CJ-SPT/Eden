using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal static class MissionGatePayment
{
    internal static MissionGateActivation Apply(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        long missionId,
        MissionGate gate,
        long now
    )
    {
        var key = gate.Access.Key;
        var unlock = key is { Consume: true }
            ? world
                .Filter<SavedMissionGateUnlock>(g =>
                    g.MissionId == missionId && g.GateObjectId == gate.DungeonObjectId && g.PilotId == pilotId
                )
                .SingleOrDefault()
            : null;
        var result = Pay(world, catalog, pilotId, shipId, key, unlock?.ResetAt > now);
        if (result.Items.Count == 0)
        {
            return result;
        }
        unlock ??= world
            .Add(
                new SavedMissionGateUnlock
                {
                    MissionId = missionId,
                    GateObjectId = gate.DungeonObjectId,
                    PilotId = pilotId,
                }
            )
            .Entity;
        unlock.ResetAt = MissionGateAccessRules.NextDowntime(DateTimeOffset.FromFileTime(now)).UtcDateTime.ToFileTimeUtc();
        return result;
    }

    internal static MissionGateActivation Pay(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        MissionGateKey? key,
        bool unlocked
    ) => Pay(new SqlWorldAction(world), catalog, pilotId, shipId, key, unlocked);

    internal static MissionGateActivation Pay(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        MissionGateKey? key,
        bool unlocked
    )
    {
        if (!MissionGateAccessRules.RequiresKey(key, unlocked))
        {
            return new([]);
        }
        var stacks = world
            .Filter<OwnedItem>(i =>
                i.OwnerId == pilotId && i.LocationId == shipId && i.FlagId == (int)InventoryFlag.Cargo && i.TypeId == key!.TypeId
            )
            .OrderBy(i => i.Id)
            .ToArray();
        if (stacks.Sum(i => (long)i.Quantity) < key!.Quantity)
        {
            throw new InvalidDataException(
                $"This gate requires {key.Quantity} x {catalog.RequireItem(key.TypeId).Name} in your ship's cargo hold."
            );
        }
        if (!key.Consume)
        {
            return new([]);
        }
        List<InventoryChange> changes = [];
        int remaining = key.Quantity;
        foreach (var stack in stacks)
        {
            if (remaining == 0)
            {
                break;
            }
            var before = PilotInventoryStore.Read(catalog, stack);
            int amount = Math.Min(remaining, stack.Quantity);
            stack.Quantity -= amount;
            remaining -= amount;
            changes.Add(new(before with { Quantity = stack.Quantity }, before));
            if (stack.Quantity == 0)
            {
                world.Remove(stack);
            }
        }
        return new(changes);
    }
}
