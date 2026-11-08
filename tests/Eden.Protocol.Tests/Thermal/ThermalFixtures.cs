using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Tests.Thermal;

internal static class ThermalFixtures
{
    internal sealed record Modifier(
        string Domain,
        string Func,
        int Operation,
        DogmaAttributeId ModifiedAttributeID,
        DogmaAttributeId ModifyingAttributeID
    );

    internal sealed record Program(int EffectID, int EffectCategory, IReadOnlyList<Modifier> ModifierInfo);

    internal sealed record Profile(
        int TypeId,
        int GroupId,
        ModuleRack Rack,
        int EffectId,
        IReadOnlyDictionary<DogmaAttributeId, double> Attributes
    );

    internal sealed record Hull(
        int TypeId,
        IReadOnlyDictionary<DogmaAttributeId, double> Slots,
        IReadOnlyDictionary<DogmaAttributeId, double> Attributes
    );

    internal sealed record Definition(
        DogmaAttributeId Id,
        double DefaultValue,
        bool Stackable,
        DogmaAttributeId? Minimum,
        DogmaAttributeId? Maximum
    );

    internal sealed record Matrix(
        int Build,
        string SourceFingerprint,
        IReadOnlyList<Program> Programs,
        IReadOnlyList<Profile> Profiles,
        IReadOnlyList<Hull> Hulls,
        IReadOnlyList<Definition> Definitions
    );

    internal sealed record Vector(
        double Heat,
        double Milliseconds,
        double Incoming,
        double Dissipation,
        double Generation,
        double Capacity,
        double Expected
    );

    internal sealed record Reference(int Build, string ArchiveSha256, IReadOnlyList<Vector> Vectors);

    internal static T Read<T>(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Eden.Protocol.Tests.Fixtures." + resource)!;
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<T>(stream, options)!;
    }
}
