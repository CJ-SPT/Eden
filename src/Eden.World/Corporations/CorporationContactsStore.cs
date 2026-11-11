using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationContactsStore(WorldDatabase database) : ICorporationContacts
{
    public long Revision(int corporationId) => database.CorporationContactStamps.GetValueOrDefault(corporationId);

    public CorporationContactBook Read(long actorId)
    {
        using WorldDbContext world = new(database.Path);
        var pilot = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown contact reader.");
        int owner = pilot.CorporationId;
        return new(
            owner,
            world
                .Set<SavedCorporationContact>()
                .AsNoTracking()
                .Where(c => c.CorporationId == owner)
                .OrderBy(c => c.ContactId)
                .ToArray()
                .Select(c => c.Snapshot())
                .ToArray(),
            world
                .Set<SavedCorporationContactLabel>()
                .AsNoTracking()
                .Where(l => l.CorporationId == owner)
                .OrderBy(l => l.Id)
                .ToArray()
                .Select(l => l.Snapshot())
                .ToArray()
        );
    }

    public void SetStanding(long actorId, IReadOnlyList<long> contacts, double standing)
    {
        ContactRules.CheckStanding(standing);
        Write(
            actorId,
            (world, corporation) =>
            {
                CheckTargets(world, corporation.Id, contacts);
                foreach (long id in contacts.Distinct())
                {
                    var saved =
                        world.Set<SavedCorporationContact>().Find(corporation.Id, id)
                        ?? world.Add(new SavedCorporationContact { CorporationId = corporation.Id, ContactId = id }).Entity;
                    saved.Standing = standing;
                }
            }
        );
    }

    public void Delete(long actorId, IReadOnlyList<long> contacts) =>
        Write(
            actorId,
            (world, corporation) =>
            {
                CheckTargets(world, corporation.Id, contacts);
                world
                    .Set<SavedCorporationContact>()
                    .Where(c => c.CorporationId == corporation.Id && contacts.Contains(c.ContactId))
                    .ExecuteDelete();
            }
        );

    public long CreateLabel(long actorId, string name, int? color)
    {
        ContactRules.CheckLabelName(name);
        ContactRules.CheckLabelColor(color);
        long id = 0;
        Write(
            actorId,
            (world, corporation) =>
            {
                CheckName(world, corporation.Id, name, null);
                var used = world
                    .Set<SavedCorporationContactLabel>()
                    .Where(l => l.CorporationId == corporation.Id)
                    .Select(l => l.Id)
                    .ToHashSet();
                id = Enumerable.Range(0, ContactRules.MaximumLabels).Select(bit => 1L << bit).FirstOrDefault(bit => !used.Contains(bit));
                if (id == 0)
                {
                    throw new InvalidDataException("No more corporation contact labels are available.");
                }
                world.Add(
                    new SavedCorporationContactLabel
                    {
                        CorporationId = corporation.Id,
                        Id = id,
                        Name = name.Trim(),
                        Color = color,
                    }
                );
            }
        );
        return id;
    }

    public void EditLabel(long actorId, long labelId, string? name, int? color)
    {
        ContactRules.CheckLabelId(labelId);
        ContactRules.CheckLabelColor(color);
        if (name is not null)
        {
            ContactRules.CheckLabelName(name);
        }
        if (name is null && color is null)
        {
            throw new InvalidDataException("Choose a label name or color to change.");
        }
        Write(
            actorId,
            (world, corporation) =>
            {
                var label = Label(world, corporation.Id, labelId);
                if (name is not null)
                {
                    CheckName(world, corporation.Id, name, labelId);
                    label.Name = name.Trim();
                }
                if (color is not null)
                {
                    label.Color = color;
                }
            }
        );
    }

    public void DeleteLabel(long actorId, long labelId)
    {
        ContactRules.CheckLabelId(labelId);
        Write(
            actorId,
            (world, corporation) =>
            {
                var label = Label(world, corporation.Id, labelId);
                foreach (
                    var contact in world
                        .Set<SavedCorporationContact>()
                        .Where(c => c.CorporationId == corporation.Id && (c.LabelMask & labelId) != 0)
                )
                {
                    contact.LabelMask &= ~labelId;
                }
                world.Remove(label);
            }
        );
    }

    public void SetLabels(long actorId, IReadOnlyList<long> contacts, long mask, bool assign) =>
        Write(
            actorId,
            (world, corporation) =>
            {
                CheckTargets(world, corporation.Id, contacts);
                long available = world
                    .Set<SavedCorporationContactLabel>()
                    .Where(l => l.CorporationId == corporation.Id)
                    .Select(l => l.Id)
                    .AsEnumerable()
                    .Aggregate(0L, (all, id) => all | id);
                if (mask <= 0 || (mask & ~available) != 0)
                {
                    throw new InvalidDataException("Choose existing corporation contact labels.");
                }
                var saved = world
                    .Set<SavedCorporationContact>()
                    .Where(c => c.CorporationId == corporation.Id && contacts.Contains(c.ContactId))
                    .ToArray();
                if (saved.Length != contacts.Distinct().Count())
                {
                    throw new InvalidDataException("Choose existing corporation contacts before assigning labels.");
                }
                foreach (var contact in saved)
                {
                    contact.LabelMask = assign ? contact.LabelMask | mask : contact.LabelMask & ~mask;
                }
            }
        );

    private void Write(long actorId, Action<WorldDbContext, SavedCorporation> action)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var corporation = CorporationRoleStore.Current(world, actorId);
        if (!CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.Diplomat))
        {
            throw new InvalidDataException("Only the CEO, a Director or a Diplomat can change corporation contacts.");
        }
        action(world, corporation);
        world.SaveChanges();
        transaction.Commit();
        database.CorporationContactStamps.AddOrUpdate(corporation.Id, 1, (_, revision) => revision + 1);
    }

    private void CheckTargets(WorldDbContext world, int corporationId, IReadOnlyList<long> contacts)
    {
        ContactRules.CheckContacts(corporationId, contacts);
        var known = world.Pilots.Where(p => contacts.Contains(p.Id)).Select(p => p.Id).ToHashSet();
        known.UnionWith(world.Set<SavedCorporation>().Where(c => contacts.Contains((long)c.Id)).Select(c => (long)c.Id));
        known.UnionWith(database.Catalog.NpcCorporations.Keys.Where(id => contacts.Contains((long)id)).Select(id => (long)id));
        if (contacts.Any(id => id == corporationId || !known.Contains(id)))
        {
            throw new InvalidDataException("Choose other known pilots or corporations as contacts.");
        }
    }

    private static SavedCorporationContactLabel Label(WorldDbContext world, int corporationId, long labelId) =>
        world.Set<SavedCorporationContactLabel>().Find(corporationId, labelId)
        ?? throw new InvalidDataException("Unknown corporation contact label.");

    private static void CheckName(WorldDbContext world, int corporationId, string name, long? except)
    {
        if (
            world
                .Set<SavedCorporationContactLabel>()
                .Where(l => l.CorporationId == corporationId && l.Id != except)
                .AsEnumerable()
                .Any(l => string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        )
        {
            throw new InvalidDataException("A corporation contact label already has this name.");
        }
    }
}
