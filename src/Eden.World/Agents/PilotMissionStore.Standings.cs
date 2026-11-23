using Eden.Data.Catalog;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Catalog;
using Eden.World.FactionWarfare;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static bool HasDeclineWindow(AgentType type) =>
        type is AgentType.Basic or AgentType.GenericStoryline or AgentType.Storyline or AgentType.FactionalWarfare;

    private static long DeclineWindowEnds(SavedAgentDecline? decline) =>
        decline is { LastFreeDeclineAt: > 0 } ? checked(decline.LastFreeDeclineAt + MissionRules.FreeDeclineInterval.Ticks) : 0;

    private static void DeclineStanding(
        WorldAction world,
        CatalogDbContext catalog,
        CatalogAgent agent,
        SavedMission mission,
        long now,
        List<MissionStandingChange> changes
    )
    {
        if (!HasDeclineWindow(agent.Type))
        {
            return;
        }
        var decline = world.Set<SavedAgentDecline>().Find(mission.PilotId, agent.Id);
        if (now >= DeclineWindowEnds(decline))
        {
            decline ??= world.Add(new SavedAgentDecline { PilotId = mission.PilotId, AgentId = agent.Id }).Entity;
            decline.LastFreeDeclineAt = now;
            return;
        }
        if (world is not EconomyTransaction)
        {
            throw new InvalidDataException("Wait four hours after your last free decline before declining another offer.");
        }
        ApplyMissionPenalty(world, catalog, agent, mission, StandingEvent.AgentMissionDeclined, now, changes);
    }

    private static void ApplyMissionPenalty(
        WorldAction world,
        CatalogDbContext catalog,
        CatalogAgent agent,
        SavedMission mission,
        StandingEvent reason,
        long now,
        List<MissionStandingChange>? changes
    )
    {
        if (world is not EconomyTransaction)
        {
            return;
        }
        if (MilitiaMissionCatalog.Issuer(mission.MissionId) is { } faction)
        {
            if (world.Pilots.Find(mission.PilotId) is { } pilot)
            {
                MilitiaProgressionStore.AdjustStanding(
                    world,
                    pilot,
                    faction,
                    -MilitiaProgressionRules.MissionFailureFraction,
                    reason,
                    mission.SystemId,
                    now,
                    changes
                );
            }
            return;
        }
        if (agent.Type is not (AgentType.Basic or AgentType.GenericStoryline or AgentType.Storyline or AgentType.Event))
        {
            return;
        }
        bool decline = reason == StandingEvent.AgentMissionDeclined;
        int missionTitleId = checked((int)(catalog.Read(CatalogData.Client.Missions, (long)mission.MissionId)?.NameID ?? 0));
        ApplyMissionStanding(
            world,
            mission.PilotId,
            agent.Id,
            -(decline ? MissionStandingRules.DeclineAgentFraction : MissionStandingRules.QuitAgentFraction),
            now,
            agent.CorporationId,
            mission.SystemId,
            changes,
            reason,
            missionTitleId: missionTitleId
        );
        ApplyMissionStanding(
            world,
            mission.PilotId,
            agent.CorporationId,
            -(decline ? MissionStandingRules.DeclineCorporationFraction : MissionStandingRules.QuitCorporationFraction),
            now,
            agent.CorporationId,
            mission.SystemId,
            changes,
            reason,
            missionTitleId: missionTitleId
        );
    }

    private static void ApplyStorylineDerivedStandings(
        WorldAction world,
        CatalogDbContext catalog,
        CatalogAgent agent,
        SavedMission mission,
        int faction,
        long now,
        List<MissionStandingChange> changes
    )
    {
        if (
            world is not EconomyTransaction
            || agent.Type is not (AgentType.GenericStoryline or AgentType.Storyline)
            || mission.FactionFraction <= 0
        )
        {
            return;
        }
        foreach (var relation in MissionFactionStandingCatalog.ForFaction(catalog, faction))
        {
            double fraction = MissionStandingRules.DerivedFraction(mission.FactionFraction, relation.Standing, relation.SizeRatio);
            ApplyMissionStanding(
                world,
                mission.PilotId,
                relation.FactionId,
                fraction,
                now,
                agent.CorporationId,
                mission.SystemId,
                changes,
                fraction > 0 ? StandingEvent.DerivedModificationPositive : StandingEvent.DerivedModificationNegative,
                relation.Standing
            );
        }
    }

    private static void ApplyMissionStanding(
        WorldAction world,
        long pilotId,
        long source,
        double fraction,
        long now,
        int corporation,
        int system,
        List<MissionStandingChange>? changes,
        StandingEvent reason = StandingEvent.AgentMissionCompleted,
        double? derivedCap = null,
        int missionTitleId = 0
    )
    {
        if (fraction == 0)
        {
            return;
        }
        var standing = world.Set<SavedNpcStanding>().Find(pilotId, source);
        double before = standing?.Value ?? 0;
        double after = fraction > 0 ? MissionRules.Gain(before, fraction) : StandingRules.FactionLoss(before, -fraction);
        if (derivedCap is double cap)
        {
            after = MissionStandingRules.ApplyDerivedCap(before, after, cap);
        }
        if (before == after)
        {
            return;
        }
        standing ??= world.Add(new SavedNpcStanding { PilotId = pilotId, SourceId = source }).Entity;
        standing.Value = after;
        world.Add(
            new SavedStandingTransaction
            {
                PilotId = pilotId,
                SourceId = source,
                Time = now,
                Event = reason,
                Before = before,
                After = after,
                VictimCorporationId = corporation,
                SystemId = system,
                VictimTypeId = missionTitleId,
            }
        );
        changes?.Add(new(source, before, after));
    }
}
