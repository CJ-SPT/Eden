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
    TimeProvider clock,
    IStructureContacts contacts,
    ISystemObjects systemObjects,
    ICapitalNavigation? capitalNavigation = null
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
        if (service == "corpRegistry" && method == "GetStructureReinforceDefault")
        {
            return args.Length == 0 && keywords is not { Count: > 0 }
                ? new StructureReinforceDefaultReply()
                : new PilotCallResult.Failure("Structure reinforcement defaults do not accept arguments.");
        }
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
            if (service == "structureDirectory" && method is "GetMyAccessibleOnlineCynoBeaconStructures" or "GetSolarSystemsWithBeacons")
            {
                if (args.Length != 0)
                {
                    return new PilotCallResult.Failure("Cynosural beacon directory calls do not accept arguments.");
                }
                return method == "GetMyAccessibleOnlineCynoBeaconStructures"
                    ? new BlueList<StructureCynoBeaconRow>(
                        capitalNavigation?.AccessibleBeacons(pilot).Select(beacon => new StructureCynoBeaconRow(beacon)).ToArray() ?? []
                    )
                    : new BlueList<int>(
                        capitalNavigation?.AccessibleBeacons(null).Select(beacon => beacon.SystemId).Distinct().Order().ToArray() ?? []
                    );
            }
            if (service == "structureDirectory" && method == "GetStructureMapData")
            {
                if (args is not [long systemId] || systemId is <= 0 or > int.MaxValue)
                {
                    return new PilotCallResult.Failure("Choose a valid solar system for structure map data.");
                }
                var mapped = structures.List(pilot, (int)systemId).Where(s => s.State != StructureState.Unanchored).ToArray();
                // Native map rows attach deployed structures to the system's sun (celestial group 6).
                const int sunGroupId = 6;
                int? sun =
                    mapped.Length == 0
                        ? null
                        : checked((int?)systemObjects.Load((int)systemId).FirstOrDefault(body => body.GroupId == sunGroupId)?.Id);
                return new StructureMapReply(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), mapped, sun);
            }
            if (service == "structureDirectory" && method == "GetMyDockableStructures")
            {
                int system = args switch
                {
                    [] => context.Pilot.SystemId,
                    [long requested] when requested is > 0 and <= int.MaxValue => (int)requested,
                    _ => throw new InvalidDataException("Choose a valid solar system for dockable structures."),
                };
                return new BlueList<long>(
                    structures
                        .List(pilot, system)
                        .Where(s => structures.HasService(pilot, s.Id, StructureService.Docking))
                        .Select(s => s.Id)
                        .ToArray()
                );
            }
            if (service == "structureDirectory" && method == "CheckMyDockingAccessToStructures")
            {
                if (args is not [List<object?> requested] || requested.Any(id => id is not long value || value <= 0))
                {
                    throw new InvalidDataException("Supply a list of positive structure IDs.");
                }
                return new BlueList<long>(
                    requested.Cast<long>().Distinct().Where(id => structures.HasService(pilot, id, StructureService.Docking)).ToArray()
                );
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
                if (context.Pilot.Position is not null)
                {
                    context.Flight.RefreshStructures(contacts.Contacts(context.Pilot.SystemId));
                }
                return PilotCallResult.None;
            }
            if (service == "structureSettings")
            {
                if (method == "CharacterHasSetting")
                {
                    if (args is not [long structureId, long setting] || structureId <= 0 || setting is < 0 or > int.MaxValue)
                    {
                        throw new InvalidDataException("Choose a valid structure and access setting.");
                    }
                    if ((StructureSetting)setting == StructureSetting.CanControl)
                    {
                        return structures.CanControl(pilot, structureId);
                    }
                    return StructureSettingAccess.Service((StructureSetting)setting) is { } required
                        && structures.HasService(pilot, structureId, required);
                }
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
        // Build 3662168 ConfirmDeployment sends an empty KeyVal for ordinary structures, including refineries.
        if (extra is BlueInstance { Name: "util.KeyVal" or "utillib.KeyVal" } configuration)
        {
            extra = configuration.State;
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
            ReinforceHour: hour is null ? StructureRules.DefaultReinforceHour
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
        context.Notifications.Add(new StructureServicesChanged(result.Structure.Id));
        if (context.Pilot.InStructure && context.Pilot.DockedLocationId == result.Structure.Id)
        {
            context.Notifications.Add(new StructureHangarChanged(result.Structure));
        }
    }
}

internal sealed record StructureServicesChanged(long StructureId) : PilotNotification("OnStructureServiceChanged")
{
    public override object?[] Arguments => [StructureId];
}

internal sealed record StructureHangarChanged(StructureSnapshot Structure) : PilotNotification("OnHangarViewStateUpdated")
{
    public override object?[] Arguments => [Structure.Id, new StructureHangarReply(Structure)];
}
