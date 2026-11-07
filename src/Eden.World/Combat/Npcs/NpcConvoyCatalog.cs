using System.Collections.Immutable;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

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
    private static readonly CatalogDefinition<ImmutableArray<NpcConvoyProfile>> s_profiles = new(
        "npc-convoy-profiles",
        (catalog, _) => Load(catalog),
        Estimate: values => 128 + values.Length * 8192L
    );

    internal IReadOnlyList<NpcConvoyProfile> Profiles
    {
        get
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            return catalog.Definition(s_profiles, 0);
        }
    }

    private static ImmutableArray<NpcConvoyProfile> Load(CatalogDbContext catalog)
    {
        List<NpcConvoyProfile> profiles = [];
        foreach (var type in catalog.Definitions.TypesInGroup(NpcConvoyRules.GroupId).Select(catalog.RequireItem).OrderBy(t => t.Id))
        {
            try
            {
                var definition = catalog.Read(CatalogData.Client.Types, (long)type.Id);
                var attributes = PilotFittingStore.ReadAttributes(catalog, type.Id);
                if (
                    type.Mass is not > 0
                    || type.Radius is not > 0
                    || !double.IsFinite(type.Mass.Value)
                    || !double.IsFinite(type.Radius.Value)
                    || type.Radius.Value * 2 >= NpcConvoyRules.MemberSpacing
                    || type.CategoryId != NpcEncounterDefinitions.EntityCategory
                    || definition?.RaceID is not > 0
                    || attributes.Values.Any(v => !double.IsFinite(v))
                    || attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints) <= 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity) < 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.ArmorHitPoints) < 0
                    || definition.WreckTypeID is not long wreck
                    || catalog.FittingType(wreck)?.GroupId != WreckDefinitions.Group
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
        return profiles.ToImmutableArray();
    }
}
