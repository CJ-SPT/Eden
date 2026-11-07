using System.Text.Json;
using Eden.Gameplay.Characters;

namespace Eden.World.Characters;

public sealed class SavedCombatReport
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public string SourceKey { get; set; } = "";
    public long Time { get; set; }
    public bool IsLoss { get; set; }
    public string Data { get; set; } = "";

    public ShipCombatReport Snapshot() =>
        (JsonSerializer.Deserialize<ShipCombatReport>(Data) ?? throw new InvalidDataException("Invalid saved combat report.")) with
        {
            Id = Id,
        };
}

public sealed class SavedCombatDamage
{
    public long VictimId { get; set; }
    public long AttackerId { get; set; }
    public string PartyJson { get; set; } = "";
    public double Damage { get; set; }
}

public sealed class SavedNpcStanding
{
    public long PilotId { get; set; }
    public long SourceId { get; set; }
    public double Value { get; set; }
}

public sealed class SavedStandingTransaction
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long SourceId { get; set; }
    public long Time { get; set; }
    public StandingEvent Event { get; set; }
    public double Before { get; set; }
    public double After { get; set; }
    public int VictimCorporationId { get; set; }
    public int SystemId { get; set; }
    public int VictimTypeId { get; set; }

    public StandingTransaction Snapshot() =>
        new(Id, PilotId, SourceId, Time, Event, Before, After, VictimCorporationId, SystemId, VictimTypeId);
}
