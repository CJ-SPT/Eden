using Eden.Gameplay.Combat;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Market;
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
    internal IReadOnlyList<CommandBurstBoost>? Boosts { get; init; }

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
                ["dbuffState"] = DbuffEntry.List(Boosts ?? []),
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
    // Build 3662168 entosisController.NEUTRAL uses inventorycommon.const.ownerSystem.
    private const int NeutralEntosisDefender = 1;

    private static object?[] TerritoryScore(Eden.Gameplay.Sovereignty.TerritoryProgress progress, int defender)
    {
        int controller = progress.Value < 0 ? Eden.Gameplay.Sovereignty.SovereigntyWarfareRules.Attackers : defender;
        double rate = progress.Value < 0 ? -progress.Rate : progress.Rate;
        return
        [
            controller,
            progress.ScoringTeam,
            new object?[] { Math.Abs(progress.Value), progress.UpdatedAt, rate, progress.Blocked },
            defender,
        ];
    }

    public override object ToBlue()
    {
        var fields = Fields(Body.Id, Body.TypeId, Body.OwnerId, Body.GroupId, Body.CategoryId);
        if (Body.JumpBridge is { } bridge)
        {
            fields["targetSolarsystemID"] = bridge.DestinationSystemId;
            fields["component_jumpBridge"] = bridge.Revision;
            fields["activationState"] = bridge.Active;
        }
        if (Body.TypeId == DoomsdayFieldRules.BeaconType && Body.GroupId == DoomsdayFieldRules.BeaconGroup)
        {
            fields["component_activate"] = new object?[] { true, null };
        }
        if (Body.CynoDeployable is { } deployable)
        {
            fields["component_activate"] = new object?[] { deployable.Active, deployable.Active ? null : deployable.OnlineAt };
            fields["component_decay"] = deployable.ExpiresAt;
        }
        if (Body.AgentId is long agentId)
        {
            fields["charID"] = agentId;
            fields["corpID"] = Body.OwnerId;
        }
        if (Body.Structure is { } structure)
        {
            fields["corpID"] = Body.OwnerId;
            fields["modules"] = structure
                .Equipment.Select(module => (object?)new object?[] { module.ItemId, module.TypeId, module.Slot })
                .ToList();
            fields["structureState"] = (int)structure.State;
            fields["state"] = (int)structure.State;
            // The native construction animation reads timer and upkeepState from the slim item.
            fields["timer"] = structure.TimerEnd is long timerEnd
                ? new object?[] { structure.StateStartedAt, timerEnd, structure.TimerPausedAt }
                : null;
            fields["upkeepState"] = (int)structure.UpkeepState;
            fields["stateTimer"] = fields["timer"];
            fields["deployTimes"] = structure.DeploymentTimer is { } deploy ? new List<object?> { deploy.StartedAt, deploy.EndsAt } : null;
            fields["repairing"] = structure.Battle?.RepairAt.HasValue == true;
            fields["unanchoring"] = structure.UnanchorAt;
            fields["hasCore"] = structure.HasCore;
            // Native dunRotation is expressed in degrees; deployment yaw is saved in radians.
            fields["dunRotation"] = new object?[] { structure.Yaw * 180 / Math.PI, 0d, 0d };
        }
        if (Body.Skyhook is { } skyhook)
        {
            fields["planetID"] = skyhook.PlanetId;
            fields["allianceID"] = skyhook.AllianceId == 0 ? null : skyhook.AllianceId;
            // Pinned orbitalSkyhook.OnSlimItemUpdated reads an orbital state, not the Upwell enum.
            fields["skyhook_state"] = Eden.Gameplay.Sovereignty.SovereigntyResourceRules.SkyhookShieldVulnerableState;
            fields["component_activate"] = new object?[] { skyhook.Active, skyhook.Active ? null : skyhook.OnlineAt };
        }
        if (Body.Sovereignty is { } hub)
        {
            fields["allianceID"] = hub.Claim?.AllianceId;
            fields["claimTime"] = hub.Claim?.ClaimedAt;
            fields["entosis_score"] = new object?[]
            {
                hub.Capture.ControllingAllianceId,
                hub.Capture.ScoringAllianceId,
                new object?[]
                {
                    (double)hub.Capture.ProgressTicks / hub.CaptureDuration,
                    hub.Capture.UpdatedAt,
                    (double)hub.Capture.Rate,
                    hub.Capture.Blocked,
                },
                NeutralEntosisDefender,
            };
        }
        if (Body.Sovereignty?.Conflict is { } conflict)
        {
            fields["entosis_score"] =
                conflict.Phase == Eden.Gameplay.Sovereignty.SovereigntyPhase.Secure
                    ? TerritoryScore(conflict.Capture, conflict.DefenderAllianceId)
                    : null;
        }
        if (Body.CommandNode is { } node)
        {
            fields["allianceID"] = node.AllianceId;
            fields["entosis_score"] = TerritoryScore(node.Capture, node.AllianceId);
            fields["campaign_sourceInfo"] = new object?[]
            {
                node.SourceSystemId,
                Eden.Gameplay.Sovereignty.SovereigntyWarfareRules.HubDefenseEvent,
                node.HubId,
                node.AllianceId,
            };
        }
        else if (Body.Sovereignty is null && Body.GroupId == Eden.Gameplay.Sovereignty.SovereigntyRules.HubGroupId)
        {
            fields["entosis_score"] = null;
        }
        if (Body.Wormhole is { } wormhole)
        {
            fields["wormholeAge"] = wormhole.Age;
            fields["wormholeSize"] = wormhole.Size;
            fields["maxShipJumpMass"] = Eden.Gameplay.Exploration.WormholeRules.MassClass(wormhole.MaximumShipMass);
            fields["nebulaType"] = null;
            fields["isDestTriglavian"] = 0;
            fields["otherSolarSystemClass"] = wormhole.DestinationClass;
        }
        if (Body.HackingState is { } hackingState)
        {
            fields["hackingSecurityState"] = hackingState;
        }
        if (Body.Abyssal is { Environment: false } abyss)
        {
            fields["isAbyssGateOpen"] = abyss.Open;
            fields["activationState"] = abyss.Open ? 1 : 0;
            fields["animationState"] = abyss.Open ? 1 : 0;
            if (abyss.Trace)
            {
                fields["gameModeID"] = (int)abyss.Mode;
                fields["abyssFilamentTypeID"] = abyss.FilamentTypeId;
            }
        }
        if (Body.MilitiaCapture is { } capture)
        {
            // Build 3662168 bracketMgr.UpdateCaptureFromSlim sets lastIncident using client simulation time.
            fields["capturePoint"] = new Dictionary<string, object?>
            {
                ["points"] = capture.Points,
                ["captureTime"] = capture.Minutes,
                ["captureID"] =
                    capture.Capturing is { } faction ? (object)(int)faction
                    : capture.Paused ? "contested"
                    : null,
                ["lastCapturing"] = (int?)capture.Previous,
            };
        }
        if (Body.BattlefieldCapture is { } point)
        {
            fields["component_fwCapturePoint_factionInfo"] = new object?[] { (int)point.Attacker, (int)point.Occupier };
            fields["component_fwCapturePoint_nextPointTime"] = point.NextPointAt;
        }
        if (Body.BattlefieldScore is int battlefieldScore)
        {
            fields["component_fwScoreboard_score"] = battlefieldScore;
        }
        if (Body.AccelerationGateDirection is { } direction && direction != SpacePosition.Zero)
        {
            // Build 3662168's AlignToDirection negates the model's +Z axis, which is the launch axis in the gate assets.
            fields["dunDirection"] = new object?[] { -direction.X, -direction.Y, -direction.Z };
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
        var fields = Fields(Wreck.Id, Wreck.TypeId, Wreck.OwnerId, Wreck.GroupId, Wreck.CategoryId);
        if (Wreck.PlanetId is int planetId)
        {
            fields["planetID"] = planetId;
            fields["orbitalState"] = Eden.Gameplay.Planetary.PlanetaryRules.OrbitalIdleState;
            // Pinned CustomsOffice.LoadModel reads this attribute without a default.
            // None selects the office type's ordinary catalog graphic.
            fields["level"] = null;
        }
        fields["name"] = Wreck.Name;
        fields["corpID"] = Wreck.CorporationId;
        fields["charID"] = null;
        fields["launcherID"] = Wreck.SourceId;
        fields["isEmpty"] = Wreck.Empty;
        fields["lootRights"] = new object?[] { Wreck.OwnerId, Wreck.CorporationId, Wreck.LootFleetId, Wreck.Abandoned };
        fields["securityStatus"] = Wreck.OwnerSecurityStatus;
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
        if (
            Npc.FactionId == Eden.Gameplay.Exploration.SleeperRules.FactionId
            && Npc.CorporationId == Eden.Gameplay.Exploration.SleeperRules.SystemOwner
        )
        {
            fields["corpID"] = null;
        }
        fields["charID"] = null;
        if (Npc.Role is NpcRole.MilitiaHub or NpcRole.MilitiaObjective or NpcRole.MilitiaComplexDefender)
        {
            fields["warFactionID"] = Npc.FactionId;
        }
        fields["modules"] = new List<object?>();
        // Build 3662168's NPC bounty display normally reads catalog dogma.
        fields["edenNpcBounty"] = Npc.BountyCents / (double)MarketDefinitions.CentsPerIsk;
        if (Npc.Role is NpcRole.Navy or NpcRole.Customs or NpcRole.Concord or NpcRole.MilitiaNavy)
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
        fields["allianceID"] = pilot.AllianceId;
        fields["warFactionID"] = (int?)pilot.WarFaction;
        fields["dirtTime"] = Ship.DirtTime;
        if (Ship.Compression is { Count: > 0 } facilities)
        {
            fields["compression_facility_typelists"] = new BlueMap<int, double>(facilities.ToDictionary(f => f.TypeList, f => f.Range));
        }
        fields["modules"] = (Ship.Modules ?? [])
            .Select(module => (object?)new object?[] { module.ItemId, module.TypeId, module.FlagId })
            .ToList();
        return Wrap(fields);
    }
}
