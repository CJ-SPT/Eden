using System.Globalization;
using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Industry;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed partial class CapsuleerInspection(WorldDatabase database, CatalogProvider catalog, IHumanPresence presence)
    : ICapsuleerInspection
{
    private const int PageSize = 40;

    public CapsuleerDirectoryPage Browse(CapsuleerDirectory directory, string? search = null, int page = 0)
    {
        var candidates =
            Inspect(
                (_, world) =>
                    directory switch
                    {
                        CapsuleerDirectory.Bots => BotDirectory(world),
                        CapsuleerDirectory.Corporations => world
                            .Set<SavedCorporation>()
                            .Project(
                                row => new CapsuleerDirectoryEntry(row.Id, row.Name, row.Ticker, null),
                                row => row.ClosedAt == null && row.CapsuleerFoundingSystemId != null
                            )
                            .ToArray(),
                        CapsuleerDirectory.Alliances => AllianceDirectory(world),
                        _ => throw new ArgumentOutOfRangeException(nameof(directory)),
                    }
            ) ?? [];
        string needle = search?.Trim() ?? "";
        var matches = candidates
            .Where(entry =>
                needle.Length == 0
                || entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (entry.Ticker?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || entry.Id.ToString(CultureInfo.InvariantCulture).Contains(needle, StringComparison.Ordinal)
            )
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Id)
            .ToArray();
        int index = Math.Clamp(page, 0, Math.Max(0, matches.Length - 1) / PageSize);
        return new(matches.Skip(index * PageSize).Take(PageSize).ToArray(), index, PageSize, matches.Length);
    }

    private static CapsuleerDirectoryEntry[] BotDirectory(WorldAction world)
    {
        var pilots = (EconomySet<Pilot, EconomyPilot>)world.Pilots;
        return world
            .Set<SavedCapsuleerAgent>()
            .Project(agent => new { agent.PilotId, agent.Career }, null)
            .Select(agent => new CapsuleerDirectoryEntry(
                agent.PilotId,
                pilots.ReadSnapshot(new(agent.PilotId))?.Name ?? $"Pilot {agent.PilotId}",
                Career: agent.Career
            ))
            .ToArray();
    }

    private static CapsuleerDirectoryEntry[] AllianceDirectory(WorldAction world)
    {
        var affiliated = world
            .Set<SavedCorporation>()
            .Project(row => row.Id, row => row.ClosedAt == null && row.CapsuleerFoundingSystemId != null)
            .Select(id => world.Set<SavedAllianceMember>().Find(id)?.AllianceId)
            .OfType<int>()
            .ToHashSet();
        return world
            .Set<SavedAlliance>()
            .Project(
                row => new
                {
                    row.Id,
                    row.Name,
                    row.Ticker,
                    row.CreatorId,
                },
                row => row.ClosedAt == null
            )
            .Where(row => affiliated.Contains(row.Id) || IsBot(world, row.CreatorId))
            .Select(row => new CapsuleerDirectoryEntry(row.Id, row.Name, row.Ticker))
            .ToArray();
    }

    public CapsuleerPilotInspection? Pilot(long id)
    {
        var activity = presence.Read();
        // Resolve the static catalog before taking the resident owner.
        var stations = catalog.Stations;
        var npcCorporations = catalog.NpcCorporations;
        return Inspect<CapsuleerPilotInspection>(
            (live, world) =>
            {
                if (world.Set<SavedCapsuleerAgent>().Find(id) is not { } agent || world.Pilots.Find(id) is not { } pilot)
                {
                    return null;
                }
                var ship = pilot.ActiveShip;
                var organization = CapsuleerCorporations.Organization(world, id);
                var career = live.CapsuleerWorkerChanges.TryGetValue(id, out var worker)
                    ? worker
                    : organization.ActiveWorker ?? agent.Career;
                var miner = career == CapsuleerCareer.Mining ? Miner(live, world, id) : null;
                var hauler = career == CapsuleerCareer.Hauling ? Hauler(live, world, id) : null;
                var job = live.CapsuleerJobs.GetValueOrDefault(id);
                var savedJob = job is null ? world.Set<SavedCapsuleerJob>().Find(id) : null;
                long? due = job?.DueTicks ?? savedJob?.DueTicks;
                var suspended = world.Set<SavedCapsuleerSuspension>().Find(id);
                long elapsed = suspended?.StartedTicks ?? Math.Max(activity.ElapsedTicks, world.ActiveTicks);
                var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
                var alliance = MemberAlliance(world, pilot.CorporationId);
                int system = ship.Position?.SystemId ?? (ship.StructureId is long structure ? world.StructureSystemId(structure) : 0);
                if (system <= 0)
                {
                    system = stations.GetValueOrDefault(ship.StationId)?.SolarSystemId ?? agent.CommunitySystemId;
                }
                return new(
                    id,
                    pilot.Name,
                    agent.Career,
                    career,
                    agent.CommunitySystemId,
                    system,
                    ship.Position is null ? ship.DockedLocationId : null,
                    ship.TypeId,
                    ship.Name,
                    world.Set<PilotWallet>().Find(id)?.Cents ?? 0,
                    suspended is not null,
                    activity.Active,
                    Work(miner, hauler, organization),
                    miner?.WaitingReason ?? hauler?.WaitingReason ?? hauler?.MilitiaWaitingReason,
                    job?.Action ?? savedJob?.Action,
                    due.HasValue ? TimeSpan.FromTicks(Math.Max(0, due.Value - elapsed)) : null,
                    miner?.CareerPlan ?? hauler?.CareerPlan,
                    organization.PvpFitGoal?.HullTypeId,
                    PilotTrainingStore.ReadFreePoints(world, id),
                    world
                        .Filter<QueuedLevel>(row => row.PilotId == id)
                        .OrderBy(row => row.Position)
                        .Select(row => new CapsuleerSkillInspection(row.TypeId, row.Level, FileTime(row.StartTime), FileTime(row.EndTime)))
                        .ToArray(),
                    corporation is { ClosedAt: null }
                        ? CorporationLink(corporation)
                        : new(
                            pilot.CorporationId,
                            npcCorporations.GetValueOrDefault(pilot.CorporationId) ?? $"Corporation {pilot.CorporationId}",
                            "",
                            false
                        ),
                    alliance is null ? null : AllianceLink(alliance, IsInspectable(world, alliance)),
                    organization.OperatingStationId ?? miner?.OperatingStationId,
                    organization.ApplicationId,
                    organization.Prospect?.CorporationId
                );
            }
        );
    }

    public CapsuleerCorporationInspection? Corporation(int id) =>
        Inspect<CapsuleerCorporationInspection>(
            (live, world) =>
            {
                if (world.Set<SavedCorporation>().Find(id) is not { ClosedAt: null, CapsuleerFoundingSystemId: int origin } row)
                {
                    return null;
                }
                var members = Members(world, id);
                var alliance = MemberAlliance(world, id);
                return new(
                    CorporationLink(row),
                    CorporationMembershipStore.BotLed(world, row),
                    row.CeoId,
                    row.SystemId,
                    row.StationId,
                    origin,
                    row.BalanceCents,
                    row.MemberLimit,
                    world
                        .Filter<SavedCorporationApplication>(application => application.CorporationId == id)
                        .AsEnumerable()
                        .Count(application => !CorporationMembershipRules.Closed(application.Status)),
                    members.Count(member =>
                        member.Career.HasValue && CapsuleerCorporations.Organization(world, member.Id).Relocation is not null
                    ),
                    alliance is null ? null : AllianceLink(alliance, true),
                    members,
                    world
                        .Filter<IndustryJob>(job => job.OutputCorporationId == id)
                        .Where(job =>
                            job.Status == IndustryJobStatus.Installed
                            || job.Status == IndustryJobStatus.Paused
                            || job.Status == IndustryJobStatus.Ready
                        )
                        .OrderBy(job => job.EndTime)
                        .ThenBy(job => job.Id)
                        .Select(job => new CapsuleerIndustryInspection(job.Id, job.ProductTypeId, job.Activity, job.Status, job.Runs))
                        .ToArray(),
                    Expeditions(live, world, id, members).Select(expedition => expedition.Work).ToArray()
                );
            }
        );

    public CapsuleerAllianceInspection? Alliance(int id) =>
        Inspect<CapsuleerAllianceInspection>(
            (live, world) =>
            {
                if (world.Set<SavedAlliance>().Find(id) is not { ClosedAt: null } row)
                {
                    return null;
                }
                var corporations = AllianceCorporations(world, id);
                bool founded = IsBot(world, row.CreatorId);
                if (!founded && !corporations.Any(corporation => corporation.Inspectable))
                {
                    return null;
                }
                var bill = world
                    .Filter<SavedAllianceBill>(bill => bill.AllianceId == id)
                    .AsEnumerable()
                    .Where(bill => bill.PaidAt is null)
                    .MinBy(bill => bill.DueAt);
                return new(
                    AllianceLink(row, true),
                    founded,
                    row.ExecutorCorporationId,
                    row.CreatorCorporationId,
                    corporations,
                    world.Filter<SavedSovereigntyClaim>(claim => claim.AllianceId == id).Select(claim => claim.SystemId).Order().ToArray(),
                    corporations
                        .SelectMany(corporation => Expeditions(live, world, corporation.Id, Members(world, corporation.Id)))
                        .Where(expedition => expedition.AllianceId == id)
                        .Select(expedition => expedition.Work)
                        .ToArray(),
                    FileTime(bill?.DueAt),
                    bill?.AmountCents
                );
            }
        );

    private T? Inspect<T>(Func<LiveWorld, WorldAction, T?> read)
        where T : class =>
        LiveWorld.Find(database.Path) is { Economy.IsLoaded: true } live ? live.Economy.Inspect(world => read(live, world)) : null;

    private static bool IsBot(WorldAction world, long id) => world.Set<SavedCapsuleerAgent>().Find(id) is not null;

    private static SavedAlliance? MemberAlliance(WorldAction world, int corporation) =>
        world.Set<SavedAllianceMember>().Find(corporation) is { } membership
        && world.Set<SavedAlliance>().Find(membership.AllianceId) is { ClosedAt: null } alliance
            ? alliance
            : null;

    private static CapsuleerOrganizationLink CorporationLink(SavedCorporation row) =>
        new(row.Id, row.Name, row.Ticker, row.ClosedAt is null && row.CapsuleerFoundingSystemId.HasValue);

    private static CapsuleerOrganizationLink AllianceLink(SavedAlliance row, bool inspectable) =>
        new(row.Id, row.Name, row.Ticker, inspectable);

    private static bool IsInspectable(WorldAction world, SavedAlliance alliance) =>
        IsBot(world, alliance.CreatorId) || AllianceCorporations(world, alliance.Id).Any(row => row.Inspectable);

    private static CapsuleerOrganizationLink[] AllianceCorporations(WorldAction world, int alliance) =>
        world
            .Filter<SavedAllianceMember>(member => member.AllianceId == alliance)
            .AsEnumerable()
            .Select(member => world.Set<SavedCorporation>().Find(member.CorporationId))
            .OfType<SavedCorporation>()
            .Where(row => row.ClosedAt is null)
            .Select(CorporationLink)
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id)
            .ToArray();

    private static CapsuleerMemberInspection[] Members(WorldAction world, int corporation) =>
        world
            .Pilots.Project(pilot => new { pilot.Id, pilot.Name }, pilot => pilot.CorporationId == corporation)
            .Select(pilot => new CapsuleerMemberInspection(
                pilot.Id,
                pilot.Name,
                world.Set<SavedCapsuleerAgent>().Find(pilot.Id)?.Career,
                world.Set<SavedCapsuleerSuspension>().Find(pilot.Id) is not null
            ))
            .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.Id)
            .ToArray();

    private static IEnumerable<(int AllianceId, CapsuleerSovereigntyInspection Work)> Expeditions(
        LiveWorld live,
        WorldAction world,
        int corporation,
        IEnumerable<CapsuleerMemberInspection> members
    )
    {
        foreach (var member in members.Where(member => member.Career.HasValue))
        {
            if (
                Hauler(live, world, member.Id) is { SovereigntyExpedition: { } expedition } progress
                && expedition.CorporationId == corporation
                && world.Pilots.Find(member.Id)?.ActiveShipId == progress.ShipId
            )
            {
                yield return (
                    expedition.AllianceId,
                    new(member.Id, member.Name, corporation, expedition.TargetSystemId, expedition.Duty, expedition.Returning)
                );
            }
        }
    }

    private static DateTimeOffset? FileTime(long? value) =>
        value is long ticks ? DateTimeOffset.FromFileTime(ticks).ToUniversalTime() : null;
}
