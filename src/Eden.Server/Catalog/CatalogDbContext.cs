using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Catalog;

public sealed class Region
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Constellation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int RegionId { get; set; }
    public Region Region { get; set; } = null!;
}

public sealed class SolarSystem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ConstellationId { get; set; }
    public Constellation Constellation { get; set; } = null!;
}

public sealed class Celestial
{
    public int Id { get; set; }
    public int SolarSystemId { get; set; }
    public SolarSystem SolarSystem { get; set; } = null!;
    public int TypeId { get; set; }
    public int GroupId { get; set; }
    public double? Radius { get; set; }
    public int? OrbitId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class StationDesignation
{
    public int StationId { get; set; }
    public string? OrbitName { get; set; }
    public int? PlanetNumber { get; set; }
    public int? MoonNumber { get; set; }
    public string? Operation { get; set; }

    public string Format(string system, string owner)
    {
        string orbit = OrbitName ?? system;
        if (OrbitName is null && PlanetNumber is int number)
        {
            // Build 3662168 uses Roman planet numbers and Arabic moon numbers.
            string[] units = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];
            orbit += " " + new string('X', number / 10) + units[number % 10];
            if (MoonNumber is int moon)
            {
                orbit += $" - Moon {moon}";
            }
        }
        return Operation is null ? $"{orbit} - {owner}" : $"{orbit} - {owner} {Operation}";
    }
}

public sealed class Station
{
    public int Id { get; set; }
    public int SolarSystemId { get; set; }
    public SolarSystem SolarSystem { get; set; } = null!;
    public int TypeId { get; set; }
    public ItemType Type { get; set; } = null!;
    public int OwnerId { get; set; }
    public NpcCorporation Owner { get; set; } = null!;
    public int OperationId { get; set; }
    public int OrbitId { get; set; }
    public Celestial Orbit { get; set; } = null!;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class Stargate
{
    public int Id { get; set; }
    public int SolarSystemId { get; set; }
    public SolarSystem SolarSystem { get; set; } = null!;
    public int DestinationId { get; set; }
    public Stargate Destination { get; set; } = null!;
}

public sealed class ItemCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ItemGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int CategoryId { get; set; }
    public ItemCategory Category { get; set; } = null!;
}

public sealed class ItemType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int GroupId { get; set; }
    public ItemGroup Group { get; set; } = null!;
    public double? Mass { get; set; }
    public double? Radius { get; set; }
    public double? Capacity { get; set; }
    public List<TypeAttribute> Attributes { get; } = [];
}

public sealed class NpcCorporation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class DogmaAttribute
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

[PrimaryKey(nameof(ItemTypeId), nameof(AttributeId))]
public sealed class TypeAttribute
{
    public int ItemTypeId { get; set; }
    public int AttributeId { get; set; }
    public DogmaAttribute Attribute { get; set; } = null!;
    public double Value { get; set; }
}

public sealed class CatalogDbContext(string path, bool writable = false) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Station>();
        model.Entity<StationDesignation>().HasKey(s => s.StationId);
        model.Entity<StationDesignation>().ToView("StationDesignation");
        model.Entity<Stargate>();
        model.Entity<ItemType>().HasMany(t => t.Attributes).WithOne().HasForeignKey(a => a.ItemTypeId);
        CatalogSources.Configure(model);
    }
}
