using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed partial class CorporationRoleStore(WorldDatabase database, TimeProvider clock) : ICorporationRoles
{
    internal static CorporationMemberAccess Read(WorldDbContext world, long pilotId, int corporationId) =>
        world.Set<SavedCorporationMemberAccess>().Find(pilotId) is { } row && row.CorporationId == corporationId
            ? JsonSerializer.Deserialize<CorporationMemberAccess>(row.DataJson)!
            : new(new());

    internal static CorporationMemberAccess Effective(WorldDbContext world, long pilotId, SavedCorporation? corporation)
    {
        if (corporation is null)
        {
            return new(new());
        }
        var access = Read(world, pilotId, corporation.Id);
        if (corporation.CeoId == pilotId)
        {
            return access with { Roles = new(CorporationRules.CeoRoles), BlockRoles = false };
        }
        if (access.BlockRoles)
        {
            return access with { Roles = new() };
        }
        var roles = access.Roles;
        foreach (var title in world.Set<SavedCorporationTitle>().Where(t => t.CorporationId == corporation.Id).AsEnumerable())
        {
            if ((access.TitleMask & title.TitleId) != 0)
            {
                roles = roles.Union(title.Snapshot().Roles);
            }
        }
        if (roles.Has(CorporationRole.Director))
        {
            roles = roles with { Roles = roles.Roles | CorporationRules.PersonnelManagerRole };
        }
        return access with { Roles = roles };
    }

    internal static bool Has(WorldAction world, long actorId, SavedCorporation corporation, CorporationRole role) =>
        world.Pilots.Find(actorId)?.CorporationId == corporation.Id && Effective(world, actorId, corporation).Roles.Has(role);

    public IReadOnlyList<CorporationTitle> Titles(long memberId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<CorporationTitle>>(
            database,
            world =>
            {
                var pilot = world.Pilots.Find(memberId) ?? throw new InvalidDataException("Unknown pilot.");
                if (!world.Set<SavedCorporation>().Any(c => c.Id == pilot.CorporationId))
                {
                    return [];
                }
                var saved = world
                    .Set<SavedCorporationTitle>()
                    .Where(t => t.CorporationId == pilot.CorporationId)
                    .AsEnumerable()
                    .ToDictionary(t => t.TitleId, t => t.Snapshot());
                return Enumerable
                    .Range(0, CorporationRoleRules.TitleCount)
                    .Select(index => saved.GetValueOrDefault(1L << index) ?? new CorporationTitle(1L << index, "", new()))
                    .ToArray();
            }
        );
    }

    public void UpdateMembers(long actorId, IReadOnlyList<CorporationMemberUpdate> updates)
    {
        if (updates.Count > CorporationRoleRules.MaximumBatch || updates.Select(u => u.PilotId).Distinct().Count() != updates.Count)
        {
            throw new InvalidDataException("Choose distinct members within the corporation batch limit.");
        }
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
        var corporation = Current(world, actorId);
        var actor = Effective(world, actorId, corporation);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (var update in updates)
        {
            if (world.Pilots.Find(update.PilotId)?.CorporationId != corporation.Id || update.PilotId == corporation.CeoId)
            {
                throw new InvalidDataException("Choose an ordinary member of your corporation; the CEO's roles are fixed.");
            }
            var before = Read(world, update.PilotId, corporation.Id);
            var effectiveBefore = Effective(world, update.PilotId, corporation);
            var after = before with
            {
                Roles = update.Roles.Apply(before.Roles),
                Title = update.Title ?? before.Title,
                TitleMask = update.TitleMask ?? before.TitleMask,
                BaseId = update.BaseId,
                BlockRoles = update.BlockRoles ?? before.BlockRoles,
                DivisionId = update.DivisionId ?? before.DivisionId,
                SquadronId = update.SquadronId ?? before.SquadronId,
            };
            if (
                after.TitleMask < 0
                || (after.TitleMask & ~CorporationRoleRules.AllTitles) != 0
                || after.Title.Length > 100
                || after.Title.Any(char.IsControl)
            )
            {
                throw new InvalidDataException("Choose existing titles and a member title of at most 100 characters.");
            }
            if (after.BaseId.HasValue)
            {
                using CatalogDbContext catalog = new(database.CatalogPath);
                if (catalog.FindStation(after.BaseId.Value) is null)
                {
                    throw new InvalidDataException("Choose a known station as the member's base.");
                }
            }
            if (after.BlockRoles != before.BlockRoles && actorId != update.PilotId)
            {
                throw new InvalidDataException("Only the member may change their block-roles preference.");
            }
            if (after.BlockRoles && (after.Roles.HasAuthority || after.TitleMask != 0))
            {
                throw new InvalidDataException("Remove all roles and titles before blocking role assignments.");
            }
            Save(world, update.PilotId, corporation.Id, after);
            var effectiveAfter = Effective(world, update.PilotId, corporation);
            bool selfRelinquish = actorId == update.PilotId && !after.Roles.HasAuthority && after.TitleMask == 0;
            if (!selfRelinquish)
            {
                CorporationRoleRules.Authorize(corporation.CeoId == actorId, actor.Roles, effectiveBefore.Roles, effectiveAfter.Roles);
                if (
                    (
                        after.Title != before.Title
                        || after.BaseId != before.BaseId
                        || after.DivisionId != before.DivisionId
                        || after.SquadronId != before.SquadronId
                    ) && !actor.Roles.Has(CorporationRole.PersonnelManager)
                )
                {
                    throw new InvalidDataException("Personnel management is required to change member details.");
                }
            }
            if (effectiveBefore.Roles.HasAuthority && !effectiveAfter.Roles.HasAuthority)
            {
                after = after with { RolesRemovedAt = now };
                Save(world, update.PilotId, corporation.Id, after);
                effectiveAfter = effectiveAfter with { RolesRemovedAt = now };
            }
            if (before != after)
            {
                Notify(world, corporation.Id, new(corporation.Id, update.PilotId, effectiveBefore, effectiveAfter), null);
            }
        }
        transaction.Commit();
    }

    public void UpdateTitles(long actorId, IReadOnlyList<CorporationTitle> titles)
    {
        if (titles.Count > CorporationRoleRules.TitleCount || titles.Select(t => t.Id).Distinct().Count() != titles.Count)
        {
            throw new InvalidDataException("Choose distinct corporation titles.");
        }
        foreach (var title in titles)
        {
            CorporationRoleRules.Validate(title);
        }
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
        var corporation = Current(world, actorId);
        RequireDirector(world, actorId, corporation);
        var beforeMembers = world
            .Pilots.Where(p => p.CorporationId == corporation.Id)
            .Select(p => p.Id)
            .ToArray()
            .ToDictionary(id => id, id => Effective(world, id, corporation));
        foreach (var title in titles)
        {
            var saved = world.Set<SavedCorporationTitle>().Find(corporation.Id, title.Id);
            var before = saved?.Snapshot();
            if (before == title)
            {
                continue;
            }
            if (saved is null)
            {
                saved = world.Add(new SavedCorporationTitle { CorporationId = corporation.Id, TitleId = title.Id }).Entity;
            }
            saved.DataJson = JsonSerializer.Serialize(title);
            Notify(world, corporation.Id, null, new(corporation.Id, title.Id, before, title));
        }
        world.SaveChanges();
        NotifyAffectedMembers(world, corporation, beforeMembers);
        transaction.Commit();
    }

    public void DeleteTitle(long actorId, long titleId)
    {
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
        var corporation = Current(world, actorId);
        RequireDirector(world, actorId, corporation);
        var title =
            world.Set<SavedCorporationTitle>().Find(corporation.Id, titleId)
            ?? throw new InvalidDataException("Unknown corporation title.");
        var before = world
            .Pilots.Where(p => p.CorporationId == corporation.Id)
            .Select(p => p.Id)
            .ToArray()
            .ToDictionary(id => id, id => Effective(world, id, corporation));
        world.Remove(title);
        foreach (var member in before)
        {
            var access = Read(world, member.Key, corporation.Id);
            Save(world, member.Key, corporation.Id, access with { TitleMask = access.TitleMask & ~titleId });
        }
        Notify(world, corporation.Id, null, new(corporation.Id, titleId, title.Snapshot(), null));
        world.SaveChanges();
        NotifyAffectedMembers(world, corporation, before);
        transaction.Commit();
    }

    private void NotifyAffectedMembers(
        WorldAction world,
        SavedCorporation corporation,
        IReadOnlyDictionary<long, CorporationMemberAccess> before
    )
    {
        foreach (var member in before)
        {
            var after = Effective(world, member.Key, corporation);
            if (after == member.Value)
            {
                continue;
            }
            if (member.Value.Roles.HasAuthority && !after.Roles.HasAuthority)
            {
                var saved = Read(world, member.Key, corporation.Id) with { RolesRemovedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() };
                Save(world, member.Key, corporation.Id, saved);
                after = after with { RolesRemovedAt = saved.RolesRemovedAt };
            }
            Notify(world, corporation.Id, new(corporation.Id, member.Key, member.Value, after), null);
        }
    }

    private static void Save(WorldAction world, long pilot, int corporation, CorporationMemberAccess access)
    {
        var saved =
            world.Set<SavedCorporationMemberAccess>().Find(pilot) ?? world.Add(new SavedCorporationMemberAccess { PilotId = pilot }).Entity;
        saved.CorporationId = corporation;
        saved.DataJson = JsonSerializer.Serialize(access);
    }

    internal static SavedCorporation Current(WorldAction world, long actorId)
    {
        var actor = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
        return world.Set<SavedCorporation>().Find(actor.CorporationId)
            ?? throw new InvalidDataException("Join a player corporation first.");
    }

    internal static void RequireDirector(WorldAction world, long actorId, SavedCorporation corporation)
    {
        if (!Has(world, actorId, corporation, CorporationRole.Director))
        {
            throw new InvalidDataException("Only the CEO or a Director can perform this action.");
        }
    }

    private static void Notify(WorldAction world, int corporation, CorporationRoleChange? member, CorporationTitleChange? title)
    {
        foreach (long recipient in world.Pilots.Where(p => p.CorporationId == corporation).Select(p => p.Id))
        {
            world.Add(
                new SavedCorporationChange
                {
                    PilotId = recipient,
                    RoleJson = member is null ? null : JsonSerializer.Serialize(member),
                    TitleJson = title is null ? null : JsonSerializer.Serialize(title),
                }
            );
        }
    }

    internal static SavedCorporation Current(WorldDbContext world, long actorId) => Current(new SqlWorldAction(world), actorId);

    internal static void RequireDirector(WorldDbContext world, long actorId, SavedCorporation corporation) =>
        RequireDirector(new SqlWorldAction(world), actorId, corporation);

    internal static bool Has(WorldDbContext world, long actorId, SavedCorporation corporation, CorporationRole role) =>
        Has(new SqlWorldAction(world), actorId, corporation, role);
}
