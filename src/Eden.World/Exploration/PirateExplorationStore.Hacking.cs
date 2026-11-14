using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Exploration;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Exploration;

internal sealed partial class PirateExplorationStore
{
    private static Pilot Pilot(WorldAction world, long id) =>
        world.Pilots.Find(id) ?? throw new InvalidDataException("Unknown hacking pilot.");

    private static HackingContact Contact(SavedHackingAttempt attempt) => JsonSerializer.Deserialize<HackingContact>(attempt.ContactJson)!;

    private static void ReleaseModule(WorldAction world, SavedHackingAttempt attempt)
    {
        var contact = Contact(attempt);
        if (world.Set<ActiveFittedModule>().Find(contact.Module.Id) is { } active)
        {
            world.Remove(active);
        }
    }

    private static (SavedExplorationSite Site, SavedHackingContainer Container, StaticSpaceObject Object) Access(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        long id,
        double range,
        long now
    )
    {
        var container = HackingRules.IsContainer(id) ? world.Set<SavedHackingContainer>().Find(id) : null;
        var site = container is null ? null : world.Set<SavedExplorationSite>().Find(container.SiteId);
        if (
            container is null
            || site is null
            || site.ReplaceAt is not null
            || site.ExpiresAt <= now
            || container.Destroyed
            || pilot.ActiveShip.Position is not { } position
            || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != site.SystemId
        )
        {
            throw new InvalidDataException("This exploration container is unavailable in your current system.");
        }
        var body = Object(catalog, site, container);
        if (
            site.Kind == (int)ExplorationKind.Ghost
            && site.ResponseAt is long response
            && now >= response + GhostSiteRules.VisibleCountdown
            && !GhostSiteRules.IsBonusCache(body.TypeId)
        )
        {
            throw new InvalidDataException("This research cache has detonated.");
        }
        if (
            new SpacePosition(position.X, position.Y, position.Z).DistanceTo(body.Position)
                - (catalog.FittingType(pilot.ActiveShip.TypeId)!.Radius ?? 0)
                - body.Radius
            > range
        )
        {
            throw new InvalidDataException("Move within the container's activation or transfer range.");
        }
        return (site, container, body);
    }

    public HackingContact Begin(long pilotId, long moduleId, long targetId, IReadOnlySet<long> locks, long now)
    {
        return LiveWorld.Execute(database.Path, () => BeginOwned(pilotId, moduleId, targetId, locks, now), nameof(Begin));
    }

