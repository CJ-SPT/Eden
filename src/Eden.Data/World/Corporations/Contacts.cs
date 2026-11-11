using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;

namespace Eden.Data.World.Corporations;

public sealed class SavedCorporationContact
{
    public int CorporationId { get; set; }
    public long ContactId { get; set; }
    public double Standing { get; set; }
    public long LabelMask { get; set; }

    public CorporationContact Snapshot() => new(ContactId, Standing, LabelMask);
}

public sealed class SavedCorporationContactLabel
{
    public int CorporationId { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int? Color { get; set; }

    public ContactLabel Snapshot() => new(Id, Name, Color);
}
