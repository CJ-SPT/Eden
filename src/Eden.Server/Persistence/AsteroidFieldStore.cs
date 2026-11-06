using System.Globalization;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable(InjectionType.Singleton)]
internal sealed class AsteroidFieldStore(WorldDatabase database) : IAsteroidField
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _initialized = [];

    public IReadOnlyList<StaticSpaceObject> List(int systemId)
    {
        using var phase = Eden.Protocol.PilotSessions.SessionPerformance.Phase("read.asteroids");
        return LiveReadModels.Find(database.Path) is { } resident
            ? resident.Asteroids(systemId, () => ListSql(systemId))
            : ListSql(systemId);
    }

    private IReadOnlyList<StaticSpaceObject> ListSql(int systemId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        lock (_gate)
        {
            if (!_initialized.Contains(systemId))
            {
                Initialize(world, catalog, systemId);
                _initialized.Add(systemId);
            }
        }
        var saved = world
            .Set<WorldAsteroid>()
            .AsNoTracking()
            .Where(a => a.SystemId == systemId && a.Quantity > 0)
            .OrderBy(a => a.Id)
            .ToArray();
        var types = saved.Select(a => a.TypeId).Distinct().ToArray();
        var groups = catalog.Set<ItemType>().Where(t => types.Contains(t.Id)).ToDictionary(t => t.Id, t => t.GroupId);
        return saved
            .Select(a => new StaticSpaceObject(
                a.Id,
                a.TypeId,
                groups[a.TypeId],
                AsteroidDefinitions.Category,
                1,
                new SpacePosition(a.X, a.Y, a.Z),
                a.Radius
            ))
            .ToArray();
    }

    private static void Initialize(WorldDbContext world, CatalogDbContext catalog, int systemId)
    {
        using var transaction = world.Database.BeginTransaction();
        Populate(world, catalog, systemId);
        world.SaveChanges();
        transaction.Commit();
    }

    internal static void Populate(WorldDbContext world, CatalogDbContext catalog, int systemId)
    {
        foreach (
            var belt in catalog
                .Set<Celestial>()
                .Where(b => b.SolarSystemId == systemId && b.GroupId == AsteroidDefinitions.BeltGroup)
                .OrderBy(b => b.Id)
                .ToArray()
        )
        {
            // Keep depleted rows as well: an empty belt must not regenerate on login.
            if (world.Set<WorldAsteroid>().Any(a => a.BeltId == belt.Id))
            {
                continue;
            }
            var counts =
                catalog
                    .Set<SourceRecord>()
                    .Find("client/asteroidcountbytypeidbeltid", belt.Id.ToString(CultureInfo.InvariantCulture))
                    ?.Read<Dictionary<string, long>>()
                ?? throw new InvalidDataException($"Missing asteroid counts for belt {belt.Id}.");
            if (counts.Values.Any(count => count < 0) || counts.Values.Sum() > AsteroidDefinitions.IdsPerBelt)
            {
                throw new InvalidDataException($"Invalid asteroid counts for belt {belt.Id}.");
            }
            var types = counts
                .OrderBy(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture))
                .SelectMany(pair => Enumerable.Repeat(int.Parse(pair.Key, CultureInfo.InvariantCulture), checked((int)pair.Value)))
                .ToArray();
            Random random = new(belt.Id);
            random.Shuffle(types);
            var volumes = types
                .Distinct()
                .ToDictionary(
                    type => type,
                    type =>
                    {
                        if (!catalog.Set<ItemType>().Any(t => t.Id == type && t.Group.CategoryId == AsteroidDefinitions.Category))
                        {
                            throw new InvalidDataException($"Invalid asteroid type {type} in belt {belt.Id}.");
                        }
                        double volume =
                            catalog
                                .Set<SourceRecord>()
                                .Find("client/types", type.ToString(CultureInfo.InvariantCulture))
                                ?.Read<ClientTypes>()
                                .Volume
                            ?? 0;
                        return double.IsFinite(volume) && volume > 0
                            ? volume
                            : throw new InvalidDataException($"Invalid ore volume for type {type}.");
                    }
                );
            for (int index = 0; index < types.Length; index++)
            {
                double angle = 2 * Math.PI * index / types.Length;
                double distance =
                    AsteroidDefinitions.InnerFieldRadius
                    + random.NextDouble() * (AsteroidDefinitions.OuterFieldRadius - AsteroidDefinitions.InnerFieldRadius);
                double oreVolume =
                    AsteroidDefinitions.MinimumOreVolume
                    + random.NextDouble() * (AsteroidDefinitions.MaximumOreVolume - AsteroidDefinitions.MinimumOreVolume);
                world
                    .Set<WorldAsteroid>()
                    .Add(
                        new()
                        {
                            Id = checked(AsteroidDefinitions.FirstAsteroidId + (long)belt.Id * AsteroidDefinitions.IdsPerBelt + index),
                            BeltId = belt.Id,
                            SystemId = systemId,
                            TypeId = types[index],
                            Quantity = checked((long)Math.Floor(oreVolume / volumes[types[index]])),
                            X = belt.X + Math.Cos(angle) * distance,
                            Y = belt.Y + (random.NextDouble() - 0.5) * AsteroidDefinitions.FieldThickness,
                            Z = belt.Z + Math.Sin(angle) * distance,
                            Radius = (float)Math.Pow(oreVolume, AsteroidDefinitions.RadiusGrowthExponent),
                        }
                    );
            }
        }
    }
}
