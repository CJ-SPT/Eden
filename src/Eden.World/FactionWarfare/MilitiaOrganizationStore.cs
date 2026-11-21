using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Mail;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Notifications;
using Eden.Infrastructure.Registration;
using Eden.World.Alliances;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Mail;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.FactionWarfare;

[Injectable(InjectionType.Singleton)]
internal sealed class MilitiaOrganizationStore(WorldDatabase database, TimeProvider clock, InboxChangeSignal inbox) : IMilitiaOrganizations
{
    private long _nextReview;
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Organization enlistment requires the resident world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public MilitiaOrganizationStatus Status(int corporationId)
    {
        if (MilitiaRules.CorporationFaction(corporationId) is { } faction)
        {
            return new(faction, MilitiaOrganizationPhase.Active);
        }
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return new(null);
        }
        Advance();
        return live.Economy.Inspect(world =>
            Organization(world, corporationId) is { } row
                ? new MilitiaOrganizationStatus(
                    row.Faction,
                    row.Phase,
                    row.Phase == MilitiaOrganizationPhase.Active ? row.StartedAt : row.EffectiveAt
                )
                : new(null)
        );
    }

    public MilitiaFaction? AllianceFaction(int allianceId)
    {
        Advance();
        return LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world => ActiveFaction(world.Set<SavedMilitiaOrganization>().Find(allianceId)));
    }

    public IReadOnlyList<int> Corporations(MilitiaFaction faction)
    {
        Advance();
        int npc = MilitiaRules.Corporation(faction);
        return LiveWorld
                .Find(database.Path)
                ?.Economy.Inspect(world =>
                    world
                        .Filter<SavedCorporation>(corp => corp.ClosedAt == null)
                        .AsEnumerable()
                        .Where(corp => CorporationFaction(world, corp.Id) == faction)
                        .Select(corp => corp.Id)
                        .Prepend(npc)
                        .Order()
                        .ToArray()
                )
            ?? [npc];
    }

    internal static SavedMilitiaOrganization? Organization(WorldAction world, int corporationId)
    {
        if (world is not EconomyTransaction || corporationId < CorporationRules.FirstCorporationId)
        {
            return null;
        }
        int ownerId = world.Set<SavedAllianceMember>().Find(corporationId)?.AllianceId ?? corporationId;
        return world.Set<SavedMilitiaOrganization>().Find(ownerId);
    }

    internal static MilitiaFaction? ActiveFaction(SavedMilitiaOrganization? row) =>
        row is not null && MilitiaOrganizationRules.Inherits(row.Phase) ? row.Faction : null;

    internal static MilitiaFaction? CorporationFaction(WorldAction world, int corporationId) =>
        MilitiaRules.CorporationFaction(corporationId) ?? ActiveFaction(Organization(world, corporationId));

    public void Change(long actorId, MilitiaOrganizationKind kind, MilitiaOrganizationCommand command, MilitiaFaction faction)
    {
        _ = MilitiaRules.Corporation(faction);
        var live = Resident;
        using var lease = live.EnterPersistence("militia organization command", TransactionParticipants.None);
        long now = Now;
        AdvanceLocked(live, now);
        using var world = live.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        CorporationRoleStore.RequireDirector(world, actorId, corporation);
        int owner = kind switch
        {
            MilitiaOrganizationKind.Corporation when world.Set<SavedAllianceMember>().Find(corporation.Id) is null => corporation.Id,
            MilitiaOrganizationKind.Alliance => AllianceStore.Executor(world, actorId).Id,
            _ => throw new InvalidDataException("Use alliance executor authority to manage your alliance's militia enlistment."),
        };
        var row = world.Set<SavedMilitiaOrganization>().Find(owner);
        if (command == MilitiaOrganizationCommand.Join)
        {
            if (row is not null)
            {
                throw new InvalidDataException("Withdraw the pending change or finish leaving before applying again.");
            }
            if (world.Set<SavedMilitiaEnlistment>().Find(actorId)?.DirectFaction is not null)
            {
                throw new InvalidDataException("Retire your direct enlistment before applying on behalf of an organization.");
            }
            var proposed = new SavedMilitiaOrganization
            {
                OwnerId = owner,
                Kind = kind,
                Faction = faction,
                Phase = MilitiaOrganizationPhase.Joining,
                EffectiveAt = AllianceBillingRules.NextDowntime(now),
            };
            var members = Members(world, proposed).ToArray();
            if (JoinFailure(world, members, faction, now) is { } failure)
            {
                throw new InvalidDataException(failure);
            }
            world.Add(proposed);
        }
        else
        {
            if (row is null || row.Faction != faction)
            {
                throw new InvalidDataException("There is no matching organization enlistment.");
            }
            switch (command, row.Phase)
            {
                case (MilitiaOrganizationCommand.Leave, MilitiaOrganizationPhase.Active):
                    row.Phase = MilitiaOrganizationPhase.Leaving;
                    row.EffectiveAt = AllianceBillingRules.NextDowntime(now);
                    break;
                case (MilitiaOrganizationCommand.WithdrawJoin, MilitiaOrganizationPhase.Joining):
                    Delete(world, row);
                    break;
                case (MilitiaOrganizationCommand.WithdrawLeave, MilitiaOrganizationPhase.Leaving):
                    row.Phase = MilitiaOrganizationPhase.Active;
                    row.EffectiveAt = row.StartedAt!.Value;
                    break;
                default:
                    throw new InvalidDataException("This action does not match the current enlistment phase.");
            }
        }
        world.Commit();
    }

    internal static void RequireCorporateAdmission(WorldAction world, Pilot pilot, int corporationId)
    {
        if (CorporateAdmissionFailure(world, pilot, corporationId) is { } failure)
        {
            throw new InvalidDataException(failure);
        }
    }

    internal static string? CorporateAdmissionFailure(WorldAction world, Pilot pilot, int corporationId) =>
        Organization(world, corporationId) is { } row ? MemberFailure(world, pilot, row.Faction) : null;

    internal static string? AllianceAdmissionFailure(WorldAction world, int corporationId, int? allianceId, long now)
    {
        if (world is not EconomyTransaction)
        {
            return null;
        }
        if (world.Set<SavedMilitiaOrganization>().Find(corporationId) is not null)
        {
            return "Withdraw or finish leaving your corporation's militia enlistment before changing alliances.";
        }
        if (allianceId is not int target || world.Set<SavedMilitiaOrganization>().Find(target) is not { } enrollment)
        {
            return null;
        }
        var applicants = CorporationMembers(world, corporationId).ToArray();
        var failure = applicants.Select(pilot => MemberFailure(world, pilot, enrollment.Faction)).OfType<string>().FirstOrDefault();
        return failure
            ?? (
                Standing(world, Members(world, enrollment).Concat(applicants), enrollment.Faction, now)
                < MilitiaOrganizationRules.OrganizationStandingFloor
                    ? "The enlarged alliance would have negative faction standing."
                    : null
            );
    }

    internal static void OrganizationClosed(WorldAction world, int ownerId)
    {
        if (world is EconomyTransaction && world.Set<SavedMilitiaOrganization>().Find(ownerId) is { } row)
        {
            Delete(world, row);
        }
    }

    internal static void ClearMemberWarning(WorldAction world, long pilotId)
    {
        if (world is EconomyTransaction && world.Set<SavedMilitiaStandingWarning>().Find(pilotId) is { } warning)
        {
            world.Remove(warning);
        }
    }

    internal static void CorporationDepartedAlliance(WorldAction world, int allianceId, int corporationId)
    {
        world.RemoveRange(
            world
                .Filter<SavedMilitiaStandingWarning>(warning => warning.OwnerId == allianceId && warning.CorporationId == corporationId)
                .ToArray()
        );
    }

    private static void Delete(WorldAction world, SavedMilitiaOrganization row)
    {
        int owner = row.OwnerId;
        world.RemoveRange(world.Filter<SavedMilitiaStandingWarning>(warning => warning.OwnerId == owner).ToArray());
        world.Remove(row);
    }

    public void Advance()
    {
        long now = Now;
        if (Volatile.Read(ref _nextReview) > now || LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        using var lease = live.EnterPersistence("militia organization review", TransactionParticipants.None);
        AdvanceLocked(live, Now);
    }

    private void AdvanceLocked(LiveWorld live, long now)
    {
        if (_nextReview > now)
        {
            return;
        }
        long next = AllianceBillingRules.NextDowntime(now);
        long review = next - TimeSpan.TicksPerDay;
        var due = live.Economy.Inspect(world =>
            world
                .Set<SavedMilitiaOrganization>()
                .AsEnumerable()
                .Where(row =>
                    row.Phase != MilitiaOrganizationPhase.Active && row.EffectiveAt <= now
                    || MilitiaOrganizationRules.Inherits(row.Phase) && row.ReviewedAt < review
                )
                .Select(row => row.OwnerId)
                .Order()
                .ToArray()
        );
        if (due.Length > 0)
        {
            using var world = live.Economy.Begin(TransactionParticipants.None);
            using CatalogDbContext catalog = new(database.CatalogPath);
            bool accessChanged = false;
            foreach (int id in due)
            {
                var row = world.Set<SavedMilitiaOrganization>().Find(id)!;
                var members = Members(world, row).ToArray();
                if (row.Phase == MilitiaOrganizationPhase.Leaving && row.EffectiveAt <= now)
                {
                    Delete(world, row);
                    accessChanged = true;
                    continue;
                }
                if (row.Phase == MilitiaOrganizationPhase.Joining)
                {
                    if (JoinFailure(world, members, row.Faction, now) is not null)
                    {
                        Delete(world, row);
                        continue;
                    }
                    row.Phase = MilitiaOrganizationPhase.Active;
                    row.StartedAt = row.EffectiveAt;
                    row.ReviewedAt = review;
                    accessChanged = true;
                    continue;
                }
                accessChanged |= Review(world, catalog, row, members, review);
            }
            world.AfterCommit(inbox.Publish);
            world.Commit();
            if (accessChanged)
            {
                database.NotifyAccessGroupsChanged();
            }
        }
        Volatile.Write(ref _nextReview, next);
    }

    private static IEnumerable<Pilot> Members(WorldAction world, SavedMilitiaOrganization row) =>
        row.Kind == MilitiaOrganizationKind.Alliance
            ? world
                .Filter<SavedAllianceMember>(member => member.AllianceId == row.OwnerId)
                .AsEnumerable()
                .SelectMany(member => CorporationMembers(world, member.CorporationId))
            : CorporationMembers(world, row.OwnerId);

    private static IEnumerable<Pilot> CorporationMembers(WorldAction world, int corporationId) =>
        world
            .Filter<Pilot>(pilot => pilot.CorporationId == corporationId)
            .AsEnumerable()
            .Where(pilot => world.Set<SavedCharacterDeletion>().Find(pilot.Id)?.DeletedAt is null);

    private static string? JoinFailure(WorldAction world, Pilot[] members, MilitiaFaction faction, long at)
    {
        if (members.Length == 0)
        {
            return "The organization has no eligible members.";
        }
        var failures = members.Select(pilot => MemberFailure(world, pilot, faction)).OfType<string>().Order().ToArray();
        if (failures.Length != 0)
        {
            return string.Join(" ", failures.Take(10));
        }
        return Standing(world, members, faction, at) < MilitiaOrganizationRules.OrganizationStandingFloor
            ? "The organization's unmodified faction standing must be nonnegative."
            : null;
    }

    private static string? MemberFailure(WorldAction world, Pilot pilot, MilitiaFaction faction)
    {
        double raw = world.Set<SavedNpcStanding>().Find(pilot.Id, (long)faction)?.Value ?? 0;
        var direct = world.Set<SavedMilitiaEnlistment>().Find(pilot.Id)?.DirectFaction;
        if (MilitiaOrganizationRules.AcceptsMember(raw, direct, faction))
        {
            return null;
        }
        return raw < MilitiaOrganizationRules.MemberStandingFloor
            ? string.Create(CultureInfo.InvariantCulture, $"{pilot.Name} has faction standing {raw:0.00} below -5.0.")
            : $"{pilot.Name} is directly enlisted with a different militia.";
    }

    private static double Standing(WorldAction world, IEnumerable<Pilot> members, MilitiaFaction faction, long at)
    {
        long cutoff = at - MilitiaOrganizationRules.StandingMembershipAge.Ticks;
        var samples = new List<double>();
        foreach (var pilot in members)
        {
            var raw = world.Set<SavedNpcStanding>().Find(pilot.Id, (long)faction);
            if (raw is null)
            {
                continue;
            }
            long joined =
                world
                    .Filter<SavedCorporationEmployment>(entry => entry.PilotId == pilot.Id)
                    .OrderByDescending(entry => entry.Id)
                    .Select(entry => (long?)entry.StartedAt)
                    .FirstOrDefault()
                ?? pilot.CreatedAt;
            if (joined <= cutoff)
            {
                samples.Add(raw.Value);
            }
        }
        return MilitiaOrganizationRules.AverageStanding(samples);
    }

    private static bool Review(WorldAction world, CatalogDbContext catalog, SavedMilitiaOrganization row, Pilot[] members, long review)
    {
        row.ReviewedAt = review;
        bool membershipChanged = false;
        foreach (var pilot in members.OrderBy(pilot => pilot.Id))
        {
            double raw = world.Set<SavedNpcStanding>().Find(pilot.Id, (long)row.Faction)?.Value ?? 0;
            var warning = world.Set<SavedMilitiaStandingWarning>().Find(pilot.Id);
            bool previous =
                warning is not null
                && warning.OwnerId == row.OwnerId
                && warning.CorporationId == pilot.CorporationId
                && warning.Faction == row.Faction;
            if (raw >= MilitiaOrganizationRules.MemberStandingFloor)
            {
                if (warning is not null)
                {
                    world.Remove(warning);
                }
                continue;
            }
            var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId)!;
            bool ceo = corporation.CeoId == pilot.Id;
            if (previous && warning!.ExpiresAt <= review)
            {
                NotifyMember(world, row, pilot, ceo, raw, review, removing: true);
                if (ceo)
                {
                    Delete(world, row);
                    return true;
                }
                int destination = CorporationMembershipStore.OriginalCorporation(world, catalog, pilot.Id);
                CorporationMembershipStore.Depart(world, pilot, destination, review);
                membershipChanged = true;
            }
            else if (!previous)
            {
                warning ??= world.Add(new SavedMilitiaStandingWarning { PilotId = pilot.Id }).Entity;
                warning.OwnerId = row.OwnerId;
                warning.CorporationId = pilot.CorporationId;
                warning.Faction = row.Faction;
                warning.ExpiresAt = review + MilitiaOrganizationRules.WarningDuration.Ticks;
                NotifyMember(world, row, pilot, ceo, raw, review, removing: false);
            }
        }
        double standing = Standing(world, Members(world, row), row.Faction, review);
        if (standing >= MilitiaOrganizationRules.OrganizationStandingFloor)
        {
            row.StandingExpiry = null;
        }
        else if (row.StandingExpiry <= review)
        {
            NotifyOverall(world, row, standing, review, removing: true);
            Delete(world, row);
            return true;
        }
        else if (row.StandingExpiry is null)
        {
            row.StandingExpiry = review + MilitiaOrganizationRules.WarningDuration.Ticks;
            NotifyOverall(world, row, standing, review, removing: false);
        }
        return membershipChanged;
    }

    private static void NotifyMember(
        WorldAction world,
        SavedMilitiaOrganization row,
        Pilot pilot,
        bool ceo,
        double raw,
        long at,
        bool removing
    )
    {
        bool alliance = row.Kind == MilitiaOrganizationKind.Alliance;
        var type = (ceo, alliance, removing) switch
        {
            (false, _, false) => InboxNotificationType.MilitiaMemberStandingWarning,
            (true, false, false) => InboxNotificationType.MilitiaCorporationCeoStandingWarning,
            (true, true, false) => InboxNotificationType.MilitiaAllianceCeoStandingWarning,
            (false, _, true) => InboxNotificationType.MilitiaMemberStandingRemoval,
            (true, false, true) => InboxNotificationType.MilitiaCorporationCeoStandingRemoval,
            _ => InboxNotificationType.MilitiaAllianceCeoStandingRemoval,
        };
        AddNotice(
            world,
            row.Faction,
            pilot.Id,
            type,
            at,
            new(
                pilot.Id,
                pilot.CorporationId,
                ceo && alliance ? row.OwnerId : null,
                row.Faction,
                MilitiaOrganizationRules.MemberStandingFloor,
                raw,
                removing ? null : MilitiaOrganizationRules.WarningDuration.Ticks
            )
        );
    }

    private static void NotifyOverall(WorldAction world, SavedMilitiaOrganization row, double raw, long at, bool removing)
    {
        bool alliance = row.Kind == MilitiaOrganizationKind.Alliance;
        int? corporationId = alliance ? world.Set<SavedAlliance>().Find(row.OwnerId)?.ExecutorCorporationId : row.OwnerId;
        if (corporationId is not int id || world.Set<SavedCorporation>().Find(id) is not { } corp)
        {
            return;
        }
        var type = (alliance, removing) switch
        {
            (false, false) => InboxNotificationType.MilitiaCorporationStandingWarning,
            (false, true) => InboxNotificationType.MilitiaCorporationStandingRemoval,
            (true, false) => InboxNotificationType.MilitiaAllianceStandingWarning,
            _ => InboxNotificationType.MilitiaAllianceStandingRemoval,
        };
        AddNotice(
            world,
            row.Faction,
            corp.CeoId,
            type,
            at,
            new(
                corp.CeoId,
                corp.Id,
                alliance ? row.OwnerId : null,
                row.Faction,
                MilitiaOrganizationRules.OrganizationStandingFloor,
                raw,
                removing ? null : MilitiaOrganizationRules.WarningDuration.Ticks
            )
        );
    }

    private static void AddNotice(
        WorldAction world,
        MilitiaFaction faction,
        long receiver,
        InboxNotificationType type,
        long at,
        MilitiaStandingNotificationContent content
    )
    {
        if (world.Pilots.Find(receiver) is { AccountId: > 0 })
        {
            world.Add(
                new SavedInboxNotification
                {
                    Type = type,
                    SenderId = MilitiaRules.Corporation(faction),
                    ReceiverId = receiver,
                    Created = at,
                    Message = JsonSerializer.Serialize(content),
                }
            );
        }
    }
}
