using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public sealed class SourceDataset
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Rows { get; set; }
}

[PrimaryKey(nameof(Dataset), nameof(Key))]
public sealed class SourceRecord
{
    public string Dataset { get; set; } = "";
    public string Key { get; set; } = "";
    public string Data { get; set; } = "";

    public T Read<T>() =>
        JsonSerializer.Deserialize<T>(Data, JsonSerializerOptions.Web)
        ?? throw new InvalidDataException($"Missing {Dataset} record {Key}.");
}

[PrimaryKey(nameof(Export), nameof(Client))]
public sealed class SourceOverlap
{
    public string Export { get; set; } = "";
    public string Client { get; set; } = "";
    public long SharedKeys { get; set; }
    public long DifferentRows { get; set; }
}

[PrimaryKey(nameof(Export), nameof(Client), nameof(Key))]
public sealed class SourceDifference
{
    public string Export { get; set; } = "";
    public string Client { get; set; } = "";
    public string Key { get; set; } = "";
    public string Fields { get; set; } = "";
}

internal static class CatalogSources
{
    private static readonly ICatalogSource[] s_sources = typeof(CatalogData)
        .GetNestedTypes()
        .SelectMany(group => group.GetFields())
        .Select(field => (ICatalogSource)field.GetValue(null)!)
        .ToArray();

    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SourceDataset>().HasKey(d => d.Name);
        model.Entity<SourceRecord>();
        model.Entity<SourceOverlap>();
        model.Entity<SourceDifference>();
        foreach (var source in s_sources)
        {
            source.Configure(model);
        }
    }

    internal static void CheckBuild(CatalogDbContext db)
    {
        if (
            db.Database.SqlQueryRaw<int>("PRAGMA application_id").AsEnumerable().Single() != 0x4544454E
            || db.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single() != 1
        )
        {
            throw new InvalidDataException("Unsupported catalog schema. Run tools/Setup-Catalog.ps1 with the staged client.");
        }
        var build = db.Set<SourceRecord>().SingleOrDefault(r => r.Dataset == "sde/_sde" && r.Key == "\"sde\"");
        if (build?.Read<JsonElement>().GetProperty("buildNumber").GetInt32() != 3662168)
        {
            throw new InvalidDataException("Run the catalog setup command for EVE build 3662168.");
        }
    }

    internal static void Verify(string path)
    {
        using CatalogDbContext db = new(path);
        CheckBuild(db);
        var station = db.Set<Station>()
            .Include(s => s.Orbit)
            .Include(s => s.Owner)
            .Include(s => s.SolarSystem)
                .ThenInclude(s => s.Constellation)
                    .ThenInclude(c => c.Region)
            .Single(s => s.Id == 60003760);
        var gate = db.Set<Stargate>()
            .Where(g => g.SolarSystemId == station.SolarSystemId)
            .Include(g => g.Destination)
                .ThenInclude(g => g.SolarSystem)
            .OrderBy(g => g.Id)
            .First();
        var ibis = db.Set<ItemType>()
            .Include(t => t.Group)
                .ThenInclude(g => g.Category)
            .Include(t => t.Attributes)
            .Single(t => t.Id == 601);
        var designation = db.Set<StationDesignation>().Single(s => s.StationId == station.Id);
        var blueprint = CatalogData.Sde.Blueprints.Get(db, 681);
        if (
            blueprint.Activities?.Manufacturing?.Products?.Single().TypeID != 165
            || designation.Format(station.SolarSystem.Name, station.Owner.Name) != "Jita IV - Moon 4 - Caldari Navy Assembly Plant"
            || station.Orbit.Id != 40009087
            || station.SolarSystem.Name != "Jita"
            || station.SolarSystem.Constellation.Id != 20000020
            || station.SolarSystem.Constellation.Region.Id != 10000002
            || db.Set<Celestial>().Count(c => c.SolarSystemId == station.SolarSystemId) != 49
            || db.Set<Station>().Count(s => s.SolarSystemId == station.SolarSystemId) != 18
            || gate.Destination.DestinationId != gate.Id
            || gate.Destination.SolarSystemId == station.SolarSystemId
            || ibis.Group.Category.Id != 6
            || ibis.Mass != 1163000
            || ibis.Radius != 47
            || ibis.Attributes.Single(a => a.AttributeId == 37).Value != 295
            || ibis.Attributes.Single(a => a.AttributeId == 70).Value != 4.5
        )
        {
            throw new InvalidDataException("The catalog failed Jita geography or Ibis flight verification.");
        }
        var counts = db.Set<SourceDataset>().ToDictionary(d => d.Name, d => d.Rows);
        foreach (var source in s_sources)
        {
            if (!counts.Remove(source.Name, out long expected) || source.Verify(db) != expected)
            {
                throw new InvalidDataException($"Source count mismatch for {source.Name}.");
            }
        }
        if (counts.Count != 0)
        {
            throw new InvalidDataException($"Missing typed models for {string.Join(", ", counts.Keys)}.");
        }
        Console.WriteLine(
            $"Verified {db.Set<SourceDataset>().Count()} datasets, Jita, its gate to {gate.Destination.SolarSystem.Name}, and the Ibis."
        );
    }
}
