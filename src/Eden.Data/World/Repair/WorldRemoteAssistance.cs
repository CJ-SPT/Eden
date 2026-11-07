using System.Text.Json;
using Eden.Gameplay.Repair;

namespace Eden.Data.World.Repair;

public sealed class WorldRemoteAssistance
{
    public long ModuleId { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public long TargetId { get; set; }
    public int SystemId { get; set; }
    public string ContactJson { get; set; } = string.Empty;

    public RemoteAssistanceContact Contact() => JsonSerializer.Deserialize<RemoteAssistanceContact>(ContactJson)!;
}
