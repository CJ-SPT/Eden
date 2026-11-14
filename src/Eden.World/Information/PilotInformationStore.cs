using System.Text.Json;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Information;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Information;

[Injectable]
internal sealed partial class PilotInformationStore(WorldDatabase database, ICooperativeFleets? fleets = null) : IPilotInformation
{
    private readonly NpcProfileCatalog _npcs = new();

    private static Pilot Pilot(WorldAction world, long id) =>
        world.Pilots.Find(id) ?? throw new InvalidDataException("Unknown information pilot.");

    private static SpacePosition Position(Pilot pilot) =>
        pilot.ActiveShip.Position is { } p ? new(p.X, p.Y, p.Z) : throw new InvalidDataException("Use information modules while in space.");

    private static int System(CatalogDbContext catalog, Pilot pilot) =>
        pilot.ActiveShip.Position?.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;

    public InformationActivation Begin(long pilotId, long moduleId, DogmaEffect effect, long targetId, IReadOnlySet<long> locks, long now)
    {
        using WorldActionScope transaction = new(database, Participants(pilotId, targetId));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        _ = Position(pilot);
        var item = world.Set<OwnedItem>().Find(moduleId);
        int group = item is null ? 0 : catalog.FittingType(item.TypeId)?.GroupId ?? 0;
        if (
            item is not { Singleton: true, Online: true }
            || item.OwnerId != pilotId
            || item.LocationId != pilot.ActiveShipId
            || !InventoryDefinitions.IsFittedSlot(item.FlagId)
            || !InformationCatalog.Supported(catalog, item.TypeId, group)
            || InformationRules.Effect(group) != effect
        )
        {
            throw new InvalidDataException("Use an online information module fitted to your active ship.");
        }
        PilotFittingStore.CheckHull(catalog, item.TypeId, pilot.ActiveShip.TypeId);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        var attributes = PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, item));
        var module = InformationRules.Define(moduleId, item.TypeId, effect, attributes);
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot operate information modules.");
        }
        bool targeted = effect is not (DogmaEffect.PassiveTargeting or DogmaEffect.AutomaticTargeting);
        if (targeted && (targetId == pilot.ActiveShipId || !locks.Contains(targetId)))
        {
            throw new InvalidDataException("Lock the target before activating this module.");
        }
        if (targeted)
        {
            CheckTarget(world, catalog, pilot, targetId, module, now);
        }
        if (
            effect == DogmaEffect.TractorBeam
            && world.Filter<SavedInformationCycle>(c => c.TargetId == targetId && c.Effect == DogmaEffect.TractorBeam).Any()
        )
        {
            throw new InvalidDataException("This container is already being tractored.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now || world.Set<SavedInformationCycle>().Find(moduleId) is not null)
        {
            throw new InvalidDataException("This module is already cycling.");
        }
        long end = checked(now + (long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, module.CapacitorNeed, now);
        world.Add(
            new SavedInformationCycle
            {
                ModuleId = moduleId,
                PilotId = pilotId,
                ShipId = pilot.ActiveShipId,
                TargetId = targetId,
                Effect = effect,
                StartedAt = now,
                ReadyAt = end,
                ModuleJson = JsonSerializer.Serialize(module),
            }
        );
        world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId });
        cooldown ??= world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.ReadyAt = end;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        InformationCycle cycle = new(
            pilotId,
            pilot.ActiveShipId,
            targetId,
            module,
            now,
            end,
            effect == DogmaEffect.TractorBeam ? world.Set<WorldWreck>().Find(targetId)!.OwnerId : null
        );
        if (effect == DogmaEffect.TractorBeam && LiveWorld.Find(database.Path) is { } live)
        {
            var wreck = world.Set<WorldWreck>().Find(targetId)!;
            world.AfterCommit(() =>
                live.Tractors.Start(
                    cycle,
                    Position(pilot),
                    WreckPosition(wreck),
                    (catalog.FittingType(pilot.ActiveShip.TypeId)!.Radius ?? 0) + (catalog.FittingType(wreck.TypeId)!.Radius ?? 0),
                    fleets,
                    wreck.SystemId
                )
            );
        }
        transaction.Commit();
        return new(cycle, capacitor);
    }

    private static TransactionParticipants Participants(long pilotId, long targetId) =>
        targetId == 0
            ? new(pilots: [pilotId])
            : new(
                pilots: [pilotId],
                ships: [targetId],
                resources: [new(SharedSimulationResource.Npc, targetId), new(SharedSimulationResource.Wreck, targetId)]
            );

    private bool CanTractor(long pilot, long owner) =>
        pilot == owner || fleets?.Identity(pilot) is { } source && fleets.Identity(owner)?.FleetId == source.FleetId;

    private void CheckTarget(WorldAction world, CatalogDbContext catalog, Pilot pilot, long targetId, InformationModule module, long now)
    {
        int system = System(catalog, pilot);
        SpacePosition targetPosition;
        double radius;
        if (module.Effect is DogmaEffect.TractorBeam or DogmaEffect.Salvaging)
        {
            var wreck = world.Set<WorldWreck>().Find(targetId);
            if (
                wreck is null
                || wreck.SystemId != system
                || wreck.ExpiresAt <= now
                || module.Effect == DogmaEffect.Salvaging && (wreck.SalvagedAt is not null || wreck.SalvageJson is null)
                || module.Effect == DogmaEffect.TractorBeam && !CanTractor(pilot.Id, wreck.OwnerId)
            )
            {
                throw new InvalidDataException("This wreck is unavailable or cannot be tractored by this pilot.");
            }
            targetPosition = WreckPosition(wreck);
            radius = catalog.FittingType(wreck.TypeId)!.Radius ?? 0;
            if (
                module.Effect == DogmaEffect.Salvaging
                && InformationRules.SalvageChance(
                    module.AccessBonus,
                    catalog.FittingAttributes(wreck.TypeId).GetValueOrDefault(DogmaAttributeId.AccessDifficulty)
                ) <= 0
            )
            {
                throw new InvalidDataException("This wreck is too difficult for the fitted salvager.");
            }
        }
        else if (
            world.Filter<Pilot>(p => p.ActiveShipId == targetId).SingleOrDefault() is { ActiveShip.Position: { } position } target
            && System(catalog, target) == system
        )
        {
            if (
                world.Set<ShipLoss>().Find(targetId) is not null
                || world.Filter<SavedLogoff>(l => l.ShipId == targetId && l.ExpiresAt <= now).Any()
            )
            {
                throw new InvalidDataException("The scan target is no longer available.");
            }
            targetPosition = new(position.X, position.Y, position.Z);
            radius = catalog.FittingType(target.ActiveShip.TypeId)!.Radius ?? 0;
        }
        else if (
            module.Effect == DogmaEffect.CargoScan
            && world.Set<WorldWreck>().Find(targetId) is { } container
            && container.SystemId == system
            && container.ExpiresAt > now
        )
        {
            targetPosition = WreckPosition(container);
            radius = catalog.FittingType(container.TypeId)!.Radius ?? 0;
        }
        else if (
            world.Set<WorldNpc>().Find(targetId) is { DestroyedAt: null, RetiredAt: null } npc
            && npc.SystemId == system
            && npc.Navigation != NpcNavigationState.Warping
        )
        {
            targetPosition = NpcMotion.Position(npc);
            radius = catalog.FittingType(npc.TypeId)!.Radius ?? 0;
        }
        else
        {
            throw new InvalidDataException("Scan a live pilot ship or a cargo container in the same system.");
        }
        double distance = Math.Max(
            0,
            Position(pilot).DistanceTo(targetPosition) - (catalog.FittingType(pilot.ActiveShip.TypeId)!.Radius ?? 0) - radius
        );
        // No undocumented falloff sampling is substituted for the ship scanner.
        if (distance > module.Range)
        {
            throw new InvalidDataException("The target is outside this module's verified range.");
        }
    }

    public InformationCompletion Complete(long pilotId, long moduleId, long expectedReadyAt, long now)
    {
        using WorldActionScope transaction = new(
            database,
            select: world => Participants(pilotId, world.Set<SavedInformationCycle>().Find(moduleId)?.TargetId ?? 0)
        );
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var saved = world.Set<SavedInformationCycle>().Find(moduleId);
        if (saved is null || saved.PilotId != pilotId || saved.ReadyAt != expectedReadyAt || now < expectedReadyAt)
        {
            throw new InvalidDataException("This information cycle is not ready or has already completed.");
        }
        var pilot = Pilot(world, pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot.ActiveShipId != saved.ShipId
            || item is not { Online: true, Singleton: true }
            || item.OwnerId != pilotId
            || item.LocationId != saved.ShipId
            || !InventoryDefinitions.IsFittedSlot(item.FlagId)
            || world.Set<ActiveFittedModule>().Find(moduleId)?.ShipId != saved.ShipId
        )
        {
            throw new InvalidDataException("The information module is no longer active on this ship.");
        }
        var module =
            JsonSerializer.Deserialize<InformationModule>(saved.ModuleJson)
            ?? throw new InvalidDataException("Invalid saved information module cycle.");
        if (module.ItemId != moduleId || module.TypeId != item.TypeId || module.Effect != saved.Effect)
        {
            throw new InvalidDataException("The saved information module does not match its fitted item.");
        }
        InformationCompletion result = new();
        if (saved.Effect is DogmaEffect.CargoScan or DogmaEffect.ShipScan)
        {
            CheckTarget(world, catalog, pilot, saved.TargetId, module, now);
            if (saved.Effect == DogmaEffect.CargoScan)
            {
                var cargo = world
                    .Filter<OwnedItem>(i => i.LocationId == saved.TargetId)
                    .AsEnumerable()
                    .Where(i => ShipHoldRules.Definition(i.FlagId) is not null)
                    .OrderBy(i => i.Id)
                    .Select(i => new ScanItem(i.TypeId, i.Quantity))
                    .ToArray();
                var npcCargo = world
                    .Filter<WorldNpcMiningCargo>(c => c.NpcId == saved.TargetId && c.Quantity > 0)
                    .OrderBy(c => c.TypeId)
                    .Select(c => new ScanItem(c.TypeId, checked((int)c.Quantity)))
                    .ToArray();
                var storedShips = world
                    .Filter<Ship>(s => s.StoredInId == saved.TargetId)
                    .OrderBy(s => s.Id)
                    .Select(s => new ScanItem(s.TypeId, 1))
                    .ToArray();
                result = new(Cargo: new(saved.TargetId, cargo.Concat(npcCargo).Concat(storedShips).ToArray()));
            }
            else
            {
                if (world.Filter<Pilot>(p => p.ActiveShipId == saved.TargetId).SingleOrDefault() is { } target)
                {
                    var values = PilotFittingStore.ShipAttributes(world, catalog, target);
                    var hardware = world
                        .Filter<OwnedItem>(i => i.LocationId == saved.TargetId)
                        .AsEnumerable()
                        .Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId))
                        .OrderBy(i => i.FlagId)
                        .Select(i => new ScanItem(i.TypeId, i.Quantity))
                        .ToArray();
                    result = new(
                        new(
                            saved.TargetId,
                            ShipResources.Capacitor(world, saved.TargetId, values, now),
                            values.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
                            hardware
                        )
                    );
                }
                else
                {
                    var npc = world.Set<WorldNpc>().Find(saved.TargetId)!;
                    var values = PlayerEwarStore.NpcAttributes(world, npc.Id, _npcs.Read(catalog, npc.TypeId).Attributes);
                    var energy = NpcCapacitorRules.Read(
                        NpcCapacitorRules.Profile(values),
                        world.Set<WorldNpcEnergy>().Find(npc.Id)?.State(),
                        now
                    );
                    result = new(new(npc.Id, energy.Charge, energy.Capacity, []));
                }
            }
        }
        if (saved.Effect == DogmaEffect.Salvaging)
        {
            CheckTarget(world, catalog, pilot, saved.TargetId, module, now);
            result = new(Salvage: Salvage(world, catalog, pilot, saved.TargetId, module, now));
        }
        world.Remove(saved);
        RemoveMarker(world, moduleId, saved.ShipId);
        transaction.Commit();
        return result;
    }

    public void Stop(long pilotId, long moduleId, long expectedReadyAt)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        if (world.Set<SavedInformationCycle>().Find(moduleId) is { } cycle && cycle.PilotId == pilotId && cycle.ReadyAt == expectedReadyAt)
        {
            RemoveMarker(world, moduleId, cycle.ShipId);
            world.Remove(cycle);
            world.AfterCommit(() => LiveWorld.Find(database.Path)?.Tractors.Stop(moduleId));
        }
        transaction.Commit();
    }

    public bool IsActive(InformationCycle cycle) =>
        LiveReadModels.Find(database.Path)?.InformationActive(cycle, () => ReadActive(cycle)) ?? ReadActive(cycle);

    private static void RemoveMarker(WorldAction world, long module, long ship)
    {
        if (world.Set<ActiveFittedModule>().Find(module) is { } marker && marker.ShipId == ship)
        {
            world.Remove(marker);
        }
    }

    private bool ReadActive(InformationCycle cycle) =>
        WorldActionScope.Inspect(
            database,
            world =>
                world.Set<SavedInformationCycle>().Find(cycle.Module.ItemId) is { } c
                && c.PilotId == cycle.PilotId
                && c.ShipId == cycle.ShipId
                && c.ReadyAt == cycle.ReadyAt
                && world.Set<OwnedItem>().Find(c.ModuleId) is { Online: true, Singleton: true } item
                && item.LocationId == cycle.ShipId
                && item.OwnerId == cycle.PilotId
                && world.Set<ActiveFittedModule>().Find(c.ModuleId)?.ShipId == cycle.ShipId
        );

    public IReadOnlyList<long> Hostiles(long pilotId, long now) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                long ship = Pilot(world, pilotId).ActiveShipId;
                return world
                    .Filter<SavedPilotHostility>(h => h.TargetShipId == ship && h.ExpiresAt > now && h.LastAttackAt > 0)
                    .Select(h => h.SourceShipId)
                    .AsEnumerable()
                    .Concat(
                        world
                            .Filter<NpcAggression>(a => a.TargetShipId == ship && a.LastTargetId == ship && a.LastShotAt != null)
                            .Select(a => a.NpcId)
                    )
                    .Distinct()
                    .Order()
                    .ToArray();
            }
        );

    public IReadOnlyList<MiningScanResult> Survey(long pilotId, long now)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(
            database,
            world =>
            {
                var pilot = Pilot(world, pilotId);
                var position = Position(pilot);
                InformationRules.RequireMiningSurvey(PilotFittingStore.ShipAttributes(world, catalog, pilot));
                int system = System(catalog, pilot);
                return world
                    .Filter<WorldAsteroid>(a => a.SystemId == system && a.Quantity > 0)
                    .AsEnumerable()
                    .Where(a => !MissionMiningRules.IsDeposit(a.Id) || IsPrivateDepositVisible(world, a.Id, pilotId, now))
                    .Where(a => position.DistanceTo(new(a.X, a.Y, a.Z)) <= InformationRules.MiningScanRange)
                    .OrderBy(a => a.Id)
                    .Select(a => new MiningScanResult(a.Id, a.TypeId, a.Quantity))
                    .ToArray();
            }
        );
    }

    private static bool IsPrivateDepositVisible(WorldAction world, long id, long pilot, long now) =>
        world.Set<SavedMission>().Find((id - MissionMiningRules.FirstDepositId) / MissionMiningRules.DepositsPerMission)
            is { State: MissionState.Accepted } mission
        && mission.PilotId == pilot
        && mission.ExpiresAt > now;
}
