using System.Globalization;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Planetary;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Loot;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Planetary;

[Injectable]
internal sealed class PlanetaryInventoryHandler(IPilotPlanetaryIndustry industry) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "planetOrbitalRegistryBroker" || service.StartsWith("@planetorbit:", StringComparison.Ordinal))
        {
            if (method != "GetTaxRate")
            {
                return PilotCallResult.Unsupported;
            }
            try
            {
                return args is [long office] && keywords is not { Count: > 0 }
                    ? industry.CustomsTaxRate(context.Pilot.PilotId, office)
                    : new PilotCallResult.Failure("Choose a planetary customs office.");
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        if (
            !service.StartsWith(WreckWireDefinitions.BindingPrefix, StringComparison.Ordinal)
            || !long.TryParse(
                service.AsSpan(WreckWireDefinitions.BindingPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long officeId
            )
            || !PlanetaryRules.IsOffice(officeId)
            || method is not ("Add" or "MultiAdd" or "ImportExportWithPlanet")
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (!context.Flight.CanTarget)
        {
            return new PilotCallResult.Failure("Approach the customs office outside warp before transferring cargo.");
        }
        try
        {
            context.Flight.Checkpoint();
            if (!context.Flight.CanTarget)
            {
                throw new InvalidDataException("Wait for the system transition before transferring cargo.");
            }
            if (method == "ImportExportWithPlanet")
            {
                if (args is not [long pinId, var imports, var exports, var clientRate] || keywords is { Count: > 0 })
                {
                    throw new InvalidDataException("Choose a launchpad and its import/export quantities.");
                }
                // The client quote is not authoritative; the saved skills and NPC customs
                // policy determine the actual charge in the same transaction as the goods.
                if (clientRate is not null)
                {
                    PlanetaryRequestWire.Number(clientRate);
                }
                var change = industry.ImportExport(
                    context.Pilot.PilotId,
                    officeId,
                    pinId,
                    PlanetaryRequestWire.Cargo(imports),
                    PlanetaryRequestWire.Commodities(exports)
                );
                PlanetaryIndustryHandler.Notify(context, change);
                context.Notifications.Add(new PlanetaryStateChanged(change.Colony.PlanetId));
                return PilotCallResult.None;
            }
            if (args.Length != 2 || args[1] is not long source || keywords?.Keys.Any(k => k is not ("qty" or "flag")) == true)
            {
                throw new InvalidDataException("Choose ship cargo for this customs office.");
            }
            var ids = method == "Add" ? new[] { PlanetaryRequestWire.Integer(args[0]) } : PlanetaryRequestWire.Path(args[0]);
            int flag = keywords?.GetValueOrDefault("flag") is { } wireFlag
                ? PlanetaryRequestWire.Int32(wireFlag)
                : (int)InventoryFlag.Hangar;
            int? quantity = keywords?.GetValueOrDefault("qty") is { } qty ? PlanetaryRequestWire.Int32(qty) : null;
            foreach (var change in industry.MoveOrbitCargo(context.Pilot.PilotId, officeId, true, new(ids, source, flag, quantity)))
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            return PilotCallResult.None;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
