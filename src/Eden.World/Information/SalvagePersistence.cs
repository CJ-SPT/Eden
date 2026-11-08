using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eden.Data.World.Loot;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Information;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Information;

internal static class SalvagePersistence
{
    internal const int CargoContainerType = 23;
    internal const int CargoContainerGroup = 12;

    internal static void Seed(CatalogDbContext catalog, WorldWreck wreck)
    {
        if (wreck.SalvageJson is not null || wreck.SalvagedAt is not null)
        {
            return;
        }
        string name = catalog.RequireItem(wreck.TypeId).Name;
        SalvageFamily? family =
            name.Contains("Blood", StringComparison.OrdinalIgnoreCase) ? SalvageFamily.BloodRaider
            : name.Contains("Sansha", StringComparison.OrdinalIgnoreCase) ? SalvageFamily.Sansha
            : name.Contains("Guristas", StringComparison.OrdinalIgnoreCase) || name.Contains("Caldari", StringComparison.OrdinalIgnoreCase)
                ? SalvageFamily.Caldari
            : name.Contains("Angel", StringComparison.OrdinalIgnoreCase) || name.Contains("Minmatar", StringComparison.OrdinalIgnoreCase)
                ? SalvageFamily.Minmatar
            : name.Contains("Sansha", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Blood", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Amarr", StringComparison.OrdinalIgnoreCase)
                ? SalvageFamily.Amarr
            : name.Contains("Serpentis", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gallente", StringComparison.OrdinalIgnoreCase)
                ? SalvageFamily.Gallente
            : name.Contains("Drone", StringComparison.OrdinalIgnoreCase) ? SalvageFamily.RogueDrone
            : null;
        if (family is null)
        {
            return;
        }
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{wreck.Id}:{wreck.TypeId}:{wreck.CreatedAt}")));
        var values = catalog.FittingAttributes(wreck.TypeId);
        bool advanced =
            name.Contains("Advanced", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Commander", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Officer", StringComparison.OrdinalIgnoreCase);
        if (SalvageLootRules.Pool(family.Value, advanced).Count == 0)
        {
            return;
        }
        wreck.SalvageJson = JsonSerializer.Serialize(
            SalvageLootRules.Roll(
                family.Value,
                advanced,
                values.GetValueOrDefault(DogmaAttributeId.AccessDifficulty),
                BinaryPrimitives.ReadUInt32LittleEndian(digest)
            )
        );
    }

    internal static void Upgrade(WorldDbContext world, CatalogDbContext catalog)
    {
        var columns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldWreck')").ToHashSet();
        if (!columns.Contains(nameof(WorldWreck.SalvageJson)))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldWreck ADD COLUMN SalvageJson TEXT");
        }
        if (!columns.Contains(nameof(WorldWreck.SalvagedAt)))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldWreck ADD COLUMN SalvagedAt INTEGER");
        }
        foreach (var wreck in world.Set<WorldWreck>().Where(w => w.SalvageJson == null && w.SalvagedAt == null))
        {
            Seed(catalog, wreck);
        }
        world.SaveChanges();
    }
}
