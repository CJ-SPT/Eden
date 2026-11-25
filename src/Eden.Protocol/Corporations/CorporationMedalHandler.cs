using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationMedalHandler(ICorporationMedals medals) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "corporationSvc"
            || method
                is not (
                    "CreateMedal"
                    or "GiveMedalToCharacters"
                    or "GetAllCorpMedals"
                    or "GetRecipientsOfMedal"
                    or "GetMedalDetails"
                    or "SetMedalStatus"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        long pilot = context.Pilot.PilotId;
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation medal arguments.");
            }
            switch (method, args)
            {
                case ("CreateMedal", [var title, var description, List<object?> graphics, ..]) when args.Length <= 4:
                    var design = new CorporationMedalDesign(
                        BlueRead.Text(title).Trim(),
                        BlueRead.Text(description).Trim(),
                        graphics.Select(Graphic).ToArray()
                    );
                    if (medals.Create(pilot, design, Confirmed(args)) is not long created)
                    {
                        return Confirmation("ConfirmCreatingMedal", 1);
                    }
                    context.Notifications.Add(new CorporationDecorationAdded(created));
                    return created;
                case ("GiveMedalToCharacters", [long id, List<object?> recipients, var reason, ..]) when id > 0 && args.Length <= 4:
                    var recipientIds = BlueRead.PositiveIds(recipients);
                    if (!medals.Award(pilot, id, recipientIds, BlueRead.Text(reason), Confirmed(args)))
                    {
                        return Confirmation("ConfirmGivingMedal", recipients.Count);
                    }
                    context.Notifications.Add(new CorporationDecorationAdded(id));
                    return PilotCallResult.None;
                case ("GetAllCorpMedals", [long corporation]) when corporation is > 0 and <= int.MaxValue:
                    return new CorporationMedalCatalogueReply(medals.Catalogue(pilot, (int)corporation));
                case ("GetRecipientsOfMedal", [long id]) when id > 0:
                    return new CorporationMedalRecipientsReply(medals.Recipients(pilot, id));
                case ("GetMedalDetails", [long id]) when id > 0:
                    return new CorporationMedalDetailsReply(medals.Details(id));
                case ("SetMedalStatus", [Dictionary<long, object?> changes]):
                    medals.SetStatus(pilot, changes.ToDictionary(pair => pair.Key, pair => Status(pair.Value)));
                    return PilotCallResult.None;
                case ("SetMedalStatus", [Dictionary<string, object?> { Count: 0 }]):
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Choose valid corporation medal arguments.");
            }
        }
        catch (InsufficientMedalFunds error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughMoney", new MedalCostValues(error.RequiredCents));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static bool Confirmed(object?[] args) =>
        args.ElementAtOrDefault(3) switch
        {
            null or false or 0L => false,
            true or 1L => true,
            _ => throw new InvalidDataException("Choose a valid medal confirmation."),
        };

    private static CorporationMedalStatus Status(object? value) =>
        value is long status && status is >= (long)CorporationMedalStatus.Deleted and <= (long)CorporationMedalStatus.Public
            ? (CorporationMedalStatus)status
            : throw new InvalidDataException("Choose a valid medal visibility.");

    private static CorporationMedalGraphic Graphic(object? value) =>
        value is List<object?> parts
        && parts is [long part, var graphic, long color]
        && part is 1 or 2
        && color is >= int.MinValue and <= uint.MaxValue
            ? new((int)part, BlueRead.Text(graphic), unchecked((int)color))
            : throw new InvalidDataException("Choose valid ribbon and medal layers.");

    private static PilotCallResult Confirmation(string message, int count) =>
        new PilotCallResult.NamedFailure(message, new MedalCostValues(CorporationMedalRules.FeeCents * count));
}

internal sealed record MedalCostValues(long Cents) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["cost"] = (double)Cents / MarketDefinitions.CentsPerIsk };
}

internal sealed record CorporationDecorationAdded(long MedalId) : PilotNotification("OnCorporationMedalAdded")
{
    public override object?[] Arguments => [MedalId];
}

internal sealed record DecorationIssued() : PilotNotification("OnMedalIssued")
{
    public override object?[] Arguments => [];
}
