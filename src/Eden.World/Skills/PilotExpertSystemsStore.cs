using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Data.World.Skills;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Mail;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Skills;

[Injectable]
internal sealed class PilotExpertSystemsStore(WorldDatabase database, TimeProvider clock, InboxChangeSignal signal) : IPilotExpertSystems
{
    private static readonly ExpertSystemState s_empty = new([], []);
    private static readonly long s_maxExpiry = DateTimeOffset.MaxValue.ToFileTime();

    internal sealed record Installation(
        List<SavedExpertSystem> Systems,
        SavedExpertSystem? Existing,
        SavedExpertSystem Updated,
        ExpertSystemState Before,
        ExpertSystemState After,
        long Now
    );

    public ExpertSystemState Read(long pilotId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return s_empty;
        }
        return live.Economy.Inspect(world =>
        {
            long now = Now;
            var active = world.Filter<SavedExpertSystem>(system => system.PilotId == pilotId && system.ExpiresAt > now).ToArray();
            if (active.Length == 0)
            {
                return s_empty;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            return Snapshot(catalog, active);
        });
    }

    public ExpertSystemChange Consume(long pilotId, long itemId)
    {
        using var world = Resident.Economy.Begin(new(pilots: [pilotId]));
        var pilot = RequirePilot(world, pilotId);
        if (
            InventoryPersistence.OwnedStack(world, pilotId, itemId) is not { Quantity: > 0 } item
            || !PilotInventoryStore.AccessibleItems(world, pilot, item.LocationId, item.FlagId)
            || world.Set<SavedContractEscrow>().Find(itemId) is not null
            || world.ContainedInventoryIds(itemId).Count != 0
        )
        {
            throw new InvalidDataException("That Expert System is not in your accessible personal inventory.");
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
        var installation = PrepareInstallation(world, catalog, pilotId, item.TypeId, 1, Now);
        var previous = PilotInventoryStore.Read(catalog, item);
        var result = Install(world, installation);
        item.Quantity--;
        if (item.Quantity == 0)
        {
            world.Remove(item);
        }
        world.Commit();
        return result with { Inventory = new(previous with { Quantity = item.Quantity }, previous) };
    }

    internal static Installation PrepareInstallation(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilotId,
        int typeId,
        int quantity,
        long now
    )
    {
        _ = RequirePilot(world, pilotId);
        if (quantity <= 0 || ExpertSystemCatalog.Read(catalog, typeId) is not { Activatable: true } profile)
        {
            throw new InvalidDataException("That Expert System cannot be activated.");
        }
        var systems = Installed(world, pilotId);
        var active = systems.Where(row => row.ExpiresAt > now).ToArray();
        var existing = active.FirstOrDefault(row => row.TypeId == typeId);
        if (existing is null && active.Length >= ExpertSystemRules.MaximumActive)
        {
            throw new InvalidDataException($"You can install up to {ExpertSystemRules.MaximumActive} active Expert Systems.");
        }
        if (existing is not null && existing.ExpiresAt - now > ExpertSystemRules.TopUpWindow)
        {
            throw new InvalidDataException("That Expert System still has more than 30 days remaining.");
        }
        long expires;
        try
        {
            expires = checked((existing?.ExpiresAt ?? now) + profile.DurationDays * TimeSpan.TicksPerDay * quantity);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException("The Expert System duration exceeds the supported date range.");
        }
        if (expires > s_maxExpiry)
        {
            throw new InvalidDataException("The Expert System duration exceeds the supported date range.");
        }
        SavedExpertSystem updated = new()
        {
            PilotId = pilotId,
            TypeId = typeId,
            InstalledAt = existing?.InstalledAt ?? now,
            ExpiresAt = expires,
        };
        return new(
            systems,
            existing,
            updated,
            Snapshot(catalog, systems),
            Snapshot(catalog, active.Where(row => row.TypeId != typeId).Append(updated).ToArray()),
            now
        );
    }

    internal ExpertSystemChange Install(EconomyTransaction world, Installation plan)
    {
        var events = RemoveExpired(world, plan.Systems, plan.Now);
        if (plan.Existing is { } existing)
        {
            existing.ExpiresAt = plan.Updated.ExpiresAt;
        }
        else
        {
            world.Add(plan.Updated);
        }
        events.Add(new(plan.Updated.TypeId, plan.Existing is null));
        return new(plan.Before, plan.After, null, events);
    }

    public ExpertSystemChange Remove(long pilotId, int typeId)
    {
        using var world = Resident.Economy.Begin(new(pilots: [pilotId]));
        _ = RequirePilot(world, pilotId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var systems = Installed(world, pilotId);
        var before = Snapshot(catalog, systems);
        var events = RemoveExpired(world, systems, Now);
        if (systems.Find(row => row.TypeId == typeId) is { } system)
        {
            world.Remove(system);
            systems.Remove(system);
            events.Add(new(typeId, false));
        }
        var result = new ExpertSystemChange(before, Snapshot(catalog, systems), null, events);
        world.Commit();
        return result;
    }

    public ExpertSystemChange? Expire(long pilotId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return null;
        }
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        long now = Now;
        var systems = Installed(world, pilotId);
        if (!systems.Exists(system => system.ExpiresAt <= now))
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var before = Snapshot(catalog, systems);
        var events = RemoveExpired(world, systems, now);
        var result = new ExpertSystemChange(before, Snapshot(catalog, systems), null, events);
        world.Commit();
        return result;
    }

    private List<ExpertSystemEvent> RemoveExpired(WorldAction world, List<SavedExpertSystem> systems, long now)
    {
        List<ExpertSystemEvent> events = [];
        foreach (var system in systems.Where(row => row.ExpiresAt <= now).ToArray())
        {
            PilotInboxStore.AddExpiredExpertSystem(world, system.PilotId, system.TypeId, system.ExpiresAt);
            world.Remove(system);
            systems.Remove(system);
            events.Add(new(system.TypeId, false));
        }
        if (events.Count > 0)
        {
            world.AfterCommit(signal.Publish);
        }
        return events;
    }

    private static List<SavedExpertSystem> Installed(WorldAction world, long pilotId) =>
        world.Filter<SavedExpertSystem>(system => system.PilotId == pilotId).OrderBy(system => system.TypeId).ToList();

    private static ExpertSystemState Snapshot(CatalogDbContext catalog, IReadOnlyCollection<SavedExpertSystem> systems) =>
        systems.Count == 0
            ? s_empty
            : new(
                systems
                    .OrderBy(system => system.TypeId)
                    .Select(system => new InstalledExpertSystem(system.TypeId, system.InstalledAt, system.ExpiresAt))
                    .ToArray(),
                systems
                    .SelectMany(system => ExpertSystemCatalog.Read(catalog, system.TypeId)?.Skills ?? [])
                    .GroupBy(skill => skill.TypeId)
                    .Select(group => group.MaxBy(skill => skill.Level)!)
                    .OrderBy(skill => skill.TypeId)
                    .ToArray()
            );

    private static Pilot RequirePilot(WorldAction world, long pilotId) =>
        world.Pilots.Find(pilotId) is { ActiveShip: { StoredInId: null } ship } pilot
        && ship.OwnerId == pilotId
        && world.Set<SavedCharacterDeletion>().Find(pilotId)?.DeletedAt is null
            ? pilot
            : throw new InvalidDataException("Select an active pilot to manage an Expert System.");

    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Expert Systems require the resident world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
}