    private HackingContact BeginOwned(long pilotId, long moduleId, long targetId, IReadOnlySet<long> locks, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
            var world = transaction.World;
            using CatalogDbContext catalog = new(database.CatalogPath);
            var pilot = Pilot(world, pilotId);
            var item = world.Set<OwnedItem>().Find(moduleId);
            if (
                item is not { Online: true, Singleton: true }
                || item.OwnerId != pilotId
                || item.LocationId != pilot.ActiveShipId
                || !InventoryDefinitions.IsFittedSlot(item.FlagId)
                || !HackingCatalog.Supported(catalog, item.TypeId, catalog.FittingType(item.TypeId)?.GroupId ?? 0)
            )
            {
                throw new InvalidDataException("Activate a fitted, online data or relic analyzer.");
            }
            if (!locks.Contains(targetId))
            {
                throw new InvalidDataException("Lock the exploration container before activating the analyzer.");
            }
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
            PilotFittingStore.CheckHull(catalog, item.TypeId, pilot.ActiveShip.TypeId);
            NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
            var module = HackingCatalog.Module(
                moduleId,
                item.TypeId,
                PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, item))
            );
            if (
                !double.IsFinite(module.DurationMs)
                || module.DurationMs <= 0
                || !double.IsFinite(module.Range)
                || module.Range <= 0
                || !double.IsFinite(module.CapacitorNeed)
                || module.CapacitorNeed < 0
            )
            {
                throw new InvalidDataException("Invalid fitted analyzer profile.");
            }
            var access = Access(world, catalog, pilot, targetId, module.Range, now);
            var kind = (ExplorationKind)access.Site.Kind;
            int skill = kind != ExplorationKind.Relic ? HackingRules.HackingSkill : HackingRules.ArchaeologySkill;
            if (!InventoryDefinitions.RequiredSkills.Any(p => catalog.FittingAttributes(item.TypeId).GetValueOrDefault(p.Skill) == skill))
            {
                throw new InvalidDataException("Use the analyzer appropriate to this data or relic container.");
            }
            var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
            if (
                access.Container.Hacked
                || (
                    world.Set<SavedHackingAttempt>().Find(pilotId) is not null
                    || world.Filter<SavedHackingAttempt>(a => a.ContainerId == targetId).Any()
                )
                || cooldown?.ReadyAt > now
                || world.Set<ActiveFittedModule>().Find(moduleId) is not null
            )
            {
                throw new InvalidDataException("The analyzer or container is already in use or has already been hacked.");
            }
            var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot);
            if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
            {
                throw new InvalidDataException("A destroyed ship cannot operate an analyzer.");
            }
            var definition = Definition(catalog, access.Site).Containers[access.Container.Index];
            var board = HackingRules.Create(
                access.Container.LootSeed ^ access.Container.Failures,
                Math.Clamp(definition.Difficulty, 0, 5),
                module.Coherence,
                module.Strength,
                module.Slots
            );
            double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, module.CapacitorNeed, now);
            long ready = checked(now + (long)Math.Ceiling(module.DurationMs * TimeSpan.TicksPerMillisecond));
            HackingContact contact = new(
                pilotId,
                pilot.ActiveShipId,
                access.Site.SystemId,
                targetId,
                kind,
                module,
                now,
                ready,
                board,
                capacitor
            );
            world.Add(
                new SavedHackingAttempt
                {
                    PilotId = pilotId,
                    ContainerId = targetId,
                    ContactJson = JsonSerializer.Serialize(contact),
                }
            );
            world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId });
            cooldown ??= world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
            cooldown.ReadyAt = ready;
            cooldown.Kind = ModuleCooldownKind.Cycle;
            Invalidate(world, access.Site.SystemId);
            transaction.Commit();
            return contact;
        }
    }

    public HackingTurn Act(long pilotId, HackingAction action, long now)
    {
        return LiveWorld.Execute(database.Path, () => ActOwned(pilotId, action, now), nameof(Act));
    }

    private HackingTurn ActOwned(long pilotId, HackingAction action, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldActionScope transaction = new(database, select: world => HackingParticipants(world, pilotId));
            var world = transaction.World;
            using CatalogDbContext catalog = new(database.CatalogPath);
            var attempt = world.Set<SavedHackingAttempt>().Find(pilotId) ?? throw new InvalidDataException("No hacking attempt is active.");
            var contact = Contact(attempt);
            var pilot = Pilot(world, pilotId);
            var access = Access(world, catalog, pilot, contact.ContainerId, contact.Module.Range, now);
            var item = world.Set<OwnedItem>().Find(contact.Module.Id);
            if (
                pilot.ActiveShipId != contact.ShipId
                || item is not { Online: true }
                || item.OwnerId != pilotId
                || item.LocationId != contact.ShipId
                || !InventoryDefinitions.IsFittedSlot(item.FlagId)
                || world.Set<ActiveFittedModule>().Find(item.Id) is null
            )
            {
                throw new InvalidDataException("The original ship and online analyzer are required to continue hacking.");
            }
            var next = HackingRules.Step(contact.Board, action);
            if (next.Outcome == HackingOutcome.Running)
            {
                attempt.ContactJson = JsonSerializer.Serialize(contact with { Board = next });
            }
            else
            {
                if (next.Outcome == HackingOutcome.Won)
                {
                    GrantLoot(world, catalog, access.Container, Definition(catalog, access.Site), pilotId);
                    access.Container.Hacked = true;
                    access.Container.OwnerId = pilotId;
                }
                else
                {
                    FailHack(world, catalog, access.Site, access.Container, now);
                }
                ReleaseModule(world, attempt);
                world.Remove(attempt);
                CompleteIfEmpty(world, access.Site, now);
            }
            transaction.Commit();
            return new(contact, contact.Board, next, action);
        }
    }

    public void Cancel(long pilotId, long now)
    {
        LiveWorld.Execute(database.Path, () => CancelOwned(pilotId, now), nameof(Cancel));
    }

    private void CancelOwned(long pilotId, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldActionScope transaction = new(database, select: world => HackingParticipants(world, pilotId));
            var world = transaction.World;
            var attempt = world.Set<SavedHackingAttempt>().Find(pilotId);
            if (attempt is null)
            {
                return;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            ReleaseModule(world, attempt);
            world.Remove(attempt);
            if (world.Set<SavedHackingContainer>().Find(attempt.ContainerId) is { Hacked: false, Destroyed: false } container)
            {
                var site = world.Set<SavedExplorationSite>().Find(container.SiteId)!;
                FailHack(world, catalog, site, container, now);
                CompleteIfEmpty(world, site, now);
            }
            transaction.Commit();
        }
    }

    public ExplorationContainerInventory Read(long pilotId, long containerId, long now)
    {
        return LiveWorld.Execute(database.Path, () => ReadOwned(pilotId, containerId, now), nameof(Read));
    }

    private ExplorationContainerInventory ReadOwned(long pilotId, long containerId, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            return WorldActionScope.Inspect(
                database,
                world =>
                {
                    var access = Access(world, catalog, Pilot(world, pilotId), containerId, HackingRules.LootRange, now);
                    if (!access.Container.Hacked || access.Container.OwnerId != pilotId)
                    {
                        throw new InvalidDataException("Hack this container successfully before retrieving its contents.");
                    }
                    var body = access.Object;
                    return new ExplorationContainerInventory(
                        new(
                            body.Id,
                            body.TypeId,
                            pilotId,
                            access.Site.SystemId,
                            (int)InventoryFlag.AutoFit,
                            1,
                            body.GroupId,
                            body.CategoryId,
                            PilotInventoryStore.Volume(catalog, body.TypeId),
                            true
                        ),
                        PilotInventoryStore.List(world, catalog, pilotId, containerId)
                    );
                }
            );
        }
    }

    public IReadOnlyList<InventoryChange> Take(long pilotId, long shipId, InventoryMoveRequest request, long now)
    {
        return LiveWorld.Execute(database.Path, () => TakeOwned(pilotId, shipId, request, now), nameof(Take));
    }

    private IReadOnlyList<InventoryChange> TakeOwned(long pilotId, long shipId, InventoryMoveRequest request, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
            var world = transaction.World;
            using CatalogDbContext catalog = new(database.CatalogPath);
            var pilot = Pilot(world, pilotId);
            var access = Access(world, catalog, pilot, request.SourceId, HackingRules.LootRange, now);
            if (!access.Container.Hacked || access.Container.OwnerId != pilotId)
            {
                throw new InvalidDataException("Only the successful hacker may retrieve this container's contents.");
            }
            var changes = SpaceLootTransfer.Take(
                world,
                catalog,
                pilot,
                shipId,
                request.SourceId,
                request.ItemIds,
                request.Quantity,
                (InventoryFlag)request.FlagId
            );
            world.SaveChanges();
            access.Container.Empty = !world.Filter<OwnedItem>(i => i.LocationId == request.SourceId && i.Quantity > 0).Any();
            CompleteIfEmpty(world, access.Site, now);
            transaction.Commit();
            return changes;
        }
    }
}
