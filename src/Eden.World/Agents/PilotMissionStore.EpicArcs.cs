using System.Globalization;
using Eden.Data.Catalog;
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
    private static SavedMission[] EpicArcHistory(WorldAction world, long pilotId) =>
        world
            .Filter<SavedMission>(row => row.PilotId == pilotId)
            .AsEnumerable()
            .Where(row => EpicArcCatalog.Find(row.MissionId) is not null)
            .OrderByDescending(row => row.Id)
            .ToArray();

    private static EpicArcNode? EpicArcRequest(EpicArc arc, SavedMission? last, long agentId, long now)
    {
        if (last is null || ArcCanRestart(arc, last, now))
        {
            return arc.Roots.SingleOrDefault(node => node.AgentId == agentId);
        }
        if (last.State == MissionState.Failed && EpicFailureDestination(arc, last.MissionId) is int retry)
        {
            var stage = arc.Nodes[retry];
            return stage.AgentId == agentId ? stage : null;
        }
        return null;
    }

    private static bool ArcCanRestart(EpicArc arc, SavedMission last, long now) =>
        last.State == MissionState.Declined && arc.Roots.Any(root => root.Id == last.MissionId)
        || ArcReplayDeadline(arc, last) is long deadline && now >= deadline;

    private static long? ArcReplayDeadline(EpicArc arc, SavedMission last) =>
        last.ClosedAt is long closed
        && (
            last.State == MissionState.Quit
            || last.State == MissionState.Declined && !arc.Roots.Any(root => root.Id == last.MissionId)
            || last.State == MissionState.Failed && EpicFailureDestination(arc, last.MissionId) is null
            || last.State == MissionState.Completed && arc.Nodes[last.MissionId].Successors.IsEmpty
        )
            ? checked(closed + arc.ReplayTicks)
            : null;

    private static int? EpicFailureDestination(EpicArc arc, int missionId) =>
        EpicArcCatalog.Find(missionId)?.CargoLossRetryMissionId ?? arc.Nodes[missionId].FailureMissionId;

    private static AgentAccess EpicArcAccess(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        AgentAccess access,
        long now
    )
    {
        if (world is not EconomyTransaction)
        {
            return access with { Allowed = false, Reason = "Epic arcs require the resident world." };
        }
        var history = EpicArcHistory(world, pilot.Id);
        string reason = "This agent has no epic arc assignment for you.";
        foreach (var arc in EpicArcCatalog.ForAgent(catalog, agent.Id))
        {
            var last = history.FirstOrDefault(row => arc.Nodes.ContainsKey(row.MissionId));
            if (
                last is { ClosedAt: null }
                && (
                    last.AgentId == agent.Id
                    || last.State == MissionState.Accepted && HandoffTarget(catalog, arc, last.MissionId)?.AgentId == agent.Id
                )
            )
            {
                return access with { Allowed = true, Reason = "" };
            }
            if (EpicArcRequest(arc, last, agent.Id, now) is not null)
            {
                bool startsArc = last is null || ArcCanRestart(arc, last, now);
                return !startsArc ? access with { Allowed = true, Reason = "" } : access;
            }
            if (last is not null && arc.Roots.Any(root => root.AgentId == agent.Id) && ArcReplayDeadline(arc, last) > now)
            {
                reason = "This epic arc is on replay cooldown.";
            }
        }
        return access with { Allowed = false, Reason = reason };
    }

    private static MissionProfile EpicArcOfferProfile(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        long now
    )
    {
        var history = EpicArcHistory(world, pilot.Id);
        foreach (var arc in EpicArcCatalog.ForAgent(catalog, agent.Id))
        {
            var last = history.FirstOrDefault(row => arc.Nodes.ContainsKey(row.MissionId));
            if (EpicArcRequest(arc, last, agent.Id, now) is { } offer)
            {
                return MissionCatalog.Find(catalog, offer.Id);
            }
        }
        throw new InvalidDataException("This agent has no epic arc assignment for you.");
    }

    private static EpicArcNode? HandoffTarget(CatalogDbContext catalog, EpicArc arc, int missionId) =>
        MissionCatalog.Find(catalog, missionId).Kind == MissionKind.AgentHandoff && arc.Nodes[missionId].Successors is [int successor]
            ? arc.Nodes[successor]
            : null;

    private CatalogAgent EpicArcHandoff(CatalogDbContext catalog, MissionProfile mission, CatalogAgent agent) =>
        HandoffTarget(catalog, EpicArcCatalog.Get(catalog, mission.Definition.EpicArcId!.Value), mission.Definition.MissionId) is { } target
            ? Agent(target.AgentId)
            : agent;

    private SavedMission? ContinueEpicArc(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        SavedMission row,
        int arcId,
        int? chosen,
        long now
    )
    {
        var arc = EpicArcCatalog.Get(catalog, arcId);
        var source = arc.Nodes[row.MissionId];
        if (source.Successors.IsEmpty)
        {
            return null;
        }
        int next = chosen ?? source.Successors.Single();
        var node = arc.Nodes[next];
        var agent = Agent(node.AgentId);
        var profile = MissionCatalog.Find(catalog, next);
        if (
            !profile.AvailableTo(agent)
            || world.Filter<SavedMission>(s => s.PilotId == pilot.Id && s.AgentId == agent.Id && s.ClosedAt == null && s.Id != row.Id).Any()
        )
        {
            throw new InvalidDataException("The next epic arc agent already has an assignment or is unavailable.");
        }
        return Offer(world, catalog, pilot, agent, agent.Level, false, now, profile);
    }

    public IReadOnlyList<EpicArcProgress> ArcStatus(long pilotId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world =>
        {
            List<EpicArcProgress> progress = [];
            var rows = EpicArcHistory(world, pilotId);
            var content = catalog.ReadMany(CatalogData.Client.Missions, rows.Select(row => (long)row.MissionId).Distinct());
            foreach (var run in rows.GroupBy(row => EpicArcCatalog.Find(row.MissionId)!.EpicArcId!.Value))
            {
                var arc = EpicArcCatalog.Get(catalog, run.Key);
                HashSet<int> emitted = [];
                foreach (var row in run)
                {
                    if (emitted.Add(row.MissionId))
                    {
                        bool stopped = row.State is MissionState.Quit or MissionState.Failed or MissionState.Declined;
                        int nameId = checked((int)(content.GetValueOrDefault(row.MissionId)?.NameID ?? 0));
                        progress.Add(
                            new(
                                arc.Id,
                                row.MissionId,
                                stopped ? null : row.AcceptedAt,
                                row.State == MissionState.Completed ? row.ClosedAt : null,
                                stopped ? row.ClosedAt : null,
                                stopped ? 0 : nameId
                            )
                        );
                    }
                    if (arc.Roots.Any(root => root.Id == row.MissionId))
                    {
                        break;
                    }
                }
            }
            return progress;
        });
    }

    public bool EpicArcAgentAvailable(long pilotId, long agentId) =>
        directory.Agent(agentId) is { Type: AgentType.EpicArc } && Access(pilotId, agentId).Allowed;

    public IReadOnlyList<EpicArcMessage> EpicArcMessages()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return EpicArcCatalog.Messages(catalog);
    }

    public long? EpicArcReplayAt(long pilotId, long agentId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world => EpicArcReplayAt(world, catalog, pilotId, agentId, Now));
    }

    private static long? EpicArcReplayAt(WorldAction world, CatalogDbContext catalog, long pilotId, long agentId, long now)
    {
        var history = EpicArcHistory(world, pilotId);
        return EpicArcCatalog
            .ForAgent(catalog, agentId)
            .Where(arc => arc.Roots.Any(root => root.AgentId == agentId))
            .Select(arc =>
                history.FirstOrDefault(row => arc.Nodes.ContainsKey(row.MissionId)) is { } last ? ArcReplayDeadline(arc, last) : null
            )
            .Where(at => at > now)
            .Min();
    }

    public IReadOnlyList<EpicArcChoice> EpicArcChoices(long pilotId, long agentId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect<IReadOnlyList<EpicArcChoice>>(world =>
        {
            var row = world.Filter<SavedMission>(m => m.PilotId == pilotId && m.AgentId == agentId && m.ClosedAt == null).SingleOrDefault();
            if (
                row is not { State: MissionState.Accepted }
                || EpicArcCatalog.Find(row.MissionId)?.EpicArcId is not int arcId
                || MissionCatalog.Find(catalog, row.MissionId).Kind != MissionKind.Choice
            )
            {
                return [];
            }
            return EpicArcCatalog
                .Get(catalog, arcId)
                .Nodes[row.MissionId]
                .Successors.Select(id =>
                {
                    var next = MissionCatalog.Find(catalog, id);
                    return new EpicArcChoice(row.Id, id, next.NameId, next.BriefingId, next.Name);
                })
                .ToArray();
        });
    }

    public MissionUpdate ChooseEpicArc(long pilotId, long agentId, long offerId, int missionId)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Epic arcs require the resident world.");
        return ActResident(live, pilotId, agentId, MissionAction.Complete, offerId, epicChoice: missionId);
    }

    public MissionUpdate? ArriveEpicArcAgent(long pilotId, long agentId)
    {
        if (LiveWorld.Find(database.Path) is not { } live || directory.Agent(agentId) is not { Type: AgentType.EpicArc } agent)
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var handoff = live.Economy.Inspect(world =>
            EpicArcHistory(world, pilotId)
                .FirstOrDefault(row =>
                    row.State == MissionState.Accepted
                    && row.AgentId != agentId
                    && EpicArcCatalog.Find(row.MissionId)?.EpicArcId is int arcId
                    && HandoffTarget(catalog, EpicArcCatalog.Get(catalog, arcId), row.MissionId)?.AgentId == agentId
                    && AtEpicAgent(ResidentPilot(world, pilotId), agent)
                )
        );
        return handoff is null ? null : ActResident(live, pilotId, handoff.AgentId, MissionAction.Complete, handoff.Id);
    }

    private static bool AtEpicAgent(Pilot pilot, CatalogAgent agent) =>
        agent.Space is { } space
            ? pilot.ActiveShip.Position is { } position
                && FixedAgentRules.InRange(space, position.SystemId ?? 0, new(position.X, position.Y, position.Z))
            : pilot.ActiveShip.Position is null && pilot.ActiveShip.DockedLocationId == agent.StationId;

    private static void ApplyEpicArcStandings(
        WorldAction world,
        CatalogDbContext catalog,
        CatalogAgent agent,
        SavedMission row,
        long pilotId,
        int participants,
        long now,
        List<MissionStandingChange> changes
    )
    {
        var profile = MissionCatalog.Find(catalog, row.MissionId);
        if (profile.ArcStandings is not { Count: > 0 } standings)
        {
            return;
        }
        int social = Skill(world, catalog, ResidentPilot(world, pilotId), MissionSocialSkill.Social);
        foreach (var standing in standings)
        {
            long faction = long.Parse(standing.Key, CultureInfo.InvariantCulture);
            double fraction = standing.Value / 100 * (1 + social * 0.05) / participants;
            ApplyMissionStanding(
                world,
                pilotId,
                faction,
                fraction,
                now,
                agent.CorporationId,
                row.SystemId,
                changes,
                missionTitleId: profile.NameId
            );
        }
    }

    private static InventoryChange? GrantEpicArcReward(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        SavedMission row,
        CatalogAgent agent
    )
    {
        if (row.ItemRewardTypeId <= 0 || row.ItemRewardQuantity <= 0)
        {
            return null;
        }
        var type = catalog.RequireItem(row.ItemRewardTypeId);
        var (address, flag) = EpicArcGiftLocation(pilot, agent);
        if (type.CategoryId != (int)InventoryCategory.Blueprint)
        {
            return InventoryPersistence.MergeOutput(
                world,
                catalog,
                row.PilotId,
                address,
                flag,
                row.ItemRewardTypeId,
                row.ItemRewardQuantity
            );
        }
        var item = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = row.PilotId,
                    LocationId = address,
                    FlagId = flag,
                    TypeId = row.ItemRewardTypeId,
                    Quantity = 1,
                    BlueprintRuns = row.ItemRewardQuantity,
                    Singleton = true,
                    MaterialEfficiency = 6,
                    TimeEfficiency = 6,
                }
            )
            .Entity;
        return new(PilotInventoryStore.Read(catalog, item), null);
    }

    private static (long Address, int Flag) EpicArcGiftLocation(Pilot pilot, CatalogAgent agent) =>
        (agent.StationId is int station && (agent.Space is null || pilot.ActiveShip.DockedLocationId == station))
            ? (station, (int)InventoryFlag.Hangar)
            : (pilot.ActiveShipId, (int)InventoryFlag.Cargo);
}
