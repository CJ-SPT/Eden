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
    internal static DroneContact Contact(WorldAction world, CatalogDbContext catalog, WorldDrone drone, long? now = null)
    {
        var item = world.Set<OwnedItem>().Find(drone.ItemId)!;
        var profile = DroneCatalog.Skilled(world, catalog, drone.PilotId, item.TypeId, drone.ControllerShipId);
        var effects = PlayerEwarStore.Active(world, drone.ItemId);
        if (
            now is long deadline
            && effects.Any(effect => effect.Module.Effect == DogmaEffect.FighterTackle && effect.ExpiresAt <= deadline)
        )
        {
            effects = effects.Where(effect => effect.Module.Effect != DogmaEffect.FighterTackle || effect.ExpiresAt > deadline).ToArray();
        }
        profile = AreaWeaponRules.ApplyDroneEffects(profile, effects);
        DroneContact contact = new(
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
        var workEffect = WorkEffect(drone, profile, now);
        if (
            profile.Tackle is not null
            && workEffect is { } tackle
            && world.Set<WorldPlayerEwar>().Find(drone.ItemId, tackle.TargetId) is null
        )
        {
            workEffect = null;
        }
        contact = contact with
        {
            SquadronSize = profile.FighterGroup == 0 ? 0 : item.Quantity,
            WorkEffect = workEffect,
            Mobility = profile.Mobility is null ? null : ReadWork(drone).FighterMobility,
        };
        if (now is long at)
        {
            contact = FighterMobilityRules.Apply(contact, at);
        }
        if (
            drone.SystemId is int system
            && Eden.Gameplay.Exploration.AbyssalRules.IsSystem(system)
            && world
                .Filter<Eden.Data.World.Exploration.SavedAbyssalRun>(r =>
                    r.SystemId == system && r.Status == (int)Eden.Gameplay.Exploration.AbyssalStatus.Active
                )
                .SingleOrDefault()
                is { } run
        )
        {
            contact = contact with
            {
                Abyssal = new(
                    profile,
                    (Eden.Gameplay.Exploration.AbyssalWeather)run.Weather,
                    Eden.Gameplay.Exploration.AbyssalEnvironmentRules.Penalty(run.Tier, run.Seed),
                    Eden.Gameplay.Exploration.AbyssalContent.RoomAt(contact.Position),
                    run.Seed,
                    Eden.Gameplay.Exploration.AbyssalContent.Disabled(
                        run.DisabledFields,
                        Eden.Gameplay.Exploration.AbyssalContent.RoomAt(contact.Position)
                    )
                ),
            };
            contact = Eden.Gameplay.Exploration.AbyssalDroneEnvironment.Refresh(contact);
        }
        return contact;
    }
}
