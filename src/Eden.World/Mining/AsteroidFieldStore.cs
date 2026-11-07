using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Economy;
using Eden.Data.World.Mining;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

[Injectable(InjectionType.Singleton)]
internal sealed class AsteroidFieldStore(WorldDatabase database, TimeProvider? clock = null) : IAsteroidField
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _initialized = [];

    public IReadOnlyList<StaticSpaceObject> List(int systemId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.asteroids");
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
        var groups = types.Select(catalog.FittingType).OfType<CatalogItemDefinition>().ToDictionary(type => type.Id, type => type.GroupId);
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

    private void Initialize(WorldDbContext world, CatalogDbContext catalog, int systemId)
    {
        using var transaction = world.Database.BeginTransaction();
        Populate(world, catalog, systemId, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc());
        world.SaveChanges();
        transaction.Commit();
    }

    internal static void Populate(WorldDbContext world, CatalogDbContext catalog, int systemId, long now, int? renewBelt = null)
    {
        foreach (
            var belt in catalog
                .CelestialsInSystem(systemId)
                .Where(b => b.GroupId == AsteroidDefinitions.BeltGroup && (!renewBelt.HasValue || b.Id == renewBelt))
                .OrderBy(b => b.Id)
                .ToArray()
        )
        {
            if (world.Set<SavedBeltRenewal>().Find(belt.Id) is null)
            {
                world.Add(
                    new SavedBeltRenewal
                    {
                        BeltId = belt.Id,
                        SystemId = systemId,
                        NextRenewal = ResourceRenewalRules.Next(now, EconomicStore.Policy(world).RenewalHourUtc),
                    }
                );
            }
            // Visiting/reconnecting never replenishes a saved deposit.
            if (!renewBelt.HasValue && world.Set<WorldAsteroid>().Any(a => a.BeltId == belt.Id))
            {
                continue;
            }
            var counts =
                catalog.Read(CatalogData.Client.Asteroidcountbytypeidbeltid, (long)belt.Id)
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
                        if (catalog.FittingType(type)?.CategoryId != AsteroidDefinitions.Category)
                        {
                            throw new InvalidDataException($"Invalid asteroid type {type} in belt {belt.Id}.");
                        }
                        double volume = catalog.Read(CatalogData.Client.Types, (long)type)?.Volume ?? 0;
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
                var generated = new WorldAsteroid
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
                };
                var existing = renewBelt.HasValue ? world.Set<WorldAsteroid>().Find(generated.Id) : null;
                if (existing is null)
                {
                    world.Add(generated);
                }
                else
                {
                    existing.Quantity = Math.Max(existing.Quantity, generated.Quantity);
                }
            }
        }
    }
}
