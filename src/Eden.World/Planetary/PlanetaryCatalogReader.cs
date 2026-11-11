using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Planetary;
using Eden.World.Catalog;
using Eden.World.Inventory;

namespace Eden.World.Planetary;

internal static class PlanetaryCatalogReader
{
    private const int PlanetGroup = 7;
    private static readonly HashSet<string> s_restrictedSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Amarr",
        "Arnon",
        "Aunia",
        "Auvergne",
        "Balginia",
        "Dodixie",
        "Fricoure",
        "Ichoriya",
        "Irjunen",
        "Isaziwa",
        "Isinokka",
        "Jita",
        "Lustrevik",
        "Motsu",
        "Oursulaert",
        "Rens",
        "Sankkasen",
        "Umokka",
    };
    private static readonly CatalogDefinition<PlanetaryCatalog> s_catalog = new("planetary/industry", (c, _) => Load(c));

    internal static PlanetaryCatalog Read(CatalogDbContext catalog) => catalog.Definition(s_catalog, 0);

    internal static PlanetaryPlanet Planet(CatalogDbContext catalog, int planetId)
    {
        var celestial = catalog.FindCelestial(planetId);
        if (celestial is null || celestial.GroupId != PlanetGroup)
        {
            throw new InvalidDataException("Choose a known planet.");
        }
        var definition =
            catalog.Read(CatalogData.Sde.MapPlanets, (long)planetId)
            ?? throw new InvalidDataException("This planet has no pinned planetary definition.");
        string systemName = catalog.RequireSystem(celestial.SolarSystemId).Name;
        return new(
            planetId,
            celestial.SolarSystemId,
            celestial.TypeId,
            checked((int)(definition.CelestialIndex ?? 0)),
            celestial.Radius ?? definition.Radius ?? 0,
            new(celestial.X, celestial.Y, celestial.Z),
            PlanetaryResources.Types(celestial.TypeId).Count > 0 && !s_restrictedSystems.Contains(systemName)
        );
    }

    private static PlanetaryCatalog Load(CatalogDbContext catalog)
    {
        var schematics = catalog
            .Records(CatalogData.Client.Schematics)
            .Select(row =>
            {
                var recipe = row.Value;
                var types = recipe.Types ?? throw new InvalidDataException("The planetary schematic has no ingredients.");
                PlanetaryCommodity Commodity(KeyValuePair<string, ClientSchematicsTypesValue> pair) =>
                    new(int.Parse(pair.Key, CultureInfo.InvariantCulture), checked((int)(pair.Value.Quantity ?? 0)));
                return new PlanetarySchematic(
                    checked((int)row.Key),
                    checked((int)(recipe.CycleTime ?? 0)),
                    (recipe.Pins ?? []).Select(p => checked((int)p)).ToArray(),
                    types.Where(t => t.Value.IsInput == 1).Select(Commodity).ToArray(),
                    Commodity(types.Single(t => t.Value.IsInput == 0))
                );
            })
            .ToDictionary(s => s.Id);
        var pins = new Dictionary<int, PlanetaryType>();
        var pinTypes = Enum.GetValues<PlanetaryPinKind>().SelectMany(kind => catalog.Definitions.TypesInGroup((int)kind));
        foreach (var item in pinTypes.Select(catalog.RequireItem))
        {
            var definition = catalog.Read(CatalogData.Client.Types, (long)item.Id);
            if (definition?.Published != 1)
            {
                continue;
            }
            var attributes = catalog.FittingAttributes(item.Id);
            double Attribute(DogmaAttributeId id) => attributes.GetValueOrDefault(id);
            double DefaultAttribute(DogmaAttributeId id) =>
                attributes.GetValueOrDefault(
                    id,
                    catalog.Read(CatalogData.Client.Dogmaattributes, (long)id)?.DefaultValue
                        ?? throw new InvalidDataException("Missing planetary dogma default.")
                );
            pins.Add(
                item.Id,
                new(
                    item.Id,
                    (PlanetaryPinKind)item.GroupId,
                    checked((int)Attribute(DogmaAttributeId.PlanetaryPlanetRestriction)),
                    item.Capacity ?? 0,
                    Attribute(DogmaAttributeId.CpuLoad),
                    Attribute(DogmaAttributeId.PowerLoad),
                    checked((long)Math.Ceiling((definition.BasePrice ?? 0) * 100)),
                    Attribute(DogmaAttributeId.PlanetaryExtractorHeadCpu),
                    Attribute(DogmaAttributeId.PlanetaryExtractorHeadPower),
                    DefaultAttribute(DogmaAttributeId.PlanetaryExtractorHeadRange),
                    DefaultAttribute(DogmaAttributeId.PlanetaryExtractorDecay),
                    DefaultAttribute(DogmaAttributeId.PlanetaryExtractorNoise)
                )
            );
        }
        var raw = new[]
        {
            PlanetaryResources.Temperate,
            PlanetaryResources.Ice,
            PlanetaryResources.Gas,
            PlanetaryResources.Oceanic,
            PlanetaryResources.Lava,
            PlanetaryResources.Barren,
            PlanetaryResources.Storm,
            PlanetaryResources.Plasma,
        }
            .SelectMany(PlanetaryResources.Types)
            .Distinct()
            .ToArray();
        var tiers = raw.ToDictionary(t => t, _ => 0);
        for (int tier = 1; tier <= 4; tier++)
        {
            foreach (
                var recipe in schematics
                    .Values.Where(s => !tiers.ContainsKey(s.Output.TypeId) && s.Inputs.All(i => tiers.ContainsKey(i.TypeId)))
                    .ToArray()
            )
            {
                tiers[recipe.Output.TypeId] = recipe.Inputs.Max(i => tiers[i.TypeId]) + 1;
            }
        }
        var volumes = tiers.Keys.ToDictionary(t => t, t => PilotInventoryStore.Volume(catalog, t));
        if (
            schematics.Values.Any(s =>
                s.CycleSeconds <= 0
                || s.Inputs.Count == 0
                || s.Inputs.Any(i => i.Quantity <= 0)
                || s.Output.Quantity <= 0
                || !tiers.ContainsKey(s.Output.TypeId)
            ) || volumes.Values.Any(v => !double.IsFinite(v) || v <= 0)
        )
        {
            throw new InvalidDataException("Invalid planetary production catalog.");
        }
        var link = catalog.FittingAttributes(PlanetaryRules.LinkType);
        double LinkAttribute(DogmaAttributeId id) => link.GetValueOrDefault(id);
        return new(
            pins,
            schematics,
            volumes,
            tiers,
            new(
                PlanetaryRules.LinkType,
                LinkAttribute(DogmaAttributeId.CpuLoad),
                LinkAttribute(DogmaAttributeId.PowerLoad),
                LinkAttribute(DogmaAttributeId.PlanetaryLinkCpuPerKm),
                LinkAttribute(DogmaAttributeId.PlanetaryLinkPowerPerKm),
                LinkAttribute(DogmaAttributeId.PlanetaryLinkCpuLevelMultiplier),
                LinkAttribute(DogmaAttributeId.PlanetaryLinkPowerLevelMultiplier),
                LinkAttribute(DogmaAttributeId.PlanetaryLinkCapacity)
            )
        );
    }
}
