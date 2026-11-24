using System.Collections.Frozen;
using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.World.Catalog;

public sealed partial class CatalogProvider
{
    private readonly Lock _runtimeGate = new();
    private RuntimeCatalog? _runtime;
    private RuntimeCatalog? Runtime => Volatile.Read(ref _runtime);
    internal bool RuntimePrepared => Runtime is not null;

    private sealed record RuntimeCatalog(
        CatalogRuntimeSources Sources,
        FrozenDictionary<int, ImmutableDictionary<DogmaAttributeId, double>> Attributes,
        FrozenDictionary<int, CatalogCelestialDefinition> Bodies,
        FrozenDictionary<int, ImmutableArray<CatalogCelestialDefinition>> SystemBodies,
        IReadOnlyDictionary<int, SpacePosition> FirstPositions,
        IReadOnlyDictionary<int, int> StaticGateSystems
    );

    public void PrepareRuntime()
    {
        lock (_runtimeGate)
        {
            if (_runtime is not null)
            {
                return;
            }
            RequireCurrent();
            using CatalogDbContext catalog = new(Identity.Path);
            // The core type source is pinned for this generation. Its typed table
            // can serve every runtime lookup without retaining the encoded rows too.
            _ = Pin(CatalogData.Client.Types);
            var sources = CatalogRuntimeSources.Load(catalog, CatalogData.Client.Types.Name);
            var attributes = catalog
                .Set<TypeAttribute>()
                .Select(attribute => new
                {
                    attribute.ItemTypeId,
                    attribute.AttributeId,
                    attribute.Value,
                })
                .AsEnumerable()
                .GroupBy(attribute => attribute.ItemTypeId)
                .ToFrozenDictionary(
                    group => group.Key,
                    group => group.ToImmutableDictionary(attribute => (DogmaAttributeId)attribute.AttributeId, attribute => attribute.Value)
                );
            var bodies = CelestialRows(catalog.Set<Celestial>().OrderBy(body => body.Id))
                .AsEnumerable()
                .ToFrozenDictionary(body => body.Id);
            var systems = bodies
                .Values.GroupBy(body => body.SolarSystemId)
                .ToFrozenDictionary(group => group.Key, group => group.OrderBy(body => body.Id).ToImmutableArray());
            var first = systems.ToFrozenDictionary(
                group => group.Key,
                group => new SpacePosition(group.Value[0].X, group.Value[0].Y, group.Value[0].Z)
            );
            var gates = catalog.StaticGateSystems();

            // Load the projected indexes before switching ordinary reads to the prepared generation.
            _ = Items;
            _ = Systems;
            _ = Stations;
            _ = NpcCorporations;
            _ = Regions;
            _ = Constellations;
            _ = Gates;
            _ = _gateBodies.Value;
            _ = _systemPolitics.Value;
            _ = _systemSecurity.Value;
            _ = _systemBelts.Value;
            _ = _stationNames.Value;
            _ = PublishedSkills;
            RequireCurrent();
            Volatile.Write(ref _runtime, new(sources, attributes, bodies, systems, first, gates));
            PreloadCore();
        }
    }

    internal IReadOnlyDictionary<int, SpacePosition>? RuntimeFirstPositions => Runtime?.FirstPositions;
    internal IReadOnlyDictionary<int, int>? RuntimeStaticGateSystems => Runtime?.StaticGateSystems;
}
