using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Exploration;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private const int ExplorerSlot = 0;

    private bool TryLaunchExploration(EconomyTransaction world)
    {
        if (
            Pilot.InStructure
            || !database.Settings.NpcPopulation.Pirates
            || Work.Freight is not null
            || Work.CorporationSupply is not null
            || Work.SovereigntyExpedition is not null
            || Work.Gang is { Phase: not RoamingGangPhase.Complete }
            || Work.RegionalEscort is not null
        )
        {
            return false;
        }
        bool explorer = Pilot.ShipTypeId == CapsuleerIndustry.ExplorerHull;
        if (
            !explorer
            && (
                world.Settings.FreshUniverse
                || world.Set<SavedCapsuleerAgent>().Find(pilotId)?.Slot != ExplorerSlot
                || Work.ActiveTicks < Work.ExplorationRetryAt
            )
        )
        {
            return false;
        }
        var catalog = _operation.Catalog;
        if (
            catalog.SystemPolitics(Pilot.SystemId).Security is not >= CapsuleerExplorationRules.SafeSecurity
            || CapsuleerIndustry.ExplorerKit(catalog) is not { } kit
            || catalog.CelestialsInSystem(Pilot.SystemId).MinBy(body => body.Id) is not { } anchor
        )
        {
            return false;
        }
        var modules = explorer ? ExplorationModules(world) : [];
        if (explorer && Work.ActiveTicks < Work.ExplorationRetryAt)
        {
            Wait("Preparing the next exploration sortie.");
            return true;
        }
        if (!kit.Slots.All(slot => modules.Any(item => item.TypeId == slot.TypeId)))
        {
            _work = Work with { ExplorationRetryAt = checked(Work.ActiveTicks + CapsuleerExplorationRules.RetryTicks) };
            int[] types = kit.Parts().Select(part => part.TypeId).ToArray();
            if (PilotSkillReader.UsableTypes(world, catalog, pilotId, types).Count != types.Length)
            {
                _operation.MarketChanged |= careerTraining.TrainForEquipment(world, catalog, Pilot, types, Now);
                return false;
            }
            var acquisition = ammunitionSupply.BoardExplorer(world, catalog, Pilot, kit, Now);
            _operation.MarketChanged |= acquisition.MarketChanged;
            if (!acquisition.Boarded)
            {
                return false;
            }
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
            ResetTradeHull();
            _work = Work with { ExplorationRetryAt = 0 };
            modules = ExplorationModules(world);
        }
        long launcher = modules.First(item => item.TypeId == CapsuleerIndustry.ExplorerLauncher).Id;
        if (world.Set<ModuleCooldown>().Find(launcher)?.ReadyAt > Now)
        {
            Wait("Waiting for the probe launcher to finish reloading.", 1);
            return true;
        }
        var probes = ammunitionSupply.LoadExplorationProbes(world, catalog, Pilot, launcher, Now);
        _operation.MarketChanged |= probes.MarketChanged;
        if (!probes.Loaded)
        {
            Wait("Waiting for core scanner probes.");
            return true;
        }
        _background = false;
        LauncherUndock(new(anchor.X, anchor.Y, anchor.Z));
        return true;
    }

    private void LauncherUndock(SpacePosition center)
    {
        LaunchActivity();
        _navigation.SetAtRest();
        _target = null;
        _work = Work with
        {
            Activity = HaulerActivity.Flying,
            TargetId = 0,
            ExplorationRetryAt = 0,
            Exploration = new(Pilot.StationId, center, checked(Work.ActiveTicks + CapsuleerExplorationRules.SortieTicks)),
        };
    }

    private OwnedItem[] ExplorationModules(WorldAction world) =>
        world
            .Filter<OwnedItem>(item => item.LocationId == Pilot.ShipId)
            .Where(item => item.OwnerId == pilotId && item.Online && item.Singleton && InventoryDefinitions.IsFittedSlot(item.FlagId))
            .OrderBy(item => item.FlagId)
            .ToArray();

    private IReadOnlyList<CombatAnomaly> ExplorationSignatures() =>
        Work.Exploration is { Phase: ExplorationPhase.Scanning, ScanInFlight: false } exploration
        && !Work.Retreating
        && Work.ActiveTicks < exploration.EndsAt
            ? anomalies.Signatures(Pilot.SystemId, Now)
            : [];

    private void ExplorationStep(IReadOnlyList<CombatAnomaly> signatures)
    {
        var exploration = Work.Exploration!;
        if (Pilot.Position is null)
        {
            _work = Work with { Exploration = null };
            return;
        }
        if (Work.Activity == HaulerActivity.Waiting && Work.ActiveTicks < Work.DueTicks)
        {
            return;
        }
        if (
            exploration.Phase != ExplorationPhase.Returning
            && (Work.Retreating || Work.ActiveTicks >= exploration.EndsAt || !database.Settings.NpcPopulation.Pirates)
        )
        {
            if (_navigation.Warp is not null)
            {
                Fly();
                return;
            }
            ReturnFromExploration(exploration);
            return;
        }
        switch (exploration.Phase)
        {
            case ExplorationPhase.Launching:
                DeployExplorationProbes(exploration);
                break;
            case ExplorationPhase.Scanning when Work.ActiveTicks >= exploration.ReadyTicks:
                ScanExploration(exploration, signatures);
                break;
            case ExplorationPhase.Warping:
            case ExplorationPhase.Returning:
                Fly();
                break;
            case ExplorationPhase.Working:
                HackExplorationSite(exploration);
                break;
        }
    }

    private void DeployExplorationProbes(ExplorationProgress exploration)
    {
        var world = _operation.World;
        var catalog = _operation.Catalog;
        var launcher = ExplorationModules(world).FirstOrDefault(item => item.TypeId == CapsuleerIndustry.ExplorerLauncher);
        if (launcher is null)
        {
            ReturnFromExploration(exploration);
            return;
        }
        if (world.Set<ModuleCooldown>().Find(launcher.Id)?.ReadyAt > Now)
        {
            return;
        }
        var deployed = PilotProbeScanningStore.List(world, pilotId, Pilot.SystemId, Now);
        if (deployed.Length == 0)
        {
            deployed = PilotProbeScanningStore
                .Launch(world, catalog, pilotId, launcher.Id, ProbeScanRules.MaximumProbes, Now)
                .Probes.ToArray();
        }
        if (deployed.Length < ProbeScanRules.MaximumProbes)
        {
            ReturnFromExploration(exploration);
            return;
        }
        _work = Work with { Exploration = exploration with { Phase = ExplorationPhase.Scanning } };
    }

    private void ScanExploration(ExplorationProgress exploration, IReadOnlyList<CombatAnomaly> signatures)
    {
        var world = _operation.World;
        var catalog = _operation.Catalog;
        if (!exploration.ScanInFlight)
        {
            var probes = PilotProbeScanningStore.List(world, pilotId, Pilot.SystemId, Now);
            if (probes.Length < ProbeScanRules.MaximumProbes)
            {
                ReturnFromExploration(exploration);
                return;
            }
            var placement = CapsuleerExplorationRules.Formation(probes, exploration.Center, exploration.RangeStep);
            var scan = PilotProbeScanningStore.Begin(world, catalog, pilotId, placement, signatures, Now);
            _work = Work with
            {
                Exploration = exploration with
                {
                    ReadyTicks = checked(Work.ActiveTicks + scan.ReadyAt - scan.StartedAt),
                    ScanInFlight = true,
                    Scans = exploration.Scans + 1,
                },
            };
            return;
        }
        var pending = PilotProbeScanningStore.Pending(world, pilotId);
        if (pending is null)
        {
            ReturnFromExploration(exploration);
            return;
        }
        if (pending.ReadyAt > Now)
        {
            return;
        }
        var results = PilotProbeScanningStore.Complete(world, catalog, pilotId, pending, Now);
        var candidate = results
            .Where(result =>
                (exploration.SignalId == 0 || result.Site.Id == exploration.SignalId)
                && !Work.VisitedBelts.Any(memory => memory.BeltId == result.Site.Id && memory.Until > Work.ActiveTicks)
            )
            .OrderByDescending(result =>
                result.Certainty >= ProbeScanRules.GroupThreshold
                && result.Site.Exploration is ExplorationKind.Data or ExplorationKind.Relic
            )
            .ThenByDescending(result => result.Certainty)
            .ThenBy(result => result.Site.Id)
            .FirstOrDefault();
        if (candidate is null)
        {
            ReturnFromExploration(exploration);
            return;
        }
        exploration = exploration with { SignalId = candidate.Site.Id, Center = candidate.Position, ScanInFlight = false, ReadyTicks = 0 };
        if (
            candidate.Certainty >= ProbeScanRules.GroupThreshold
            && candidate.Site.Exploration is not (ExplorationKind.Data or ExplorationKind.Relic)
        )
        {
            ReturnFromExploration(exploration);
            return;
        }
        if (candidate.Certainty >= 1 && PirateExplorationStore.OrdinarySite(world, candidate.Site.Id, Pilot.SystemId, Now))
        {
            RecallExplorationProbes(world);
            exploration = exploration with { Phase = ExplorationPhase.Warping };
            _target = ExplorationTarget(exploration);
            _work = Work with { TargetId = candidate.Site.Id, Exploration = exploration };
            StartActivityWarp();
            return;
        }
        if (exploration.Scans >= CapsuleerExplorationRules.MaximumScans || candidate.Certainty >= 1)
        {
            ReturnFromExploration(exploration);
            return;
        }
        _work = Work with { Exploration = exploration with { RangeStep = Math.Max(1, exploration.RangeStep - 1) } };
    }

    private static StaticSpaceObject? ExplorationTarget(ExplorationProgress? exploration) =>
        exploration is { Phase: ExplorationPhase.Warping or ExplorationPhase.Working }
            ? new(exploration.SignalId, CombatAnomalyRules.AnomalyTypeId, 0, (int)InventoryCategory.Celestial, 0, exploration.Center, 0)
            {
                Solid = false,
            }
            : null;

    private void RecallExplorationProbes(WorldAction world)
    {
        if (world.Set<SavedProbeScan>().Find(pilotId) is { } pending)
        {
            world.Remove(pending);
        }
        var deployed = PilotProbeScanningStore.List(world, pilotId, Pilot.SystemId, Now);
        if (deployed.Length > 0)
        {
            try
            {
                PilotProbeScanningStore.Recover(world, _operation.Catalog, pilotId, deployed.Select(probe => probe.Id).ToArray(), Now);
            }
            catch (CargoCapacityException)
            {
                // The normal probe lifetime still applies when the hold has no space.
            }
        }
    }

    private void ReturnFromExploration(ExplorationProgress exploration)
    {
        var world = _operation.World;
        hacking.Cancel(world, _operation.Catalog, pilotId, Now);
        if (exploration.Phase is ExplorationPhase.Launching or ExplorationPhase.Scanning)
        {
            RecallExplorationProbes(world);
        }
        _target = Objects.Single(body => body.Id == exploration.StationId);
        _work = Work with
        {
            TargetId = _target.Id,
            DestinationStationId = exploration.StationId,
            Activity = HaulerActivity.Flying,
            VisitedBelts =
                exploration.SignalId > 0
                    ? PilotAwareness.Remember(Work.VisitedBelts, exploration.SignalId, Work.ActiveTicks)
                    : Work.VisitedBelts,
            Exploration = exploration with { Phase = ExplorationPhase.Returning, ContainerId = 0 },
        };
        StartActivityWarp();
    }
}
