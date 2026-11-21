using System.Security.Cryptography;
using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Information;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    private void RequireResident()
    {
        if (LiveWorld.Find(database.Path) is null)
        {
            throw new InvalidDataException("Utility and mining drones require the resident world.");
        }
    }

    internal static DroneWork ReadWork(WorldDrone drone) =>
        drone.WorkJson is null ? new() : JsonSerializer.Deserialize<DroneWork>(drone.WorkJson)!;

    internal static void WriteWork(WorldDrone drone, DroneWork work) => drone.WorkJson = JsonSerializer.Serialize(work);

    internal static DroneWorkEffect? WorkEffect(WorldDrone drone, DroneProfile profile)
    {
        var work = ReadWork(drone);
        if (
            profile.Utility is not { } utility
            || drone.ControllerShipId == 0
            || drone.Activity != UtilityDroneRules.Activity(utility.Kind)
            || work.StartedAt == 0
            || work.Returning
            || work.TargetId == 0
        )
        {
            return null;
        }
        double duration =
            utility.Mining || utility.Kind == UtilityDroneKind.Salvaging ? utility.DurationMilliseconds : utility.EffectMilliseconds;
        return drone.UpdatedAt >= work.StartedAt + duration * TimeSpan.TicksPerMillisecond
            ? null
            : new(utility.Kind, work.TargetId, work.StartedAt, duration);
    }

    private static void ClearDroneEffects(WorldAction world, long id)
    {
        world.RemoveRange(world.Filter<WorldPlayerEwar>(e => e.ModuleId == id));
        world.RemoveRange(world.Filter<WorldTackle>(e => e.ModuleId == id));
    }

    private static void CancelWork(WorldAction world, WorldDrone drone)
    {
        ClearDroneEffects(world, drone.ItemId);
        if (drone.WorkJson is not null)
        {
            var work = ReadWork(drone);
            WriteWork(
                drone,
                new(DueAt: work.DueAt, Returning: work.Cargo?.Count > 0, Cargo: work.Cargo, FighterMobility: work.FighterMobility)
            );
        }
    }

    private bool ValidateWorkOrder(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        IReadOnlyList<WorldDrone?> drones,
        DroneOrder order,
        long? target,
        IReadOnlySet<long> locks,
        double range,
        int system,
        long now
    )
    {
        var profiles = drones
            .Select(d => DroneCatalog.Skilled(world, catalog, pilot.Id, world.Set<OwnedItem>().Find(d!.ItemId)!.TypeId))
            .ToArray();
        if (profiles.Any(profile => profile.Utility is not null))
        {
            RequireResident();
        }
        bool work =
            order is DroneOrder.Mine or DroneOrder.MineRepeatedly or DroneOrder.Salvage
            || order == DroneOrder.Engage && profiles.Any(p => p.Utility?.Repair == true);
        if (!work)
        {
            if (order == DroneOrder.Engage && profiles.Any(p => p.Utility is { Offensive: false }))
            {
                throw new InvalidDataException("Use the mining or salvage command for these drones.");
            }
            return false;
        }
        RequireResident();
        var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot));
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, defense, now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot command drones.");
        }
        foreach (var profile in profiles)
        {
            var utility = profile.Utility ?? throw new InvalidDataException("Choose drones with the requested capability.");
            bool matches = order switch
            {
                DroneOrder.Mine or DroneOrder.MineRepeatedly => utility.Mining,
                DroneOrder.Salvage => utility.Kind == UtilityDroneKind.Salvaging,
                _ => utility.Repair,
            };
            if (!matches)
            {
                throw new InvalidDataException("Every selected drone must support the requested work.");
            }
            if (order == DroneOrder.Salvage && target is null)
            {
                continue;
            }
            if (target is not long id || !locks.Contains(id))
            {
                throw new InvalidDataException("Lock the drone work target first.");
            }
            var endpoint =
                WorkTarget(world, catalog, pilot, utility, id, system, now)
                ?? throw new InvalidDataException("The drone work target is unavailable.");
            if (utility.Repair)
            {
                if (drones.Any(drone => drone!.ItemId == id))
                {
                    throw new InvalidDataException("A repair drone cannot repair itself.");
                }
                var recipient = endpoint.Pilot ?? (endpoint.Drone is { } repaired ? world.Pilots.Find(repaired.PilotId) : null);
                if (recipient is not null && recipient.Id != pilot.Id)
                {
                    _ = AssistancePersistence.Check(world, catalog, pilot, recipient, now);
                }
            }
            if (Position(pilot.ActiveShip.Position!).DistanceTo(endpoint.Position) > range)
            {
                throw new InvalidDataException("The target is outside drone control range.");
            }
        }
        return true;
    }

    private static void CollectInterruptedMining(WorldAction world, CatalogDbContext catalog, Pilot pilot, WorldDrone drone, long now)
    {
        var work = ReadWork(drone);
        var profile = DroneCatalog.Skilled(world, catalog, pilot.Id, world.Set<OwnedItem>().Find(drone.ItemId)!.TypeId).Utility;
        if (profile?.Mining != true || work.StartedAt == 0 || work.Returning || work.DueAt <= work.StartedAt)
        {
            return;
        }
        try
        {
            var rock = PilotMiningStore.DroneResource(world, catalog, pilot.Id, work.TargetId, profile, drone.SystemId!.Value, now);
            double fraction = Math.Clamp((now - work.StartedAt) / (double)(work.DueAt - work.StartedAt), 0, 1);
            var mined = PilotMiningStore.CollectDrone(
                world,
                catalog,
                pilot,
                drone.ItemId,
                rock,
                profile,
                new(fraction, fraction == 1, new(DrawWork(), DrawWork(), DrawWork(), DrawWork(), DrawWork())),
                now
            );
            WriteWork(
                drone,
                work with
                {
                    StartedAt = 0,
                    Cargo = mined.Extracted > 0 ? [new(mined.TypeId, mined.Extracted)] : [],
                    Returning = mined.Extracted > 0,
                }
            );
        }
        catch (InvalidDataException)
        {
            // Expired or depleted resources cannot be mined by an interrupted cycle.
        }
    }

    private sealed record WorkEndpoint(
        long Id,
        SpacePosition Position,
        float Radius,
        Pilot? Pilot = null,
        WorldDrone? Drone = null,
        WorldWreck? Wreck = null,
        Eden.Data.World.Mining.WorldAsteroid? Resource = null
    );

    private WorkEndpoint? WorkTarget(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        UtilityDroneProfile profile,
        long id,
        int system,
        long now
    )
    {
        if (profile.Mining)
        {
            var rock = PilotMiningStore.DroneResource(world, catalog, source.Id, id, profile, system, now);
            return new(id, new(rock.X, rock.Y, rock.Z), rock.Radius, Resource: rock);
        }
        if (profile.Kind == UtilityDroneKind.Salvaging)
        {
            var wreck = world.Set<WorldWreck>().Find(id);
            return
                wreck is null
                || wreck.SystemId != system
                || wreck.ExpiresAt <= now
                || wreck.SalvagedAt is not null
                || wreck.SalvageJson is null
                ? null
                : new(id, new(wreck.X, wreck.Y, wreck.Z), (float)(catalog.FittingType(wreck.TypeId)?.Radius ?? 0), Wreck: wreck);
        }
        if (profile.Repair && world.Set<WorldDrone>().Find(id) is { } drone && drone.SystemId == system)
        {
            var contact = Contact(world, catalog, drone);
            return contact.Health.Remaining.Destroyed ? null : new(id, contact.Position, contact.Profile.Radius, Drone: drone);
        }
        var pilot = FindVictim(world, catalog, source, id, system, now);
        var presence = pilot is null ? null : FindContact(world, pilot, system, now);
        if (presence is null)
        {
            return null;
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot!);
        if (
            PilotHealthStore.Read(world, id, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed
            || profile.Repair && attributes.GetValueOrDefault(DogmaAttributeId.DisallowAssistance) != 0
        )
        {
            return null;
        }
        if (profile.Repair)
        {
            _ = AssistancePersistence.Check(world, catalog, source, pilot!, now);
        }
        return new(id, presence.Position, (float)(catalog.FittingType(pilot!.ActiveShip.TypeId)?.Radius ?? 0), pilot);
    }

    private bool AdvanceWork(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldDrone drone,
        DroneContact contact,
        double controlRange,
        long now,
        int stamp,
        List<InventoryChange> changes
    )
    {
        var profile = contact.Profile.Utility!;
        if (profile.Offensive)
        {
            if (drone.Activity == DroneActivity.Fighting)
            {
                AdvanceOffensiveWork(world, catalog, pilot, drone, contact, now);
            }
            else
            {
                ClearDroneEffects(world, drone.ItemId);
            }
            // Pure offensive drones share the combat movement/legality path, but
            // never manufacture zero-damage hits or divide by zero shot quality.
            return false;
        }
        var work = ReadWork(drone);
        bool returning = work.Returning || work.Cargo?.Count > 0;
        var source = Position(pilot.ActiveShip.Position!);
        WorkEndpoint? endpoint = null;
        if (!returning && UtilityDroneRules.Working(drone.Activity))
        {
            if (work.Automatic && profile.Kind == UtilityDroneKind.Salvaging)
            {
                var candidates = world
                    .Filter<WorldWreck>(w =>
                        w.SystemId == drone.SystemId && w.ExpiresAt > now && w.SalvagedAt == null && w.SalvageJson != null
                    )
                    .ToArray();
                endpoint = candidates
                    .Where(w => CanAutoSalvage(world, pilot, w))
                    .Where(w => source.DistanceTo(new(w.X, w.Y, w.Z)) <= controlRange)
                    .OrderBy(w => new SpacePosition(w.X, w.Y, w.Z).DistanceTo(contact.Position))
                    .ThenBy(w => w.Id)
                    .Select(w => WorkTarget(world, catalog, pilot, profile, w.Id, drone.SystemId!.Value, now))
                    .FirstOrDefault();
            }
            else
            {
                try
                {
                    endpoint = WorkTarget(world, catalog, pilot, profile, drone.TargetId, drone.SystemId!.Value, now);
                }
                catch (InvalidDataException)
                {
                    // Expiry, depletion and mission access are rechecked before each cycle.
                }
            }
            if (endpoint is not null && source.DistanceTo(endpoint.Position) > controlRange)
            {
                endpoint = null;
            }
            if (endpoint is null)
            {
                drone.Activity = work.Automatic ? UtilityDroneRules.Activity(profile.Kind) : DroneActivity.Idle;
                drone.TargetId = pilot.ActiveShipId;
                work = work with { StartedAt = 0, DueAt = 0 };
            }
        }
        var target = endpoint?.Position ?? source;
        float radius = endpoint?.Radius ?? (float)(catalog.FittingType(pilot.ActiveShip.TypeId)?.Radius ?? 0);
        if (returning)
        {
            drone.TargetId = pilot.ActiveShipId;
            if (contact.Position.DistanceTo(source) - contact.Profile.Radius - radius <= DroneRules.RecallRange)
            {
                work = DeliverWork(world, catalog, pilot, work, changes);
                returning = work.Cargo?.Count > 0;
                if (drone.Activity == DroneActivity.ReturningToBay)
                {
                    WriteWork(drone, work);
                    return false;
                }
                if (!returning && drone.Activity != DroneActivity.ReturningToBay)
                {
                    drone.Activity = work.Repeat || work.Automatic ? UtilityDroneRules.Activity(profile.Kind) : DroneActivity.Idle;
                    drone.TargetId = work.TargetId;
                }
            }
        }
        if (endpoint is null && !returning)
        {
            WriteWork(drone, work);
            // A completed delivery must not pass its resource/wreck identity
            // through the combat target resolver before the next work step.
            if (work.Automatic || work.Repeat && UtilityDroneRules.Working(drone.Activity))
            {
                RechargeWorker(drone, contact, now);
                drone.UpdatedAt = now;
                return true;
            }
            // Recall/orbit use the existing physical recovery path.
            return false;
        }
        RechargeWorker(drone, contact, now);
        contact = DroneRules.Move(
            contact with
            {
                Activity = drone.Activity,
                TargetId = endpoint?.Id ?? pilot.ActiveShipId,
            },
            target,
            radius,
            !returning,
            stamp,
            Math.Min(1, (now - drone.UpdatedAt) / (double)TimeSpan.TicksPerSecond)
        );
        (drone.X, drone.Y, drone.Z) = (contact.Position.X, contact.Position.Y, contact.Position.Z);
        (drone.VelocityX, drone.VelocityY, drone.VelocityZ) = (contact.Velocity.X, contact.Velocity.Y, contact.Velocity.Z);
        drone.SpeedLimit = contact.SpeedLimit;
        drone.Orbiting = contact.Orbiting;
        drone.UpdatedAt = now;
        if (contact.Position.DistanceTo(source) > DroneRules.ShutdownDistance)
        {
            Release(drone);
            return true;
        }
        if (endpoint is not null && !returning && contact.Position.DistanceTo(target) - radius - contact.Profile.Radius <= profile.Range)
        {
            drone.TargetId = endpoint.Id;
            if (work.StartedAt == 0)
            {
                if (profile.Kind == UtilityDroneKind.ShieldRepair && work.DueAt <= now)
                {
                    RepairWork(world, catalog, pilot, endpoint, profile, now);
                }
                work = work with
                {
                    TargetId = endpoint.Id,
                    StartedAt = now,
                    DueAt = checked(now + (long)Math.Ceiling(profile.DurationMilliseconds * TimeSpan.TicksPerMillisecond)),
                };
            }
            else if (work.DueAt <= now)
            {
                try
                {
                    if (profile.Repair)
                    {
                        RepairWork(world, catalog, pilot, endpoint, profile, now);
                    }
                    else if (profile.Mining)
                    {
                        var mined = PilotMiningStore.CollectDrone(
                            world,
                            catalog,
                            pilot,
                            drone.ItemId,
                            endpoint.Resource!,
                            profile,
                            new(1, true, new(DrawWork(), DrawWork(), DrawWork(), DrawWork(), DrawWork())),
                            now
                        );
                        work = work with
                        {
                            Cargo =
                                mined.Extracted + mined.CriticalBonus > 0 ? [new(mined.TypeId, mined.Extracted + mined.CriticalBonus)] : [],
                            Returning = true,
                        };
                    }
                    else
                    {
                        work = SalvageWork(world, catalog, endpoint.Wreck!, profile, work, now);
                    }
                    work = work with
                    {
                        StartedAt = profile.Repair ? now : 0,
                        DueAt = checked(now + (long)Math.Ceiling(profile.DurationMilliseconds * TimeSpan.TicksPerMillisecond)),
                    };
                }
                catch (InvalidDataException)
                {
                    drone.Activity = DroneActivity.Idle;
                    work = work with { StartedAt = 0, DueAt = 0 };
                }
            }
        }
        else if (!returning)
        {
            work = work with { StartedAt = 0, DueAt = 0 };
        }
        WriteWork(drone, work);
        return true;
    }

    private static void RechargeWorker(WorldDrone drone, DroneContact contact, long now)
    {
        drone.Shield = ChargedResource.Recharged(
            drone.Shield,
            contact.Profile.Defense.Shield,
            contact.Profile.Defense.ShieldRechargeMilliseconds,
            Math.Min(1, (now - drone.UpdatedAt) / (double)TimeSpan.TicksPerSecond) * 1000
        );
    }

    private bool CanAutoSalvage(WorldAction world, Pilot pilot, WorldWreck wreck) =>
        wreck.OwnerId == pilot.Id
        || wreck.OwnerId == pilot.CorporationId
        || world.Pilots.Find(wreck.OwnerId) is { } owner
            && (
                owner.CorporationId == pilot.CorporationId
                || fleets.Membership(pilot.Id) is { } membership && fleets.Membership(owner.Id)?.FleetId == membership.FleetId
            );

    private static double DrawWork() => RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution;

    private static DroneWork DeliverWork(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        DroneWork work,
        List<InventoryChange> changes
    )
    {
        List<DroneCargo> remaining = [];
        foreach (var cargo in work.Cargo ?? [])
        {
            var type = catalog.RequireItem(cargo.TypeId);
            var family = type.GroupId == MiningDefinitions.IceGroup ? MiningFamily.Ice : MiningFamily.Ore;
            var hold =
                type.CategoryId == AsteroidDefinitions.Category
                    ? PilotMiningStore.OreHold(catalog, pilot.ActiveShip.TypeId, family)
                    : InventoryFlag.Cargo;
            double capacity = ShipHoldRules.Capacity((int)hold, PilotFittingStore.ShipAttributes(world, catalog, pilot));
            double used = InventoryPersistence.UsedVolume(world, catalog, pilot.Id, pilot.ActiveShipId, (int)hold);
            double volume = PilotInventoryStore.Volume(catalog, cargo.TypeId);
            int quantity = (int)Math.Min(cargo.Quantity, Math.Max(0, Math.Floor((capacity - used) / volume)));
            if (quantity > 0)
            {
                changes.Add(
                    InventoryPersistence.MergeOutput(world, catalog, pilot.Id, pilot.ActiveShipId, (int)hold, cargo.TypeId, quantity)
                );
            }
            if (quantity < cargo.Quantity)
            {
                remaining.Add(cargo with { Quantity = cargo.Quantity - quantity });
            }
        }
        return work with { Cargo = remaining, Returning = remaining.Count > 0 };
    }

    private static DroneWork SalvageWork(
        WorldAction world,
        CatalogDbContext catalog,
        WorldWreck wreck,
        UtilityDroneProfile profile,
        DroneWork work,
        long now
    )
    {
        double chance = InformationRules.SalvageChance(
            profile.Amount,
            catalog.FittingAttributes(wreck.TypeId).GetValueOrDefault(DogmaAttributeId.AccessDifficulty)
        );
        if (chance <= 0)
        {
            throw new InvalidDataException("This wreck is too difficult for this salvage drone.");
        }
        if (DrawWork() >= chance)
        {
            return work;
        }
        var drops = JsonSerializer.Deserialize<ScanItem[]>(wreck.SalvageJson!)!;
        wreck.SalvagedAt = now;
        bool occupied = world.Filter<OwnedItem>(i => i.LocationId == wreck.Id).Any() || world.Ships.Any(s => s.StoredInId == wreck.Id);
        if (occupied)
        {
            wreck.TypeId = SalvagePersistence.CargoContainerType;
            wreck.Name = "Cargo Container";
        }
        else
        {
            wreck.ExpiresAt = now;
        }
        return work with { Cargo = drops.Select(d => new DroneCargo(d.TypeId, d.Quantity)).ToArray(), Returning = true };
    }

    private void RepairWork(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        WorkEndpoint target,
        UtilityDroneProfile profile,
        long now
    )
    {
        var kind = profile.Kind switch
        {
            UtilityDroneKind.ShieldRepair => RemoteRepairKind.Shield,
            UtilityDroneKind.ArmorRepair => RemoteRepairKind.Armor,
            _ => RemoteRepairKind.Hull,
        };
        if (target.Drone is { } drone)
        {
            if (world.Pilots.Find(drone.PilotId) is { } owner && owner.Id != source.Id)
            {
                AssistancePersistence.Apply(world, catalog, source, owner, now, now);
            }
            var health = Contact(world, catalog, drone).Health;
            var repaired = RemoteRepairRules.Apply(health, kind, profile.Amount);
            drone.Shield = repaired.Remaining.Shield;
            drone.ArmorDamage = repaired.Defense.Armor - repaired.Remaining.Armor;
            drone.StructureDamage = repaired.Defense.Structure - repaired.Remaining.Structure;
            return;
        }
        var pilot = target.Pilot!;
        AssistancePersistence.Apply(world, catalog, source, pilot, now, now);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        var before = PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(attributes), now);
        var after = RemoteRepairRules.Apply(
            before,
            kind,
            profile.Amount * attributes.GetValueOrDefault(DogmaAttributeId.RemoteRepairImpedance, 1)
        );
        var saved = world.Set<ShipHealthState>().Find(after.ShipId) ?? world.Add(new ShipHealthState { ShipId = after.ShipId }).Entity;
        saved.ShieldCharge = after.Remaining.Shield;
        saved.ArmorDamage = after.Defense.Armor - after.Remaining.Armor;
        saved.StructureDamage = after.Defense.Structure - after.Remaining.Structure;
        saved.UpdatedAt = now;
        world.AfterCommit(() => repairEvents.Publish(new(pilot.Id, after)));
    }
}
