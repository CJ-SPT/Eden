using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Protocol.Combat;
using Eden.Protocol.Drones;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

internal sealed record BallparkSnapshot(
    byte[] State,
    long Ego,
    IReadOnlyList<SlimItem> Slims,
    SlimItem SolarSystem,
    IReadOnlyDictionary<long, ShipDamageState> Damage,
    IReadOnlyList<DroneState>? Drones = null
) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["state"] = State,
                ["ego"] = Ego,
                ["slims"] = new BlueList<SlimItem>(Slims),
                ["solItem"] = SolarSystem,
                ["droneState"] = Drones is null ? new List<object?>() : new BlueList<DroneState>(Drones),
                ["damageState"] = new BlueMap<long, ShipDamageState>(Damage),
                ["effectStates"] = new List<object?>(),
                ["dbuffState"] = new Dictionary<long, object?>(),
                ["industryLevel"] = 0,
                ["researchLevel"] = 0,
            }
        );
}

internal sealed record BallparkAddition(byte[] State, IReadOnlyList<SlimItem> Slims, IReadOnlyDictionary<long, ShipDamageState> Damage)
    : BlueModel
{
    public override object ToBlue() => new object?[] { State, new BlueList<SlimItem>(Slims), new BlueMap<long, ShipDamageState>(Damage) };
}

internal abstract record SlimItem : BlueModel
{
    public abstract override object ToBlue();

    protected static Dictionary<string, object?> Fields(long id, int type, long owner, int group, int category) =>
        new()
        {
            ["itemID"] = id,
            ["typeID"] = type,
            ["ownerID"] = owner,
            ["groupID"] = group,
            ["categoryID"] = category,
        };

    protected static BlueInstance Wrap(Dictionary<string, object?> fields) => new("eve.common.script.util.slimItem.SlimItem", fields);
}

internal sealed record SolarSystemSlim(int SystemId) : SlimItem
{
    // Pinned client: solar-system type/group 5, owner 1, celestial category 2.
    public override object ToBlue() => Wrap(Fields(SystemId, 5, 1, 5, 2));
}

internal sealed record MissileSlimItem(MissileContact Missile) : SlimItem
{
    public override object ToBlue()
    {
        var fields = Fields(Missile.Id, Missile.TypeId, Missile.PilotId, Missile.GroupId, (int)InventoryCategory.Charge);
        fields["sourceShipID"] = Missile.SourceShipId;
        fields["launchModules"] = new BlueList<long>(Missile.LaunchModules);
        return Wrap(fields);
    }
}

internal sealed record StaticSlimItem(StaticSpaceObject Body) : SlimItem
{
    public override object ToBlue()
    {
        var fields = Fields(Body.Id, Body.TypeId, Body.OwnerId, Body.GroupId, Body.CategoryId);
        if (Body.HackingState is { } hackingState)
        {
            fields["hackingSecurityState"] = hackingState;
        }
        if (Body.Container is { } container)
        {
            fields["isEmpty"] = container.Empty;
            fields["lootRights"] = new object?[] { container.OwnerId, null, null, false };
        }
        if (Body.Name is { } name)
        {
            fields["name"] = new BlueUnicode(name);
        }
        if (Body.StandingThresholds is { } thresholds)
        {
            fields["hostile_response_threshold"] = thresholds.Hostile;
            fields["friendly_response_threshold"] = thresholds.Friendly;
        }
        if (Body.DestinationGateId.HasValue)
        {
            fields["destinationCorruptionStageAndMaximum"] = new object?[] { 0, 5 };
            fields["localCorruptionStageAndMaximum"] = new object?[] { 0, 5 };
            fields["destinationSuppressionStageAndMaximum"] = new object?[] { 0, 5 };
            fields["localSuppressionStageAndMaximum"] = new object?[] { 0, 5 };
            fields["jumps"] = new BlueInstance(
                "eve.common.script.sys.rowset.Rowset",
                new Dictionary<string, object?>
                {
                    ["header"] = new List<object?> { "toCelestialID", "locationID" },
                    ["lines"] = new List<object?> { new object?[] { Body.DestinationGateId, Body.DestinationSystemId } },
                }
            );
        }
        if (Body.CategoryId == (int)InventoryCategory.Station)
        {
            fields["corpID"] = Body.OwnerId;
        }
        return Wrap(fields);
    }
}

