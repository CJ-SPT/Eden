using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Mining;

[Injectable]
internal sealed class PersonalMiningLedgerHandler(IPersonalMiningLedger ledger) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "characterMiningLedger" || method != "GetCharacterLogs")
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Read the selected pilot's personal mining ledger without additional arguments.");
        }
        try
        {
            return new BlueList<PersonalMiningLogReply>(
                ledger.Read(context.Pilot.PilotId).Select(l => new PersonalMiningLogReply(l)).ToArray()
            );
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record PersonalMiningLogReply(PersonalMiningLog Log) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["entryID"] = Log.Id,
                ["eventDate"] = Log.Day,
                ["characterID"] = Log.PilotId,
                ["corporationID"] = Log.CorporationId,
                ["solarSystemID"] = Log.SystemId,
                // Native ledgerEntry uses the lower-case spelling; other discovery consumers use solarSystemID.
                ["solarsystemID"] = Log.SystemId,
                ["typeID"] = Log.TypeId,
                ["quantity"] = Log.Quantity.Collected,
                ["quantityWasted"] = Log.Quantity.Wasted,
                ["quantityCritical"] = Log.Quantity.Critical,
            }
        );
}
