using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Industry;

internal static class IndustryWire
{
    private static BlueInstance KeyVal(Dictionary<string, object?> values) => new("utillib.KeyVal", values);

    internal static object Facility(IndustryFacility facility, IReadOnlyList<int> products, IReadOnlyList<int> scienceCategories) =>
        new Dictionary<string, object?>
        {
            ["facilityID"] = facility.Id,
            ["typeID"] = facility.TypeId,
            ["ownerID"] = facility.OwnerId,
            ["solarSystemID"] = facility.SystemId,
            ["online"] = true,
            ["tax"] = facility.Tax,
            ["sccTaxModifier"] = 1d,
            ["serviceAccess"] = new Dictionary<long, object?>(),
            ["rigModifiers"] = new Dictionary<long, object?>(),
            ["globalModifiers"] = new Dictionary<string, object?>
            {
                ["faction"] = 1d,
                ["system"] = IndustryDefinitions.Activities.ToDictionary(a => (long)a, _ => (object?)facility.CostIndex),
            },
            ["activities"] = IndustryDefinitions
                .Activities.Where(a => a == IndustryActivity.Manufacturing ? facility.Manufacturing : facility.Laboratory)
                .ToDictionary(
                    a => (long)a,
                    a =>
                        (object?)
                            new object?[]
                            {
                                Modifiers(facility.TimeMultiplier),
                                Modifiers(a == IndustryActivity.Manufacturing ? facility.MaterialMultiplier : 1),
                                new List<object?>(),
                                a == IndustryActivity.Manufacturing ? new List<object?>() : scienceCategories.Cast<object?>().ToList(),
                                new List<object?>(),
                                a == IndustryActivity.Manufacturing ? products.Cast<object?>().ToList() : new List<object?>(),
                            }
                ),
        };

    // industryCommon.Facility consumes five-value modifier tuples; Reference.HULL is 12 in build 3662168.
    private static List<object?> Modifiers(double multiplier) =>
        multiplier == 1 ? [] : [new object?[] { multiplier, null, null, null, 12L }];

    internal static BlueInstance Blueprint(OwnedBlueprint blueprint)
    {
        var item = blueprint.Item;
        return KeyVal(
            new()
            {
                ["itemID"] = item.Id,
                ["typeID"] = item.TypeId,
                ["ownerID"] = item.OwnerId,
                ["quantity"] = InventoryRow.From(item).Quantity,
                ["timeEfficiency"] = blueprint.TimeEfficiency,
                ["materialEfficiency"] = blueprint.MaterialEfficiency,
                ["runs"] = blueprint.RunsRemaining,
                ["locationID"] = item.LocationId,
                ["locationTypeID"] = blueprint.Facility.TypeId,
                ["locationFlagID"] = item.FlagId,
                ["flagID"] = item.FlagId,
                ["facilityID"] = blueprint.Facility.Id,
                ["solarSystemID"] = blueprint.Facility.SystemId,
                ["jobID"] = blueprint.JobId,
                ["isImpounded"] = false,
            }
        );
    }

    internal static BlueInstance Job(IndustryJobDetails job) =>
        KeyVal(
            new()
            {
                ["jobID"] = job.Id,
                ["ownerID"] = job.PilotId,
                ["installerID"] = job.PilotId,
                ["blueprintID"] = job.BlueprintId,
                ["blueprintTypeID"] = job.BlueprintTypeId,
                ["blueprintCopy"] = job.BlueprintCopy,
                ["blueprintLocationID"] = job.Facility.Id,
                ["blueprintLocationFlagID"] = (int)InventoryFlag.Hangar,
                ["facilityID"] = job.Facility.Id,
                ["stationID"] = job.Facility.Id,
                ["solarSystemID"] = job.Facility.SystemId,
                ["activityID"] = (int)job.Activity,
                ["status"] = (int)job.Status,
                ["completedCharacterID"] = job.CompletedAt.HasValue ? job.PilotId : null,
                ["startDate"] = job.StartTime,
                ["endDate"] = job.EndTime,
                ["pauseDate"] = job.SuspendedAt,
                ["runs"] = job.Runs,
                ["licensedRuns"] = job.LicensedRuns,
                ["successfulRuns"] = job.SuccessfulRuns,
                ["cost"] = job.CostCents / 100d,
                ["timeInSeconds"] = job.DurationSeconds,
                ["probability"] = job.Probability,
                ["productTypeID"] = job.ProductTypeId,
                ["optionalTypeID"] = job.OptionalTypeId,
                ["optionalTypeID2"] = null,
                ["outputLocationID"] = job.Facility.Id,
                ["outputFlagID"] = (int)InventoryFlag.Hangar,
            }
        );

    internal static object Location(IndustryFacility facility, long pilotId) =>
        new BlueNewObject(
            BlueNewObject.IndustryLocationClass,
            new Dictionary<string, object?>
            {
                ["itemID"] = facility.Id,
                ["typeID"] = facility.TypeId,
                ["ownerID"] = pilotId,
                ["flagID"] = (int)InventoryFlag.Hangar,
                ["canView"] = true,
                ["canTake"] = true,
                ["solarSystemID"] = facility.SystemId,
            }
        );
}
