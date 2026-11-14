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
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private sealed record MissionPayment(long BalanceCents, long PointsBefore, long PointsAfter);

    private SavedMission Offer(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        int level,
        bool storyline,
        long now
    )
    {
        if (!storyline && agent.Type != AgentType.Basic)
        {
            throw new InvalidDataException("This special agent does not offer ordinary missions.");
        }
        if (!storyline && MissionRules.KindForDivision(agent.DivisionId) is null)
        {
            throw new InvalidDataException("This agent's mission division is not implemented.");
        }
        var profiles = ProfilesForAgent(catalog, agent, level, storyline)
            .OrderBy(p => p.Definition.OfferOrder)
            .ThenBy(p => p.Definition.Gates.IsEmpty ? 0 : 1)
            .ThenBy(p => p.Definition.MissionId)
            .ToArray();
        if (profiles.Length == 0)
        {
            throw new InvalidDataException("No supported mission content is available for this agent and level yet.");
        }
        int sequence = world.Set<SavedMission>().Count(m => m.PilotId == pilot.Id && m.AgentId == agent.Id);
        var profile = profiles[sequence % profiles.Length];
        var skill = profile.Kind switch
        {
            MissionKind.Combat => MissionSocialSkill.SecurityConnections,
            MissionKind.Mining => MissionSocialSkill.MiningConnections,
            _ => MissionSocialSkill.DistributionConnections,
        };
        var reward = MissionRules.Reward(
            level,
            profile.Definition.Difficulty,
            Skill(world, catalog, pilot, MissionSocialSkill.Negotiation),
            Skill(world, catalog, pilot, skill),
            Skill(world, catalog, pilot, MissionSocialSkill.Social),
            storyline
        );
        reward = WorldTuningRules.Mission(reward, world.Settings.Rewards);
        var station = catalog.FindStation(agent.StationId!.Value) ?? throw new InvalidDataException("The agent's station is unavailable.");
        var destination = station;
        if (profile.Kind == MissionKind.Courier)
        {
            var neighbors = catalog.GatesInSystem(station.SolarSystemId).Select(g => g.DestinationSystemId).ToHashSet();
            var destinations = catalog
                .Stations()
                .Where(s => s.Id != station.Id && neighbors.Contains(s.SolarSystemId))
                .OrderByDescending(s => s.OwnerId == agent.CorporationId)
                .ThenBy(s => s.Id)
                .ToArray();
            destination =
                destinations.FirstOrDefault()
                ?? catalog.StationsInSystem(station.SolarSystemId).FirstOrDefault(s => s.Id != station.Id)
                ?? throw new InvalidDataException("This agent has no supported courier destination.");
        }
        // A fixed offset from a real station gives each live mission a distinct location.
        // Historical closed missions remain part of the sequence so restarts cannot overlap sites.
        long serial = world.Next("WorldMission");
        double offset = MissionRules.PlacementOffset * serial;
        var row = new SavedMission
        {
            PilotId = pilot.Id,
            AgentId = agent.Id,
            MissionId = profile.Definition.MissionId,
            Level = level,
            Storyline = storyline,
            State = MissionState.Offered,
            OfferedAt = now,
            ExpiresAt = checked(now + profile.Lifetime.Ticks),
            DestinationStation = destination.Id,
            SystemId = destination.SolarSystemId,
            X = station.X + offset,
            Y = station.Y + MissionRules.PlacementOffset,
            Z = station.Z - offset,
            IskCents = reward.IskCents,
            BonusIskCents = MissionBonusRules.Quote(reward.IskCents),
            BonusWindowTicks = MissionBonusRules.Window.Ticks,
            LoyaltyPoints = reward.LoyaltyPoints,
            CorporationFraction = reward.CorporationFraction,
            FactionFraction = reward.FactionFraction,
        };
        world.Add(row);
        return row;
    }

    private static IEnumerable<MissionProfile> ProfilesForAgent(CatalogDbContext catalog, CatalogAgent agent, int level, bool storyline)
    {
        var kind = MissionRules.KindForDivision(agent.DivisionId);
        return MissionCatalog
            .Read(catalog)
            .Where(profile =>
                profile.Definition.Level == level
                && profile.Definition.Storyline == storyline
                && (storyline || profile.Kind == kind)
                && !profile.AttacksFaction(agent.FactionId)
                && profile.AvailableTo(agent)
            );
    }

    private MissionPayment Pay(
        WorldAction world,
        Pilot pilot,
        CatalogAgent agent,
        SavedMission mission,
        long now,
        List<MissionStandingChange> standings,
        IReadOnlyList<long> sharers
    )
    {
        int count = sharers.Count + 1;
        long iskShare = mission.IskCents / count;
        long bonus = Bonus(mission)?.Payable(now) ?? 0;
        long bonusShare = bonus / count;
        long lpShare = mission.LoyaltyPoints / count;
        double standingShare = mission.CorporationFraction / count;
        foreach (long recipient in sharers)
        {
            Credit(world, recipient, agent, mission, now, iskShare, bonusShare, lpShare);
            List<MissionStandingChange> gains = [];
            Gain(world, recipient, agent.CorporationId, standingShare, now, agent.CorporationId, mission.SystemId, gains);
            foreach (var gain in gains)
            {
                world.AfterCommit(() => fleets.NotifyMissionStanding(recipient, gain.SourceId, gain.After));
            }
        }
        var payment = Credit(
            world,
            pilot.Id,
            agent,
            mission,
            now,
            mission.IskCents - iskShare * sharers.Count,
            bonus - bonusShare * sharers.Count,
            mission.LoyaltyPoints - lpShare * sharers.Count
        );
        Gain(world, pilot.Id, agent.Id, mission.CorporationFraction, now, agent.CorporationId, mission.SystemId, standings);
        Gain(world, pilot.Id, agent.CorporationId, standingShare, now, agent.CorporationId, mission.SystemId, standings);
        if (mission.Storyline && agent.FactionId is int faction)
        {
            Gain(world, pilot.Id, faction, mission.FactionFraction, now, agent.CorporationId, mission.SystemId, standings);
        }
        return payment;
    }

    private static MissionPayment Credit(
        WorldAction world,
        long pilotId,
        CatalogAgent agent,
        SavedMission mission,
        long now,
        long isk,
        long bonus,
        long points
    )
    {
        var wallet = world.Set<PilotWallet>().Find(pilotId) ?? world.Add(new PilotWallet { PilotId = pilotId }).Entity;
        wallet.Cents = checked(wallet.Cents + isk);
        world.Add(
            new SavedMarketPayment
            {
                PilotId = pilotId,
                CounterpartyId = agent.Id,
                OrderId = mission.Id,
                Kind = WalletEntryType.AgentMissionReward,
                AmountCents = isk,
                BalanceCents = wallet.Cents,
                Time = now,
            }
        );
        if (bonus > 0)
        {
            wallet.Cents = checked(wallet.Cents + bonus);
            world.Add(
                new SavedMarketPayment
                {
                    PilotId = pilotId,
                    CounterpartyId = agent.Id,
                    OrderId = mission.Id,
                    Kind = WalletEntryType.AgentMissionTimeBonusReward,
                    AmountCents = bonus,
                    BalanceCents = wallet.Cents,
                    Time = now,
                }
            );
        }
        var lp =
            world.Set<SavedLoyaltyBalance>().Find(pilotId, agent.CorporationId)
            ?? world.Add(new SavedLoyaltyBalance { PilotId = pilotId, IssuerCorporationId = agent.CorporationId }).Entity;
        long before = lp.Points;
        lp.Points = checked(lp.Points + points);
        return new(wallet.Cents, before, lp.Points);
    }

    private static void Gain(
        WorldAction world,
        long pilotId,
        long source,
        double fraction,
        long now,
        int corporation,
        int system,
        List<MissionStandingChange> changes
    )
    {
        if (fraction == 0)
        {
            return;
        }
        var standing =
            world.Set<SavedNpcStanding>().Find(pilotId, source)
            ?? world.Add(new SavedNpcStanding { PilotId = pilotId, SourceId = source }).Entity;
        double before = standing.Value;
        standing.Value = MissionRules.Gain(before, fraction);
        if (before != standing.Value)
        {
            world.Add(
                new SavedStandingTransaction
                {
                    PilotId = pilotId,
                    SourceId = source,
                    Time = now,
                    Event = StandingEvent.AgentMissionCompleted,
                    Before = before,
                    After = standing.Value,
                    VictimCorporationId = corporation,
                    SystemId = system,
                }
            );
            changes.Add(new(source, before, standing.Value));
        }
    }

    private void Progress(WorldAction world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent, int level, long now)
    {
        if (agent.FactionId is not int faction || level > 4)
        {
            return;
        }
        var progress =
            world.Filter<SavedMissionProgress>(p => p.PilotId == pilot.Id && p.FactionId == faction && p.Level == level).SingleOrDefault()
            ?? world
                .Add(
                    new SavedMissionProgress
                    {
                        PilotId = pilot.Id,
                        FactionId = faction,
                        Level = level,
                    }
                )
                .Entity;
        progress.Completed++;
        if (progress.Completed < MissionRules.StorylineInterval)
        {
            return;
        }
        // Native storyline agents are level one. The offer inherits the regular mission level.
        var busy = world.Set<SavedMission>().Where(m => m.PilotId == pilot.Id && m.ClosedAt == null).Select(m => m.AgentId).ToHashSet();
        var distances = Distances(catalog, agent.SystemId!.Value);
        bool highSecurity =
            (catalog.Read(CatalogData.Client.Systems, (long)agent.SystemId.Value)?.SecurityStatus ?? 0) >= CrimewatchRules.HighSecurity;
        var next = directory
            .Agents()
            .Where(a =>
                a.FactionId == faction
                && a.StationId.HasValue
                && a.SystemId.HasValue
                && a.Type is AgentType.GenericStoryline or AgentType.Storyline
                && !busy.Contains(a.Id)
                && distances.ContainsKey(a.SystemId.Value)
                && (
                    !highSecurity
                    || (catalog.Read(CatalogData.Client.Systems, (long)a.SystemId.Value)?.SecurityStatus ?? 0)
                        >= CrimewatchRules.HighSecurity
                )
            )
            .OrderBy(a => distances[a.SystemId!.Value])
            .ThenBy(a => a.Id)
            .FirstOrDefault();
        if (next is null)
        {
            return;
        }
        _ = Offer(world, catalog, pilot, next, level, true, now);
        progress.Completed -= MissionRules.StorylineInterval;
    }

    private static Dictionary<int, int> Distances(CatalogDbContext catalog, int system)
    {
        Dictionary<int, int> result = new() { [system] = 0 };
        Queue<int> pending = new();
        pending.Enqueue(system);
        while (pending.TryDequeue(out int current))
        {
            foreach (var gate in catalog.GatesInSystem(current))
            {
                if (result.TryAdd(gate.DestinationSystemId, result[current] + 1))
                {
                    pending.Enqueue(gate.DestinationSystemId);
                }
            }
        }
        return result;
    }

    private static bool Expire(WorldDbContext world, SavedMission mission, long now)
    {
        if (mission.ClosedAt is null && now >= mission.ExpiresAt)
        {
            Close(world, mission, MissionState.Failed, now);
            return true;
        }
        return false;
    }

    private static void Close(WorldDbContext world, SavedMission mission, MissionState state, long now)
    {
        if (MissionCatalog.IsMining(mission.MissionId))
        {
            MissionDeposits(world.Set<WorldAsteroid>(), mission.Id).ExecuteDelete();
        }
        long firstContainer = MissionContainerRules.Id(mission.Id, 0);
        long endContainer = checked(firstContainer + MissionRules.NpcsPerMission);
        world
            .Set<Eden.Data.World.Inventory.OwnedItem>()
            .Where(i => i.LocationId >= firstContainer && i.LocationId < endContainer)
            .ExecuteDelete();
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
