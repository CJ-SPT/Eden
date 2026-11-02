namespace Eden.Protocol.Serialization;

public static class PlanetAttributeFile
{
    public static IEnumerable<KeyValuePair<long, long[]>> Read(byte[] bytes)
    {
        if (BlueMarshal.Decode(bytes) is not Dictionary<long, object?> planets)
        {
            throw new InvalidDataException("Expected a Blue planet attribute dictionary.");
        }
        foreach (var (id, data) in planets)
        {
            if (data is not List<object?> { Count: 4 } fields)
            {
                throw new InvalidDataException($"Invalid attribute tuple for planet {id}.");
            }
            yield return new(id, fields.Cast<long>().ToArray());
        }
    }
}