internal sealed record WreckSlimItem(WreckContact Wreck) : SlimItem
{
    public override object ToBlue()
    {
        var fields = Fields(Wreck.Id, Wreck.TypeId, Wreck.OwnerId, Wreck.GroupId, WreckDefinitions.Category);
        fields["name"] = Wreck.Name;
        fields["corpID"] = Wreck.CorporationId;
        fields["charID"] = null;
        fields["launcherID"] = Wreck.SourceId;
        fields["isEmpty"] = Wreck.Empty;
        fields["lootRights"] = new object?[] { Wreck.OwnerId, Wreck.CorporationId, null, false };
        return Wrap(fields);
    }
}

internal sealed record NpcSlimItem(NpcContact Npc) : SlimItem
{
    // Build 3662168 compares standings in [-10, 10] with these thresholds.
    // Match the catalog's customs policy: blue until threatAttackingMe turns red,
    // instead of inheriting hostile mission thresholds from a navy hull's type.
    // The signed login adapter exempts CONCORD from the red attacking override.
    private const double DefensivePatrolResponseThreshold = -11;

    // Pinned entityStandings pirate thresholds lie above the standings range,
    // keeping ordinary hostile NPCs red. Omitting these fields makes the native
    // overview log an error and serialize a Sentry event on every refresh.
    private const double HostileNpcResponseThreshold = 11;

    public override object ToBlue()
    {
        var fields = Fields(Npc.Id, Npc.TypeId, Npc.CorporationId, Npc.GroupId, Npc.CategoryId);
        // Native UI labels need Python unicode; UTF-8 bytes corrupt the diamond fleet marker.
        fields["name"] = new BlueUnicode(Npc.Name);
        fields["corpID"] = Npc.CorporationId;
        fields["charID"] = null;
        fields["modules"] = new List<object?>();
        if (Npc.Role is NpcRole.Navy or NpcRole.Customs or NpcRole.Concord)
        {
            fields["hostile_response_threshold"] = DefensivePatrolResponseThreshold;
            fields["friendly_response_threshold"] = DefensivePatrolResponseThreshold;
        }
        else if (NpcPopulationRules.IsMining(Npc.Role))
        {
            fields["hostile_response_threshold"] = Eden.Gameplay.Mining.NpcMiningRules.HostileStanding;
            fields["friendly_response_threshold"] = Eden.Gameplay.Mining.NpcMiningRules.FriendlyStanding;
        }
        else
        {
            double fallback = Npc.Role == NpcRole.Convoy ? DefensivePatrolResponseThreshold : HostileNpcResponseThreshold;
            fields["hostile_response_threshold"] = Npc.StandingThresholds?.Hostile ?? fallback;
            fields["friendly_response_threshold"] = Npc.StandingThresholds?.Friendly ?? fallback;
        }
        return Wrap(fields);
    }
}

internal sealed record ShipSlimItem(ShipContact Ship) : SlimItem
{
    public override object ToBlue()
    {
        var pilot = Ship.Pilot;
        var fields = Fields(pilot.ShipId, pilot.ShipTypeId, pilot.PilotId, pilot.ShipGroupId, pilot.ShipCategoryId);
        fields["name"] = pilot.ShipName;
        fields["charID"] = pilot.PilotId;
        fields["corpID"] = pilot.CorpId;
        fields["dirtTime"] = Ship.DirtTime;
        fields["modules"] = (Ship.Modules ?? [])
            .Select(module => (object?)new object?[] { module.ItemId, module.TypeId, module.FlagId })
            .ToList();
        return Wrap(fields);
    }
}
