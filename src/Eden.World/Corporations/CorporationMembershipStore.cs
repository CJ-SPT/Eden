using System.Text.Json;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Storage;

namespace Eden.World.Corporations;

public sealed class SavedCorporationApplication
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public string Text { get; set; } = "";
    public CorporationApplicationStatus Status { get; set; }
    public long AppliedAt { get; set; }
    public long UpdatedAt { get; set; }
    public string Response { get; set; } = "";

    public CorporationApplication Snapshot() => new(Id, PilotId, CorporationId, Text, Status, AppliedAt, UpdatedAt, Response);
}

public sealed class SavedCorporationChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public string? ApplicationJson { get; set; }
    public string? MembershipJson { get; set; }
    public string? AdministrationJson { get; set; }
    public string? RoleJson { get; set; }
    public string? TitleJson { get; set; }
    public string? ShareJson { get; set; }

    public CorporationChange Snapshot() =>
        new(
            Id,
            ApplicationJson is null ? null : JsonSerializer.Deserialize<CorporationApplication>(ApplicationJson),
            MembershipJson is null ? null : JsonSerializer.Deserialize<CorporationMemberChange>(MembershipJson),
            AdministrationJson is null ? null : JsonSerializer.Deserialize<CorporationAdministrationChange>(AdministrationJson),
            RoleJson is null ? null : JsonSerializer.Deserialize<CorporationRoleChange>(RoleJson),
            TitleJson is null ? null : JsonSerializer.Deserialize<CorporationTitleChange>(TitleJson),
            ShareJson is null ? null : JsonSerializer.Deserialize<CorporationShareChange>(ShareJson)
        );
}

