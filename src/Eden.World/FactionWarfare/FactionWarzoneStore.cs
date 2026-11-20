using System.Collections.Immutable;
using Eden.Data.Catalog.FactionWarfare;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.FactionWarfare;

[Injectable(InjectionType.Singleton)]
internal sealed class FactionWarzoneStore(WorldDatabase database) : IFactionWarzones
{
    private static readonly ImmutableArray<WarzoneSystem> Initial = FactionWarzoneCatalog
        .Roster.Select(seed => Read(seed, _ => null))
        .ToImmutableArray();

    public ImmutableArray<WarzoneDefinition> Warzones => FactionWarzoneCatalog.Warzones;
    public ImmutableArray<MilitiaHeadquarters> Headquarters => FactionWarzoneCatalog.Headquarters;

    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Faction warfare occupation requires the resident world.");

    public WarzoneSystem? System(int systemId)
    {
        if (!FactionWarzoneCatalog.Systems.ContainsKey(systemId))
        {
            return null;
        }
        return Resident.Economy.Inspect(world => System(world, systemId));
    }

    internal static WarzoneSystem? System(WorldAction world, int systemId)
    {
        if (!FactionWarzoneCatalog.Systems.TryGetValue(systemId, out var seed))
        {
            return null;
        }
        var occupations = world.Set<SavedWarzoneOccupation>();
        return Read(seed, id => occupations.Find(id));
    }

    public ImmutableArray<WarzoneSystem> Systems() =>
        Resident.Economy.Inspect(world =>
        {
            var occupations = world.Set<SavedWarzoneOccupation>().ToDictionary(row => row.SystemId);
            return occupations.Count == 0
                ? Initial
                : FactionWarzoneCatalog.Roster.Select(seed => Read(seed, id => occupations.GetValueOrDefault(id))).ToImmutableArray();
        });

    private static WarzoneSystem Read(WarzoneSeed seed, Func<int, SavedWarzoneOccupation?> occupation)
    {
        var saved = occupation(seed.SystemId);
        var warzone = FactionWarzoneCatalog.Warzone(seed.OriginalOccupier);
        return new(
            seed.SystemId,
            warzone,
            saved?.Occupier ?? seed.OriginalOccupier,
            FactionWarzoneRules.Adjacency(seed.SystemId, Occupier, id => FactionWarzoneCatalog.Systems[id].Neighbors),
            saved?.VictoryPoints ?? 0,
            saved?.CapturedAt
        );

        MilitiaFaction Occupier(int id) => occupation(id)?.Occupier ?? FactionWarzoneCatalog.Systems[id].OriginalOccupier;
    }
}
