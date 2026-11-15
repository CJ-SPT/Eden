using Eden.Gameplay.Fleets;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fleets;

internal sealed partial class CooperativeFleetHandler
{
    private PilotCallResult AdvertCall(PilotCallContext context, string method, object?[] args)
    {
        long actor = context.Pilot.PilotId;
        switch (method, args)
        {
            case ("GetAvailableFleetAds", []):
                return new BlueMap<long, FleetAdvertReply>(
                    fleets.AvailableAdverts(actor, security.Read(actor).Value).ToDictionary(ad => ad.FleetId, ad => AdvertReply(ad, true))
                );
            case ("GetMyFleetFinderAdvert", []):
                return AdvertResult(fleets.MyAdvert(actor));
            case ("AddFleetFinderAdvert", [var data]):
                return AdvertReply(fleets.SetAdvert(actor, ReadAdvert(data)));
            case ("RemoveFleetFinderAdvert", []):
                var previous = fleets.MyAdvert(actor);
                fleets.RemoveAdvert(actor);
                return AdvertResult(previous);
            case ("ApplyToJoinFleet", [long id, bool]):
                return fleets.ApplyToFleet(actor, id, security.Read(actor).Value);
            case ("UpdateAdvertInfo", [long, var diff]):
                return UpdateAdvert(actor, diff, true);
            case ("UpdateAdvertAllowedEntities" or "UpdateFleetAdvertWithNewLeader", [var entities]):
                return UpdateAdvert(actor, entities, false);
            default:
                throw new InvalidDataException("Invalid or unsupported fleet advert request.");
        }
    }

    private PilotCallResult AdvertResult(FleetAdvert? advert) => advert is null ? PilotCallResult.None : AdvertReply(advert);

    private FleetAdvertReply AdvertReply(FleetAdvert advert, bool listed = false) =>
        new(advert, listed, security.Read(advert.Leader.Pilot.Id).Value);

    private PilotCallResult UpdateAdvert(long actor, object? data, bool diff)
    {
        var ad = fleets.MyAdvert(actor) ?? throw new InvalidDataException("Create a fleet advert before updating it.");
        var fields = AdvertFields(data);
        FleetAdvertAccess Update(FleetAdvertAccess access, string prefix)
        {
            IReadOnlyList<long> Entities(IReadOnlyList<long> current, string kind) =>
                diff
                    ? current
                        .Concat(AdvertEntities(fields.GetValueOrDefault(prefix + "ToAddTo" + kind)))
                        .Except(AdvertEntities(fields.GetValueOrDefault(prefix + "ToRemoveFrom" + kind)))
                        .ToArray()
                    : AdvertEntities(fields.GetValueOrDefault(prefix + "_" + kind.ToLowerInvariant() + "Entities"));
            return access with { Allowed = Entities(access.Allowed, "Allowed"), Disallowed = Entities(access.Disallowed, "Disallowed") };
        }
        return AdvertReply(
            fleets.SetAdvert(
                actor,
                ad.Settings with
                {
                    Public = Update(ad.Settings.Public, "public"),
                    MemberGroups = Update(ad.Settings.MemberGroups, "membergroups"),
                }
            )
        );
    }

    private static Dictionary<string, object?> AdvertFields(object? value) =>
        BlueRead.Fields(value is BlueInstance { Name: "util.KeyVal" } instance ? instance.State : value);

    private static FleetAdvertSettings ReadAdvert(object? data)
    {
        var fields = AdvertFields(data);
        string Text(string name) =>
            fields.GetValueOrDefault(name) is string or BlueUnicode or byte[]
                ? BlueMarshal.Text(fields[name])
                : throw new InvalidDataException("Expected fleet advert text.");
        bool Flag(string name, bool fallback = false) =>
            fields.GetValueOrDefault(name) switch
            {
                null => fallback,
                bool value => value,
                _ => throw new InvalidDataException("Expected a fleet advert checkbox value."),
            };
        int? Integer(string name) =>
            fields.GetValueOrDefault(name) switch
            {
                null => null,
                long value when value is >= 0 and <= int.MaxValue => (int)value,
                _ => throw new InvalidDataException("Expected a fleet advert integer."),
            };
        double? Number(string name) =>
            fields.GetValueOrDefault(name) switch
            {
                null => null,
                double value when double.IsFinite(value) => value,
                long value => value,
                _ => throw new InvalidDataException("Expected a fleet advert threshold."),
            };
        FleetAdvertAccess Access(string prefix) =>
            new(
                Number(prefix + "_minStanding"),
                Number(prefix + "_minSecurity"),
                AdvertEntities(fields.GetValueOrDefault(prefix + "_allowedEntities")),
                AdvertEntities(fields.GetValueOrDefault(prefix + "_disallowedEntities"))
            );
        return new(
            Text("fleetName"),
            Text("description"),
            (FleetInviteScope)(Integer("inviteScope") ?? 0),
            Integer("activityValue"),
            Flag("useAdvanceOptions"),
            Flag("newPlayerFriendly"),
            Access("public"),
            Access("membergroups"),
            Flag("joinNeedsApproval"),
            Flag("hideInfo"),
            Flag("updateOnBossChange", true),
            Integer("advertJoinLimit")
        );
    }

    private static IReadOnlyList<long> AdvertEntities(object? value)
    {
        IEnumerable<object?> values = value switch
        {
            null => [],
            HashSet<long> ids => ids.Cast<object?>(),
            List<object?> list => list,
            BlueConstructor { Name: "__builtin__.set", Arguments: [List<object?> list] } => list,
            _ => throw new InvalidDataException("Expected fleet advert entity IDs."),
        };
        return values
            .Select(id =>
                id is long number && number > 0 ? number : throw new InvalidDataException("Expected a positive fleet advert entity ID.")
            )
            .ToArray();
    }
}
