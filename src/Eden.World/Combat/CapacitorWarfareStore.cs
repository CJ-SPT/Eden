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
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable]
internal sealed class CapacitorWarfareStore(WorldDatabase database, ICapacitorEvents events, NpcProfileCatalog profiles, TimeProvider clock)
    : IPilotCapacitorWarfare
{
    private sealed record Target(
        long Id,
        int TypeId,
        SpacePosition Position,
        IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
        Pilot? Pilot = null,
        WorldNpc? Npc = null
    );

    public bool IsOnline(long pilotId, long shipId, long moduleId) =>
        LiveReadModels.Find(database.Path)?.CapacitorModuleOnline(pilotId, shipId, moduleId, ReadOnline)
        ?? ReadOnline(pilotId, shipId, moduleId);

    private bool ReadOnline(long pilot, long ship, long module) =>
        WorldActionScope.Inspect(
            database,
            world =>
                world
                    .Set<OwnedItem>()
                    .Any(item =>
                        item.Id == module
                        && item.OwnerId == pilot
                        && item.LocationId == ship
                        && item.Singleton
                        && item.Online
                        && InventoryDefinitions.IsHighSlot(item.FlagId)
                    )
        );

    public CapacitorActivation Activate(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now
    ) => ActivateCycle(pilotId, moduleId, effect, targetId, locks, now, false);

    // Native overload activation remains gated on the damage/burnout owner.
    internal CapacitorActivation ActivateOverloaded(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now
    ) => ActivateCycle(pilotId, moduleId, effect, targetId, locks, now, true);

    private CapacitorActivation ActivateCycle(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now,
        bool overloaded
    )
    {
        using WorldActionScope transaction = new(database, select: world => CapacitorParticipants(world, pilotId, moduleId, targetId));
        var world = transaction.World;
        overloaded |= PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var source = Pilot(world, pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        int group = item is null ? 0 : catalog.FittingType(item.TypeId)?.GroupId ?? 0;
        if (
            source.ActiveShip.Position is null
            || item is null
            || item.OwnerId != pilotId
            || item.LocationId != source.ActiveShipId
            || !item.Singleton
            || !item.Online
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || !CapacitorModuleCatalog.Supported(catalog, item.TypeId, group)
            || CapacitorWarfareRules.Effect(group) != effect
        )
        {
            throw new InvalidDataException("Use an online fitted capacitor module on your active ship in space.");
        }
        if (targetId == source.ActiveShipId || !locks.Contains(targetId))
        {
            throw new InvalidDataException("Lock another nearby ship or NPC before applying a capacitor module.");
        }
        int system = CrimewatchPersistence.System(catalog, source.ActiveShip);
        var target = ReadTarget(world, catalog, targetId, system, now);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, item.Id, now);
        bool cached = !overloaded && world.CachedFittingSnapshot(pilotId, source.ActiveShipId, world.FitRevision(pilotId)) is not null;
        var calculation = cached
            ? null
            : FittingCalculation.Read(world, catalog, source, overloadedModules: overloaded ? new HashSet<long> { moduleId } : null);
        var module = CapacitorWarfareRules.Define(
            item.Id,
            item.TypeId,
            effect,
            PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, item), calculation)
        );
        var sourceAttributes = PilotFittingStore.ShipAttributes(world, catalog, source, calculation: calculation);
        var targetAttributes = target.Attributes;
        CheckAlive(source, sourceAttributes);
        if (target.Pilot is { } player)
        {
            CheckAlive(player, targetAttributes);
        }
        bool transfer = module.Kind == CapacitorModuleKind.Transfer;
        if (transfer && target.Pilot is null)
        {
            throw new InvalidDataException("Remote capacitor assistance currently requires another pilot's ship.");
        }
        if (
            targetAttributes.GetValueOrDefault(transfer ? DogmaAttributeId.DisallowAssistance : DogmaAttributeId.DisallowOffensiveModifiers)
            != 0
        )
        {
            throw new InvalidDataException("That ship cannot receive this capacitor effect.");
        }
        var a = source.ActiveShip.Position;
        double distance = Math.Max(
            0,
            new SpacePosition(a.X, a.Y, a.Z).DistanceTo(target.Position)
                - (catalog.FittingType(source.ActiveShip.TypeId)!.Radius ?? 0)
                - (catalog.FittingType(target.TypeId)!.Radius ?? 0)
        );
        if (
            distance > Math.Min(TackleDefinitions.MaximumDistance, sourceAttributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
            || distance > module.Optimal && module.Falloff == 0
        )
        {
            throw new InvalidDataException("The capacitor target is outside range.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The capacitor module is still cycling.");
        }
        long end = checked(now + (long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        if (transfer)
        {
            AssistancePersistence.Apply(world, catalog, source, target.Pilot!, now, end);
        }
        var decision =
            !transfer && target.Pilot is { } victim ? CrimewatchPersistence.CheckPlayer(world, catalog, source, victim, system, now) : null;
        var npcEnergy = target.Npc is null ? null : world.Set<WorldNpcEnergy>().Find(targetId);
        var targetPool = target.Npc is null
            ? new CapacitorPool(
                ShipResources.Capacitor(world, targetId, targetAttributes, now),
                targetAttributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity)
            )
            : NpcCapacitorRules.Read(NpcCapacitorRules.Profile(targetAttributes), npcEnergy?.State(), now);
        var applied = CapacitorWarfareRules.Apply(
            module,
            new(
                ShipResources.Capacitor(world, source.ActiveShipId, sourceAttributes, now),
                sourceAttributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity)
            ),
            targetPool,
            distance,
            targetAttributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            targetAttributes.GetValueOrDefault(
                transfer ? DogmaAttributeId.RemoteCapacitorImpedance : DogmaAttributeId.EnergyWarfareResistance,
                1
            )
        );
        SaveEnergy(source.ActiveShipId, applied.Source);
        if (target.Pilot is { } recipient)
        {
            SaveEnergy(targetId, applied.Target);
            if (!transfer)
            {
                CrimewatchPersistence.Player(world, catalog, source.Id, recipient.Id, system, decision!, now, end);
            }
        }
        else
        {
            npcEnergy ??= world.Add(new WorldNpcEnergy { NpcId = targetId }).Entity;
            npcEnergy.Charge = applied.Target;
            npcEnergy.UpdatedAt = Math.Max(now, npcEnergy.UpdatedAt);
            CrimewatchPersistence.Npc(world, source.Id, system, end);
            NpcHostility.Record(world, catalog, target.Npc!, source.ActiveShipId, now);
        }
        if (!transfer)
        {
            Eden.World.Drones.DroneCombatPersistence.Module(world, source.ActiveShipId, targetId, target.Pilot is not null, now);
        }
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = end;
        if (world.Set<ActiveFittedModule>().Find(moduleId) is null)
        {
            world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = source.ActiveShipId });
        }
        var heat = overloaded ? ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation!, moduleId, now, end) : null;
        world.SaveChanges();
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, source.ActiveShipId, heat);
        }
        events.Publish(new(source.Id, source.ActiveShipId));
        if (target.Pilot is { } receiver)
        {
            events.Publish(new(receiver.Id, targetId));
        }
        return new(
            new(module, source.ActiveShipId, targetId, now, end, target.Pilot is null ? TackleTargetKind.Npc : TackleTargetKind.Ship),
            applied.Source,
            applied.Target,
            applied.Amount
        );

        void CheckAlive(Pilot pilot, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
        {
            if (
                PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed
                || world.Set<ShipLoss>().Any(l => l.ShipId == pilot.ActiveShipId)
            )
            {
                throw new InvalidDataException("A destroyed ship cannot send or receive capacitor effects.");
            }
        }
        void SaveEnergy(long ship, double charge)
        {
            var saved = world.Set<ShipEnergy>().Find(ship) ?? world.Add(new ShipEnergy { ShipId = ship }).Entity;
            saved.Charge = charge;
            saved.UpdatedAt = Math.Max(saved.UpdatedAt, now);
        }
    }

    private static Pilot Pilot(WorldAction world, long id) => world.Pilots.Single(p => p.Id == id);

    private Target ReadTarget(WorldAction world, CatalogDbContext catalog, long id, int system, long now)
    {
        var player = world.Pilots.SingleOrDefault(p => p.ActiveShipId == id);
        if (player is not null)
        {
            var position = player.ActiveShip.Position;
            if (
                position is null
                || CrimewatchPersistence.System(catalog, player.ActiveShip) != system
                || world.Set<SavedLogoff>().Any(l => l.ShipId == id && l.ExpiresAt <= now)
            )
            {
                throw new InvalidDataException("The capacitor target is unavailable.");
            }
            return new(
                id,
                player.ActiveShip.TypeId,
                new(position.X, position.Y, position.Z),
                PilotFittingStore.ShipAttributes(world, catalog, player),
                player
            );
        }
        var npc = world.Set<WorldNpc>().Find(id);
        if (
            npc is null
            || npc.DestroyedAt.HasValue
            || npc.RetiredAt.HasValue
            || npc.SystemId != system
            || npc.Navigation == NpcNavigationState.Warping
        )
        {
            throw new InvalidDataException("The capacitor target is unavailable.");
        }
        var profile = profiles.Read(catalog, npc.TypeId);
        if (npc.StructureDamage >= profile.Defense.Structure)
        {
            throw new InvalidDataException("A destroyed NPC cannot receive capacitor effects.");
        }
        return new(id, npc.TypeId, NpcMotion.Position(npc), PlayerEwarStore.NpcAttributes(world, id, profile.Attributes), Npc: npc);
    }

    public void Stop(long pilotId, long shipId, long moduleId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        if (world.Set<OwnedItem>().Any(i => i.Id == moduleId && i.OwnerId == pilotId && i.LocationId == shipId))
        {
            ShipHeatPersistence.CancelSource(world, pilotId, shipId, moduleId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            if (world.Set<ActiveFittedModule>().SingleOrDefault(i => i.ModuleId == moduleId && i.ShipId == shipId) is { } active)
            {
                world.Remove(active);
            }
            world.SaveChanges();
        }
        transaction.Commit();
    }

    private static TransactionParticipants CapacitorParticipants(WorldAction world, long pilot, long module, long target)
    {
        long source = world.Pilots.Where(row => row.Id == pilot).Select(row => row.ActiveShipId).SingleOrDefault();
        long recipient = world.Pilots.Where(row => row.ActiveShipId == target).Select(row => row.Id).SingleOrDefault();
        long previous = world.Set<ActiveFittedModule>().Where(row => row.ModuleId == module).Select(row => row.ShipId).SingleOrDefault();
        return new(
            pilots: recipient == 0 ? [pilot] : [pilot, recipient],
            ships: new[] { source, previous, recipient == 0 ? 0 : target }.Where(id => id != 0),
            resources: recipient == 0 ? [new(SharedSimulationResource.Npc, target)] : []
        );
    }
}
