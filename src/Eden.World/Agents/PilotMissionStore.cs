using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.FactionWarfare;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal sealed record MissionTrackingView(IReadOnlyList<MissionTrackingEntry> Entries, long ExpiresAt);

[Injectable(InjectionType.Singleton)]
internal sealed partial class PilotMissionStore(
    WorldDatabase database,
    INpcDirectory directory,
    TimeProvider clock,
    PilotTrainingStore training,
    ICooperativeFleets fleets
) : IPilotMissions, IPilotMissionContainers, IPilotResearch
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private CatalogAgent Agent(long id) => directory.Agent(id) ?? throw new InvalidDataException("Choose an active NPC agent.");

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.Account).Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == id)
        ?? throw new InvalidDataException("Unknown mission pilot.");

    private static int Skill(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, MissionSocialSkill skill) =>
        Skill(world, catalog, pilot, (int)skill);

    private static int Skill(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, int skill) =>
        Skill(new SqlWorldAction(world), catalog, pilot, skill);

    private static int Skill(WorldAction world, CatalogDbContext catalog, Pilot pilot, MissionSocialSkill skill) =>
        Skill(world, catalog, pilot, (int)skill);

    private static int Skill(WorldAction world, CatalogDbContext catalog, Pilot pilot, int skill)
    {
        int level = PilotSkillReader.Level(world, catalog, pilot.Id, skill);
        if (pilot.Account.CloneGrade == AccountCloneGrade.Alpha)
        {
            var alpha = catalog.Read(
                CatalogData.Client.Clonegrades,
                pilot.RaceId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            int maximum = checked((int)(alpha?.Skills?.FirstOrDefault(s => s.TypeID == skill)?.Level ?? 0));
            level = Math.Min(level, maximum);
        }
        return level;
    }

    public AgentAccess Access(long pilotId, long agentId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return AccessOwned(pilotId, agentId);
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world => Access(world, catalog, ResidentPilot(world, pilotId), Agent(agentId)));
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
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(
            database,
            world =>
            {
                var pilot = ResidentPilot(world, pilotId);
                var agent = Agent(agentId);
                var access = Access(world, catalog, pilot, agent);
                long now = Now;
                // Show Info observes saved deadlines without expiring or creating offers.
                var active = world
                    .Filter<SavedMission>(m =>
                        m.PilotId == pilotId
                        && m.AgentId == agentId
                        && m.ClosedAt == null
                        && m.ExpiresAt > now
                        && (m.State == MissionState.Offered || m.State == MissionState.Accepted)
                    )
                    .SingleOrDefault();
                bool content =
                    agent.Type is AgentType.Basic or AgentType.Event or AgentType.FactionalWarfare
                    && access.Allowed
                    && ProfilesForAgent(catalog, agent, agent.Level, false).Any();
                bool fixedService = agent.Type == AgentType.Event && MissionCatalog.ForFixedAgent(catalog, agent.Id).Any();
                var information = AgentInformationRules.Describe(
                    agent,
                    access,
                    content,
                    active is not null,
                    active?.Storyline == true,
                    fixedService
                );
                if (agent.Type != AgentType.Research)
                {
                    return information;
                }
                var project = world.Filter<SavedResearchProject>(p => p.PilotId == pilotId && p.AgentId == agentId).SingleOrDefault();
                return information with
                {
                    ResearchFields = ResearchFields(catalog, agentId),
                    Research = project is null
                        ? null
                        : new(
                            project.Id,
                            agent,
                            project.SkillId,
                            ResearchRules.Accrue(project.Points, project.PointsPerDay, project.SettledAt, now),
                            project.PointsPerDay
                        ),
                };
            }
        );
    }

    private static AgentAccess Access(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent) =>
        Access(new SqlWorldAction(world), catalog, pilot, agent);

    private static AgentAccess Access(WorldAction world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent)
    {
        bool fixedAgent = agent.Type == AgentType.Event && MissionCatalog.ForFixedAgent(catalog, agent.Id).Any();
        if (
            (agent.Type is not (AgentType.Basic or AgentType.Research or AgentType.FactionalWarfare) && !fixedAgent)
            || agent.Level is < 1 or > MissionRules.MaximumAgentLevel
        )
        {
            return new(false, "This special agent's mission service is not implemented.", 0, 0, 0, 0);
        }
        if (
            fixedAgent
            && (
                world is not EconomyTransaction
                || agent.Space is null
                || world.Filter<SavedMission>(m => m.PilotId == pilot.Id && m.AgentId == agent.Id && m.ClosedAt != null).Any()
            )
        )
        {
            return new(false, "This one-time agent assignment is unavailable or has already ended.", 0, 0, 0, 0);
        }
        if (
            agent.Type == AgentType.FactionalWarfare
            && (world is not EconomyTransaction || (int?)MilitiaEnlistmentStore.Effective(world, pilot) != agent.FactionId)
        )
        {
            return new(false, "Enlist in this agent's militia to request missions.", 0, 0, 0, 0);
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
        if (fixedAgent)
        {
            return FixedAgentRules.Access(
                agent.Level,
                Effective(agent.Id),
                Effective(agent.CorporationId),
                agent.FactionId is int fixedFaction ? Effective(fixedFaction) : 0,
                pilot.Account.CloneGrade == AccountCloneGrade.Omega
            );
        }
        return agent.Type == AgentType.Research
            ? ResearchRules.Access(
                agent.Level,
                Effective(agent.Id),
                Effective(agent.CorporationId),
                agent.FactionId is int researchFaction ? Effective(researchFaction) : 0,
                pilot.Account.CloneGrade == AccountCloneGrade.Omega
            )
            : MissionRules.Access(
                agent.Level,
                Effective(agent.Id),
                Effective(agent.CorporationId),
                agent.FactionId is int faction ? Effective(faction) : 0,
                pilot.Account.CloneGrade == AccountCloneGrade.Omega
            );
    }

    public IReadOnlyList<MissionTrackingEntry> Tracking(long pilotId) =>
        LiveWorld.Execute(database.Path, () => TrackingOwned(pilotId), nameof(Tracking));

    private IReadOnlyList<MissionTrackingEntry> TrackingOwned(long pilotId)
    {
        var reads = LiveReadModels.Find(database.Path);
        return (reads?.MissionTracking(pilotId, Now, () => LoadTracking(pilotId)) ?? LoadTracking(pilotId)).Entries;
    }

    private sealed record MissionTrackingTargetRow(long MissionId, int TypeId, long Id, double HomeX, double HomeY, double HomeZ);

    private MissionTrackingView LoadTracking(long pilotId)
    {
        using WorldActionScope action = new(database, new(pilots: [pilotId]));
        var world = action.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = ResidentPilot(world, pilotId);
        long now = Now;
        var rows = world
            .Filter<SavedMission>(mission => mission.PilotId == pilotId && mission.ClosedAt == null)
            .OrderBy(mission => mission.Id)
            .ToArray();
        var active = rows.Where(mission => !Expire(world, mission, now)).ToArray();
        long expires = active.Select(mission => mission.ExpiresAt).DefaultIfEmpty(long.MaxValue).Min();
        var offers = active
            .Where(mission => mission.State == MissionState.Accepted)
            .Select(mission => Snapshot(world, catalog, pilot, mission))
            .ToArray();
        var entries = TrackingEntries(world, catalog, offers);
        action.Commit();
        return new(entries, expires);
    }

    private static IReadOnlyList<MissionTrackingEntry> TrackingEntries(
        WorldAction world,
        CatalogDbContext catalog,
        IReadOnlyList<MissionOffer> offers
    )
    {
        if (offers.Count == 0)
        {
            return [];
        }
        var npcs = world.Set<WorldNpc>();
        var targets = offers
            .SelectMany(offer =>
            {
                long mission = offer.Id;
                var members = world
                    .Filter<SavedMissionMember>(member => member.MissionId == mission && member.CompletesMission)
                    .Select(member => member.NpcId)
                    .ToArray();
                var structures = world
                    .Filter<SavedMissionStructure>(structure =>
                        structure.MissionId == mission
                        && (structure.Required || structure.CompletesMission)
                        && structure.DestroyedAt == null
                    )
                    .Select(structure => structure.NpcId)
                    .ToArray();
                return members
                    .Concat(structures)
                    .Select(id => npcs.Find(id))
                    .OfType<WorldNpc>()
                    .Select(npc => new MissionTrackingTargetRow(
                        mission,
                        npc.TypeId,
                        npc.Id,
                        npc.HomeX ?? npc.X,
                        npc.HomeY ?? npc.Y,
                        npc.HomeZ ?? npc.Z
                    ));
            })
            .ToLookup(target => target.MissionId);
        return Array.AsReadOnly(
            offers
                .Select(offer => new MissionTrackingEntry(
                    offer,
                    Array.AsReadOnly(
                        MissionCatalog.Find(catalog, offer.MissionId).Rooms.Select(room => offer.Position + room.Position).ToArray()
                    )
                )
                {
                    Targets = Array.AsReadOnly(
                        targets[offer.Id]
                            .OrderBy(target => target.Id)
                            .Select(target => new MissionTrackingTarget(
                                target.TypeId,
                                target.Id,
                                new(target.HomeX, target.HomeY, target.HomeZ)
                            ))
                            .ToArray()
                    ),
                })
                .ToArray()
        );
    }

    public IReadOnlyList<MissionOffer> Journal(long pilotId, long? agentId = null)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return JournalOwned(pilotId, agentId);
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Read(
            world =>
            {
                var pilot = ResidentPilot(world, pilotId);
                var rows = world
                    .Filter<SavedMission>(m => m.PilotId == pilotId && m.ClosedAt == null && (agentId == null || m.AgentId == agentId))
                    .OrderBy(m => m.Id)
                    .ToArray();
                long now = Now;
                return rows.Where(m => !Expire(world, m, now)).Select(m => Snapshot(world, catalog, pilot, m)).ToArray();
            },
            new(pilots: [pilotId])
        );
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
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return CurrentOwned(pilotId, agentId);
        }
        _ = Agent(agentId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Read(
            world =>
            {
                var row = world
                    .Filter<SavedMission>(m => m.PilotId == pilotId && m.AgentId == agentId)
                    .OrderByDescending(m => m.Id)
                    .FirstOrDefault();
                if (row is null)
                {
                    return null;
                }
                Expire(world, row, Now);
                return Snapshot(world, catalog, ResidentPilot(world, pilotId), row);
            },
            new(pilots: [pilotId])
        );
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
        return LiveWorld.Find(database.Path) is { } live
            ? ActResident(live, pilotId, agentId, action, offerId)
            : ActOwned(pilotId, agentId, action, offerId);
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
            var result = Act(new SqlWorldAction(world), catalog, pilot, agent, action, offerId, now, []);
            transaction.Commit();
            return result;
        }
    }

    private MissionUpdate Act(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        MissionAction action,
        long? offerId,
        long now,
        IReadOnlyList<long> sharers
    )
    {
        long pilotId = pilot.Id;
        long agentId = agent.Id;
        if (world is not EconomyTransaction && (agent.Type == AgentType.FactionalWarfare || MissionCatalog.FixedAgentIds.Contains(agentId)))
        {
            throw new InvalidDataException("One-time and militia agent actions require the resident world.");
        }
        var row = world.Set<SavedMission>().SingleOrDefault(m => m.PilotId == pilotId && m.AgentId == agentId && m.ClosedAt == null);
        if (row is not null && Expire(world, row, now))
        {
            world.SaveChanges();
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
        SavedMission? continuation = null;
        switch (action)
        {
            case MissionAction.Request:
                if (row is null)
                {
                    RequireAgentAccess(world, catalog, pilot, agent);
                    RequireAgentLocation(pilot, agent);
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
                RequireAgentLocation(pilot, agent);
                var profile = MissionCatalog.Find(catalog, row.MissionId);
                if (!profile.AvailableTo(agent) || profile.AttacksFaction(agent.FactionId))
                {
                    throw new InvalidDataException("This mission is unavailable to this agent's corporation or faction.");
                }
                RequireChainAcceptance(world, catalog, agent, row, profile);
                RequireFixedAssignment(world, catalog, row, profile);
                RequireResidentReward(world, row, profile);
                row.State = MissionState.Accepted;
                row.AcceptedAt = now;
                row.ExpiresAt = checked(now + profile.Lifetime.Ticks);
                if (profile.Kind == MissionKind.Courier)
                {
                    changes.Add(CreateCourierCargo(world, catalog, pilotId, agent.StationId!.Value, profile));
                }
                if (profile.Kind == MissionKind.Mining)
                {
                    SeedMiningDeposits(world, row, profile);
                }
                SeedContainers(world, catalog, row, profile);
                SeedStructures(world, catalog, row, profile, now);
                eventName = "accepted";
                break;
            case MissionAction.Complete:
                RequireState(row, MissionState.Accepted);
                var mission = MissionCatalog.Find(catalog, row!.MissionId);
                RequireChainAcceptance(world, catalog, agent, row, mission);
                RequireFixedAssignment(world, catalog, row, mission);
                RequireResidentReward(world, row, mission);
                if (mission.Kind == MissionKind.Courier)
                {
                    RequireStation(pilot, row.DestinationStation);
                }
                else if (agent.Type == AgentType.FactionalWarfare)
                {
                    if ((int?)MilitiaEnlistmentStore.Effective(world, pilot) != agent.FactionId || pilot.ActiveShip.Position is not null)
                    {
                        throw new InvalidDataException("Dock while enlisted in the issuing militia to complete the mission.");
                    }
                }
                else
                {
                    RequireAgentLocation(pilot, agent);
                }
                if (mission.Kind is MissionKind.Combat or MissionKind.Mining && !row.ObjectiveComplete)
                {
                    throw new InvalidDataException(
                        mission.Kind == MissionKind.Mining
                            ? "Mine the required deposits in your mission pocket first."
                            : "The mission's combat objective is not complete."
                    );
                }
                if (mission.CargoType > 0)
                {
                    ConsumeCargo(world, catalog, pilot, row, mission, changes);
                }
                payment = Pay(world, pilot, agent, row, now, standings, sharers);
                if (GrantItemReward(world, catalog, row, agent.StationId) is { } rewardChange)
                {
                    changes.Add(rewardChange);
                }
                Close(world, row, MissionState.Completed, now);
                if (!row.Storyline && agent.Type != AgentType.FactionalWarfare)
                {
                    Progress(world, catalog, pilot, agent, row.Level, now);
                }
                continuation = ContinueChain(world, catalog, pilot, agent, row, mission, now);
                eventName = "completed";
                break;
            case MissionAction.Decline:
                RequireState(row, MissionState.Offered);
                var decline =
                    world.Set<SavedAgentDecline>().Find(pilotId, agentId)
                    ?? world.Add(new SavedAgentDecline { PilotId = pilotId, AgentId = agentId }).Entity;
                bool free = decline.LastFreeDeclineAt == 0 || now >= decline.LastFreeDeclineAt + MissionRules.FreeDeclineInterval.Ticks;
                if (!free)
                {
                    if (MilitiaMissionCatalog.Issuer(row!.MissionId) is not { } issuer)
                    {
                        throw new InvalidDataException("Wait four hours after your last free decline before declining another offer.");
                    }
                    MilitiaProgressionStore.AdjustStanding(
                        world,
                        pilot,
                        issuer,
                        -MilitiaProgressionRules.MissionFailureFraction,
                        StandingEvent.AgentMissionDeclined,
                        row.SystemId,
                        now,
                        standings
                    );
                }
                else
                {
                    decline.LastFreeDeclineAt = now;
                }
                Close(world, row!, MissionState.Declined, now);
                eventName = "offer_declined";
                break;
            case MissionAction.Quit:
                RequireState(row, MissionState.Accepted);
                if (MilitiaMissionCatalog.Issuer(row!.MissionId) is { } quitIssuer)
                {
                    MilitiaProgressionStore.AdjustStanding(
                        world,
                        pilot,
                        quitIssuer,
                        -MilitiaProgressionRules.MissionFailureFraction,
                        StandingEvent.AgentMissionFailed,
                        row.SystemId,
                        now,
                        standings
                    );
                }
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
        return new(snapshot, eventName, changes, standings, payment?.BalanceCents, payment?.PointsBefore ?? 0, payment?.PointsAfter ?? 0)
        {
            Continuation = continuation is null ? null : Snapshot(world, catalog, pilot, continuation),
        };
    }

    private static void RequireState(SavedMission? mission, MissionState state)
    {
        if (mission?.State != state)
        {
            throw new InvalidDataException("The mission is not in the required state.");
        }
    }

    private static void RequireAgentAccess(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent) =>
        RequireAgentAccess(new SqlWorldAction(world), catalog, pilot, agent);

    private static void RequireAgentAccess(WorldAction world, CatalogDbContext catalog, Pilot pilot, CatalogAgent agent)
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

    private static void RequireAgentLocation(Pilot pilot, CatalogAgent agent)
    {
        if (agent.Space is not { } space)
        {
            RequireStation(pilot, agent.StationId);
            return;
        }
        var position = pilot.ActiveShip.Position;
        if (position is null || !FixedAgentRules.InRange(space, position.SystemId ?? 0, new(position.X, position.Y, position.Z)))
        {
            throw new InvalidDataException("Approach this agent in its solar system to exchange mission cargo.");
        }
    }

    private MissionOffer Snapshot(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, SavedMission row) =>
        Snapshot(new SqlWorldAction(world), catalog, pilot, row);

    private MissionOffer Snapshot(WorldAction world, CatalogDbContext catalog, Pilot pilot, SavedMission row)
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
            || (
                profile.Kind is MissionKind.Courier or MissionKind.Trade
                    ? hasCargo
                    : row.ObjectiveComplete && (profile.CargoType == 0 || hasCargo)
            );
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
            Bonus = Bonus(row),
            EnemyFactionId = profile.EnemyFaction,
            HasShipRestrictions = profile
                .Rooms.SelectMany(r => r.Gates)
                .Any(g => g.Access.AllowedShips is not null || g.Access.AllowedRaces.Count > 0),
            ItemReward = row.ItemRewardTypeId > 0 && row.ItemRewardQuantity > 0 ? new(row.ItemRewardTypeId, row.ItemRewardQuantity) : null,
            Retrieval =
                profile.Kind != MissionKind.Courier && profile.CargoType > 0
                    ? new(
                        profile.CargoType,
                        profile.CargoQuantity,
                        profile.CargoVolume,
                        hasCargo || row.State == MissionState.Completed,
                        agent.Space?.TypeId ?? catalog.RequireStation(agent.StationId!.Value).TypeId
                    )
                    : null,
        };
    }

    private static MissionBonus? Bonus(SavedMission row) =>
        row.BonusIskCents > 0 && row.BonusWindowTicks > 0
            ? new(
                row.BonusIskCents,
                row.BonusWindowTicks,
                row.AcceptedAt is long accepted ? checked(accepted + row.BonusWindowTicks) : null
            )
            : null;

    private static OwnedItem[] Cargo(WorldAction world, Pilot pilot, SavedMission row, MissionProfile profile) =>
        world
            .Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == pilot.Id
                && i.TypeId == profile.CargoType
                && i.Singleton == profile.CargoSingleton
                && i.Quantity > 0
                && (!i.Singleton || i.Quantity == 1)
                && (
                    i.LocationId == row.DestinationStation && i.FlagId == (int)InventoryFlag.Hangar
                    || i.LocationId == pilot.ActiveShipId && i.FlagId == (int)InventoryFlag.Cargo
                    || profile.Definition.Mining
                        && i.LocationId == pilot.ActiveShipId
                        && (
                            i.FlagId == (int)InventoryFlag.GeneralMiningHold
                            || i.FlagId == (int)InventoryFlag.GasHold
                            || i.FlagId == (int)InventoryFlag.IceHold
                        )
                )
            )
            .OrderBy(i => i.Id)
            .ToArray();

    private static void ConsumeCargo(
        WorldAction world,
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
