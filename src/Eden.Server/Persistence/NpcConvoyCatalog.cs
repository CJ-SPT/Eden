using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Loot;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal sealed record NpcConvoyProfile(
    int TypeId,
    int RaceId,
    double Mass,
    double Radius,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes
);

[Injectable(InjectionType.Singleton)]
internal sealed class NpcConvoyCatalog(WorldDatabase database)
{
    private readonly Lazy<IReadOnlyList<NpcConvoyProfile>> _profiles = new(() => Load(database));

    internal IReadOnlyList<NpcConvoyProfile> Profiles => _profiles.Value;

    private static IReadOnlyList<NpcConvoyProfile> Load(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<NpcConvoyProfile> profiles = [];
        foreach (
            var type in catalog
                .Set<ItemType>()
                .Include(t => t.Group)
                .Where(t => t.GroupId == NpcConvoyRules.GroupId)
                .OrderBy(t => t.Id)
                .ToArray()
        )
        {
            try
            {
                var definition = catalog.FindSource("client/types", type.Id.ToString(CultureInfo.InvariantCulture))?.Read<ClientTypes>();
                var attributes = PilotFittingStore.ReadAttributes(catalog, type.Id);
                if (
                    type.Mass is not > 0
                    || type.Radius is not > 0
                    || !double.IsFinite(type.Mass.Value)
                    || !double.IsFinite(type.Radius.Value)
                    || type.Radius.Value * 2 >= NpcConvoyRules.MemberSpacing
                    || type.Group.CategoryId != NpcEncounterDefinitions.EntityCategory
                    || definition?.RaceID is not > 0
                    || attributes.Values.Any(v => !double.IsFinite(v))
                    || attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints) <= 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity) < 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.ArmorHitPoints) < 0
                    || definition.WreckTypeID is not long wreck
                    || !catalog.Set<ItemType>().Any(t => t.Id == wreck && t.GroupId == WreckDefinitions.Group)
                )
                {
                    throw new InvalidDataException("Incomplete convoy identity, health, movement or wreck definition.");
                }
                _ = NpcMotionRules.SpeedFraction(attributes);
                if (!double.IsFinite(NpcConvoyRules.DurationSeconds(type.Mass.Value, attributes)))
                {
                    throw new InvalidDataException("Invalid convoy travel duration.");
                }
                profiles.Add(new(type.Id, checked((int)definition.RaceID.Value), type.Mass.Value, type.Radius.Value, attributes));
            }
            catch (InvalidDataException error)
            {
                ServerLog.Console.Warning($"NPC convoys: skipped type {type.Id}: {error.Message}");
            }
        }
        return profiles;
    }
}
