using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

[Injectable(InjectionType.Singleton)]
public sealed class SkillEditorStore(WorldDatabase database, TimeProvider clock) : ISkillEditor
{
    private const int SkillCategory = 16;
    private const int SkillLevelEffect = 132;
    private static readonly int[] Races = [1, 2, 4, 8];
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly Lazy<ImmutableDictionary<int, SkillDefinition>> _original = new(() => LoadOriginal(database));
    private readonly object _initialize = new();
    private bool _initialized;
    private CatalogProvider Catalog => CatalogProvider.Open(database.OriginalCatalogPath);

    private SkillEditorDbContext Open()
    {
        lock (_initialize)
        {
            if (!_initialized)
            {
                using SkillEditorDbContext setup = new(database.Path);
                setup.Database.EnsureCreated();
                setup.Database.ExecuteSqlRaw(
                    """
                    CREATE TRIGGER IF NOT EXISTS SkillRevisionImmutableUpdate BEFORE UPDATE ON SkillEditRevision
                        BEGIN SELECT RAISE(ABORT,'Skill revisions are immutable'); END;
                    CREATE TRIGGER IF NOT EXISTS SkillRevisionImmutableDelete BEFORE DELETE ON SkillEditRevision
                        BEGIN SELECT RAISE(ABORT,'Skill revisions are immutable'); END;
                    CREATE TRIGGER IF NOT EXISTS SkillIdentityImmutable BEFORE UPDATE OF TypeId,UserCreated ON SkillEdit
                        BEGIN SELECT RAISE(ABORT,'Skill identity and origin are immutable'); END;
                    CREATE TRIGGER IF NOT EXISTS SkillIdentityRetained BEFORE DELETE ON SkillEdit
                        BEGIN SELECT RAISE(ABORT,'Retain skill identities and history'); END;
                    """
                );
                using var transaction = setup.Database.BeginTransaction();
                if (!setup.Set<SavedSkillEditorState>().Any())
                {
                    setup.Add(new SavedSkillEditorState { CatalogFingerprint = Catalog.Identity.SourceFingerprint });
                    setup.SaveChanges();
                }
                transaction.Commit();
                _initialized = true;
            }
        }
        return new(database.Path);
    }

    internal static SkillDefinition Decode(string document) =>
        JsonSerializer.Deserialize<SkillDefinition>(document, Json) ?? throw new InvalidDataException("The skill definition is empty.");

    private SavedSkillEditorState State(SkillEditorDbContext db, long? generation = null)
    {
        var state = db.Set<SavedSkillEditorState>().Single();
        if (state.CatalogFingerprint != Catalog.Identity.SourceFingerprint)
        {
            throw new InvalidDataException("These skill edits belong to a different catalog. Restore the matching original catalog.");
        }
        if (generation.HasValue && state.Generation != generation)
        {
            throw new InvalidOperationException("Skills changed in another editor. Reload before saving; your draft has been kept.");
        }
        return state;
    }

