using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Combat;

namespace Eden.Data.World.Combat;

public sealed class SavedAreaWeapon
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public long ModuleId { get; set; }
    public int SystemId { get; set; }
    public long ResolvesAt { get; set; }
    public string Snapshot { get; set; } = "";

    internal AreaWeaponFlight Flight() =>
        JsonSerializer.Deserialize(Snapshot, AreaWeaponJson.Default.AreaWeaponFlight)
        ?? throw new InvalidDataException("Invalid saved area weapon.");

    internal void Save(AreaWeaponFlight flight)
    {
        ResolvesAt = flight.ResolvesAt;
        Snapshot = JsonSerializer.Serialize(flight, AreaWeaponJson.Default.AreaWeaponFlight);
    }
}

[JsonSerializable(typeof(AreaWeaponFlight))]
[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true)]
internal sealed partial class AreaWeaponJson : JsonSerializerContext;
