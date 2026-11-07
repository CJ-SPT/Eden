using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Skills;

[PrimaryKey(nameof(PilotId), nameof(TypeId))]
public sealed class PilotSkill
{
    public long PilotId { get; set; }
    public int TypeId { get; set; }
    public long Points { get; set; }
}

[PrimaryKey(nameof(PilotId), nameof(Position))]
public sealed class QueuedLevel
{
    public long PilotId { get; set; }
    public int Position { get; set; }
    public int TypeId { get; set; }
    public int Level { get; set; }
    public long StartPoints { get; set; }
    public long EndPoints { get; set; }
    public long? StartTime { get; set; }
    public long? EndTime { get; set; }
    public string PeriodsJson { get; set; } = "[]";
}

public sealed class SavedFreeSkillPoints
{
    public long PilotId { get; set; }
    public long Points { get; set; }
    public long Revision { get; set; }
}

public sealed class SavedSkillExtraction
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long ItemId { get; set; }
    public long Time { get; set; }
    public string AllocationsJson { get; set; } = "[]";
}

public sealed class SavedSkillInjection
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long ItemId { get; set; }
    public int TypeId { get; set; }
    public int Quantity { get; set; }
    public long Points { get; set; }
    public long TotalBefore { get; set; }
    public long TotalAfter { get; set; }
    public int UsesSpent { get; set; }
    public long Time { get; set; }
}

public sealed class SavedSkillInjectorState
{
    public long PilotId { get; set; }
    public long NextAlphaInjection { get; set; }
    public long Revision { get; set; }
}

public sealed class TrainingLog
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long Time { get; set; }
    public int TypeId { get; set; }
    public long Points { get; set; }
    public int EventId { get; set; }
}