    public SkillEditorSnapshot Read()
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        var state = State(db);
        var edited = db.Set<SavedSkillEdit>().ToDictionary(row => row.TypeId);
        return new(
            state.Generation,
            state.CatalogFingerprint,
            _original.Value.Keys.Union(edited.Keys).Order().Select(id => Entry(id, edited.GetValueOrDefault(id))).ToImmutableArray()
        );
    }

    private SkillEditorEntry Entry(int id, SavedSkillEdit? edit) =>
        new(
            edit is null ? _original.Value[id] : Decode(edit.Document),
            _original.Value.GetValueOrDefault(id),
            edit?.UserCreated ?? false,
            edit?.Deleted ?? false,
            edit?.Revision ?? 0
        );

    public IReadOnlyList<SkillEditorOption> Groups() =>
        _original
            .Value.Values.Select(skill => skill.GroupId)
            .Distinct()
            .Order()
            .Select(id => new SkillEditorOption(id, GroupName(id)))
            .ToArray();

    private string GroupName(int id)
    {
        var group = Catalog.Find(CatalogData.Client.Groups, (long)id);
        return group?.GroupNameID is long name ? Text(name) : $"Skill group {id}";
    }

    private string Text(long? id) => id is long key ? Catalog.Find(CatalogData.Client.LocalizationFsdEnUs, key)?.Text ?? "" : "";

    public IReadOnlyList<SkillEditorOption> Attributes() =>
        Catalog
            .Pin(CatalogData.Client.Dogmaattributes)
            .Select(pair => new SkillEditorOption(checked((int)pair.Key), pair.Value.Name ?? $"Attribute {pair.Key}"))
            .OrderBy(option => option.Name)
            .ToArray();

    public IReadOnlyList<SkillEditorOption> Effects() =>
        Catalog
            .Pin(CatalogData.Client.Dogmaeffects)
            .Select(pair => new SkillEditorOption(checked((int)pair.Key), pair.Value.EffectName ?? $"Effect {pair.Key}"))
            .OrderBy(option => option.Name)
            .ToArray();

    public SkillEditorEntry Save(long generation, SkillDefinition definition, string author)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Change(generation, definition.TypeId, definition, author, "Saved");
    }

    public SkillEditorEntry Create(long generation, SkillDefinition definition, string author)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Change(generation, null, definition, author, "Created");
    }

    public SkillEditorEntry Delete(long generation, int typeId, string author) => Change(generation, typeId, null, author, "Deleted");

    public SkillEditorEntry Restore(long generation, int typeId, long? revision, string author)
    {
        SkillDefinition definition;
        if (revision is long id)
        {
            using var db = Open();
            var saved = db.Set<SavedSkillEditRevision>().Find(id);
            if (saved?.TypeId != typeId)
            {
                throw new InvalidDataException("Choose a revision belonging to this skill.");
            }
            definition = Decode(saved.Document);
        }
        else
        {
            definition =
                _original.Value.GetValueOrDefault(typeId)
                ?? throw new InvalidDataException("Custom skills have no original catalog version.");
        }
        return Change(generation, typeId, definition, author, "Restored");
    }

    private SkillEditorEntry Change(long generation, int? typeId, SkillDefinition? definition, string author, string operation)
    {
        ArgumentNullException.ThrowIfNull(author);
        if (string.IsNullOrWhiteSpace(author) || author.Length > 100)
        {
            throw new InvalidDataException("A valid administrator name is required.");
        }
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        var state = State(db, generation);
        SavedSkillEdit? edit;
        if (typeId is int existing)
        {
            edit = db.Set<SavedSkillEdit>().Find(existing);
            if (edit is null && !_original.Value.ContainsKey(existing))
            {
                throw new InvalidDataException("Unknown skill.");
            }
            if (definition is null && edit?.UserCreated != true)
            {
                throw new InvalidDataException("Only user-created skills can be removed. Original skills can be restored instead.");
            }
            if (definition is not null && edit?.Deleted == true && operation != "Restored")
            {
                throw new InvalidDataException("Restore the deleted skill before editing it.");
            }
        }
        else
        {
            if (definition?.TypeId != 0)
            {
                throw new InvalidDataException("The server assigns IDs to new skills.");
            }
            while (Catalog.Items.ContainsKey(state.NextTypeId) || db.Set<SavedSkillEdit>().Any(row => row.TypeId == state.NextTypeId))
            {
                state.NextTypeId = checked(state.NextTypeId + 1);
            }
            typeId = state.NextTypeId;
            state.NextTypeId = checked(state.NextTypeId + 1);
            definition = definition with { TypeId = typeId.Value };
            edit = new() { TypeId = typeId.Value, UserCreated = true };
            db.Add(edit);
        }
        edit ??= new() { TypeId = typeId.Value };
        if (db.Entry(edit).State == EntityState.Detached)
        {
            db.Add(edit);
        }
        var definitions = _original.Value.ToDictionary();
        foreach (var saved in db.Set<SavedSkillEdit>().Where(row => !row.Deleted))
        {
            definitions[saved.TypeId] = Decode(saved.Document);
        }
        foreach (var deleted in db.Set<SavedSkillEdit>().Where(row => row.Deleted).Select(row => row.TypeId))
        {
            definitions.Remove(deleted);
        }
        if (definition is null)
        {
            if (definitions.Values.Any(skill => skill.TypeId != typeId && skill.Prerequisites.Any(required => required.TypeId == typeId)))
            {
                throw new InvalidDataException("Another skill requires this skill. Remove that prerequisite first.");
            }
            definition = Decode(edit.Document);
        }
        else
        {
            definition = Normalize(definition);
            definitions[definition.TypeId] = definition;
            Validate(definition, definitions);
        }
        state.Generation = checked(state.Generation + 1);
        edit.Revision = state.Generation;
        edit.Deleted = operation == "Deleted";
        edit.Document = JsonSerializer.Serialize(definition, Json);
        db.Add(
            new SavedSkillEditRevision
            {
                Id = state.Generation,
                TypeId = typeId.Value,
                Author = author,
                CreatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                Operation = operation,
                Document = edit.Document,
            }
        );
        db.SaveChanges();
        transaction.Commit();
        return Entry(typeId.Value, edit);
    }

    private static SkillDefinition Normalize(SkillDefinition skill)
    {
        if (
            (!skill.Prerequisites.IsDefault && skill.Prerequisites.Any(value => value is null))
            || (!skill.AlphaLimits.IsDefault && skill.AlphaLimits.Any(value => value is null))
            || (!skill.Attributes.IsDefault && skill.Attributes.Any(value => value is null))
            || (!skill.Effects.IsDefault && skill.Effects.Any(value => value is null))
        )
        {
            throw new InvalidDataException("Skill collections cannot contain empty entries.");
        }
        return skill with
        {
            Name = skill.Name?.Trim() ?? "",
            Description = skill.Description ?? "",
            Prerequisites = skill.Prerequisites.IsDefault ? [] : skill.Prerequisites.OrderBy(value => value.TypeId).ToImmutableArray(),
            AlphaLimits = skill.AlphaLimits.IsDefault ? [] : skill.AlphaLimits.OrderBy(value => value.RaceId).ToImmutableArray(),
            Attributes = skill.Attributes.IsDefault ? [] : skill.Attributes.OrderBy(value => value.AttributeId).ToImmutableArray(),
            Effects = skill.Effects.IsDefault ? [] : skill.Effects.OrderBy(value => value.EffectId).ToImmutableArray(),
        };
    }

    private void Validate(SkillDefinition skill, IReadOnlyDictionary<int, SkillDefinition> definitions)
    {
        if (skill.Name.Length is < 1 or > 200 || skill.Description.Length > 16000 || skill.Name.Any(char.IsControl))
        {
            throw new InvalidDataException("Provide a name of 1–200 characters and a description of at most 16,000 characters.");
        }
        if (!Groups().Any(group => group.Id == skill.GroupId) || !double.IsFinite(skill.Rank) || skill.Rank is <= 0 or > 1000)
        {
            throw new InvalidDataException("Choose an existing skill group and a positive training multiplier up to 1,000.");
        }
        if (!IsTrainingAttribute(skill.PrimaryAttribute) || !IsTrainingAttribute(skill.SecondaryAttribute))
        {
            throw new InvalidDataException("Choose valid primary and secondary character attributes.");
        }
        if (!double.IsFinite(skill.BasePrice) || skill.BasePrice < 0 || skill.BasePrice > 1e15)
        {
            throw new InvalidDataException("Skillbook price must be finite, nonnegative and no more than 1,000 trillion ISK.");
        }
        if (
            skill.IconId is int icon && Catalog.Find(CatalogData.Client.Iconids, (long)icon) is null
            || skill.MarketGroupId is int market && Catalog.Find(CatalogData.Client.Marketgroups, (long)market) is null
        )
        {
            throw new InvalidDataException("Choose existing icon and market group IDs, or leave them empty.");
        }
        if (
            skill.Prerequisites.Length > 6
            || skill.Prerequisites.DistinctBy(value => value.TypeId).Count() != skill.Prerequisites.Length
            || skill.Prerequisites.Any(value => value.Level is < 1 or > 5 || !definitions.ContainsKey(value.TypeId))
        )
        {
            throw new InvalidDataException("Choose at most six distinct existing skills at levels I through V.");
        }
        if (
            skill.AlphaLimits.Length != Races.Length
            || skill.AlphaLimits.Select(value => value.RaceId).Order().SequenceEqual(Races) == false
            || skill.AlphaLimits.Any(value => value.Level is < 0 or > 5)
        )
        {
            throw new InvalidDataException("Provide an Alpha level from zero through five for every race.");
        }
        if (
            skill.Attributes.Length > 200
            || skill.Attributes.DistinctBy(value => value.AttributeId).Count() != skill.Attributes.Length
            || skill.Attributes.Any(value =>
                ManagedAttribute(value.AttributeId)
                || !double.IsFinite(value.Value)
                || Catalog.Find(CatalogData.Client.Dogmaattributes, (long)value.AttributeId) is null
            )
        )
        {
            throw new InvalidDataException(
                "Additional attributes must be distinct, known and finite. Edit training and prerequisites in their own fields."
            );
        }
        if (
            skill.Effects.Length > 100
            || skill.Effects.DistinctBy(value => value.EffectId).Count() != skill.Effects.Length
            || skill.Effects.Count(value => value.IsDefault) > 1
            || skill.Effects.Any(value => Catalog.Find(CatalogData.Client.Dogmaeffects, (long)value.EffectId) is null)
        )
        {
            throw new InvalidDataException("Effects must be distinct catalog effects, with at most one default effect.");
        }
        HashSet<int> visiting = [];
        bool originalSkill = _original.Value.TryGetValue(skill.TypeId, out var original);
        bool changedBonuses =
            !originalSkill
            || !skill.Attributes.OrderBy(value => value.AttributeId).SequenceEqual(original!.Attributes.OrderBy(value => value.AttributeId))
            || !skill.Effects.OrderBy(value => value.EffectId).SequenceEqual(original!.Effects.OrderBy(value => value.EffectId));
        if (changedBonuses)
        {
            if (originalSkill && !SkillEditingRules.CatalogBonuses(skill.TypeId))
            {
                throw new InvalidDataException(
                    "This original skill uses a dedicated gameplay calculation. Its bonus attributes and effects must be retained."
                );
            }
            foreach (var reference in skill.Effects.Where(effect => effect.EffectId != SkillLevelEffect))
            {
                var effect = Catalog.Get(CatalogData.Client.Dogmaeffects, (long)reference.EffectId);
                if (
                    effect.EffectCategory is not ((int)PassiveEffectPhase.Fitted) and not ((int)PassiveEffectPhase.Online)
                    || effect.ModifierInfo is not { Length: > 0 } modifiers
                    || modifiers.Any(value =>
                        PassiveCatalog.Parse(value, effect.EffectCategory.Value)
                            is not { Domain: ModifierDomain.Ship or ModifierDomain.Item }
                    )
                )
                {
                    throw new InvalidDataException(
                        $"Effect {reference.EffectId} is not a supported passive ship or module bonus. Choose a supported catalog effect."
                    );
                }
            }
        }
        HashSet<int> visited = [];
        void Visit(int id)
        {
            if (visited.Contains(id))
            {
                return;
            }
            if (!visiting.Add(id))
            {
                throw new InvalidDataException("Skill prerequisites cannot contain a cycle.");
            }
            foreach (var required in definitions[id].Prerequisites)
            {
                Visit(required.TypeId);
            }
            visiting.Remove(id);
            visited.Add(id);
        }
        Visit(skill.TypeId);
    }

    private static bool IsTrainingAttribute(int id) => id is >= 164 and <= 168;

    internal static bool ManagedAttribute(int id) =>
        id is (int)DogmaAttributeId.PrimaryAttribute or (int)DogmaAttributeId.SecondaryAttribute or (int)DogmaAttributeId.SkillTimeConstant
        || InventoryDefinitions.RequiredSkills.Any(pair => (int)pair.Skill == id || (int)pair.Level == id);

    public IReadOnlyList<SkillEditorRevision> History(int typeId)
    {
        using var db = Open();
        State(db);
        return db.Set<SavedSkillEditRevision>()
            .Where(row => row.TypeId == typeId)
            .OrderByDescending(row => row.Id)
            .AsEnumerable()
            .Select(row => new SkillEditorRevision(
                row.Id,
                row.TypeId,
                row.Author,
                DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt),
                row.Operation,
                Decode(row.Document)
            ))
            .ToArray();
    }

    public void StagePackage(long generation, string packageId, string? clientRoot = null)
    {
        SkillPackageActivation.ValidateId(packageId);
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        var state = State(db, generation);
        state.PendingPackage = packageId;
        state.ClientRoot = clientRoot ?? state.ClientRoot;
        db.SaveChanges();
        transaction.Commit();
    }

    private static ImmutableDictionary<int, SkillDefinition> LoadOriginal(WorldDatabase database)
    {
        var catalog = CatalogProvider.Open(database.OriginalCatalogPath);
        var alpha = Races.ToDictionary(
            race => race,
            race => catalog.Find(CatalogData.Client.Clonegrades, race.ToString(CultureInfo.InvariantCulture))?.Skills ?? []
        );
        return catalog
            .Items.Values.Where(item => item.CategoryId == SkillCategory)
            .ToImmutableDictionary(
                item => item.Id,
                item =>
                {
                    var type = catalog.Find(CatalogData.Client.Types, (long)item.Id);
                    var attributes = catalog.Attributes(item.Id);
                    var dogma = catalog.Find(CatalogData.Client.Typedogma, (long)item.Id);
                    string description = type?.DescriptionID is long id
                        ? catalog.Find(CatalogData.Client.LocalizationFsdEnUs, id)?.Text ?? ""
                        : "";
                    return new SkillDefinition
                    {
                        TypeId = item.Id,
                        Name = item.Name,
                        Description = description,
                        GroupId = item.GroupId,
                        IconId = type?.IconID is long icon ? checked((int)icon) : null,
                        MarketGroupId = type?.MarketGroupID is long market ? checked((int)market) : null,
                        Published = type?.Published == 1,
                        BasePrice = type?.BasePrice ?? 0,
                        Rank = attributes.GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1),
                        PrimaryAttribute = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.PrimaryAttribute, 165)),
                        SecondaryAttribute = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.SecondaryAttribute, 166)),
                        Prerequisites = catalog
                            .RequiredSkills(item.Id)
                            .Select(pair => new SkillPrerequisite(pair.Key, pair.Value))
                            .ToImmutableArray(),
                        AlphaLimits = Races
                            .Select(race => new SkillAlphaLimit(
                                race,
                                checked((int)(alpha[race].FirstOrDefault(value => value.TypeID == item.Id)?.Level ?? 0))
                            ))
                            .ToImmutableArray(),
                        Attributes = attributes
                            .Where(pair => !ManagedAttribute((int)pair.Key))
                            .Select(pair => new SkillAttributeValue((int)pair.Key, pair.Value))
                            .ToImmutableArray(),
                        Effects = (dogma?.DogmaEffects ?? [])
                            .Select(effect => new SkillEffectReference(checked((int)effect.EffectID!.Value), effect.IsDefault == 1))
                            .ToImmutableArray(),
                    };
                }
            );
    }
}
