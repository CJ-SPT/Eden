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
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

[Injectable(InjectionType.Singleton)]
public sealed partial class SkillEditorStore(WorldDatabase database, TimeProvider clock) : ISkillEditor
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
    private CatalogProvider? _catalog;
    private CatalogProvider Catalog
    {
        get
        {
            // Retain the base generation even when the running world uses a published package.
            _catalog = CatalogProvider.Open(database.OriginalCatalogPath);
            return _catalog;
        }
    }
    private readonly Lazy<SkillEditorChoices> _choices = new(() => LoadChoices(database));

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

    public IReadOnlyList<SkillEditorOption> Groups() => Choices().Groups;

    public IReadOnlyList<SkillEditorOption> Attributes() => Choices().Attributes;

    public IReadOnlyList<SkillEditorOption> Effects() => Choices().Effects;

    public SkillEditorChoices Choices() => _choices.Value;

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

    private static SkillDefinition Normalize(SkillDefinition skill) => SkillDefinitionValidation.Normalize(skill);

    private void Validate(SkillDefinition skill, IReadOnlyDictionary<int, SkillDefinition> definitions) =>
        SkillDefinitionValidation.Validate(skill, definitions, _original.Value, Choices());

    internal static bool ManagedAttribute(int id) => SkillDefinitionValidation.ManagedAttribute(id);

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

    public static ImmutableDictionary<int, SkillDefinition> LoadOriginal(WorldDatabase database, bool active = false)
    {
        using var scope = new CatalogConnectionScope().Begin();
        var catalog = CatalogProvider.Open(active ? database.CatalogPath : database.OriginalCatalogPath);
        var items = catalog.Items.Values.Where(item => item.CategoryId == SkillCategory).ToArray();
        var ids = items.Select(item => (long)item.Id).ToArray();
        var types = catalog.FindMany(CatalogData.Client.Types, ids);
        var dogmas = catalog.FindMany(CatalogData.Client.Typedogma, ids);
        _ = catalog.FindMany(CatalogData.Client.Requiredskillsfortypes, ids);
        var attributeSets = catalog.AttributeSets(items.Select(item => item.Id));
        var descriptions = catalog.FindMany(
            CatalogData.Client.LocalizationFsdEnUs,
            types.Values.Where(type => type.DescriptionID.HasValue).Select(type => type.DescriptionID!.Value)
        );
        var alpha = Races.ToDictionary(
            race => race,
            race => catalog.Find(CatalogData.Client.Clonegrades, race.ToString(CultureInfo.InvariantCulture))?.Skills ?? []
        );
        return items.ToImmutableDictionary(
            item => item.Id,
            item =>
            {
                var type = types.GetValueOrDefault(item.Id);
                var attributes = attributeSets[item.Id];
                var dogma = dogmas.GetValueOrDefault(item.Id);
                string description = type?.DescriptionID is long id ? descriptions.GetValueOrDefault(id)?.Text ?? "" : "";
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
