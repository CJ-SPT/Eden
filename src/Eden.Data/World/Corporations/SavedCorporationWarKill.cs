using Eden.Gameplay.Corporations;

namespace Eden.Data.World.Corporations;

public sealed class SavedCorporationWarKill
{
    public long Id { get; set; }
    public long WarId { get; set; }
    public int VictimOrganizationId { get; set; }
    public bool AttackerLoss { get; set; }
    public int GroupId { get; set; }
    public WarReportGroup? Group { get; set; }
    public long Time { get; set; }
    public decimal IskLost { get; set; }
    public decimal IskDestroyed { get; set; }
    public string Report { get; set; } = "";
}
