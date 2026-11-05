using System.Collections.Frozen;
using Eden.Gameplay.Dogma;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Catalog;

public sealed partial class CatalogDbContext
{
    private readonly CatalogFittingInputs _fittingInputs = writable ? new() : CatalogFittingReadScope.Inputs(path);

    internal ItemType? FittingType(int typeId)
    {
        if (writable)
        {
            return Set<ItemType>().Find(typeId);
        }
        if (!_fittingInputs.Types.TryGetValue(typeId, out var type))
        {
            type = Set<ItemType>().Find(typeId);
            _fittingInputs.Types.Add(typeId, type);
        }
        return type;
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
        if (!writable && _fittingInputs.Attributes.TryGetValue(typeId, out var attributes))
        {
            return attributes;
        }
        var loaded = Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == typeId)
            .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value)
            .ToFrozenDictionary();
        if (!writable)
        {
            _fittingInputs.Attributes.Add(typeId, loaded);
        }
        return loaded;
    }
}
