using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Eden.Data.World.Modding;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Modding;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Modding;

[Injectable(InjectionType.Singleton)]
public sealed class ModStore(WorldDatabase database)
{
    private readonly object _gate = new();
    public bool OwnsCatalog
    {
        get
        {
            if (!File.Exists(ModDbContext.FilePath(database.Path)))
            {
                return false;
            }
            using var db = Open();
            var state = db.Set<SavedModConfiguration>().Single();
            return state.Active is not null || state.Activating;
        }
    }

    public string Root => Path.GetFullPath(database.Path) + ".mods";

    private ModDbContext Open()
    {
        var db = new ModDbContext(database.Path);
        db.Database.EnsureCreated();
        if (!db.Set<SavedModConfiguration>().Any())
        {
            db.Add(new SavedModConfiguration());
            db.SaveChanges();
        }
        return db;
    }

    public ModReference Reference() =>
        new(
            ModFormat.Version,
            ModFormat.ClientBuild,
            database.Catalog.Identity.SourceFingerprint,
            SkillEditorStore.LoadOriginal(database).Values.OrderBy(value => value.TypeId).ToArray(),
            SkillEditorStore.LoadChoices(database)
        );

    public string WorldIdentity()
    {
        lock (_gate)
        {
            string? saved = ReadState("", "world-identity");
            if (saved is not null)
            {
                return JsonSerializer.Deserialize<string>(saved, ModFormat.Json)!;
            }
            string identity = Guid.NewGuid().ToString("N");
            WriteState("", "world-identity", JsonSerializer.Serialize(identity, ModFormat.Json));
            return identity;
        }
    }

    public ModProjectDocument ExportLegacy(string id)
    {
        ModPackages.Identity(id);
        var editor = new SkillEditorStore(database, TimeProvider.System);
        var snapshot = editor.Read();
        var edited = snapshot.Skills.Where(value => value.UserCreated || value.IsModified || value.Revision > 0).ToArray();
        var skills = edited
            .Select(value => new ModSkill("skill-" + value.Definition.TypeId, value.Definition)
            {
                Replaces = value.UserCreated ? null : "catalog:" + value.Definition.TypeId,
                Retired = value.Deleted,
            })
            .ToList();
        return new()
        {
            Manifest = new()
            {
                Id = id,
                Name = "Migrated skill edits",
                CatalogFingerprint = snapshot.CatalogFingerprint,
                Overrides = skills.Where(value => value.Replaces is not null).Select(value => value.Replaces!).ToArray(),
            },
            Skills = skills,
            History = edited.SelectMany(value => editor.History(value.Definition.TypeId)).OrderBy(value => value.Id).ToList(),
            Generation = snapshot.Generation,
            NextTypeId = Math.Max(
                SkillEditingRules.FirstCustomTypeId,
                edited.Where(value => value.UserCreated).Select(value => value.Definition.TypeId + 1).DefaultIfEmpty().Max()
            ),
            LegacyIdentities = new(
                WorldIdentity(),
                skills
                    .Where(value => value.Replaces is null)
                    .Select(value => new ModIdentityAllocation(id + ":" + value.Id, value.Definition.TypeId))
                    .ToArray()
            ),
        };
    }

