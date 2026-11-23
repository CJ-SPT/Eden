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
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.FactionWarfare;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private sealed record MissionPayment(long? BalanceCents, long PointsBefore, long PointsAfter)
    {
        internal long? CorporationPointsAfter { get; init; }
    }

    private SavedMission Offer(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        int level,
        bool storyline,
        long now,
        MissionProfile? continuation = null,
        int? courierDestination = null
    )
    {
        if (agent.Type == AgentType.Heraldry)
        {
            return OfferParagon(world, catalog, pilot, agent, now);
        }
        bool fixedAgent = agent.Type == AgentType.Event && MissionCatalog.ForFixedAgent(catalog, agent.Id).Any();
        if (fixedAgent)
        {
            FixedAgentRules.RequireAvailable(
                world is EconomyTransaction,
                true,
                world.Filter<SavedMission>(m => m.PilotId == pilot.Id && m.AgentId == agent.Id && m.ClosedAt != null).Any()
            );
            storyline = true;
        }
        if (!storyline && agent.Type is not (AgentType.Basic or AgentType.FactionalWarfare or AgentType.Research))
        {
            throw new InvalidDataException("This special agent does not offer ordinary missions.");
        }
        bool research = agent.Type == AgentType.Research;
        if (!storyline && !research && agent.Type != AgentType.FactionalWarfare && MissionRules.KindForDivision(agent.DivisionId) is null)
        {
            throw new InvalidDataException("This agent's mission division is not implemented.");
        }
        var profiles = research
            ? []
            : ProfilesForAgent(catalog, agent, level, storyline)
                .Where(p => p.Definition.Chain is null || world is EconomyTransaction)
                .Where(p => p.Kind != MissionKind.Trade && p.Definition.ItemReward is null || world is EconomyTransaction)
                .OrderBy(p => p.Definition.OfferOrder)
                .ThenBy(p => p.Definition.Gates.IsEmpty ? 0 : 1)
                .ThenBy(p => p.Definition.MissionId)
                .ToArray();
        if (!research && continuation is null && profiles.Length == 0)
        {
            throw new InvalidDataException("No supported mission content is available for this agent and level yet.");
        }
        int sequence = research ? 0 : world.Set<SavedMission>().Count(m => m.PilotId == pilot.Id && m.AgentId == agent.Id);
        var profile = research
            ? ResearchMissionProfile(world, catalog, pilot, agent, now)
            : continuation ?? profiles[sequence % profiles.Length];
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
        if (research)
        {
            reward = reward with { IskCents = 0, LoyaltyPoints = 0, FactionFraction = 0 };
        }
        var station = agent.Space is null
            ? catalog.FindStation(agent.StationId!.Value) ?? throw new InvalidDataException("The agent's station is unavailable.")
            : null;
        var destination = station;
        int? warzoneDestination =
            agent.Type == AgentType.FactionalWarfare
                ? MilitiaDestination(world, catalog, station!.SolarSystemId, (MilitiaFaction)agent.FactionId!.Value, sequence)
                : null;
        if (profile.Kind == MissionKind.Courier && courierDestination is int selected)
        {
            destination = catalog.RequireStation(selected);
        }
        else if (profile.Kind == MissionKind.Courier)
        {
            var neighbors = catalog.GatesInSystem(station!.SolarSystemId).Select(g => g.DestinationSystemId).ToHashSet();
            var destinations = catalog
                .Stations()
                .Where(s => s.Id != station.Id && neighbors.Contains(s.SolarSystemId))
                .OrderByDescending(s => s.OwnerId == agent.CorporationId)
                .ThenBy(s => s.Id)
                .ToArray();
            destination =
                destinations.FirstOrDefault()
                ?? catalog.StationsInSystem(station.SolarSystemId).FirstOrDefault(s => s.Id != station.Id)
                ?? (research ? ResearchCourierDestination(catalog, station, agent.CorporationId) : null)
                ?? throw new InvalidDataException("This agent has no supported courier destination.");
        }
        long serial = world.Next("WorldMission");
        int systemId = warzoneDestination ?? agent.Space?.SystemId ?? destination!.SolarSystemId;
        var position = profile.DungeonId.HasValue
            ? MissionPlacementRules.Place(PlacementAnchor(catalog, systemId), serial)
            : agent.Space?.Position ?? new SpacePosition(destination!.X, destination.Y, destination.Z);
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
            DestinationStation = destination?.Id ?? 0,
            SystemId = systemId,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            IskCents = reward.IskCents,
            BonusIskCents = MissionBonusRules.Quote(reward.IskCents),
            BonusWindowTicks = research ? 0 : MissionBonusRules.Window.Ticks,
            LoyaltyPoints = reward.LoyaltyPoints,
            CorporationFraction = reward.CorporationFraction,
            FactionFraction = reward.FactionFraction,
        };
        if (profile.Definition.ItemReward is { } itemReward)
        {
            var quoted = MissionItemRewardRules.Quote(itemReward.Types, itemReward.Quantity, Random.Shared.Next(itemReward.Types.Length));
            row.ItemRewardTypeId = quoted.TypeId;
            row.ItemRewardQuantity = quoted.Quantity;
        }
        world.Add(row);
        return row;
    }

    private static SpacePosition PlacementAnchor(CatalogDbContext catalog, int systemId)
    {
        const int planetGroup = 7;
        var bodies = catalog.CelestialsInSystem(systemId);
        if ((bodies.FirstOrDefault(body => body.GroupId == planetGroup) ?? bodies.FirstOrDefault()) is { } anchor)
        {
            return new(anchor.X, anchor.Y, anchor.Z);
        }
        return catalog.StationsInSystem(systemId).FirstOrDefault() is { } station
            ? new(station.X, station.Y, station.Z)
            : SpacePosition.Zero;
    }

    private static IEnumerable<MissionProfile> ProfilesForAgent(CatalogDbContext catalog, CatalogAgent agent, int level, bool storyline)
    {
        if (agent.Type == AgentType.Event)
        {
            return MissionCatalog.ForFixedAgent(catalog, agent.Id).Where(p => p.Definition.Level == level && p.AvailableTo(agent));
        }
        if (agent.Type == AgentType.FactionalWarfare)
        {
            return MissionCatalog.ForMilitia(catalog, agent).Where(profile => profile.AvailableTo(agent));
        }
        var kind = MissionRules.KindForDivision(agent.DivisionId);
        return MissionCatalog
            .ForLevel(catalog, level, storyline)
            .Where(profile =>
                (storyline || profile.Kind == kind)
                && !profile.AttacksFaction(agent.FactionId)
                && profile.AvailableTo(agent)
                && (profile.Definition.Chain is null || profile.Definition.Chain.PreviousMissionId == 0)
                && MissionCatalog.ChainAvailable(catalog, profile, agent)
            );
    }

    private static int MilitiaDestination(WorldAction world, CatalogDbContext catalog, int start, MilitiaFaction faction, int sequence)
    {
        var opponent = MilitiaRules.Opponent(faction);
        var distances = Distances(catalog, start);
        var systems = FactionWarzoneCatalog
            .Roster.Where(seed =>
                distances.ContainsKey(seed.SystemId)
                && FactionWarzoneStore.Occupier(world, seed.SystemId) == opponent
                && FactionWarzoneStore.System(world, seed.SystemId)!.PendingTransferAt is null
            )
            .OrderBy(seed => distances[seed.SystemId])
            .ThenBy(seed => seed.SystemId)
            .Take(5)
            .ToArray();
        return systems.Length > 0
            ? systems[sequence % systems.Length].SystemId
            : throw new InvalidDataException("This station has no reachable opposing warzone destination.");
    }

    private MissionPayment Pay(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        SavedMission mission,
        int missionTitleId,
        long now,
        List<MissionStandingChange> standings,
        IReadOnlyList<long> sharers
    )
    {
        int count = sharers.Count + 1;
        int corporation = agent.CorporationId;
        long iskShare = mission.IskCents / count;
        long bonus = Bonus(mission)?.Payable(now) ?? 0;
        long bonusShare = bonus / count;
        long lpShare = mission.LoyaltyPoints / count;
        double standingShare = mission.CorporationFraction / count;
        foreach (long recipient in sharers)
        {
            Credit(world, recipient, agent, mission, now, iskShare, bonusShare, lpShare);
            List<MissionStandingChange> gains = [];
            ApplyMissionStanding(
                world,
                recipient,
                corporation,
                standingShare,
                now,
                corporation,
                mission.SystemId,
                gains,
                missionTitleId: missionTitleId
            );
            foreach (var gain in gains)
            {
                world.AfterCommit(() => fleets.NotifyStandingChanged(recipient, gain.SourceId, gain.After));
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
        ApplyMissionStanding(
            world,
            pilot.Id,
            agent.Id,
            mission.CorporationFraction,
            now,
            corporation,
            mission.SystemId,
            standings,
            missionTitleId: missionTitleId
        );
        if (MilitiaMissionCatalog.Issuer(mission.MissionId) is { } militia)
        {
            MilitiaProgressionStore.AdjustStanding(
                world,
                pilot,
                militia,
                standingShare,
                StandingEvent.AgentMissionCompleted,
                mission.SystemId,
                now,
                standings
            );
        }
        else
        {
            ApplyMissionStanding(
                world,
                pilot.Id,
                corporation,
                standingShare,
                now,
                corporation,
                mission.SystemId,
                standings,
                missionTitleId: missionTitleId
            );
        }
        if (mission.Storyline && agent.FactionId is int faction)
        {
            ApplyMissionStanding(
                world,
                pilot.Id,
                faction,
                mission.FactionFraction,
                now,
                corporation,
                mission.SystemId,
                standings,
                missionTitleId: missionTitleId
            );
            ApplyStorylineDerivedStandings(world, catalog, agent, mission, faction, now, standings);
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
        var faction = MilitiaMissionCatalog.Issuer(mission.MissionId);
        int issuer = faction is { } militia ? MilitiaRules.Corporation(militia) : agent.CorporationId;
        var lp = world.Set<SavedLoyaltyBalance>().Find(pilotId, issuer);
        long before = lp?.Points ?? 0;
        if (faction is { } side)
        {
            MilitiaLoyalty.Credit(world, side, world.Pilots.Find(pilotId)!, points, MilitiaLoyaltySource.Mission, mission.Id, now);
        }
        else
        {
            lp ??= world.Add(new SavedLoyaltyBalance { PilotId = pilotId, IssuerCorporationId = issuer }).Entity;
            lp.Points = checked(lp.Points + points);
        }
        return new(wallet.Cents, before, world.Set<SavedLoyaltyBalance>().Find(pilotId, issuer)?.Points ?? 0);
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
        var active = world.Filter<SavedMission>(m => m.PilotId == pilot.Id && m.ClosedAt == null).ToArray();
        var busy = active.Where(m => !Expire(world, catalog, m, now)).Select(m => m.AgentId).ToHashSet();
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
            // Missing or supplier-restricted storyline content must not roll back
            // the ordinary mission's cargo consumption and earned rewards.
            .Where(a => ProfilesForAgent(catalog, a, level, true).Any())
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
        if (
            mission.ClosedAt is null
            && now >= mission.ExpiresAt
            && (MissionCatalog.IsFixed(mission.MissionId) || ResearchMissionCatalog.IsResearch(mission.MissionId))
        )
        {
            throw new InvalidDataException("This mission's expiry requires the resident world.");
        }
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
