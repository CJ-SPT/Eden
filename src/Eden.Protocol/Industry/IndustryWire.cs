using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Industry;

internal static class IndustryWire
{
    private static BlueInstance KeyVal(Dictionary<string, object?> values) => new("utillib.KeyVal", values);

    internal static object Facility(IndustryFacility facility, IReadOnlyList<int> products) =>
        new Dictionary<string, object?>
        {
            ["facilityID"] = facility.Id,
            ["typeID"] = facility.TypeId,
            ["ownerID"] = facility.OwnerId,
            ["solarSystemID"] = facility.SystemId,
            ["online"] = true,
            // Adjusted industry prices are absent, matching the pinned client's
            // zero-price fallback used by reprocessing. No hidden installation fee.
            ["tax"] = 0d,
            ["sccTaxModifier"] = 1d,
            ["serviceAccess"] = new Dictionary<long, object?>(),
            ["rigModifiers"] = new Dictionary<long, object?>(),
            ["globalModifiers"] = new Dictionary<string, object?>
            {
                ["faction"] = 1d,
                ["system"] = new Dictionary<long, object?> { [(long)IndustryActivity.Manufacturing] = 1d },
            },
            ["activities"] = new Dictionary<long, object?>
            {
                [(long)IndustryActivity.Manufacturing] = new object?[]
                {
                    new List<object?>(),
                    new List<object?>(),
                    new List<object?>(),
                    new List<object?>(),
                    new List<object?>(),
                    products.Cast<object?>().ToList(),
                },
            },
        };

    internal static BlueInstance Blueprint(OwnedBlueprint blueprint)
    {
        var item = blueprint.Item;
        return KeyVal(
            new()
            {
                ["itemID"] = item.Id,
                ["typeID"] = item.TypeId,
                ["ownerID"] = item.OwnerId,
                ["quantity"] = item.Singleton ? InventoryWireDefinitions.AssembledQuantity : item.Quantity,
                ["timeEfficiency"] = 0,
                ["materialEfficiency"] = 0,
                ["runs"] = -1,
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

    internal static BlueInstance Job(ManufacturingJob job) =>
        KeyVal(
            new()
            {
                ["jobID"] = job.Id,
                ["ownerID"] = job.PilotId,
                ["installerID"] = job.PilotId,
                ["blueprintID"] = job.BlueprintId,
                ["blueprintTypeID"] = job.BlueprintTypeId,
                ["blueprintCopy"] = false,
                ["blueprintLocationID"] = job.Facility.Id,
                ["blueprintLocationFlagID"] = (int)InventoryFlag.Hangar,
                ["facilityID"] = job.Facility.Id,
                ["stationID"] = job.Facility.Id,
                ["solarSystemID"] = job.Facility.SystemId,
                ["activityID"] = (int)IndustryActivity.Manufacturing,
                ["status"] = (int)job.Status,
                ["completedCharacterID"] = job.CompletedAt.HasValue ? job.PilotId : null,
                ["startDate"] = job.StartTime,
                ["endDate"] = job.EndTime,
                ["pauseDate"] = null,
                ["runs"] = job.Runs,
                ["licensedRuns"] = 0,
                ["successfulRuns"] = job.Status == IndustryJobStatus.Delivered ? job.Runs : null,
                ["cost"] = 0d,
                ["timeInSeconds"] = job.DurationSeconds,
                ["probability"] = 1d,
                ["productTypeID"] = job.ProductTypeId,
                ["optionalTypeID"] = null,
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
