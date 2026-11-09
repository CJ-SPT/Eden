using System.Security.Cryptography;
using System.Text.Json;
using Eden.Data.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Skills;

public sealed record SkillClientPackage(string Id, long Generation, string CodeSha256, long CodeLength);

public static class SkillPackageActivation
{
    public static string DirectoryPath(string worldPath, string id)
    {
        ValidateId(id);
        return Path.GetFullPath(worldPath) + ".skill-packages/" + id;
    }

    public static void ValidateId(string id)
    {
        if (id.Length != 64 || !id.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Invalid skill package identity.");
        }
    }

    public static SkillClientPackage? Active(string worldPath)
    {
        string marker = Path.GetFullPath(worldPath) + ".skills-active";
        return File.Exists(marker) ? ReadPackage(worldPath, File.ReadAllText(marker)) : null;
    }

    public static SkillClientPackage ReadPackage(string worldPath, string id) =>
        JsonSerializer.Deserialize<SkillClientPackage>(
            File.ReadAllText(Path.Combine(DirectoryPath(worldPath, id), "package.json")),
            SkillEditorStore.Json
        ) ?? throw new InvalidDataException("The skill package is empty.");

    public static string CatalogPath(string worldPath, string original) =>
        Active(worldPath) is { } package ? Path.Combine(DirectoryPath(worldPath, package.Id), "catalog.sqlite") : original;

    internal static bool Activate(WorldDatabase database)
    {
        if (!File.Exists(SkillEditorDbContext.FilePath(database.Path)))
        {
            return false;
        }
        using SkillEditorDbContext db = new(database.Path);
        using var transaction = db.Database.BeginTransaction();
        var state = db.Set<SavedSkillEditorState>().Single();
        if (state.CatalogFingerprint != Eden.World.Catalog.CatalogProvider.Open(database.OriginalCatalogPath).Identity.SourceFingerprint)
        {
            throw new InvalidDataException("The skill editor requires its matching original catalog.");
        }
        if (state.PendingPackage is not { } id)
        {
            return false;
        }
        var package = ReadPackage(database.Path, id);
        string folder = DirectoryPath(database.Path, id);
        if (package.Id != id || !File.Exists(Path.Combine(folder, "catalog.sqlite")) || !File.Exists(Path.Combine(folder, "code.ccp")))
        {
            throw new InvalidDataException("The published skill package is incomplete.");
        }
        using (var code = File.OpenRead(Path.Combine(folder, "code.ccp")))
        {
            if (code.Length != package.CodeLength || Convert.ToHexString(SHA256.HashData(code)) != package.CodeSha256)
            {
                throw new InvalidDataException("The published skill client archive failed verification.");
            }
        }
        string marker = Path.GetFullPath(database.Path) + ".skills-active";
        string temporary = marker + ".tmp";
        File.WriteAllText(temporary, id);
        File.Move(temporary, marker, overwrite: true);
        transaction.Commit();
        database.ResetSkillCatalog();
        Eden.World.Fittings.ResidentFitCache.CatalogChanged(database.OriginalCatalogPath);
        return true;
    }

    internal static void CompleteActivation(WorldDatabase database)
    {
        using SkillEditorDbContext db = new(database.Path);
        using var transaction = db.Database.BeginTransaction();
        var state = db.Set<SavedSkillEditorState>().Single();
        if (state.PendingPackage == Active(database.Path)?.Id)
        {
            state.PendingPackage = null;
            db.SaveChanges();
        }
        transaction.Commit();
    }
}
