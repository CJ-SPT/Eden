using System.Text.Json;
using Eden.Gameplay.Corporations;

namespace Eden.Data.World.Corporations;

public sealed class SavedCorporationMedal
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public string DataJson { get; set; } = "";

    public CorporationMedal Snapshot() =>
        JsonSerializer.Deserialize<CorporationMedal>(DataJson) ?? throw new InvalidDataException("Invalid saved corporation medal.");
}

public sealed class SavedCorporationMedalAward
{
    public long Id { get; set; }
    public long MedalId { get; set; }
    public long RecipientId { get; set; }
    public string DataJson { get; set; } = "";

    public CorporationMedalAward Snapshot() =>
        JsonSerializer.Deserialize<CorporationMedalAward>(DataJson) ?? throw new InvalidDataException("Invalid saved medal award.");
}
