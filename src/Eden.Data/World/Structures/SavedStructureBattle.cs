namespace Eden.Data.World.Structures;

public sealed class SavedStructureBattle
{
    public long StructureId { get; set; }
    public double Shield { get; set; }
    public double Armor { get; set; }
    public double Hull { get; set; }
    public long? RepairAt { get; set; }
    public long DamageWindowAt { get; set; }
    public double ShieldWindow { get; set; }
    public double ArmorWindow { get; set; }
    public double HullWindow { get; set; }
    public long PausedUntil { get; set; }
}
