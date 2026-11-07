using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eden.Data.Catalog;

// The pinned source has no non-null observation at this position. Never accept
// an invented payload if a later catalog populates an empty field or dataset.
[JsonConverter(typeof(CatalogAbsentConverter))]
public readonly record struct CatalogAbsent;

internal sealed class CatalogAbsentConverter : JsonConverter<CatalogAbsent>
{
    public override CatalogAbsent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Null)
        {
            throw new JsonException("A pinned-empty catalog field cannot contain a value.");
        }
        return default;
    }

    public override void Write(Utf8JsonWriter writer, CatalogAbsent value, JsonSerializerOptions options) => writer.WriteNullValue();
}
