using Eden.Gameplay.Structures;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

internal sealed record StructureMapReply(long VersionTime) : BlueModel
{
    // Build 3662168 iterates this result to append dynamic structures to the local map rows.
    private static readonly byte[] s_emptyRows = BlueMarshal.Encode(BlueEmpty.List);

    public override object ToBlue() =>
        new BlueInstance(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            new object?[]
            {
                new Dictionary<string, object?> { ["versionCheck"] = "5 minutes", ["sessionInfo"] = "charid" },
                s_emptyRows,
                new object?[] { VersionTime, 0L },
            }
        );
}

internal sealed record StructureInfoReply(StructureSnapshot Structure, bool Access, bool NativeInstance = true) : BlueModel
{
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
            ["wars"] = new List<object?>(),
            ["services"] = Access ? s.Services.ToDictionary(service => (long)service, _ => (object?)0d) : new Dictionary<long, object?>(),
            ["state"] = (int)s.State,
            ["stateTimerStart"] = s.StateEndsAt.HasValue ? s.StateStartedAt : null,
            ["stateTimerEnd"] = s.StateEndsAt,
            ["unanchoring"] = s.UnanchorAt,
            ["upkeepState"] = s.Modules.Any(m => m.Online) ? 1 : 2,
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
                ["_upkeepState"] = Structure.Modules.Any(m => m.Online) ? 1 : 2,
                ["_damageState"] = Structure.State == StructureState.Onlining ? new object?[] { 0d, 0d, 1d } : new object?[] { 1d, 1d, 1d },
                ["_timerStartAt"] = Structure.StateEndsAt.HasValue ? Structure.StateStartedAt : null,
                ["_timerEndAt"] = Structure.StateEndsAt,
                ["_timerPauseAt"] = null,
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