    public string Install(string archive)
    {
        var package = ModPackages.Read(archive);
        if (package.Manifest.CatalogFingerprint != Reference().CatalogFingerprint)
        {
            throw new InvalidDataException("This mod requires a different original catalog.");
        }
        lock (_gate)
        {
            string directory = Path.Combine(Root, "packages");
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, package.Hash + ".edenmod");
            if (!File.Exists(target))
            {
                string temporary = target + ".tmp";
                File.Copy(archive, temporary, overwrite: true);
                if (ModPackages.Read(temporary).Hash != package.Hash)
                {
                    throw new InvalidDataException("The mod archive changed during installation.");
                }
                File.Move(temporary, target);
            }
        }
        return package.Hash;
    }

    public void Enable(string hash, bool enabled)
    {
        SkillPackageActivation.ValidateId(hash);
        lock (_gate)
        {
            using var db = Open();
            var state = db.Set<SavedModConfiguration>().Single();
            var hashes = JsonSerializer.Deserialize<List<string>>(state.Enabled, ModFormat.Json)!;
            hashes.Remove(hash);
            if (enabled)
            {
                _ = ModPackages.Read(Path.Combine(Root, "packages", hash + ".edenmod"));
                hashes.Add(hash);
            }
            state.Enabled = JsonSerializer.Serialize(hashes.Order(), ModFormat.Json);
            state.Generation++;
            state.Pending = null;
            db.SaveChanges();
        }
    }

    public IReadOnlyList<ModPackage> Enabled()
    {
        lock (_gate)
        {
            using var db = Open();
            var state = db.Set<SavedModConfiguration>().Single();
            var hashes = JsonSerializer.Deserialize<string[]>(state.Enabled, ModFormat.Json)!;
            return ModPackages.Resolve(
                hashes.Select(hash => ModPackages.Read(Path.Combine(Root, "packages", hash + ".edenmod"))),
                Reference().CatalogFingerprint
            );
        }
    }

    public ModSetPackage? ActiveSet
    {
        get
        {
            if (!File.Exists(ModDbContext.FilePath(database.Path)))
            {
                return null;
            }
            using var db = Open();
            return db.Set<SavedModConfiguration>().Single().Active is { } id ? ReadSet(id) : null;
        }
    }

    public ModSetStatus Status()
    {
        lock (_gate)
        {
            using var db = Open();
            var state = db.Set<SavedModConfiguration>().Single();
            return new(
                state.Active is { } active ? ReadSet(active) : null,
                state.Pending is { } pending ? ReadSet(pending) : null,
                JsonSerializer
                    .Deserialize<string[]>(state.Enabled, ModFormat.Json)!
                    .Select(hash => ModPackages.Read(Path.Combine(Root, "packages", hash + ".edenmod")))
                    .Select(value => new ModDependency(value.Manifest.Id, value.Manifest.Version))
                    .ToArray()
            );
        }
    }

    public ModSetPackage ReadSet(string id)
    {
        SkillPackageActivation.ValidateId(id);
        return JsonSerializer.Deserialize<ModSetPackage>(File.ReadAllText(Path.Combine(SetDirectory(id), "mod-set.json")), ModFormat.Json)
            ?? throw new InvalidDataException("Missing mod set.");
    }

    public string SetDirectory(string id)
    {
        SkillPackageActivation.ValidateId(id);
        // Reuse the existing immutable catalog/publication reader and launcher archive recovery.
        return SkillPackageActivation.DirectoryPath(database.Path, id);
    }

    public (IReadOnlyList<ModPackage> Packages, SkillEditorSnapshot Snapshot) Prepare()
    {
        lock (_gate)
        {
            var packages = Enabled();
            if (
                !OwnsCatalog
                && File.Exists(SkillEditorDbContext.FilePath(database.Path))
                && new SkillEditorStore(database, TimeProvider.System).Read().Skills.Any(value => value.UserCreated || value.IsModified)
                && !packages.Any(value => value.Contents.ContainsKey("legacy-identities.json"))
            )
            {
                throw new InvalidDataException(
                    "Export and enable the migrated skill project before replacing this world's legacy skill edits."
                );
            }
            return (packages, Compose(packages));
        }
    }

    public SkillEditorSnapshot Compose(IReadOnlyList<ModPackage> packages)
    {
        lock (_gate)
        {
            var reference = Reference();
            var originals = reference.Skills.ToDictionary(value => value.TypeId);
            var definitions = originals.ToDictionary();
            Dictionary<string, string> owners = [];
            HashSet<int> retired = [];
            string worldIdentity = WorldIdentity();
            using var db = Open();
            using var transaction = db.Database.BeginTransaction();
            var identities = db.Set<SavedModIdentity>().ToDictionary(value => value.LogicalId);
            foreach (var package in packages.Where(value => value.Contents.ContainsKey("legacy-identities.json")))
            {
                var legacy =
                    JsonSerializer.Deserialize<ModLegacyIdentities>(package.Contents["legacy-identities.json"], ModFormat.Json)
                    ?? throw new InvalidDataException("Missing legacy identities.");
                if (legacy.WorldIdentity != worldIdentity)
                {
                    throw new InvalidDataException("This migrated skill project belongs to a different saved world.");
                }
                foreach (var allocation in legacy.Skills)
                {
                    if (
                        !allocation.LogicalId.StartsWith(package.Manifest.Id + ":", StringComparison.Ordinal)
                        || allocation.TypeId < SkillEditingRules.FirstCustomTypeId
                        || identities.Values.Any(value => value.TypeId == allocation.TypeId && value.LogicalId != allocation.LogicalId)
                        || identities.TryGetValue(allocation.LogicalId, out var existing) && existing.TypeId != allocation.TypeId
                    )
                    {
                        throw new InvalidDataException("Migrated skill identities conflict with retained world identities.");
                    }
                    if (!identities.ContainsKey(allocation.LogicalId))
                    {
                        var identity = new SavedModIdentity { LogicalId = allocation.LogicalId, TypeId = allocation.TypeId };
                        identities.Add(identity.LogicalId, identity);
                        db.Add(identity);
                    }
                }
            }
            int next = Math.Max(
                SkillEditingRules.FirstCustomTypeId,
                identities.Values.Select(value => value.TypeId + 1).DefaultIfEmpty().Max()
            );
            var payloads = packages
                .Select(package =>
                    (
                        Package: package,
                        Skills: package.Contents.TryGetValue("skills.json", out var bytes)
                            ? JsonSerializer.Deserialize<ModSkill[]>(bytes, ModFormat.Json) ?? []
                            : Array.Empty<ModSkill>()
                    )
                )
                .ToArray();
            HashSet<string> present = [];
            foreach (var payload in payloads)
            {
                foreach (var skill in payload.Skills)
                {
                    ModPackages.Identity(skill.Id);
                    string logical = payload.Package.Manifest.Id + ":" + skill.Id;
                    if (!present.Add(logical))
                    {
                        throw new InvalidDataException("Duplicate logical skill identity.");
                    }
                    if (skill.Replaces is not null || identities.ContainsKey(logical))
                    {
                        continue;
                    }
                    while (definitions.ContainsKey(next))
                    {
                        next = checked(next + 1);
                    }
                    var identity = new SavedModIdentity { LogicalId = logical, TypeId = next++ };
                    db.Add(identity);
                    identities.Add(logical, identity);
                }
            }
            int Resolve(string name)
            {
                if (
                    name.StartsWith("catalog:", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan("catalog:".Length), out int id)
                    && originals.ContainsKey(id)
                )
                {
                    return id;
                }
                return identities.TryGetValue(name, out var identity)
                    ? identity.TypeId
                    : throw new InvalidDataException($"Unknown skill reference {name}.");
            }
            foreach (var payload in payloads)
            {
                var manifest = payload.Package.Manifest;
                foreach (var skill in payload.Skills)
                {
                    string logical = manifest.Id + ":" + skill.Id;
                    string target = skill.Replaces ?? logical;
                    if (skill.Replaces is not null)
                    {
                        string owner = owners.GetValueOrDefault(target) ?? target.Split(':')[0];
                        if (
                            !manifest.Overrides.Contains(target)
                            || owner != "catalog" && !manifest.Dependencies.Any(value => value.Id == owner)
                        )
                        {
                            throw new InvalidDataException($"{manifest.Id} must explicitly depend on and override {target}.");
                        }
                    }
                    int typeId = Resolve(target);
                    if (owners.TryGetValue(target, out var previous) && !manifest.Dependencies.Any(value => value.Id == previous))
                    {
                        throw new InvalidDataException("Competing skill overrides.");
                    }
                    owners[target] = manifest.Id;
                    var definition = SkillDefinitionValidation.Normalize(
                        skill.Definition with
                        {
                            TypeId = typeId,
                            Prerequisites = skill.Definition.Prerequisites.AddRange(
                                skill.Requirements.Select(value => new SkillPrerequisite(Resolve(value.Skill), value.Level))
                            ),
                        }
                    );
                    definitions[typeId] = definition;
                    if (skill.Retired)
                    {
                        if (originals.ContainsKey(typeId))
                        {
                            throw new InvalidDataException("Original catalog skills cannot be retired.");
                        }
                        retired.Add(typeId);
                    }
                    if (skill.Replaces is null)
                    {
                        identities[logical].Definition = JsonSerializer.Serialize(definition, ModFormat.Json);
                    }
                }
            }
            foreach (var identity in identities.Values.Where(value => !present.Contains(value.LogicalId) && value.Definition.Length > 0))
            {
                definitions[identity.TypeId] = JsonSerializer.Deserialize<SkillDefinition>(identity.Definition, ModFormat.Json)!;
                retired.Add(identity.TypeId);
            }
            foreach (
                var definition in definitions.Values.Where(value =>
                    !retired.Contains(value.TypeId)
                    && (!originals.TryGetValue(value.TypeId, out var original) || !SkillEditingRules.SameDefinition(value, original))
                )
            )
            {
                SkillDefinitionValidation.Validate(definition, definitions, originals, reference.Choices);
                if (definition.Prerequisites.Any(value => retired.Contains(value.TypeId)))
                {
                    throw new InvalidDataException("A current skill requires a retired skill.");
                }
            }
            db.SaveChanges();
            transaction.Commit();
            var state = db.Set<SavedModConfiguration>().Single();
            return new(
                state.Generation,
                reference.CatalogFingerprint,
                definitions
                    .Values.OrderBy(value => value.TypeId)
                    .Select(value => new SkillEditorEntry(
                        value,
                        originals.GetValueOrDefault(value.TypeId),
                        !originals.ContainsKey(value.TypeId),
                        retired.Contains(value.TypeId),
                        state.Generation
                    ))
                    .ToImmutableArray()
            );
        }
    }

    public void Stage(ModSetPackage package, long generation)
    {
        lock (_gate)
        {
            using var db = Open();
            var state = db.Set<SavedModConfiguration>().Single();
            if (state.Generation != generation)
            {
                throw new InvalidOperationException("The selected mods changed during composition. Build again.");
            }
            state.Pending = package.Id;
            db.SaveChanges();
        }
    }

    public bool BeginActivation()
    {
        lock (_gate)
        {
            if (!File.Exists(ModDbContext.FilePath(database.Path)))
            {
                return false;
            }
            using var db = Open();
            var state = db.Set<SavedModConfiguration>().Single();
            if (state.Activating)
            {
                WriteMarker(state.Previous);
                state.Active =
                    state.Previous is { } previous && File.Exists(Path.Combine(SetDirectory(previous), "mod-set.json")) ? previous : null;
                state.Activating = false;
                db.SaveChanges();
                throw new InvalidDataException("An interrupted mod activation was recovered. Inspect the pending set before restarting.");
            }
            if (state.Pending is not { } id)
            {
                return false;
            }
            VerifySet(id);
            state.Previous = SkillPackageActivation.Active(database.Path)?.Id;
            state.Activating = true;
            db.SaveChanges();
            WriteMarker(id);
            database.ResetSkillCatalog();
            return true;
        }
    }

    public void CompleteActivation()
    {
        lock (_gate)
        {
            using var db = Open();
            var state = db.Set<SavedModConfiguration>().Single();
            if (state.Activating)
            {
                state.Active = state.Pending;
                state.Pending = null;
                state.Activating = false;
                db.SaveChanges();
            }
        }
    }

    private void WriteMarker(string? id)
    {
        string marker = Path.GetFullPath(database.Path) + ".skills-active";
        if (id is null)
        {
            File.Delete(marker);
        }
        else
        {
            File.WriteAllText(marker + ".tmp", id);
            File.Move(marker + ".tmp", marker, overwrite: true);
        }
        database.ResetSkillCatalog();
        Eden.World.Fittings.ResidentFitCache.CatalogChanged(database.OriginalCatalogPath);
    }

    public void VerifySet(string id)
    {
        string folder = SetDirectory(id);
        var package = ReadSet(id);
        using var stream = File.OpenRead(Path.Combine(folder, "code.ccp"));
        if (
            package.Id != id
            || stream.Length != package.CodeLength
            || Convert.ToHexString(SHA256.HashData(stream)) != package.CodeSha256
            || !File.Exists(Path.Combine(folder, "catalog.sqlite"))
        )
        {
            throw new InvalidDataException("The composed mod set failed verification.");
        }
        var files = JsonSerializer.Deserialize<ModFile[]>(File.ReadAllText(Path.Combine(folder, "files.json")), ModFormat.Json)!;
        foreach (var file in files)
        {
            ModPackages.RelativePath(file.Path);
            using var input = File.OpenRead(Path.Combine(folder, file.Path));
            if (input.Length != file.Length || Convert.ToHexString(SHA256.HashData(input)) != file.Sha256)
            {
                throw new InvalidDataException("A composed mod file failed verification.");
            }
        }
    }

    public int ResolveSkill(string logicalId)
    {
        using var db = Open();
        return db.Set<SavedModIdentity>().Find(logicalId)?.TypeId ?? throw new InvalidDataException("Unknown logical skill.");
    }

    public string? ReadState(string modId, string key)
    {
        using var db = Open();
        return db.Set<SavedModState>().Find(modId, key)?.Json;
    }

    public void WriteState(string modId, string key, string json)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || json.Length > ModFormat.MaximumCommandBytes)
        {
            throw new InvalidDataException("Mod state is too large or its key is invalid.");
        }
        using var document = JsonDocument.Parse(json);
        lock (_gate)
        {
            using var db = Open();
            var state = db.Set<SavedModState>().Find(modId, key);
            if (state is null)
            {
                state = new() { ModId = modId, Key = key };
                db.Add(state);
            }
            state.Json = json;
            db.SaveChanges();
        }
    }
}
