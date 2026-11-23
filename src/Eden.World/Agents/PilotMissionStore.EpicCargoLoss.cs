using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    internal static void EpicArcCargoDestroyed(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        IReadOnlyList<OwnedItem> items,
        IReadOnlySet<long> survivors,
        long now
    )
    {
        if (world is not EconomyTransaction || items.Count == 0 || items.All(item => survivors.Contains(item.Id)))
        {
            return;
        }
        var candidates = world
            .Filter<SavedMission>(row => row.PilotId == pilotId && row.ClosedAt == null)
            .AsEnumerable()
            .Where(row => EpicArcCatalog.Find(row.MissionId) is { FailOnCargoLoss: true })
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }
        var destroyed = items.Where(item => !survivors.Contains(item.Id)).ToDictionary(item => item.Id);
        var destroyedTypes = destroyed.Values.Select(item => item.TypeId).ToHashSet();
        foreach (var row in candidates)
        {
            var profile = MissionCatalog.Find(catalog, row.MissionId);
            if (row.State == MissionState.Offered && profile.Definition.CargoLossRetryMissionId is null)
            {
                continue;
            }
            bool Lost(int type, int quantity) =>
                destroyedTypes.Contains(type)
                && world
                    .Filter<OwnedItem>(item => item.OwnerId == pilotId && item.TypeId == type)
                    .AsEnumerable()
                    .Where(item => !destroyed.ContainsKey(item.Id) && !MissionContainerRules.IsContainer(item.LocationId))
                    .Sum(item => (long)item.Quantity) < quantity;
            bool requiredCargoLost =
                Lost(profile.CargoType, profile.CargoQuantity)
                || profile.Definition.CareerVisit?.RequiredCargoTypeId is int visitCargo && !row.ObjectiveComplete && Lost(visitCargo, 1)
                || profile.Definition.Containers.Any(container =>
                    container.DepositTypeId > 0
                    && !MissionDeliveryComplete(world, profile, row, container.ObjectId)
                    && Lost(container.DepositTypeId, container.DepositQuantity)
                );
            if (!requiredCargoLost)
            {
                continue;
            }
            if (row.State == MissionState.Accepted && NpcDirectory.Agent(catalog, row.AgentId) is { } agent)
            {
                List<MissionStandingChange> standings = [];
                ApplyMissionPenalty(world, catalog, agent, row, StandingEvent.AgentMissionFailed, now, standings);
                if (LiveWorld.Find(world.WorldPath)?.Fleets is { } notifications)
                {
                    foreach (var change in standings)
                    {
                        world.AfterCommit(() => notifications.NotifyStandingChanged(pilotId, change.SourceId, change.After));
                    }
                }
            }
            Close(world, row, MissionState.Failed, now);
        }
    }
}
