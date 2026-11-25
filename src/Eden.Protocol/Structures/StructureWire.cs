using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

internal sealed record StructureCynoBeaconRow(StructureCynoBeacon Beacon) : BlueModel
{
    public override object ToBlue() =>
        new List<object?> { Beacon.Id, Beacon.TypeId, Beacon.OwnerId, Beacon.SystemId, (int)Beacon.State, Beacon.Name };
}

internal sealed record StructureReinforceDefaultReply(int Hour) : BlueModel
{
    // The deployment UI unpacks a tuple (weekday, UTC hour); 255 means no preferred weekday.
    public override object ToBlue() => new object?[] { StructureRules.NoReinforcementWeekday, Hour };
}

internal sealed record StructureMapReply(long VersionTime, IReadOnlyList<StructureSnapshot> Structures, int? SunId) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            new object?[]
            {
                new Dictionary<string, object?> { ["versionCheck"] = "5 minutes", ["sessionInfo"] = "charid" },
                BlueMarshal.Encode(Structures.Count == 0 ? BlueEmpty.List : new StructureMapRows(Structures, SunId)),
                new object?[] { VersionTime, 0L },
            }
        );
}

internal sealed record StructureMapRows(IReadOnlyList<StructureSnapshot> Structures, int? SunId) : BlueModel
{
    internal static BlueConstructor Header() =>
        BlueRows.Descriptor([
            ("groupID", BlueColumnType.Int32),
            ("typeID", BlueColumnType.Int32),
            ("itemID", BlueColumnType.Int64),
            ("itemName", BlueColumnType.Unicode),
            ("locationID", BlueColumnType.Int32),
            ("orbitID", BlueColumnType.Int32),
            ("connector", BlueColumnType.Boolean),
            ("x", BlueColumnType.Float64),
            ("y", BlueColumnType.Float64),
            ("z", BlueColumnType.Float64),
            ("celestialIndex", BlueColumnType.Int32),
            ("orbitIndex", BlueColumnType.Int32),
        ]);

    public override object ToBlue() =>
        new BlueConstructor(
            "carbon.common.script.sys.crowset.CRowset",
            [Header(), Structures.Select(s => (object?)new StructureMapRow(s, SunId).ToBlue()).ToList()]
        );
}

internal sealed record StructureMapRow(StructureSnapshot Structure, int? SunId) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                StructureMapRows.Header(),
                new object?[]
                {
                    Structure.Profile.GroupId,
                    Structure.Profile.TypeId,
                    Structure.Id,
                    new BlueUnicode(Structure.Name),
                    Structure.SystemId,
                    SunId,
                    false,
                    Structure.Position.X,
                    Structure.Position.Y,
                    Structure.Position.Z,
                    null,
                    null,
                },
            ]
        );
}

internal sealed record StructureInfoReply(StructureSnapshot Structure, bool Access, bool NativeInstance = true) : BlueModel
{
    internal IReadOnlyList<long> WarIds { get; init; } = [];

    public override object ToBlue()
    {
        var s = Structure;
        Dictionary<string, object?> fields = new()
        {
            ["typeID"] = s.Profile.TypeId,
            ["structureID"] = s.Id,
            ["ownerID"] = s.CorporationId,
            ["solarSystemID"] = s.SystemId,
            ["itemName"] = new BlueUnicode(s.Name),
            ["inSpace"] = s.State != StructureState.Unanchored,
            ["wars"] = new BlueList<long>(WarIds),
            ["services"] = Access ? s.Services.ToDictionary(service => (long)service, _ => (object?)0d) : new Dictionary<long, object?>(),
            ["state"] = (int)s.State,
            ["stateTimerStart"] = s.TimerEnd.HasValue ? s.StateStartedAt : null,
            ["stateTimerEnd"] = s.TimerEnd,
            ["stateTimerPaused"] = s.TimerPausedAt,
            ["unanchoring"] = s.UnanchorAt,
            ["upkeepState"] = (int)s.Upkeep,
            ["profileID"] = s.CorporationId,
            ["reinforceHour"] = s.ReinforceHour,
            ["reinforceWeekday"] = s.ReinforceWeekday,
            ["fuelExpires"] = s.Modules.Where(m => m.NextFuelAt.HasValue).Select(m => m.NextFuelAt).Min(),
        };
        return NativeInstance ? new BlueInstance("util.KeyVal", fields) : fields;
    }
}

internal sealed record StructureDirectoryReply(IReadOnlyList<StructureInfoReply> Structures) : BlueModel
{
    public override object ToBlue() => Structures.ToDictionary(s => s.Structure.Id, s => s.ToBlue());
}

internal sealed record StructureServicesReply(IReadOnlyList<StructureService> Services) : BlueModel
{
    public override object ToBlue() => Services.ToDictionary(s => (long)s, _ => (object?)0d);
}

internal sealed record StructureHangarReply(StructureSnapshot Structure) : BlueModel
{
    public override object ToBlue() =>
        new BlueNewObject(
            BlueNewObject.StructureHangarClass,
            new Dictionary<string, object?>
            {
                ["_operatingState"] = (int)Structure.State,
                ["_upkeepState"] = (int)Structure.Upkeep,
                ["_damageState"] =
                    Structure.DamageFractions is { } health ? new object?[] { health.Shield, health.Armor, health.Structure }
                    : Structure.State == StructureState.Onlining ? new object?[] { 0d, 0d, 1d }
                    : new object?[] { 1d, 1d, 1d },
                ["_timerStartAt"] = Structure.TimerEnd.HasValue ? Structure.StateStartedAt : null,
                ["_timerEndAt"] = Structure.TimerEnd,
                ["_timerPauseAt"] = Structure.TimerPausedAt,
            }
        );
}

internal sealed record StructureAccessProfileReply(int CorporationId) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["profileID"] = CorporationId,
                ["name"] = new BlueUnicode("Corporation members"),
                ["description"] = new BlueUnicode("Access for members of the owning corporation."),
                ["isDefault"] = true,
            }
        );
}
