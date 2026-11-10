using System.Collections.Immutable;
using System.Security.Cryptography;
using Eden.Gameplay.Maps;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Maps;

[Injectable(InjectionType.Singleton)]
internal sealed class CelestialResourceDirectory(WorldDatabase database) : ICelestialResourceDirectory
{
    private static readonly CatalogDefinition<CelestialResourceSnapshot> s_resources = new(
        "maps/celestial-resources",
        (catalog, _) => Load(catalog),
        Estimate: snapshot => 256 + snapshot.Planets.Count * 128L
    );

    public CelestialResourceSnapshot Read()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog.Definition(s_resources, 0);
    }

    private static CelestialResourceSnapshot Load(CatalogDbContext catalog)
    {
        var planets = catalog
            .Records(CatalogData.Sde.PlanetResources)
            .OrderBy(row => row.Key)
            .Select(row => new PlanetResourceDefinition(
                row.Key,
                row.Value.Power ?? 0,
                row.Value.Workforce ?? 0,
                row.Value.Reagent is { } reagent
                    ? new(
                        checked((int)(reagent.TypeId ?? 0)),
                        reagent.AmountPerCycle ?? 0,
                        reagent.CyclePeriod ?? 0,
                        reagent.SecuredCapacity ?? 0,
                        reagent.UnsecuredCapacity ?? 0
                    )
                    : null
            ))
            .ToImmutableArray();
        using MemoryStream image = new();
        using (BinaryWriter writer = new(image, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var planet in planets)
            {
                if (planet.PlanetId <= 0 || planet.Power < 0 || planet.Workforce < 0)
                {
                    throw new InvalidDataException("Invalid catalog planetary resource definition.");
                }
                writer.Write(planet.PlanetId);
                writer.Write(planet.Power);
                writer.Write(planet.Workforce);
                writer.Write(planet.Reagent is not null);
                if (planet.Reagent is { } reagent)
                {
                    if (
                        reagent.TypeId <= 0
                        || reagent.AmountPerCycle is < 0 or > uint.MaxValue
                        || reagent.CycleSeconds <= 0
                        || reagent.SecuredCapacity is < 0 or > uint.MaxValue
                        || reagent.UnsecuredCapacity is < 0 or > uint.MaxValue
                    )
                    {
                        throw new InvalidDataException("Invalid catalog planetary reagent definition.");
                    }
                    writer.Write(reagent.TypeId);
                    writer.Write(reagent.AmountPerCycle);
                    writer.Write(reagent.CycleSeconds);
                    writer.Write(reagent.SecuredCapacity);
                    writer.Write(reagent.UnsecuredCapacity);
                }
            }
        }
        return new(Convert.ToHexStringLower(SHA256.HashData(image.GetBuffer().AsSpan(0, checked((int)image.Length)))), planets);
    }
}
