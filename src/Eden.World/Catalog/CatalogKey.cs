using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Eden.World.Catalog;

// The importer stores json.dumps(..., ensure_ascii=False). Match that spelling,
// including lowercase control escapes, rather than the web serializer's HTML/Unicode escaping.
internal static class CatalogKey
{
    internal static string Encode<T>(T key)
    {
        if (key is not string value)
        {
            return JsonSerializer.Serialize(key);
        }
        StringBuilder encoded = new(value.Length + 2);
        encoded.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"':
                    encoded.Append("\\\"");
                    break;
                case '\\':
                    encoded.Append("\\\\");
                    break;
                case '\b':
                    encoded.Append("\\b");
                    break;
                case '\f':
                    encoded.Append("\\f");
                    break;
                case '\n':
                    encoded.Append("\\n");
                    break;
                case '\r':
                    encoded.Append("\\r");
                    break;
                case '\t':
                    encoded.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        encoded.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        encoded.Append(character);
                    }
                    break;
            }
        }
        return encoded.Append('"').ToString();
    }
}
