using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class StructureHandler(
    IPilotStructures structures,
    Eden.Gameplay.Corporations.IPlayerCorporations corporations,
    TimeProvider clock
) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "structureHangarViewMgr" && method == "GetMyHangarViewState")
        {
            return
                args.Length == 0
                && keywords is not { Count: > 0 }
                && context.Pilot.InStructure
                && structures.Read(context.Pilot.PilotId, context.Pilot.DockedLocationId) is { } hangar
                ? new StructureHangarReply(hangar)
                : PilotCallResult.None;
        }
        if (service == "structureProfiles" && method == "GetProfiles" && args.Length == 0 && keywords is not { Count: > 0 })
        {
            var identity = corporations.Identity(context.Pilot.PilotId);
            return
                (
                    identity.Roles
                    & (long)(
                        Eden.Gameplay.Corporations.CorporationRole.StationManager | Eden.Gameplay.Corporations.CorporationRole.Director
                    )
                ) != 0
                ? new BlueList<StructureAccessProfileReply>([new(identity.Id)])
                : new BlueList<StructureAccessProfileReply>([]);
        }
        if (
            service == "structureProfiles"
            && method == "GetProfileSettings"
            && args is [long profile]
            && profile == context.Pilot.CorpId
            && keywords is not { Count: > 0 }
        )
        {
            return BlueEmpty.Dictionary;
        }
        if (
            service == "ship"
            && method == "Scoop"
            && args is [long recovered]
            && recovered >= StructureRules.FirstStructureId
            && structures.Read(context.Pilot.PilotId, recovered) is not null
        )
        {
            try
            {
                Notify(context, structures.Recover(context.Pilot.PilotId, recovered));
                return PilotCallResult.None;
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        if (service is not ("structureDirectory" or "structureDeployment" or "structureSettings"))
        {
            return PilotCallResult.Unsupported;
        }
        // The station directory retains NPC CharacterHasService and the market directory retains its character read.
        if (
            service == "structureSettings" && method == "CharacterHasService"
            || service == "structureDirectory" && method == "GetMyCharacterStructures"
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected structure arguments.");
            }
            long pilot = context.Pilot.PilotId;
            if (service == "structureDirectory" && method == "GetStructureMapData")
            {
                return args is [long systemId] && systemId is > 0 and <= int.MaxValue
                    ? new StructureMapReply(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
                    : new PilotCallResult.Failure("Choose a valid solar system for structure map data.");
            }
            if (service == "structureDeployment")
            {
                StructureResult result = (method, args) switch
                {
                    ("Anchor", _) => structures.Deploy(pilot, Deployment(context, args)),
                    ("Unanchor", [long id]) => structures.Unanchor(pilot, id),
                    ("CancelUnanchor", [long id]) => structures.Unanchor(pilot, id, true),
                    ("RenameStructure", [long id, string or byte[]]) => structures.Rename(pilot, id, BlueMarshal.Text(args[1])),
                    _ => throw new InvalidDataException("Invalid structure deployment request."),
                };
                Notify(context, result);
                return PilotCallResult.None;
            }
            if (service == "structureSettings")
            {
                if (args is [long id] && method == "CharacterGetServices")
                {
                    var s = structures.Read(pilot, id);
                    return new StructureServicesReply(
                        s is not null && structures.HasService(pilot, id, StructureService.Docking) ? s.Services : []
                    );
                }
                if (args is [long settingStructure, long requested] && Enum.IsDefined(typeof(StructureService), checked((int)requested)))
                {
                    bool allowed = structures.HasService(pilot, settingStructure, (StructureService)requested);
                    return method switch
                    {
                        "CharacterGetService" => allowed ? 0d : PilotCallResult.None,
                        "CharacterCheckService" => allowed
                            ? PilotCallResult.None
                            : new PilotCallResult.Failure("This structure service is unavailable or access is denied."),
                        _ => PilotCallResult.Unsupported,
                    };
                }
                return PilotCallResult.Unsupported;
            }
            if (method == "GetStructureDescription" && args is [long descriptionId])
            {
                return structures.Read(pilot, descriptionId)?.Description ?? "";
            }
            if (method == "SetStructureDescription" && args is [long descriptionId2, string or byte[]])
            {
                Notify(context, structures.Describe(pilot, descriptionId2, BlueMarshal.Text(args[1])));
                return PilotCallResult.None;
            }
            IReadOnlyList<StructureSnapshot>? list = (method, args) switch
            {
                ("GetStructureInfo", [long id]) => structures.Read(pilot, id) is { } s ? [s] : [],
                ("GetMyCorporationStructures" or "GetCorporationStructures", []) => structures
                    .List(pilot)
                    .Where(s => s.CorporationId == context.Pilot.CorpId)
                    .ToArray(),
                ("GetStructuresInSystem" or "GetSolarsystemStructures", [long system]) when system is > 0 and <= int.MaxValue =>
                    structures.List(pilot, (int)system),
                ("GetMyDockableStructures", []) => structures
                    .List(pilot)
                    .Where(s => structures.HasService(pilot, s.Id, StructureService.Docking))
                    .ToArray(),
                ("GetStructures", [List<object?> ids]) when ids.All(i => i is long) => structures
                    .List(pilot)
                    .Where(s => ids.Contains(s.Id))
                    .ToArray(),
                _ => null,
            };
            if (list is null)
            {
                return PilotCallResult.Unsupported;
            }
            var entries = list.Select(s => new StructureInfoReply(s, structures.HasService(pilot, s.Id, StructureService.Docking)))
                .ToArray();
            return method == "GetStructureInfo"
                ? entries.FirstOrDefault() is { } entry
                    ? entry
                    : PilotCallResult.None
                : new StructureDirectoryReply(entries);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static StructureDeployment Deployment(PilotCallContext context, object?[] args)
    {
        if (
            args is not [long item, var x, var z, var yaw, var profile, string or byte[], string or byte[], var day, var hour, var extra]
            || context.Pilot.Position is not { } position
        )
        {
            throw new InvalidDataException("Deploy a packaged structure from your active ship in space.");
        }
        if (profile is not null && !Equals(profile, 0L) && !Equals(profile, (long)context.Pilot.CorpId))
        {
            throw new InvalidDataException("Choose your corporation's structure access profile.");
        }
        if (
            extra is not null
            && extra is not Dictionary<string, object?> { Count: 0 }
            && extra is not Dictionary<object, object?> { Count: 0 }
        )
        {
            throw new InvalidDataException("This deployment configuration is not supported.");
        }
        return new(
            item,
            StructureRules.DeploymentPosition(position, Number(x), Number(z)),
            Number(yaw),
            BlueMarshal.Text(args[5]),
            BlueMarshal.Text(args[6]),
            ReinforceWeekday: day is null ? 0
                : day is long d ? checked((int)d)
                : throw new InvalidDataException("Invalid reinforcement day."),
            ReinforceHour: hour is null ? 18
                : hour is long h ? checked((int)h)
                : throw new InvalidDataException("Invalid reinforcement hour.")
        );
    }

    private static double Number(object? value) =>
        value switch
        {
            double number when double.IsFinite(number) => number,
            long number => number,
            _ => throw new InvalidDataException("Expected a finite structure coordinate."),
        };

    internal static void Notify(PilotCallContext context, StructureResult result)
    {
        foreach (var change in result.Changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
    }
}
