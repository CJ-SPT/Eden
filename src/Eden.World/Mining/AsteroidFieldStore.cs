using System.Globalization;
using System.Text.Json;
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
    internal sealed record PreparedBelt(int BeltId, long Catalog, string Deposits);

    private static readonly CatalogDefinition<PreparedBelt> PreparedBelts = new(
        "prepared-asteroid-belt",
        (catalog, belt) =>
            new(belt, catalog.Definitions.Identity.Generation, JsonSerializer.Serialize(Generate(catalog, catalog.FindCelestial(belt)!))),
        Estimate: prepared => prepared.Deposits.Length * sizeof(char) + 64
    );
    private static readonly AsyncLocal<IReadOnlyDictionary<int, PreparedBelt>?> Prepared = new();

    internal static PreparedBelt Prepare(CatalogDbContext catalog, int belt) => catalog.Definition(PreparedBelts, belt);

    internal static IDisposable UsePrepared(IReadOnlyDictionary<int, PreparedBelt> belts)
    {
        var previous = Prepared.Value;
        Prepared.Value = belts;
        return new PreparationLease(previous);
    }

    private sealed class PreparationLease(IReadOnlyDictionary<int, PreparedBelt>? previous) : IDisposable
    {
        public void Dispose() => Prepared.Value = previous;
    }

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
        using WorldDbContext world = new(database.Path) { Participants = new(systems: [systemId]) };
        using CatalogDbContext catalog = new(database.CatalogPath);
        lock (_gate)
        {
            if (!_initialized.Contains(systemId))
            {
                Initialize(world, catalog, systemId);
                _initialized.Add(systemId);
            }
        }
        return List(world, catalog, systemId);
    }

    internal static IReadOnlyList<StaticSpaceObject> List(WorldDbContext world, CatalogDbContext catalog, int systemId)
    {
        var saved = world
            .Set<WorldAsteroid>()
            .AsNoTracking()
            .Where(a => a.SystemId == systemId && a.Quantity > 0)
            .OrderBy(a => a.Id)
            .ToArray();
        var types = saved.Select(a => a.TypeId).Distinct().ToArray();
        var definitions = types.Select(catalog.FittingType).OfType<CatalogItemDefinition>().ToDictionary(type => type.Id);
        return saved
            .Select(a => new StaticSpaceObject(
                a.Id,
                a.TypeId,
                definitions[a.TypeId].GroupId,
                definitions[a.TypeId].CategoryId,
                1,
                new SpacePosition(a.X, a.Y, a.Z),
                a.Radius
            )
            {
                Solid = definitions[a.TypeId].GroupId != MiningDefinitions.GasCloudGroup,
                IsGlobal = false,
            })
            .ToArray();
    }

    private void Initialize(WorldDbContext world, CatalogDbContext catalog, int systemId)
    {
        using var transaction = world.Database.BeginTransaction();
        Populate(world, catalog, systemId, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc());
        world.SaveChanges();
        transaction.Commit();
    }

    internal static void Populate(WorldDbContext world, CatalogDbContext catalog, int systemId, long now)
    {
        long? nextRenewal = null;
        var belts = catalog
            .CelestialsInSystem(systemId)
            .Where(b => b.GroupId == AsteroidDefinitions.BeltGroup)
            .OrderBy(b => b.Id)
            .ToArray();
        if (belts.Length == 0)
        {
            return;
        }
        var beltIds = belts.Select(b => b.Id).ToArray();
        var renewed = world.Set<SavedBeltRenewal>().Where(b => beltIds.Contains(b.BeltId)).Select(b => b.BeltId).ToHashSet();
        renewed.UnionWith(world.Set<SavedBeltRenewal>().Local.Select(b => b.BeltId));
        var populated = world.Set<WorldAsteroid>().Where(a => beltIds.Contains(a.BeltId)).Select(a => a.BeltId).Distinct().ToHashSet();
        foreach (var belt in belts)
        {
            if (!renewed.Contains(belt.Id))
            {
                world.Add(
                    new SavedBeltRenewal
                    {
                        BeltId = belt.Id,
                        SystemId = systemId,
                        NextRenewal = nextRenewal ??= ResourceRenewalRules.Next(now, EconomicStore.Policy(world).RenewalHourUtc),
                    }
                );
            }
            // Visiting/reconnecting never replenishes a saved deposit.
            if (populated.Contains(belt.Id))
            {
                continue;
            }
            var rocks = Generate(catalog, belt);
            if (world.Database.CurrentTransaction is null)
            {
                // Standalone callers commit the deposits and renewal rows together in SaveChanges.
                world.AddRange(rocks);
                continue;
            }
            // A traveling pilot can initialize thousands of deposits in one action.
            // Insert each generated belt together in the caller's world transaction.
            string deposits =
                Prepared.Value?.GetValueOrDefault(belt.Id) is { } prepared && prepared.Catalog == catalog.Definitions.Identity.Generation
                    ? prepared.Deposits
                    : Prepare(catalog, belt.Id).Deposits;
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAsteroid(Id,BeltId,SystemId,TypeId,Quantity,X,Y,Z,Radius)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.BeltId'),json_extract(value,'$.SystemId'),
                    json_extract(value,'$.TypeId'),json_extract(value,'$.Quantity'),json_extract(value,'$.X'),
                    json_extract(value,'$.Y'),json_extract(value,'$.Z'),json_extract(value,'$.Radius')
                FROM json_each({deposits})
                """
            );
        }
    }

    internal static void Renew(WorldDbContext world, CatalogDbContext catalog, IReadOnlyList<SavedBeltRenewal> belts)
    {
        // Warm cold belt counts together rather than opening one catalog connection per belt.
        _ = catalog.ReadMany(CatalogData.Client.Asteroidcountbytypeidbeltid, belts.Select(belt => (long)belt.BeltId));
        var generated = belts
            .SelectMany(belt =>
                catalog
                    .CelestialsInSystem(belt.SystemId)
                    .Where(body => body.Id == belt.BeltId && body.GroupId == AsteroidDefinitions.BeltGroup)
                    .SelectMany(body => Generate(catalog, body))
            )
            .ToArray();
        if (generated.Length == 0)
        {
            return;
        }
        // One parameter and one command per bounded batch. Existing geometry and
        // above-baseline deposits survive; unchanged rocks cause no write/invalidation.
        string deposits = JsonSerializer.Serialize(generated);
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldAsteroid(Id,BeltId,SystemId,TypeId,Quantity,X,Y,Z,Radius)
            SELECT json_extract(value,'$.Id'),json_extract(value,'$.BeltId'),json_extract(value,'$.SystemId'),
                json_extract(value,'$.TypeId'),json_extract(value,'$.Quantity'),json_extract(value,'$.X'),
                json_extract(value,'$.Y'),json_extract(value,'$.Z'),json_extract(value,'$.Radius')
            FROM json_each({deposits}) AS deposit
            WHERE NOT EXISTS (
                SELECT 1 FROM WorldAsteroid AS saved
                WHERE saved.Id=json_extract(deposit.value,'$.Id') AND saved.Quantity>=json_extract(deposit.value,'$.Quantity')
            )
            ON CONFLICT(Id) DO UPDATE SET Quantity=excluded.Quantity
                WHERE WorldAsteroid.Quantity < excluded.Quantity
            """
        );
    }

    private static IEnumerable<WorldAsteroid> Generate(CatalogDbContext catalog, CatalogCelestialDefinition belt)
    {
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
            yield return new WorldAsteroid
            {
                Id = checked(AsteroidDefinitions.FirstAsteroidId + (long)belt.Id * AsteroidDefinitions.IdsPerBelt + index),
                BeltId = belt.Id,
                SystemId = belt.SolarSystemId,
                TypeId = types[index],
                Quantity = checked((long)Math.Floor(oreVolume / volumes[types[index]])),
                X = belt.X + Math.Cos(angle) * distance,
                Y = belt.Y + (random.NextDouble() - 0.5) * AsteroidDefinitions.FieldThickness,
                Z = belt.Z + Math.Sin(angle) * distance,
                Radius = (float)Math.Pow(oreVolume, AsteroidDefinitions.RadiusGrowthExponent),
            };
        }
    }
}
