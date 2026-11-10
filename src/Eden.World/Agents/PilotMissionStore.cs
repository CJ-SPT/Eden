using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

[Injectable(InjectionType.Singleton)]
internal sealed partial class PilotMissionStore(
    WorldDatabase database,
    INpcDirectory directory,
    TimeProvider clock,
    PilotTrainingStore training
) : IPilotMissions, IPilotMissionContainers
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private CatalogAgent Agent(long id) => directory.Agent(id) ?? throw new InvalidDataException("Choose an active NPC agent.");

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.Account).Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == id)
        ?? throw new InvalidDataException("Unknown mission pilot.");

    private static int Skill(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, MissionSocialSkill skill)
    {
        int level = PilotSkillReader.Level(world, catalog, pilot.Id, (int)skill);
        if (pilot.Account.CloneGrade == AccountCloneGrade.Alpha)
        {
            var alpha = catalog.Read(
                CatalogData.Client.Clonegrades,
                pilot.RaceId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            int maximum = checked((int)(alpha?.Skills?.FirstOrDefault(s => s.TypeID == (int)skill)?.Level ?? 0));
            level = Math.Min(level, maximum);
        }
        return level;
    }

    public AgentAccess Access(long pilotId, long agentId)
    {
        return LiveWorld.Execute(database.Path, () => AccessOwned(pilotId, agentId), nameof(Access));
    }

    private AgentAccess AccessOwned(long pilotId, long agentId)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            return Access(world, catalog, Pilot(world, pilotId), Agent(agentId));
        }
    }

    public AgentServiceInformation Information(long pilotId, long agentId)
    {
        return LiveWorld.Execute(database.Path, () => InformationOwned(pilotId, agentId), nameof(Information));
    }

    private AgentServiceInformation InformationOwned(long pilotId, long agentId)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            var pilot = Pilot(world, pilotId);
            var agent = Agent(agentId);
            var access = Access(world, catalog, pilot, agent);
            long now = Now;
            // Show Info observes saved deadlines without expiring or creating offers.
            var active = world
                .Set<SavedMission>()
                .AsNoTracking()
                .SingleOrDefault(m =>
                    m.PilotId == pilotId
                    && m.AgentId == agentId
                    && m.ClosedAt == null
                    && m.ExpiresAt > now
                    && (m.State == MissionState.Offered || m.State == MissionState.Accepted)
                );
            bool content = agent.Type == AgentType.Basic && access.Allowed && ProfilesForAgent(catalog, agent, agent.Level, false).Any();
            return AgentInformationRules.Describe(agent, access, content, active is not null, active?.Storyline == true);
        }
    }

    private static AgentAccess Access(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent)
    {
        if (agent.Type != AgentType.Basic || agent.Level is < 1 or > 5)
        {
            return new(false, "This special agent's mission service is not implemented.", 0, 0, 0, 0);
        }
        // Pinned standingUtil.GetStandingBonusSkillTypeID and appConst.factionsPirates.
        var factionKind = (AgentStandingFaction?)agent.FactionId;
        bool pirate =
            factionKind
            is AgentStandingFaction.Guristas
                or AgentStandingFaction.AngelCartel
                or AgentStandingFaction.BloodRaiders
                or AgentStandingFaction.Sansha
                or AgentStandingFaction.Serpentis
                or AgentStandingFaction.Deathless;
        bool bonuses =
            factionKind
            is not (
                AgentStandingFaction.Drifters
                or AgentStandingFaction.RogueDrones
                or AgentStandingFaction.Triglavian
                or AgentStandingFaction.Edencom
            );
        int connections = Skill(world, catalog, pilot, MissionSocialSkill.Connections);
        int diplomacy = Skill(world, catalog, pilot, MissionSocialSkill.Diplomacy);
        int criminal = Skill(world, catalog, pilot, MissionSocialSkill.CriminalConnections);
        double Effective(long source) =>
            MissionRules.EffectiveStanding(
                world.Set<SavedNpcStanding>().Find(pilot.Id, source)?.Value ?? 0,
                connections,
                diplomacy,
                criminal,
                pirate,
                bonuses
            );
        return MissionRules.Access(
            agent.Level,
            Effective(agent.Id),
            Effective(agent.CorporationId),
            agent.FactionId is int faction ? Effective(faction) : 0,
            pilot.Account.CloneGrade == AccountCloneGrade.Omega
        );
    }

    public IReadOnlyList<MissionOffer> Journal(long pilotId, long? agentId = null)
    {
        return LiveWorld.Execute(database.Path, () => JournalOwned(pilotId, agentId), nameof(Journal));
    }

    private IReadOnlyList<MissionOffer> JournalOwned(long pilotId, long? agentId = null)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            var pilot = Pilot(world, pilotId);
            var rows = world
                .Set<SavedMission>()
                .Where(m => m.PilotId == pilotId && m.ClosedAt == null && (agentId == null || m.AgentId == agentId))
                .OrderBy(m => m.Id)
                .ToArray();
            foreach (var row in rows)
            {
                Expire(world, row, Now);
            }
            world.SaveChanges();
            transaction.Commit();
            return rows.Where(m => m.ClosedAt is null).Select(m => Snapshot(world, catalog, pilot, m)).ToArray();
        }
    }

    public MissionOffer? Current(long pilotId, long agentId)
    {
        return LiveWorld.Execute(database.Path, () => CurrentOwned(pilotId, agentId), nameof(Current));
    }

    private MissionOffer? CurrentOwned(long pilotId, long agentId)
    {
        _ = Agent(agentId);
        _ = Journal(pilotId, agentId);
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            var pilot = Pilot(world, pilotId);
            var row = world
                .Set<SavedMission>()
                .Where(m => m.PilotId == pilotId && m.AgentId == agentId)
                .OrderByDescending(m => m.Id)
                .FirstOrDefault();
            return row is null ? null : Snapshot(world, catalog, pilot, row);
        }
    }

    public MissionUpdate Act(long pilotId, long agentId, MissionAction action, long? offerId = null)
    {
        return LiveWorld.Execute(database.Path, () => ActOwned(pilotId, agentId, action, offerId), nameof(Act));
    }

    private MissionUpdate ActOwned(long pilotId, long agentId, MissionAction action, long? offerId = null)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            var agent = Agent(agentId);
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            long now = Now;
            training.Advance(world, pilotId, now);
            var pilot = Pilot(world, pilotId);
            var row = world.Set<SavedMission>().SingleOrDefault(m => m.PilotId == pilotId && m.AgentId == agentId && m.ClosedAt == null);
            if (row is not null && Expire(world, row, now))
            {
                world.SaveChanges();
                transaction.Commit();
                return new(Snapshot(world, catalog, pilot, row), "failed", [], []);
            }
            if (offerId.HasValue && (row is null || row.Id != offerId))
            {
                throw new InvalidDataException("This mission action is stale. Reopen the agent conversation.");
            }
            List<InventoryChange> changes = [];
            List<MissionStandingChange> standings = [];
            string eventName = "";
            MissionPayment? payment = null;
            switch (action)
            {
                case MissionAction.Request:
                    if (row is null)
                    {
                        RequireAgentAccess(world, catalog, pilot, agent);
                        RequireStation(pilot, agent.StationId);
                        row = Offer(world, catalog, pilot, agent, agent.Level, false, now);
                        eventName = "offered";
                    }
                    break;
                case MissionAction.Accept:
                    RequireState(row, MissionState.Offered);
                    if (!row!.Storyline)
                    {
                        RequireAgentAccess(world, catalog, pilot, agent);
                    }
                    RequireStation(pilot, agent.StationId);
                    var profile = MissionCatalog.Find(catalog, row.MissionId);
                    if (!profile.AvailableTo(agent) || profile.AttacksFaction(agent.FactionId))
                    {
                        throw new InvalidDataException("This mission is unavailable to this agent's corporation or faction.");
                    }
                    row.State = MissionState.Accepted;
                    row.AcceptedAt = now;
                    row.ExpiresAt = checked(now + MissionRules.MissionLifetime.Ticks);
                    if (profile.Kind == MissionKind.Courier)
                    {
                        changes.Add(
                            InventoryPersistence.MergeOutput(
                                world,
                                catalog,
                                pilotId,
                                agent.StationId!.Value,
                                (int)InventoryFlag.Hangar,
                                profile.CargoType,
                                profile.CargoQuantity
                            )
                        );
                    }
                    SeedContainers(world, catalog, row, profile);
                    SeedStructures(world, catalog, row, profile, now);
                    eventName = "accepted";
                    break;
                case MissionAction.Complete:
                    RequireState(row, MissionState.Accepted);
                    var mission = MissionCatalog.Find(catalog, row!.MissionId);
                    RequireStation(pilot, mission.Kind == MissionKind.Courier ? row.DestinationStation : agent.StationId);
                    if (mission.Kind == MissionKind.Courier)
                    {
                        ConsumeCargo(world, catalog, pilot, row, mission, changes);
                    }
                    else if (!row.ObjectiveComplete)
                    {
                        throw new InvalidDataException("The mission's combat objective is not complete.");
                    }
                    if (mission.Kind == MissionKind.Combat && mission.CargoType > 0)
                    {
                        ConsumeCargo(world, catalog, pilot, row, mission, changes);
                    }
                    payment = Pay(world, pilot, agent, row, now, standings);
                    Close(world, row, MissionState.Completed, now);
                    if (!row.Storyline)
                    {
                        Progress(world, catalog, pilot, agent, row.Level, now);
                    }
                    eventName = "completed";
                    break;
                case MissionAction.Decline:
                    RequireState(row, MissionState.Offered);
                    var decline =
                        world.Set<SavedAgentDecline>().Find(pilotId, agentId)
                        ?? world.Add(new SavedAgentDecline { PilotId = pilotId, AgentId = agentId }).Entity;
                    if (decline.LastFreeDeclineAt > 0 && now < decline.LastFreeDeclineAt + MissionRules.FreeDeclineInterval.Ticks)
                    {
                        throw new InvalidDataException("Wait four hours after your last free decline before declining another offer.");
                    }
                    decline.LastFreeDeclineAt = now;
                    Close(world, row!, MissionState.Declined, now);
                    eventName = "offer_declined";
                    break;
                case MissionAction.Quit:
                    RequireState(row, MissionState.Accepted);
                    Close(world, row!, MissionState.Quit, now);
                    eventName = "quit";
                    break;
                case MissionAction.View:
                case MissionAction.Defer:
                    break;
                default:
                    throw new InvalidDataException("Unknown mission action.");
            }
            world.SaveChanges();
            // Generated inventory IDs and mission IDs must be visible in the reply.
            var snapshot = row is null ? null : Snapshot(world, catalog, pilot, row);
            transaction.Commit();
            return new(
                snapshot,
                eventName,
                changes,
                standings,
                payment?.BalanceCents,
                payment?.PointsBefore ?? 0,
                payment?.PointsAfter ?? 0
            );
        }
    }

    private static void RequireState(SavedMission? mission, MissionState state)
    {
        if (mission?.State != state)
        {
            throw new InvalidDataException("The mission is not in the required state.");
        }
    }

    private static void RequireAgentAccess(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent)
    {
        var access = Access(world, catalog, pilot, agent);
        if (!access.Allowed)
        {
            throw new InvalidDataException(access.Reason);
        }
    }

    private static void RequireStation(Pilot pilot, int? station)
    {
        if (station is null || pilot.ActiveShip.Position is not null || pilot.ActiveShip.DockedLocationId != station)
        {
            throw new InvalidDataException("Dock at the mission's required station to perform this action.");
        }
    }

    private MissionOffer Snapshot(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, SavedMission row)
    {
        var profile = MissionCatalog.Find(catalog, row.MissionId);
        var agent = Agent(row.AgentId);
        MissionCargo? cargo =
            profile.Kind == MissionKind.Courier
                ? new(profile.CargoType, profile.CargoQuantity, profile.CargoVolume, agent.StationId!.Value, row.DestinationStation)
                {
                    PickupTypeId = catalog.FindStation(agent.StationId.Value)!.TypeId,
                    DestinationTypeId = catalog.FindStation(row.DestinationStation)!.TypeId,
                    PickupSystemId = agent.SystemId!.Value,
                }
                : null;
        bool hasCargo = profile.CargoType > 0 && Cargo(world, pilot, row, profile).Sum(i => (long)i.Quantity) >= profile.CargoQuantity;
        bool complete =
            row.State == MissionState.Completed
            || (profile.Kind == MissionKind.Courier ? hasCargo : row.ObjectiveComplete && (profile.CargoType == 0 || hasCargo));
        return new(
            row.Id,
            row.MissionId,
            profile.Name,
            profile.Briefing,
            agent,
            row.Level,
            profile.Kind,
            row.State,
            row.OfferedAt,
            row.AcceptedAt,
            row.ExpiresAt,
            new(row.IskCents, row.LoyaltyPoints, row.CorporationFraction, row.FactionFraction),
            row.Storyline,
            profile.DungeonId,
            row.SystemId,
            new(row.X, row.Y, row.Z),
            complete,
            cargo
        )
        {
            NameId = profile.NameId,
            BriefingId = profile.BriefingId,
            PilotId = row.PilotId,
            CombatComplete = row.ObjectiveComplete,
            Retrieval =
                profile.Kind == MissionKind.Combat && profile.CargoType > 0
                    ? new(
                        profile.CargoType,
                        profile.CargoQuantity,
                        profile.CargoVolume,
                        hasCargo || row.State == MissionState.Completed,
                        catalog.RequireStation(agent.StationId!.Value).TypeId
                    )
                    : null,
        };
    }

    private static OwnedItem[] Cargo(WorldDbContext world, Pilot pilot, SavedMission row, MissionProfile profile) =>
        world
            .Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == pilot.Id
                && i.TypeId == profile.CargoType
                && !i.Singleton
                && i.Quantity > 0
                && (
                    i.LocationId == row.DestinationStation && i.FlagId == (int)InventoryFlag.Hangar
                    || i.LocationId == pilot.ActiveShipId && i.FlagId == (int)InventoryFlag.Cargo
                )
            )
            .OrderBy(i => i.Id)
            .ToArray();

    private static void ConsumeCargo(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        SavedMission row,
        MissionProfile profile,
        List<InventoryChange> changes
    )
    {
        var items = Cargo(world, pilot, row, profile);
        if (items.Sum(i => (long)i.Quantity) < profile.CargoQuantity)
        {
            throw new InvalidDataException("Deliver the required mission cargo in your ship or personal destination hangar.");
        }
        int remaining = profile.CargoQuantity;
        foreach (var item in items)
        {
            if (remaining == 0)
            {
                break;
            }
            var previous = PilotInventoryStore.Read(catalog, item);
            int amount = Math.Min(item.Quantity, remaining);
            item.Quantity -= amount;
            remaining -= amount;
            changes.Add(new(previous with { Quantity = item.Quantity }, previous));
            if (item.Quantity == 0)
            {
                world.Remove(item);
            }
        }
    }
}