[Injectable]
internal sealed class CorporationMembershipStore(WorldDatabase database, TimeProvider clock) : ICorporationMembership
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public IReadOnlyList<CorporationApplication> Applications(long pilotId, bool forCorporation)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = Pilot(world, pilotId);
        var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
        if (
            forCorporation
            && (corporation is null || !CorporationRoleStore.Has(world, pilotId, corporation, CorporationRole.PersonnelManager))
        )
        {
            throw new InvalidDataException("Personnel management is required to review corporation applications.");
        }
        return world
            .Set<SavedCorporationApplication>()
            .Where(a => forCorporation ? a.CorporationId == pilot.CorporationId : a.PilotId == pilotId)
            .OrderByDescending(a => a.Id)
            .AsEnumerable()
            .Select(a => a.Snapshot())
            .ToArray();
    }

    public CorporationApplication Apply(long pilotId, int corporationId, string text)
    {
        CorporationMembershipRules.CheckText(text);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = Pilot(world, pilotId);
        var corporation = Corporation(world, corporationId);
        CheckJoin(world, pilot, corporation);
        if (!corporation.Snapshot(0).Details.ApplicationsEnabled)
        {
            throw new InvalidDataException("This corporation is not accepting applications.");
        }
        if (
            world
                .Set<SavedCorporationApplication>()
                .Where(a => a.PilotId == pilotId && a.CorporationId == corporationId)
                .AsEnumerable()
                .Any(a => !CorporationMembershipRules.Closed(a.Status))
        )
        {
            throw new InvalidDataException("You already have an active application to this corporation.");
        }
        long now = Now;
        var application = world
            .Add(
                new SavedCorporationApplication
                {
                    PilotId = pilotId,
                    CorporationId = corporationId,
                    Text = text,
                    Status = CorporationApplicationStatus.Applied,
                    AppliedAt = now,
                    UpdatedAt = now,
                }
            )
            .Entity;
        world.SaveChanges();
        NotifyApplication(world, application);
        world.SaveChanges();
        transaction.Commit();
        return application.Snapshot();
    }

    public CorporationApplication Update(long actorId, CorporationApplicationUpdate update)
    {
        CorporationMembershipRules.CheckText(update.Text);
        CorporationMembershipRules.CheckText(update.Response);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var actor = Pilot(world, actorId);
        var application =
            world.Set<SavedCorporationApplication>().Find(update.Id) ?? throw new InvalidDataException("Unknown corporation application.");
        if (
            application.PilotId != update.PilotId
            || application.CorporationId != update.CorporationId
            || update.AppliedAt.HasValue && update.AppliedAt != application.AppliedAt
        )
        {
            throw new InvalidDataException("The corporation application identifiers changed.");
        }
        var corporation = Corporation(world, application.CorporationId);
        bool applicant = actorId == application.PilotId;
        bool ceo = CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.PersonnelManager);
        CorporationMembershipRules.CheckTransition(application.Status, update.Status, applicant, ceo);
        var pilot = Pilot(world, application.PilotId);
        if (update.Status is CorporationApplicationStatus.Joined or CorporationApplicationStatus.Offered)
        {
            CheckJoin(world, pilot, corporation);
        }
        if (update.Status == CorporationApplicationStatus.Joined)
        {
            Move(world, pilot, corporation.Id, Now);
        }
        application.Status = update.Status;
        // A recruiter cannot replace the applicant's statement while sending an offer or rejection.
        if (applicant)
        {
            application.Text = update.Text;
        }
        application.Response = update.Response;
        application.UpdatedAt = Now;
        NotifyApplication(world, application);
        if (update.Status == CorporationApplicationStatus.Joined)
        {
            WithdrawOtherApplications(world, pilot.Id, application.Id, Now);
        }
        world.SaveChanges();
        transaction.Commit();
        return application.Snapshot();
    }

    public CorporationLeavePermission CanRemove(long actorId, long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        return Permission(world, actorId, pilotId);
    }

    public void Remove(long actorId, long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var permission = Permission(world, actorId, pilotId);
        if (!permission.Allowed)
        {
            throw new InvalidDataException(permission.Reason);
        }
        var pilot = Pilot(world, pilotId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var history = world
            .Set<SavedCorporationEmployment>()
            .Where(e => e.PilotId == pilotId)
            .OrderBy(e => e.Id)
            .Select(e => e.CorporationId)
            .ToArray();
        int destination = history.FirstOrDefault(id => catalog.Set<NpcCorporation>().Any(c => c.Id == id));
        if (destination == 0)
        {
            throw new InvalidDataException("No original NPC corporation is recorded for this pilot.");
        }
        Move(world, pilot, destination, Now);
        world.SaveChanges();
        transaction.Commit();
    }

    private CorporationLeavePermission Permission(WorldDbContext world, long actorId, long pilotId)
    {
        var actor = Pilot(world, actorId);
        var pilot = Pilot(world, pilotId);
        var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
        if (
            corporation is null
            || actor.CorporationId != pilot.CorporationId
            || actorId != pilotId && !CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.PersonnelManager)
        )
        {
            return new(false, "Only members may leave, and personnel management is required to remove another member.");
        }
        if (corporation.CeoId == pilotId)
        {
            return new(false, "The CEO must transfer ownership before leaving.");
        }
        var access = CorporationRoleStore.Effective(world, pilotId, corporation);
        if (access.Roles.HasAuthority)
        {
            return new(false, "Remove corporation roles and titles before leaving or removing this member.", "CrpCantQuitNotInStasis");
        }
        if (
            access.RolesRemovedAt is long removed
            && clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() < removed + CorporationRoleRules.RemovalDelay.Ticks
        )
        {
            return new(false, "Wait 24 hours after the last corporation role was removed.");
        }
        return new(true, "");
    }

    internal static void Move(WorldDbContext world, Pilot pilot, int corporationId, long now)
    {
        if (world.Set<SavedCorporationWalletSelection>().Find(pilot.Id) is { } selection)
        {
            world.Remove(selection);
        }
        if (world.Set<SavedCorporationMemberAccess>().Find(pilot.Id) is { } access)
        {
            world.Remove(access);
        }
        int previous = pilot.CorporationId;
        int oldCount = world.Pilots.Count(p => p.CorporationId == previous);
        int newCount = world.Pilots.Count(p => p.CorporationId == corporationId);
        var recipients = world
            .Pilots.Where(p => p.CorporationId == previous || p.CorporationId == corporationId)
            .Select(p => p.Id)
            .ToArray();
        if (!world.Set<SavedCorporationEmployment>().Any(e => e.PilotId == pilot.Id))
        {
            world.Add(
                new SavedCorporationEmployment
                {
                    PilotId = pilot.Id,
                    CorporationId = previous,
                    StartedAt = pilot.CreatedAt,
                }
            );
        }
        pilot.CorporationId = corporationId;
        world.Add(
            new SavedCorporationEmployment
            {
                PilotId = pilot.Id,
                CorporationId = corporationId,
                StartedAt = now,
            }
        );
        var change = new CorporationMemberChange(pilot.Id, previous, corporationId, oldCount - 1, newCount + 1, now);
        foreach (long recipient in recipients)
        {
            world.Add(new SavedCorporationChange { PilotId = recipient, MembershipJson = JsonSerializer.Serialize(change) });
        }
    }

    internal static void WithdrawOtherApplications(WorldDbContext world, long pilotId, long? keepId, long now)
    {
        foreach (
            var other in world
                .Set<SavedCorporationApplication>()
                .Where(a => a.PilotId == pilotId && (keepId == null || a.Id != keepId))
                .AsEnumerable()
                .Where(a => !CorporationMembershipRules.Closed(a.Status))
        )
        {
            other.Status = CorporationApplicationStatus.Withdrawn;
            other.UpdatedAt = now;
            NotifyApplication(world, other);
        }
    }

    private static void CheckJoin(WorldDbContext world, Pilot pilot, SavedCorporation corporation) =>
        CorporationMembershipRules.CheckJoin(
            pilot.CorporationId == corporation.Id,
            world.Set<SavedCorporation>().Any(c => c.CeoId == pilot.Id),
            world.Pilots.Count(p => p.CorporationId == corporation.Id),
            corporation.MemberLimit
        );

    private static Pilot Pilot(WorldDbContext world, long id) => world.Pilots.Find(id) ?? throw new InvalidDataException("Unknown pilot.");

    private static SavedCorporation Corporation(WorldDbContext world, int id) =>
        world.Set<SavedCorporation>().Find(id) ?? throw new InvalidDataException("Unknown player corporation.");

    private static void NotifyApplication(WorldDbContext world, SavedCorporationApplication application)
    {
        var corporation = Corporation(world, application.CorporationId);
        string json = JsonSerializer.Serialize(application.Snapshot());
        var recruiters = world
            .Pilots.Where(p => p.CorporationId == corporation.Id)
            .Select(p => p.Id)
            .ToArray()
            .Where(id => CorporationRoleStore.Has(world, id, corporation, CorporationRole.PersonnelManager));
        foreach (long recipient in recruiters.Append(application.PilotId).Distinct())
        {
            world.Add(new SavedCorporationChange { PilotId = recipient, ApplicationJson = json });
        }
    }

    public long Head(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<SavedCorporationChange>().Where(c => c.PilotId == pilotId).Max(c => (long?)c.Id) ?? 0;
    }

    public IReadOnlyList<CorporationChange> Changes(long pilotId, long after)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<SavedCorporationChange>()
            .Where(c => c.PilotId == pilotId && c.Id > after)
            .OrderBy(c => c.Id)
            .Take(CorporationMembershipRules.ChangePageSize)
            .AsEnumerable()
            .Select(c => c.Snapshot())
            .ToArray();
    }
}
