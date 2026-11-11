using Eden.Data.World.Capsuleers;
using Eden.Data.World.Drones;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal static void ProjectDrones(WorldAction world, IReadOnlyList<LiveDrone> drones)
    {
        foreach (var drone in drones)
        {
            var contact = drone.Contact;
            var row = world.Set<WorldDrone>().Find(contact.Id);
            if (row is null)
            {
                continue;
            }
            row.ControllerShipId = contact.ShipId;
            row.Activity = contact.Activity;
            row.TargetId = contact.TargetId;
            row.X = contact.Position.X;
            row.Y = contact.Position.Y;
            row.Z = contact.Position.Z;
            row.VelocityX = contact.Velocity.X;
            row.VelocityY = contact.Velocity.Y;
            row.VelocityZ = contact.Velocity.Z;
            row.SpeedLimit = contact.SpeedLimit;
            row.Orbiting = contact.Orbiting;
            row.Shield = contact.Health.Remaining.Shield;
            row.ArmorDamage = drone.ArmorDamage;
            row.StructureDamage = drone.StructureDamage;
            row.UpdatedAt = drone.UpdatedAt;
            row.ReadyAt = drone.ReadyAt;
            row.CommandedAt = drone.CommandedAt;
            row.ShotAt = contact.ShotAt;
            row.ShotTargetId = contact.ShotTargetId;
            row.DelegationMode = contact.Delegation?.Mode ?? DroneDelegationMode.None;
            row.DelegatePilotId = contact.Delegation?.PilotId ?? 0;
            row.DelegateShipId = contact.Delegation?.ShipId ?? 0;
            row.DelegateFleetId = contact.Delegation?.FleetId ?? 0;
            row.OwnerMembership = contact.Delegation?.OwnerMembership ?? 0;
            row.DelegateMembership = contact.Delegation?.DelegateMembership ?? 0;
            row.ReactionRevision = contact.Delegation?.ReactionRevision ?? 0;
        }
    }

    internal static void ProjectDroneTriggers(WorldAction world, IReadOnlyList<DroneCombatTriggers> values)
    {
        foreach (var value in values)
        {
            var row =
                world.Set<WorldDroneCombatTriggers>().Find(value.ShipId)
                ?? world.Add(new WorldDroneCombatTriggers { ShipId = value.ShipId }).Entity;
            row.OffensiveRevision = value.Offensive.Revision;
            row.OffensiveTargetId = value.Offensive.OtherId;
            row.OffensiveAt = value.Offensive.At;
            row.IncomingRevision = value.Incoming.Revision;
            row.IncomingSourceId = value.Incoming.OtherId;
            row.IncomingAt = value.Incoming.At;
        }
    }

    private LiveDrone[] LoadDrones(WorldAction world, int system)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return world
            .Set<WorldDrone>()
            .Where(d => d.SystemId == system && !world.Set<SavedCapsuleerSuspension>().Any(pause => pause.PilotId == d.PilotId))
            .AsEnumerable()
            .Select(d => new LiveDrone(
                PilotDroneStore.Contact(world, catalog, d),
                d.UpdatedAt,
                d.ReadyAt,
                d.CommandedAt,
                d.ArmorDamage,
                d.StructureDamage
            ))
            .ToArray();
    }

    private DronePilot ReadDroneInputs(WorldAction world, long pilotId, LiveDrone[] drones)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var fit = FittingCalculation.Read(world, catalog, pilot);
        var saved = world.Set<WorldDroneSettings>().Find(pilotId);
        ResidentSet<int> qualified = [];
        foreach (int type in drones.Select(d => d.Contact.Profile.TypeId).Distinct())
        {
            try
            {
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, type);
                qualified.Add(type);
            }
            catch (InvalidDataException)
            {
                // A lost qualification releases the same drones as the SQL adapter.
            }
        }
        return new DronePilot(
            DroneCatalog.ControlLimit(fit),
            fit.Graph.Read(pilot.ActiveShipId, DogmaAttributeId.DroneBandwidth),
            fit.Graph.Read(pilotId, DogmaAttributeId.DroneControlDistance),
            saved is null ? new() : new(saved.Aggressive, saved.FocusFire),
            qualified
        );
    }

    private static LiveDrone CommittedDrone(LiveDrone current, WorldDrone saved) =>
        current with
        {
            Contact = current.Contact with
            {
                CorporationId = saved.CorporationId,
                Activity = saved.Activity,
                TargetId = saved.TargetId,
                Position = new(saved.X, saved.Y, saved.Z),
                Velocity = new(saved.VelocityX, saved.VelocityY, saved.VelocityZ),
                Health = current.Contact.Health with
                {
                    Remaining = new(
                        Math.Min(current.Contact.Profile.Defense.Shield, saved.Shield),
                        Math.Max(0, current.Contact.Profile.Defense.Armor - saved.ArmorDamage),
                        Math.Max(0, current.Contact.Profile.Defense.Structure - saved.StructureDamage)
                    ),
                },
                ShotAt = saved.ShotAt,
                ShotTargetId = saved.ShotTargetId,
                SpeedLimit = saved.SpeedLimit,
                Orbiting = saved.Orbiting,
                Delegation = DroneDelegationPersistence.Read(saved),
            },
            UpdatedAt = saved.UpdatedAt,
            ReadyAt = saved.ReadyAt,
            CommandedAt = saved.CommandedAt,
            ArmorDamage = saved.ArmorDamage,
            StructureDamage = saved.StructureDamage,
        };
}
