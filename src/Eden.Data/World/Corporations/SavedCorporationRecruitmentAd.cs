using System.Text.Json;
using Eden.Gameplay.Corporations;

namespace Eden.Data.World.Corporations;

public sealed class SavedCorporationRecruitmentAd
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public string DetailsJson { get; set; } = "";

    public CorporationRecruitmentAd Snapshot(int members) =>
        new(Id, CorporationId, CreatedAt, ExpiresAt, JsonSerializer.Deserialize<CorporationRecruitmentDraft>(DetailsJson)!, members);
}
