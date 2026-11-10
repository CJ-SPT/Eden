using Eden.Gameplay.Structures;

namespace Eden.Data.World.Structures;

public sealed class SavedStructure
{
    public long Id { get; set; }
    public int TypeId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int CorporationId { get; set; }
    public int SystemId { get; set; }
    public int? MoonId { get; set; }
    public int ReinforceWeekday { get; set; }
    public int ReinforceHour { get; set; } = 18;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public StructureState State { get; set; }
    public long? StateEndsAt { get; set; }
    public long StateStartedAt { get; set; }
    public long? UnanchorAt { get; set; }
    public bool HasCore { get; set; }
    public bool PublicAccess { get; set; }
    public long? LowPowerAt { get; set; }
    public long DeployedAt { get; set; }
}

public sealed class SavedStructureService
{
    public long ItemId { get; set; }
    public long StructureId { get; set; }
    public int Slot { get; set; }
    public bool Online { get; set; }
    public long? NextFuelAt { get; set; }
}

public sealed class SavedStructureAccess
{
    public long StructureId { get; set; }
    public long MemberId { get; set; }
    public bool Allowed { get; set; }
}
