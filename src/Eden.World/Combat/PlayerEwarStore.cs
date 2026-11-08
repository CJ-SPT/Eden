using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable]
internal sealed class PlayerEwarStore(WorldDatabase database, INpcEwarRoll rolls) : IPilotElectronicWarfare
{
    public bool IsOnline(long pilotId, long shipId, long moduleId) =>
        LiveReadModels.Find(database.Path)?.EwarModuleOnline(pilotId, shipId, moduleId, ReadOnline)
        ?? ReadOnline(pilotId, shipId, moduleId);

    private bool ReadOnline(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<OwnedItem>()
            .Any(i =>
                i.Id == moduleId
                && i.OwnerId == pilotId
                && i.LocationId == shipId
                && i.Singleton
                && i.Online
                && i.FlagId >= (int)InventoryFlag.MediumSlot0
                && i.FlagId <= (int)InventoryFlag.MediumSlot7
            );
    }

    private sealed record Target(
        TackleTarget Identity,
        SpacePosition Position,
        int TypeId,
        IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
        Pilot? Pilot = null,
        WorldNpc? Npc = null
    );

    public ElectronicWarfareActivation Activate(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        TackleTarget? target,
        IReadOnlySet<long> lockedTargets,
        long now
    )
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        int group = item is null ? 0 : catalog.FittingType(item.TypeId)!.GroupId;
        if (
            pilot.ActiveShip.Position is not { } position
            || item is null
            || item.OwnerId != pilotId
            || item.LocationId != pilot.ActiveShipId
            || !item.Singleton
            || !item.Online
            || !InventoryDefinitions.IsMediumSlot(item.FlagId)
            || !PlayerEwarCatalog.Supported(catalog, item.TypeId, group)
            || FittingCatalog.DefaultEffect(catalog, item.TypeId) != effect
        )
        {
            throw new InvalidDataException("Use an online fitted electronic warfare module on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, item.Id, now);
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var module = PlayerEwarRules.Define(item.Id, item.TypeId, effect, calculation.Graph.Attributes(item.Id));
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate electronic warfare.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && cooldown.ReadyAt > now)
        {
            throw new InvalidDataException("The module is still cycling or reloading.");
        }
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        SpacePosition origin = new(position.X, position.Y, position.Z);
        double radius = catalog.FittingType(pilot.ActiveShip.TypeId)!.Radius ?? 0;
        if (target is null || target.Id == pilot.ActiveShipId || !lockedTargets.Contains(target.Id))
        {
            throw new InvalidDataException("Lock a nearby ship or NPC before applying electronic warfare.");
        }
        var victim =
            ReadTarget(world, catalog, target, system, now)
            ?? throw new InvalidDataException("The electronic warfare target is unavailable.");
        if (Distance(victim) > Math.Min(TackleDefinitions.MaximumDistance, ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange)))
        {
            throw new InvalidDataException("The target is outside targeting range.");
        }
        if (Distance(victim) > module.Optimal && module.Falloff == 0)
        {
            throw new InvalidDataException("The target is outside electronic warfare range.");
        }
        // Legality, resource payment and effect state share one transaction.
        var decision = victim.Pilot is null ? null : CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim.Pilot, system, now);
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, module.CapacitorNeed, now);
        long expires = checked(now + (long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        world.Set<WorldPlayerEwar>().Where(e => e.ModuleId == moduleId).ExecuteDelete();
        var applied = PlayerEwarRules.Attempt(module, Distance(victim), victim.Attributes, rolls.Next());
        ElectronicWarfareContact contact = new(pilotId, pilot.ActiveShipId, module, victim.Identity, applied, now, expires);
        Eden.World.Drones.DroneCombatPersistence.Module(world, pilot.ActiveShipId, victim.Identity.Id, victim.Pilot is not null, now);
        world.Add(
            new WorldPlayerEwar
            {
                ModuleId = moduleId,
                TargetId = victim.Identity.Id,
                PilotId = pilotId,
                SourceShipId = pilot.ActiveShipId,
                SystemId = system,
                TargetKind = victim.Identity.Kind,
                ContactJson = JsonSerializer.Serialize(contact),
            }
        );
        if (victim.Pilot is { } player)
        {
            CrimewatchPersistence.Player(world, pilotId, player.Id, system, decision!, now, expires);
        }
        if (victim.Npc is { } npc)
        {
            CrimewatchPersistence.Npc(world, pilotId, system, expires);
            NpcHostility.Record(world, catalog, npc, pilot.ActiveShipId, now);
        }
        if (world.Set<ActiveFittedModule>().Find(moduleId) is null)
        {
            world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId });
        }
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = expires;
        world.SaveChanges();
        transaction.Commit();
        return new(module, target, capacitor, [contact]);

        double Distance(Target victim) =>
            Math.Max(0, origin.DistanceTo(victim.Position) - radius - (catalog.FittingType(victim.TypeId)!.Radius ?? 0));
    }

    private static Target? ReadTarget(WorldDbContext world, CatalogDbContext catalog, TackleTarget target, int system, long now)
    {
        if (target.Kind == TackleTargetKind.Npc)
        {
            var npc = world.Set<WorldNpc>().Find(target.Id);
            return
                npc is null
                || npc.DestroyedAt.HasValue
                || npc.RetiredAt.HasValue
                || npc.SystemId != system
                || npc.Navigation == NpcNavigationState.Warping
                ? null
                : new(target, NpcMotion.Position(npc), npc.TypeId, PilotFittingStore.ReadAttributes(catalog, npc.TypeId), Npc: npc);
        }
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.ActiveShipId == target.Id);
        if (
            pilot?.ActiveShip.Position is not { } position
            || CrimewatchPersistence.System(catalog, pilot.ActiveShip) != system
            || world.Set<ShipLoss>().Any(l => l.ShipId == target.Id)
            || world.Set<SavedLogoff>().Any(l => l.ShipId == target.Id && l.ExpiresAt <= now)
        )
        {
            return null;
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot.Id);
        return PilotHealthStore.Read(world, target.Id, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed
            ? null
            : new(target, new(position.X, position.Y, position.Z), pilot.ActiveShip.TypeId, attributes, pilot);
    }

    internal static IReadOnlyList<ElectronicWarfareContact> Active(WorldDbContext world, long targetId) =>
        world
            .Set<WorldPlayerEwar>()
            .Where(e => e.TargetId == targetId)
            .OrderBy(e => e.ModuleId)
            .AsNoTracking()
            .AsEnumerable()
            .Select(e => e.Contact())
            .ToArray();

    public IReadOnlyList<ElectronicWarfareContact> Incoming(long targetId)
    {
        if (LiveReadModels.Find(database.Path) is { } reads)
        {
            return reads.PlayerEwarIncoming(targetId);
        }
        using WorldDbContext world = new(database.Path);
        return Active(world, targetId);
    }

    public void Sweep(long now)
    {
        if (LiveReadModels.Find(database.Path) is { } reads && !reads.HasPlayerEwar())
        {
            return;
        }
        using WorldDbContext world = new(database.Path);
        if (!world.Set<WorldPlayerEwar>().Any())
        {
            return;
        }
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (var row in world.Set<WorldPlayerEwar>().ToArray())
        {
            var source = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == row.PilotId);
            var item = world.Set<OwnedItem>().Find(row.ModuleId);
            // Paid rows belong to the source controller, including at repeat
            // boundaries. A victim advancing first cannot expire a continuous effect.
            bool valid =
                source?.ActiveShip.Position is not null
                && source.ActiveShipId == row.SourceShipId
                && CrimewatchPersistence.System(catalog, source.ActiveShip) == row.SystemId
                && item is { Online: true, Singleton: true }
                && item.OwnerId == row.PilotId
                && item.LocationId == row.SourceShipId
                && InventoryDefinitions.IsMediumSlot(item.FlagId)
                && !world.Set<ShipLoss>().Any(l => l.ShipId == row.SourceShipId)
                && ReadTarget(world, catalog, new(row.TargetId, row.TargetKind), row.SystemId, now) is not null;
            if (!valid)
            {
                world.Remove(row);
            }
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public void Stop(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        world.Set<WorldPlayerEwar>().Where(e => e.PilotId == pilotId && e.SourceShipId == shipId && e.ModuleId == moduleId).ExecuteDelete();
        if (world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            world.Set<ActiveFittedModule>().Where(m => m.ModuleId == moduleId && m.ShipId == shipId).ExecuteDelete();
        }
        transaction.Commit();
    }

    public void Cancel(long pilotId, long shipId, bool incoming = false)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var modules = world
            .Set<WorldPlayerEwar>()
            .Where(e => e.PilotId == pilotId && e.SourceShipId == shipId)
            .Select(e => e.ModuleId)
            .Distinct()
            .ToArray();
        world
            .Set<WorldPlayerEwar>()
            .Where(e =>
                e.PilotId == pilotId && e.SourceShipId == shipId
                || incoming && e.TargetId == shipId && e.TargetKind == TackleTargetKind.Ship
            )
            .ExecuteDelete();
        world.Set<ActiveFittedModule>().Where(m => modules.Contains(m.ModuleId) && m.ShipId == shipId).ExecuteDelete();
        transaction.Commit();
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> NpcAttributes(
        WorldDbContext world,
        long npcId,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double missileRange = 0
    ) => PlayerEwarRules.NpcAttributes(attributes, Active(world, npcId), missileRange);
}
