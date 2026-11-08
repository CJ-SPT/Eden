using Eden.Gameplay.Information;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<(long Pilot, long Ship, long Module, long Ready), bool> _information = new(
        "resident.information",
        "WorldInformationCycle",
        "WorldActiveModule",
        "WorldItem",
        "WorldPilot",
        "WorldShip"
    );

    internal bool InformationActive(InformationCycle cycle, Func<bool> load) =>
        Read(_information, (cycle.PilotId, cycle.ShipId, cycle.Module.ItemId, cycle.ReadyAt), load);
}
