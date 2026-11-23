using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Redemption;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static readonly TimeSpan s_paragonCooldown = TimeSpan.FromHours(23);

    private static long? ParagonReplayAt(WorldAction world, CatalogDbContext catalog, long pilotId, long now)
    {
        var definitions = ParagonMissionCatalog.Read(catalog);
        long threshold = now - s_paragonCooldown.Ticks;
        long? last = world
            .Filter<SavedMission>(m => m.PilotId == pilotId)
            .Where(m => m.State == MissionState.Completed && m.ClosedAt > threshold && definitions.ContainsKey(m.MissionId))
            .Max(m => m.ClosedAt);
        return last + s_paragonCooldown.Ticks;
    }

    private static AgentAccess ParagonAccess(WorldAction world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent, long now)
    {
        string reason =
            world is not EconomyTransaction || !ParagonMissionCatalog.IsAgent(agent) || ParagonMissionCatalog.Read(catalog).Count == 0
                ? "This Paragon agent has no available mission service."
            : ParagonReplayAt(world, catalog, pilot.Id, now) is not null ? "IRIS is on cooldown after your last Paragon mission."
            : "";
        return new(reason.Length == 0, reason, 0, 0, 0, 0);
    }

    private static SavedMission OfferParagon(WorldAction world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent, long now)
    {
        var definitions = ParagonMissionCatalog.Read(catalog);
        int sequence = world.Filter<SavedMission>(row => row.PilotId == pilot.Id).Count(row => row.AgentId == agent.Id);
        int missionId = definitions.Keys.Order().ElementAt(sequence % definitions.Count);
        var authored = definitions[missionId];
        var profile = MissionCatalog.Find(catalog, missionId);
        var station = catalog.RequireStation(agent.StationId!.Value);
        return world
            .Add(
                new SavedMission
                {
                    PilotId = pilot.Id,
                    AgentId = agent.Id,
                    MissionId = missionId,
                    Level = agent.Level,
                    State = MissionState.Offered,
                    OfferedAt = now,
                    ExpiresAt = checked(now + profile.Lifetime.Ticks),
                    DestinationStation = station.Id,
                    SystemId = station.SolarSystemId,
                    X = station.X,
                    Y = station.Y,
                    Z = station.Z,
                    LoyaltyPoints = (
                        pilot.Account.CloneGrade == AccountCloneGrade.Omega ? authored.FixedLpRewardOmega : authored.FixedLpRewardAlpha
                    )!.Value,
                }
            )
            .Entity;
    }

    private static MissionPayment PayParagon(WorldAction world, Pilot pilot, SavedMission mission)
    {
        const int issuer = CurrencyRewardRules.ParagonCorporation;
        var wallet =
            world.Set<SavedLoyaltyBalance>().Find(pilot.Id, issuer)
            ?? world.Add(new SavedLoyaltyBalance { PilotId = pilot.Id, IssuerCorporationId = issuer }).Entity;
        long before = wallet.Points;
        wallet.Points = CurrencyRewardRules.Credit(before, mission.LoyaltyPoints);
        long? corporationPoints = null;
        if (world.Set<SavedCorporation>().Find(pilot.CorporationId) is { } playerCorporation)
        {
            var corporation =
                world.Set<SavedCorporationLoyaltyBalance>().Find(pilot.CorporationId, issuer)
                ?? world
                    .Add(new SavedCorporationLoyaltyBalance { CorporationId = pilot.CorporationId, IssuerCorporationId = issuer })
                    .Entity;
            corporation.Points = CurrencyRewardRules.Credit(corporation.Points, mission.LoyaltyPoints);
            if (CorporationRoleStore.Has(world, pilot.Id, playerCorporation, CorporationRole.Accountant | CorporationRole.JuniorAccountant))
            {
                corporationPoints = corporation.Points;
            }
        }
        return new(null, before, wallet.Points) { CorporationPointsAfter = corporationPoints };
    }

    private static IReadOnlyList<long> RetireParagonOffers(WorldAction world, CatalogDbContext catalog, SavedMission completed, long now)
    {
        var definitions = ParagonMissionCatalog.Read(catalog);
        var others = world
            .Filter<SavedMission>(m => m.PilotId == completed.PilotId)
            .Where(m => m.Id != completed.Id && m.ClosedAt == null && definitions.ContainsKey(m.MissionId))
            .ToArray();
        foreach (var sibling in others)
        {
            sibling.State = MissionState.Failed;
            sibling.ClosedAt = now;
        }
        return others.Select(m => m.AgentId).ToArray();
    }
}
