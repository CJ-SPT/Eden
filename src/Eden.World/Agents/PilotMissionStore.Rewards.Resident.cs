using Eden.Data.Catalog;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private const int MaximumOtherMissionRecipients = 9;

    public bool CanShareReward(long pilotId, long agentId, long offerId)
    {
        if (LiveWorld.Find(database.Path) is not { } live || fleets.Fleetmates(pilotId).Count == 0)
        {
            return false;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world =>
        {
            var row = world.Set<SavedMission>().Find(offerId);
            var pilot = world.Pilots.Find(pilotId);
            if (
                pilot is null
                || row is null
                || row.PilotId != pilotId
                || row.AgentId != agentId
                || row.State != MissionState.Accepted
                || row.ClosedAt is not null
                || row.ExpiresAt <= Now
            )
            {
                return false;
            }
            var offer = Snapshot(world, catalog, pilot, row);
            if (offer.Agent.Space is not null || PersonalRewards(offer.Agent.Type))
            {
                return false;
            }
            int? station = offer.Cargo?.DestinationStation ?? offer.Agent.StationId;
            return offer.ObjectiveComplete && pilot.ActiveShip.Position is null && pilot.ActiveShip.DockedLocationId == station;
        });
    }

    public MissionUpdate CompleteWithFleet(long pilotId, long agentId, long offerId) =>
        ActResident(
            LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Fleet rewards require an active resident world."),
            pilotId,
            agentId,
            MissionAction.Complete,
            offerId,
            share: true
        );

    private MissionUpdate ActResident(
        LiveWorld live,
        long pilotId,
        long agentId,
        MissionAction action,
        long? offerId,
        bool share = false,
        int? epicChoice = null
    )
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var transaction = live.Economy.Begin(new(pilots: [pilotId]));
        long now = Now;
        training.Advance(transaction, pilotId, now);
        var pilot = ResidentPilot(transaction, pilotId);
        var agent = Agent(agentId);
        if (share && (agent.Space is not null || PersonalRewards(agent.Type)))
        {
            throw new InvalidDataException("This agent's mission rewards are personal.");
        }
        long[] recipients = share ? fleets.Fleetmates(pilotId).Select(p => p.Id).Where(transaction.PilotExists).ToArray() : [];
        if (recipients.Length > MaximumOtherMissionRecipients)
        {
            Random.Shared.Shuffle(recipients.AsSpan());
            recipients = recipients[..MaximumOtherMissionRecipients];
        }
        var result = Act(transaction, catalog, pilot, agent, action, offerId, now, recipients, epicChoice);
        transaction.Commit();
        return result;
    }

    private static Pilot ResidentPilot(WorldAction world, long pilotId) =>
        world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown mission pilot.");

    private static bool PersonalRewards(AgentType type) =>
        type is AgentType.FactionalWarfare or AgentType.Research or AgentType.Heraldry or AgentType.Career;

    private static bool Expire(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMission mission,
        long now,
        List<MissionStandingChange>? standings = null
    )
    {
        if (mission.ClosedAt is null && now >= mission.ExpiresAt)
        {
            if (
                mission.State == MissionState.Accepted
                && world is EconomyTransaction
                && NpcDirectory.Agent(catalog, mission.AgentId) is { } agent
            )
            {
                var changes = standings ?? [];
                ApplyMissionPenalty(world, catalog, agent, mission, StandingEvent.AgentMissionFailed, now, changes);
                if (standings is null && changes.Count != 0 && LiveWorld.Find(world.WorldPath)?.Fleets is { } notifications)
                {
                    foreach (var change in changes)
                    {
                        world.AfterCommit(() => notifications.NotifyStandingChanged(mission.PilotId, change.SourceId, change.After));
                    }
                }
            }
            Close(world, mission, MissionState.Failed, now);
            return true;
        }
        return false;
    }

    private static void Close(WorldAction world, SavedMission mission, MissionState state, long now)
    {
        CloseCareerSalvage(world, mission);
        RetireMissionExploration(world, mission);
        if (MissionCatalog.IsMining(mission.MissionId))
        {
            world.RemoveRange(MissionDeposits(world.Set<WorldAsteroid>(), mission.Id));
        }
        long firstContainer = MissionContainerRules.Id(mission.Id, 0);
        // Query the indexed locations in the complete reserved range, including
        // containers from older content revisions, without scanning every saved item.
        var containers = Enumerable.Range(0, MissionRules.NpcsPerMission).Select(index => checked(firstContainer + index)).ToHashSet();
        world.RemoveRange(world.Set<Eden.Data.World.Inventory.OwnedItem>().Where(i => containers.Contains(i.LocationId)));
        mission.State = state;
        mission.ClosedAt = now;
        foreach (
            var npc in world
                .Set<WorldNpc>()
                .Where(n =>
                    n.AnchorKind == Eden.Gameplay.Combat.NpcAnchorKind.Mission
                    && n.EncounterId == MissionRules.FirstSiteId + mission.Id
                    && n.DestroyedAt == null
                    && n.RetiredAt == null
                )
        )
        {
            npc.RetiredAt = now;
        }
    }
}
