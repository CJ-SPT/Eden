using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Repair;

[Injectable]
internal sealed class DeveloperRepairCommands(
    IPilotDeveloperRepairs repairs,
    FittingOperations fittings,
    MagazineWire magazines,
    TimeProvider clock
)
{
    internal PilotCallResult Execute(PilotCallContext context, string[] command)
    {
        DeveloperRepairRequest request = command switch
        {
            ["/repairmodules"] => new(DeveloperRepairKind.FittedModules),
            ["/heal"] => new(DeveloperRepairKind.Ship),
            ["/heal", var subject] when subject.Equals("me", StringComparison.OrdinalIgnoreCase) => new(DeveloperRepairKind.Ship),
            ["/heal", var subject, var capacitor]
                when subject.Equals("me", StringComparison.OrdinalIgnoreCase)
                    && capacitor.Equals("capac=1", StringComparison.OrdinalIgnoreCase) => new(DeveloperRepairKind.Capacitor),
            ["/heal", var item] when long.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0 => id
            == context.Pilot.ShipId
                ? new(DeveloperRepairKind.Ship)
                : new(DeveloperRepairKind.OwnedModule, id),
            _ => throw new InvalidDataException("Use /heal me, /heal me capac=1, /heal <shipOrModuleID>, or /repairmodules."),
        };
        if (context.ControlledStructureId.HasValue)
        {
            throw new InvalidDataException("Release structure control before repairing your ship.");
        }
        long pilot = context.Pilot.PilotId;
        var result = repairs.Restore(pilot, context.Pilot.ShipId, request);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (result.Modules.Count > 0)
        {
            context.Notifications.Add(
                new ModuleAttributesChanged(
                    result
                        .Modules.Select(module => new ModuleAttributeChange(
                            pilot,
                            new DogmaItemKey.Item(module.ModuleId),
                            DogmaAttributeId.Damage,
                            now,
                            module.RemainingDamage
                        ))
                        .ToArray()
                )
            );
        }
        magazines.NotifyChanges(context, result.Charges);
        fittings.NotifyShipAttributes(context);
        return PilotCallResult.None;
    }
}
