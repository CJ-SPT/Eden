using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Eden.Gameplay.Characters;

namespace Eden.Protocol.Characters;

// Native UI settings are a dynamic boundary. Only inert scalar/collection data crosses it.
// The client reads YAML then GetDictFromList, so the root is a list of key/value pairs.
internal static class SharedSettingsWire
{
    private const int MaximumDepth = 32;
    private const int MaximumNodes = 20000;

    internal static string Encode(object? value)
    {
        if (value is not IDictionary dictionary)
        {
            throw new InvalidDataException("Expected a shared settings dictionary.");
        }
        StringBuilder output = new();
        int nodes = 0;
        Write(dictionary, 0, root: true);
        string payload = output.ToString();
        SharedSettingRules.Check(SharedSettingKind.Overview, payload);
        return payload;

        void Write(object? item, int depth, bool root = false)
        {
            if (depth > MaximumDepth || ++nodes > MaximumNodes || output.Length > SharedSettingRules.MaximumPayloadBytes)
            {
                throw new InvalidDataException("Shared settings are too large or too deeply nested.");
            }
            switch (item)
            {
                case null:
                    output.Append("null");
                    break;
                case string text:
                    if (text.Length > SharedSettingRules.MaximumPayloadBytes || text.Contains('\0'))
                    {
                        throw new InvalidDataException("Invalid shared settings text.");
                    }
                    output.Append(JsonSerializer.Serialize(text));
                    break;
                case byte[] bytes:
                    if (bytes.Length > SharedSettingRules.MaximumPayloadBytes)
                    {
                        throw new InvalidDataException("Shared settings text is too large.");
                    }
                    Write(Encoding.UTF8.GetString(bytes), depth + 1);
                    break;
                case bool boolean:
                    output.Append(boolean ? "true" : "false");
                    break;
                case long or int:
                    output.Append(Convert.ToString(item, CultureInfo.InvariantCulture));
                    break;
                case double number when double.IsFinite(number):
                    string formatted = number.ToString("R", CultureInfo.InvariantCulture);
                    // YAML 1.1 requires a decimal point to resolve exponent notation as a float.
                    if (!formatted.Contains('.'))
                    {
                        int exponent = formatted.IndexOf('E');
                        formatted = exponent < 0 ? formatted + ".0" : formatted.Insert(exponent, ".0");
                    }
                    output.Append(formatted);
                    break;
                case IDictionary map:
                    output.Append(root ? '[' : '{');
                    bool first = true;
                    foreach (DictionaryEntry entry in map)
                    {
                        if (entry.Key is not (string or byte[] or long or int))
                        {
                            throw new InvalidDataException("Unsupported shared settings key.");
                        }
                        if (!first)
                        {
                            output.Append(", ");
                        }
                        first = false;
                        if (root)
                        {
                            output.Append('[');
                        }
                        Write(entry.Key, depth + 1);
                        output.Append(root ? ", " : ": ");
                        Write(entry.Value, depth + 1);
                        if (root)
                        {
                            output.Append(']');
                        }
                    }
                    output.Append(root ? ']' : '}');
                    break;
                case IList list:
                    output.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i != 0)
                        {
                            output.Append(", ");
                        }
                        Write(list[i], depth + 1);
                    }
                    output.Append(']');
                    break;
                default:
                    throw new InvalidDataException("Unsupported shared settings value.");
            }
        }
    }
}
