using System.Security.Cryptography;
using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    internal static DroneContact Contact(WorldAction world, CatalogDbContext catalog, WorldDrone drone)
    {
        var profile = DroneCatalog.Skilled(
            world,
            catalog,
            drone.PilotId,
            world.Set<OwnedItem>().Find(drone.ItemId)!.TypeId,
            drone.ControllerShipId
        );
        return new(
            drone.ItemId,
            drone.PilotId,
            drone.ControllerShipId,
            drone.CorporationId,
            drone.SystemId ?? 0,
            profile,
            drone.Activity,
            drone.TargetId,
            new(drone.X, drone.Y, drone.Z),
            new(drone.VelocityX, drone.VelocityY, drone.VelocityZ),
            new(
                drone.ItemId,
                profile.Defense,
                new(
                    Math.Min(profile.Defense.Shield, drone.Shield),
                    Math.Max(0, profile.Defense.Armor - drone.ArmorDamage),
                    Math.Max(0, profile.Defense.Structure - drone.StructureDamage)
                )
            ),
            drone.ShotAt,
            drone.ShotTargetId,
            drone.SpeedLimit,
            drone.Orbiting,
            DroneDelegationPersistence.Read(drone)
        );
    }
}
