using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

[PrimaryKey(nameof(PilotId), nameof(TypeId))]
public sealed class PilotSkill
{
    public long PilotId { get; set; }
    public int TypeId { get; set; }
    public long Points { get; set; }
}
