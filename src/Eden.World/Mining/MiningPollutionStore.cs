using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Drones;
using Eden.Data.World.Mining;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Mining;

[Injectable(InjectionType.Singleton)]
internal sealed class MiningPollutionStore(
    WorldDatabase database,
    IPilotCombatPresence presence,
    IPilotDamageEvents damageEvents,
    PilotLossSettlement losses,
    SqliteWorld lookup
) : IMiningPollutionClouds
{
    private const DogmaAttributeId SkillChanceBonus = (DogmaAttributeId)543;
    private const DogmaAttributeId CloudEffectDelay = (DogmaAttributeId)544;
    private const DogmaAttributeId CloudDuration = (DogmaAttributeId)545;
    private static readonly CatalogDefinition<MiningPollutionProfile> s_profile = new("mining-pollution-cloud", ReadProfile);

    internal static MiningPollutionProfile Profile(CatalogDbContext catalog) => catalog.Definition(s_profile, MiningPollutionRules.TypeId);

    private static MiningPollutionProfile ReadProfile(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.RequireItem(typeId);
        var attributes = catalog.FittingAttributes(typeId);
        var damage = new DamageVector(
            attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        );
        long interval = checked((long)(attributes.GetValueOrDefault(CloudEffectDelay) * TimeSpan.TicksPerMillisecond));
        long lifetime = checked((long)(attributes.GetValueOrDefault(CloudDuration) * TimeSpan.TicksPerMillisecond));
        if (
            type is not { GroupId: MiningPollutionRules.GroupId, CategoryId: MiningPollutionRules.CategoryId, Radius: > 0 }
            || interval <= 0
            || lifetime <= interval
            || damage.Total <= 0
        )
        {
            throw new InvalidDataException("The catalog has an invalid mining pollution cloud.");
        }
        return new((float)type.Radius.Value, interval, lifetime, damage);
    }

    internal static void Begin(WorldAction world, CatalogDbContext catalog, Pilot pilot, WorldAsteroid rock, long now)
    {
        if (world is not EconomyTransaction || catalog.FittingType(rock.TypeId)?.GroupId != MiningDefinitions.MercoxitGroup)
        {
            return;
        }
        double oreChance = catalog.FittingAttributes(rock.TypeId).GetValueOrDefault(DogmaAttributeId.MiningDamageCloudChance);
        if (oreChance <= 0 || !double.IsFinite(oreChance))
        {
            return;
        }
        double chance = MiningPollutionRules.Chance(
            oreChance,
            catalog.FittingAttributes(MiningDefinitions.DeepCoreMiningSkill).GetValueOrDefault(SkillChanceBonus),
            PilotSkillReader.OperationalLevel(world, catalog, pilot.Id, MiningDefinitions.DeepCoreMiningSkill)
        );
        if (Random.Shared.NextDouble() >= chance)
        {
            return;
        }
        var prior = world.Filter<SavedMiningPollutionCloud>(cloud => cloud.AsteroidId == rock.Id).SingleOrDefault();
        if (prior is not null)
        {
            if (prior.ExpiresAt > now)
            {
                return;
            }
            world.Remove(prior);
        }
        var profile = Profile(catalog);
        world.Add(
            new SavedMiningPollutionCloud
            {
                Id = world.Next("SavedMiningPollutionCloud", MiningPollutionRules.FirstCloudId),
                AsteroidId = rock.Id,
                SystemId = rock.SystemId,
                X = rock.X,
                Y = rock.Y,
                Z = rock.Z,
                StartedAt = now,
                NextPulseAt = checked(now + profile.Interval),
                ExpiresAt = checked(now + profile.Lifetime),
            }
        );
    }

    internal static IReadOnlyList<StaticSpaceObject> Contacts(LiveWorld live, string catalogPath, int system, long now)
    {
        if (!live.Economy.HasMiningPollution(system))
        {
            return [];
        }
        using CatalogDbContext catalog = new(catalogPath);
        var profile = Profile(catalog);
        return live.Economy.Inspect(world =>
            world
                .Filter<SavedMiningPollutionCloud>(cloud => cloud.SystemId == system && cloud.ExpiresAt > now)
                .OrderBy(cloud => cloud.Id)
                .Select(cloud => new StaticSpaceObject(
                    cloud.Id,
                    MiningPollutionRules.TypeId,
                    MiningPollutionRules.GroupId,
                    MiningPollutionRules.CategoryId,
                    1,
                    Center(cloud),
                    profile.Radius
                )
                {
                    Solid = false,
                    IsGlobal = false,
                })
                .ToArray()
        );
    }

    public void Advance(long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        foreach (int system in live.Economy.DueMiningPollutionSystems(now))
        {
            LiveWorld.Execute(database.Path, system, () => Settle(live, system, now), nameof(MiningPollutionStore));
        }
    }

    private void Settle(LiveWorld live, int system, long now)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var profile = Profile(catalog);
        using var world = live.Economy.Begin(new(systems: [system]));
        var clouds = world
            .Filter<SavedMiningPollutionCloud>(cloud => cloud.SystemId == system && (cloud.ExpiresAt <= now || cloud.NextPulseAt <= now))
            .OrderBy(cloud => cloud.NextPulseAt)
            .ThenBy(cloud => cloud.Id)
            .ToArray();
        foreach (var cloud in clouds)
        {
            if (cloud.ExpiresAt <= now)
            {
                world.Remove(cloud);
                continue;
            }
            cloud.NextPulseAt = MiningPollutionRules.NextPulse(cloud.StartedAt, profile.Interval, now);
            Pulse(world, catalog, cloud, profile, now);
        }
        world.Commit();
    }

    private void Pulse(
        WorldAction world,
        CatalogDbContext catalog,
        SavedMiningPollutionCloud cloud,
        MiningPollutionProfile profile,
        long now
    )
    {
        var center = Center(cloud);
        int system = cloud.SystemId;
        var nearby = presence
            .NearbyShips(system, center)
            .Concat(
                world
                    .Filter<FlightPosition>(row => row.SystemId == system)
                    .AsEnumerable()
                    .Where(row => MiningPollutionRules.Contains(center, profile.Radius, new(row.X, row.Y, row.Z)))
                    .Select(row => row.ShipId)
            )
            .Distinct()
            .Order()
            .ToArray();
        var source = new CombatParty(cloud.Id, null, 0, null, MiningPollutionRules.TypeId, MiningPollutionRules.TypeId, null, 0);
        foreach (long ship in nearby)
        {
            var pilot = world.Filter<Pilot>(row => row.ActiveShipId == ship).SingleOrDefault();
            if (
                pilot?.ActiveShip.Position is not { } saved
                || (saved.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != system
                || presence.IsTravelProtected(system, ship)
                || world.TetherProtected(ship, now)
                || world.Set<ShipLoss>().Find(ship) is not null
                || world.Set<ShipLoss>().Local.Any(loss => loss.PilotId == pilot.Id && loss.Time == now)
            )
            {
                continue;
            }
            var logoff = world.Set<SavedLogoff>().Find(pilot.Id);
            if (logoff?.ShipId != ship)
            {
                logoff = null;
            }
            if (logoff is not null && logoff.ExpiresAt <= now)
            {
                continue;
            }
            var position = presence.FindArea(system, ship)?.Position ?? (logoff is null ? null : new(saved.X, saved.Y, saved.Z));
            if (position is null || !MiningPollutionRules.Contains(center, profile.Radius, position))
            {
                continue;
            }
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot));
            var result = PilotHealthStore.Apply(world, ship, defense, profile.Damage, now);
            PilotInteractionHistoryStore.Damage(world, ship, source, result.Applied);
            var loss = result.Remaining.Destroyed ? losses.Apply(world, catalog, pilot, ship, system, saved, now, source) : null;
            if (loss is not null)
            {
                world.SaveChanges();
            }
            var replacement = loss is null ? null : lookup.FindPilot(world, pilot.AccountId, pilot.Id);
            PilotDamage notification = new(
                pilot.Id,
                0,
                cloud.Id,
                new(ship, defense, result.Remaining),
                profile.Damage,
                result.Applied,
                1,
                MiningPollutionRules.TypeId,
                false,
                replacement,
                loss?.Insurance
            )
            {
                InventoryChanges = loss?.Changes ?? [],
            };
            world.AfterCommit(() => damageEvents.Publish(notification));
        }
        foreach (var drone in world.Filter<WorldDrone>(drone => drone.SystemId == system).ToArray())
        {
            if (MiningPollutionRules.Contains(center, profile.Radius, new(drone.X, drone.Y, drone.Z)))
            {
                SmartbombStore.DamageDrone(world, drone, PilotDroneStore.Contact(world, catalog, drone), profile.Damage, now);
            }
        }
    }

    private static SpacePosition Center(SavedMiningPollutionCloud cloud) => new(cloud.X, cloud.Y, cloud.Z);
}
