using System.Text.Json;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Propulsion;

namespace Eden.World.Combat;

public sealed class WorldPlayerEwar
{
    public long ModuleId { get; set; }
    public long TargetId { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public int SystemId { get; set; }
    public TackleTargetKind TargetKind { get; set; }
    public string ContactJson { get; set; } = "";

    internal ElectronicWarfareContact Contact() =>
        JsonSerializer.Deserialize<ElectronicWarfareContact>(ContactJson)
        ?? throw new InvalidDataException("Invalid saved electronic warfare contact.");
}
