using System.Text.Json;
using Eden.Gameplay.Corporations;

namespace Eden.Data.World.Corporations;

public sealed class SavedCorporationVote
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public long StartedAt { get; set; }
    public long EndsAt { get; set; }
    public string DataJson { get; set; } = "";

    public CorporationVoteCase Snapshot() =>
        new(Id, CorporationId, StartedAt, EndsAt, JsonSerializer.Deserialize<CorporationVoteData>(DataJson)!);
}
