using System.Collections.Frozen;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;

namespace Eden.World.Catalog;

public sealed partial class CatalogDbContext
{
    private readonly CatalogConnectionInputs _connectionInputs = writable ? new() : CatalogConnectionScope.Inputs(path);

    internal CatalogItemDefinition RequireItem(int typeId) =>
        FittingType(typeId) ?? throw new InvalidDataException($"Missing catalog item type {typeId}.");

    internal CatalogItemDefinition? FittingType(long typeId) =>
        typeId is >= int.MinValue and <= int.MaxValue ? FittingType((int)typeId) : null;

    internal string? NpcCorporationName(int corporationId) =>
        writable ? Set<NpcCorporation>().Find(corporationId)?.Name : Definitions.NpcCorporations.GetValueOrDefault(corporationId);

    internal bool NpcCorporationNameExists(string name) =>
        (writable ? Set<NpcCorporation>().Select(corporation => corporation.Name).AsEnumerable() : Definitions.NpcCorporations.Values).Any(
            candidate => EqualAsciiNoCase(candidate, name)
        );

    private static bool EqualAsciiNoCase(string left, string right)
    {
        // Preserve SQLite's built-in NOCASE behavior for catalog-name uniqueness.
        if (left.Length != right.Length)
        {
            return false;
        }
        for (int index = 0; index < left.Length; index++)
        {
            char first = left[index] is >= 'A' and <= 'Z' ? (char)(left[index] + ('a' - 'A')) : left[index];
            char second = right[index] is >= 'A' and <= 'Z' ? (char)(right[index] + ('a' - 'A')) : right[index];
            if (first != second)
            {
                return false;
            }
        }
        return true;
    }

    internal CatalogItemDefinition? FittingType(int typeId)
    {
        if (writable)
        {
            var type = Set<ItemType>().Find(typeId);
            if (type is null)
            {
                return null;
            }
            int category = Set<ItemGroup>().Find(type.GroupId)?.CategoryId ?? 0;
            return new(type.Id, type.GroupId, category, type.Name, type.Mass, type.Radius, type.Capacity);
        }
        return Definitions.Items.GetValueOrDefault(typeId);
    }

    internal IReadOnlyDictionary<DogmaAttributeId, double> FittingAttributes(int typeId)
    {
        if (!writable)
        {
            return Definitions.Attributes(typeId);
        }
        var loaded = Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == typeId)
            .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value)
            .ToFrozenDictionary();
        return loaded;
    }
}
