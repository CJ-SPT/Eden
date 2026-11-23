using System.Collections.Frozen;
using System.Collections.Immutable;
using Eden.Data.World.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed class WormholeEnvironmentStore(WorldDatabase database) : IWormholeEnvironments
{
    private const long FirstEffectBeaconId = 8_810_000_000_000;

    private sealed record EffectView(CatalogProvider Catalog, IReadOnlyList<WormholeSystemEffect> Effects);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, EffectView> _views = [];
    private const int SystemEffectCategory = 7;
    private static readonly CatalogDefinition<FrozenDictionary<int, WormholeEnvironment>> s_environments = new(
        "wormhole-environments",
        Load
    );

    public IReadOnlyList<WormholeSystemEffect> Effects(int systemId)
    {
        var provider = database.Catalog;
        if (_views.TryGetValue(systemId, out var cached) && ReferenceEquals(cached.Catalog, provider))
        {
            return cached.Effects;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var environment = At(catalog, systemId);
        IReadOnlyList<WormholeSystemEffect> effects = environment is null
            ? []
            : environment
                .EffectIds.Select(id => new WormholeSystemEffect(id, FirstEffectBeaconId + systemId, environment.TypeId))
                .ToArray();
        _views[systemId] = new(provider, effects);
        return effects;
    }

    internal static WormholeEnvironment? At(CatalogDbContext catalog, FlightPosition? position) =>
        position?.SystemId is int system ? At(catalog, system) : null;

    internal static WormholeEnvironment? At(CatalogDbContext catalog, int system) =>
        WormholeRules.IsWormholeSystem(system) ? Environments(catalog).GetValueOrDefault(system) : null;

    internal static FrozenDictionary<int, WormholeEnvironment> Environments(CatalogDbContext catalog) =>
        catalog.GlobalDefinition(s_environments);

    private static FrozenDictionary<int, WormholeEnvironment> Load(CatalogDbContext catalog, int unused)
    {
        Dictionary<int, WormholeEnvironment> environments = [];
        Dictionary<int, WormholeEnvironment> profiles = [];
        foreach (var sun in catalog.Records(CatalogData.Sde.MapSecondarySuns).Values)
        {
            if (sun.SolarSystemID is not long systemId || sun.EffectBeaconTypeID is not > 0)
            {
                continue;
            }
            int system = checked((int)systemId);
            int type = checked((int)sun.EffectBeaconTypeID.Value);
            if (!WormholeRules.IsWormholeSystem(system))
            {
                continue;
            }
            if (!profiles.TryGetValue(type, out var profile))
            {
                var attributes = catalog.FittingAttributes(type);
                var dogma = catalog.Read(CatalogData.Client.Typedogma, (long)type);
                List<WormholeSystemModifier> modifiers = [];
                List<int> ids = [];
                foreach (var reference in dogma?.DogmaEffects ?? [])
                {
                    var effect = catalog.Require(
                        CatalogData.Client.Dogmaeffects,
                        reference.EffectID ?? throw new InvalidDataException("Missing wormhole system effect ID.")
                    );
                    if (effect.EffectCategory != SystemEffectCategory)
                    {
                        throw new InvalidDataException($"Wormhole beacon {type} has a non-system effect.");
                    }
                    ids.Add(checked((int)effect.EffectID!.Value));
                    foreach (var source in effect.ModifierInfo ?? [])
                    {
                        var selector = source.Func switch
                        {
                            "ItemModifier" when source.Domain == "shipID" => ModifierSelector.Item,
                            "LocationModifier" when source.Domain == "shipID" => ModifierSelector.Location,
                            "LocationGroupModifier" when source.Domain == "shipID" => ModifierSelector.Group,
                            "LocationRequiredSkillModifier" when source.Domain == "shipID" => ModifierSelector.RequiredSkill,
                            "OwnerRequiredSkillModifier" when source.Domain == "charID" => ModifierSelector.OwnerRequiredSkill,
                            _ => throw new InvalidDataException($"Unsupported wormhole beacon selector on type {type}."),
                        };
                        if (
                            source.Operation is not ((int)DogmaOperation.PostMultiply or (int)DogmaOperation.PostPercent)
                            || source.ModifiedAttributeID is null
                            || source.ModifyingAttributeID is null
                            || !attributes.TryGetValue((DogmaAttributeId)source.ModifyingAttributeID.Value, out double value)
                            || !double.IsFinite(value)
                        )
                        {
                            throw new InvalidDataException($"Invalid wormhole beacon modifier on type {type}.");
                        }
                        int? selection = selector == ModifierSelector.Group ? (int?)source.GroupID : (int?)source.SkillTypeID;
                        if (
                            selector is ModifierSelector.Group or ModifierSelector.RequiredSkill or ModifierSelector.OwnerRequiredSkill
                            && selection is not > 0
                        )
                        {
                            throw new InvalidDataException($"Missing wormhole beacon selection on type {type}.");
                        }
                        modifiers.Add(
                            new(
                                selector,
                                selection,
                                (DogmaAttributeId)source.ModifiedAttributeID.Value,
                                (DogmaOperation)source.Operation.Value,
                                value
                            )
                        );
                    }
                }
                profile = new(type, modifiers.Distinct().ToImmutableArray()) { EffectIds = ids.Distinct().Order().ToImmutableArray() };
                profiles.Add(type, profile);
            }
            environments.Add(system, profile);
        }
        return environments.ToFrozenDictionary();
    }
}
