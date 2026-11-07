using System.Collections.Frozen;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;

namespace Eden.World.Catalog;

public sealed partial class CatalogDbContext
{
    private readonly CatalogFittingInputs _fittingInputs = writable ? new() : CatalogFittingReadScope.Inputs(path);

    internal CatalogItemDefinition RequireItem(int typeId) =>
        FittingType(typeId) ?? throw new InvalidDataException($"Missing catalog item type {typeId}.");

    internal CatalogItemDefinition? FittingType(long typeId) =>
        typeId is >= int.MinValue and <= int.MaxValue ? FittingType((int)typeId) : null;

    internal string? NpcCorporationName(int corporationId) =>
        writable ? Set<NpcCorporation>().Find(corporationId)?.Name : Definitions.NpcCorporations.GetValueOrDefault(corporationId);

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

    internal SourceRecord? FindSource(string dataset, string key)
    {
        // Read-only operation contexts see immutable catalog inputs. Writable
        // tooling/fixtures retain fresh reads after edits, including raw SQL.
        if (writable)
        {
            return Set<SourceRecord>().Find(dataset, key);
        }
        if (!_fittingInputs.Sources.TryGetValue((dataset, key), out var record))
        {
            record = Set<SourceRecord>().Find(dataset, key);
            _fittingInputs.Sources.Add((dataset, key), record);
        }
        return record;
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
