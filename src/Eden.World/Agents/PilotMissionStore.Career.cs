using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static long Deadline(MissionProfile profile, long now) =>
        profile.Lifetime == TimeSpan.MaxValue ? long.MaxValue : checked(now + profile.Lifetime.Ticks);

    private static SavedMission? LatestCareerMission(WorldAction world, long pilot, long agent) =>
        world.Filter<SavedMission>(m => m.PilotId == pilot && m.AgentId == agent).OrderByDescending(m => m.Id).FirstOrDefault();

    private static AgentAccess CareerAccess(WorldAction world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent, long now)
    {
        string reason = "";
        if (world is not EconomyTransaction || CareerMissionCatalog.ForAgent(catalog, agent).Count == 0)
        {
            reason = "This career agent has no supported mission chain.";
        }
        else if (LatestCareerMission(world, pilot.Id, agent.Id) is { ClosedAt: not null } latest)
        {
            reason = latest.State switch
            {
                MissionState.Completed when CareerMissionCatalog.Find(latest.MissionId)?.Chain is { NextMissionId: 0 } =>
                    "You have completed this agent's career missions.",
                MissionState.Declined or MissionState.Quit => "You ended this agent's career mission chain.",
                MissionState.Failed when !CareerRetryReady(latest, now) => "This career mission can be retried after downtime.",
                _ => "",
            };
        }
        return new(reason.Length == 0, reason, 0, 0, 0, 0);
    }

    private static bool CareerRetryReady(SavedMission row, long now) =>
        row.ClosedAt is long closed
        && now >= MissionGateAccessRules.NextDowntime(DateTimeOffset.FromFileTime(closed)).UtcDateTime.ToFileTimeUtc();

    public IReadOnlySet<long> CompletedCareerAgents(long pilotId, IReadOnlySet<long> agentIds)
    {
        if (agentIds.Count == 0 || LiveWorld.Find(database.Path) is not { } live)
        {
            return new HashSet<long>();
        }
        return live.Economy.Inspect(world =>
            world
                .Filter<SavedMission>(m => m.PilotId == pilotId)
                .Where(m => m.State == MissionState.Completed && m.ClosedAt != null && agentIds.Contains(m.AgentId))
                .AsEnumerable()
                .Where(m =>
                    CareerMissionCatalog.Find(m.MissionId) is { Chain.NextMissionId: 0 } part && part.FixedAgentIds.Contains(m.AgentId)
                )
                .Select(m => m.AgentId)
                .ToHashSet()
        );
    }

    private static MissionProfile CareerOfferProfile(WorldAction world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent, long now)
    {
        var latest = LatestCareerMission(world, pilot.Id, agent.Id);
        int? next = latest?.State switch
        {
            null => CareerMissionCatalog.ForAgent(catalog, agent).SingleOrDefault(m => m.Chain!.PreviousMissionId == 0)?.MissionId,
            MissionState.Completed => CareerMissionCatalog.Find(latest.MissionId)?.Chain?.NextMissionId,
            MissionState.Failed when CareerRetryReady(latest, now) => latest.MissionId,
            _ => null,
        };
        if (next is not > 0 || !MissionCatalog.ChainAvailable(catalog, MissionCatalog.Find(catalog, next.Value), agent))
        {
            throw new InvalidDataException("This agent's career mission chain is unavailable.");
        }
        return MissionCatalog.Find(catalog, next.Value);
    }

    private static InventoryChange CareerGift(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMission row,
        MissionProfile profile,
        int station
    )
    {
        var gift = profile.CareerGift!;
        int hangar = (int)InventoryFlag.Hangar;
        if (catalog.RequireItem(gift.TypeId).CategoryId != (int)InventoryCategory.Blueprint)
        {
            return InventoryPersistence.MergeOutput(world, catalog, row.PilotId, station, hangar, gift.TypeId, gift.Quantity);
        }
        // Career industry starter copies have finite runs. Captured examples have ME 5 and TE 2.
        var copy = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = row.PilotId,
                    LocationId = station,
                    FlagId = hangar,
                    TypeId = gift.TypeId,
                    Quantity = 1,
                    Singleton = true,
                    BlueprintRuns = profile.Definition.GiftBlueprintRuns ?? gift.Quantity,
                    MaterialEfficiency = 5,
                    TimeEfficiency = 2,
                }
            )
            .Entity;
        return new(PilotInventoryStore.Read(catalog, copy), null);
    }

    private static InventoryChange? CareerBonus(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMission row,
        MissionProfile profile,
        int station,
        long now
    )
    {
        if (profile.CareerBonusItem is not { } item || row.AcceptedAt is not long accepted || now >= accepted + row.BonusWindowTicks)
        {
            return null;
        }
        return InventoryPersistence.MergeOutput(
            world,
            catalog,
            row.PilotId,
            station,
            (int)InventoryFlag.Hangar,
            item.TypeId,
            item.Quantity
        );
    }
}
