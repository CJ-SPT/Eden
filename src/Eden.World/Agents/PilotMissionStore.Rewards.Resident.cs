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

    private MissionUpdate ActResident(LiveWorld live, long pilotId, long agentId, MissionAction action, long? offerId, bool share = false)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var transaction = live.Economy.Begin(new(pilots: [pilotId]));
        long now = Now;
        training.Advance(transaction, pilotId, now);
        var pilot = ResidentPilot(transaction, pilotId);
        long[] recipients = share ? fleets.Fleetmates(pilotId).Select(p => p.Id).Where(transaction.PilotExists).ToArray() : [];
        if (recipients.Length > MaximumOtherMissionRecipients)
        {
            Random.Shared.Shuffle(recipients.AsSpan());
            recipients = recipients[..MaximumOtherMissionRecipients];
        }
        var result = Act(transaction, catalog, pilot, Agent(agentId), action, offerId, now, recipients);
        transaction.Commit();
        return result;
    }

    private static Pilot ResidentPilot(WorldAction world, long pilotId) =>
        world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown mission pilot.");

    private static bool Expire(WorldAction world, SavedMission mission, long now)
    {
        if (mission.ClosedAt is null && now >= mission.ExpiresAt)
        {
            Close(world, mission, MissionState.Failed, now);
            return true;
        }
        return false;
    }

    private static void Close(WorldAction world, SavedMission mission, MissionState state, long now)
    {
        if (MissionCatalog.IsMining(mission.MissionId))
        {
            world.RemoveRange(MissionDeposits(world.Set<WorldAsteroid>(), mission.Id));
        }
        long firstContainer = MissionContainerRules.Id(mission.Id, 0);
        long endContainer = checked(firstContainer + MissionRules.NpcsPerMission);
        world.RemoveRange(
            world.Set<Eden.Data.World.Inventory.OwnedItem>().Where(i => i.LocationId >= firstContainer && i.LocationId < endContainer)
        );
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
