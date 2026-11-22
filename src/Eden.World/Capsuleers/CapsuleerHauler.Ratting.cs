using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Loot;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private void AdvanceRattingClock(long elapsed)
    {
        if (Work.Ratting is { } activity)
        {
            _work = Work with
            {
                Ratting = activity with
                {
                    RemainingTicks = Math.Max(0, activity.RemainingTicks - Math.Max(0, elapsed - Work.ActiveTicks)),
                    LastObservedAt = Now,
                },
            };
        }
    }

    private CapsuleerAttacker? ProactiveTarget()
    {
        if (Work.Ratting is { Patrol: { } patrol } sortie)
        {
            return Pilot.Position is not null && !_background ? PatrolTarget(sortie, patrol) : null;
        }
        if (Work.Ratting is not { Phase: RattingPhase.Fighting } activity || _background || Pilot.Position is null)
        {
            return null;
        }
        var enemies = ratting.Pirates(Pilot, _target!, Now);
        if (enemies.Length == 0)
        {
            _work = Work with { Ratting = activity with { Phase = RattingPhase.Looting, TargetId = 0 } };
            return null;
        }
        if (activity.RemainingTicks <= 0 || !ratting.Assess(Pilot, enemies, Now, FreshCombat).Capable)
        {
            _work = Work with { Ratting = activity with { Phase = RattingPhase.Returning }, Retreating = enemies.Length > 0 };
            return null;
        }
        var target = enemies.FirstOrDefault(e => e.Id == activity.TargetId) ?? enemies.First();
        _work = Work with { Ratting = activity with { TargetId = target.Id } };
        return new(
            0,
            target.Id,
            target.TypeId,
            target.Position,
            target.Navigation?.Velocity ?? SpacePosition.Zero,
            target.Radius,
            RattingSignature(target),
            0,
            false,
            true
        );
    }

    private double RattingSignature(NpcContact target) =>
        Eden.World.Runtime.Combat.LiveCombatRuntime.Find(database.Path)?.RattingNpc(target.Id, Now)?.SignatureRadius ?? 1;

    private CombatAnomaly? ScoutDepartureAnomaly()
    {
        if (_background || Work.Activity != HaulerActivity.Docked || Work.ActiveTicks < Work.DueTicks || Work.Ratting is not null)
        {
            return null;
        }
        var policy = database.Settings.Capsuleers;
        long sequence = checked(Work.ActivitySequence + 1);
        return
            policy.AnomalyRatting
            && (!policy.BeltRatting || sequence % RattingRules.AnomalyDepartureInterval == 0)
            && (Work.Journey is null || Pilot.StationId == Work.Journey.DestinationStationId)
            && RattingRules.Choose(pilotId, sequence)
            ? ratting.ScoutAnomaly(Pilot.SystemId, Now, pilotId, sequence, Work.VisitedBelts, Work.ActiveTicks)
            : null;
    }

    private bool TryLaunchActivity(CombatAnomaly? scoutedAnomaly)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("traffic.launchActivity");
        var supplies = Work.Resupply!;
        if (TryLaunchSortie(supplies, scoutedAnomaly))
        {
            return true;
        }
        if (supplies.DestinationStationId is null && supplies.Requirements.Length > 0)
        {
            var avoided = (Work.Routine?.Threats ?? []).Where(t => t.ExpiresAt > Work.ActiveTicks).Select(t => t.SystemId).ToHashSet();
            var shopping = resupply.Shop(
                _operation.World,
                _operation.Catalog,
                pilotId,
                Pilot.StationId,
                (HullClass)Pilot.ShipGroupId,
                supplies,
                avoided
            );
            if (shopping is not null)
            {
                _route = shopping;
                _work = Work with
                {
                    Journey = new(
                        Work.Journey?.Kind ?? TrafficJourneyKind.Local,
                        Work.Journey?.HomeStationId ?? Pilot.StationId,
                        shopping.SourceStationId,
                        shopping.DestinationStationId,
                        shopping.Systems,
                        Work.Journey?.Sequence ?? 0
                    ),
                    Resupply = supplies with { DestinationStationId = shopping.DestinationStationId },
                };
                LaunchActivity();
                BeginTravel();
                return true;
            }
        }
        return false;
    }

    private bool TryLaunchSortie(
        ResupplyProgress supplies,
        CombatAnomaly? site,
        FittingCalculation? fit = null,
        bool intentionalHunt = false,
        bool fundingFit = false
    )
    {
        var policy = database.Settings.Capsuleers;
        if (!policy.BeltRatting && (_background || !policy.AnomalyRatting && !policy.RoamingPvp && !policy.GateCamps))
        {
            return false;
        }
        if (
            (!FreshCombat || Pilot.ShipGroupId != (int)HullClass.Corvette)
            && (
                TrafficRules.LightHull((HullClass)Pilot.ShipGroupId) || !CapsuleerPatrolFit.CombatHull(_operation.Catalog, Pilot.ShipTypeId)
            )
        )
        {
            return false;
        }
        var profile = CapsuleerDefenseProfile.Read(_operation.World, _operation.Catalog, pilotId, fit);
        if (
            !profile.Combat
            || profile.RattingWeapons.Length == 0
            || !profile.RattingWeapons.Any(w => w.AvailableShots > 0)
            || CapsuleerDefenseRules.Retreat(
                PilotHealthStore.Read(_operation.World, Pilot.ShipId, ShipHealth.DefenseFrom(profile.ShipAttributes), Now),
                true
            )
            || Work.Journey is { } current && Pilot.StationId != current.DestinationStationId
        )
        {
            return false;
        }
        long sequence = checked(Work.ActivitySequence + 1);
        _work = Work with { ActivitySequence = sequence };
        if (intentionalHunt)
        {
            return !_background
                && HasPvpShip
                && (
                    CapsuleerIndustry.RequiresEarnedPvp(_operation.World) && TryLaunchCamp(sequence, profile)
                    || policy.RoamPercent > 0 && TryLaunchPatrol(sequence, intentional: true)
                );
        }
        if (
            !fundingFit
            && !FreshCombat
            && !_background
            && HasPvpShip
            && (_operation.World is not Eden.World.Runtime.Economy.EconomyTransaction work || !DynamicWorkPilot(work))
            && (TryLaunchCamp(sequence, profile) || TryLaunchPatrol(sequence))
        )
        {
            return true;
        }
        if (!FreshCombat && (supplies.Requirements.Any(r => !r.Drone) || !RattingRules.Choose(pilotId, sequence)))
        {
            return false;
        }
        long[] belts =
            site is not null ? [site.Id]
            : !policy.BeltRatting ? []
            : PilotAwareness
                .Circuit(
                    Objects.Where(o => o.GroupId == AsteroidDefinitions.BeltGroup).Select(o => o.Id),
                    pilotId,
                    sequence,
                    Work.VisitedBelts,
                    Work.ActiveTicks
                )
                .Take(RattingRules.MaximumBelts)
                .ToArray();
        if (belts.Length == 0)
        {
            return false;
        }
        if (policy.WreckSalvage && _operation.World is Eden.World.Runtime.Economy.EconomyTransaction resident)
        {
            PrepareSalvager(resident, profile.ShipAttributes);
        }
        _background = false;
        _work = Work with
        {
            Ratting = new(Pilot.StationId, belts, 0, TimeSpan.FromMinutes(RattingRules.MaximumMinutes).Ticks)
            {
                LastObservedAt = Now,
                AnomalyPosition = site?.Position,
            },
        };
        LaunchActivity();
        FlyToBelt();
        return true;
    }

    private void LaunchActivity()
    {
        var launch = StationUndocking.Launch(Pilot.ShipId, Pilot.Home, Pilot.Flight);
        SqliteWorld.SetPosition(_operation.World, _operation.Catalog, Pilot.ShipId, launch.Position, Pilot.SystemId, Now);
        _operation.TravelChanged = true;
        _pilot = Pilot with { Position = launch.Position };
        _navigation.AdoptHull();
        _navigation.SetVelocity(launch.Velocity);
        RefreshObjects();
        _work = Work with { Retreating = false, DueTicks = 0, LaneDeparting = false, WaitingReason = null };
    }

    private void FlyToBelt()
    {
        var activity = Work.Ratting!;
        _target = SiteTarget(activity) ?? Objects.Single(o => o.Id == activity.Belts[activity.BeltIndex]);
        _work = Work with
        {
            Activity = HaulerActivity.Flying,
            TargetId = _target.Id,
            Ratting = activity with { Phase = RattingPhase.TravellingToBelt, TargetId = 0 },
        };
        StartActivityWarp();
    }

    private static StaticSpaceObject? SiteTarget(RattingProgress? activity) =>
        activity?.AnomalyPosition is { } site
            ? new(activity.Belts[activity.BeltIndex], CombatAnomalyRules.AnomalyTypeId, 0, (int)InventoryCategory.Celestial, 0, site, 0)
            {
                Solid = false,
            }
            : null;

    private void StartActivityWarp()
    {
        var saved = _operation.World.Pilots.Single(p => p.Id == pilotId);
        CrimewatchPersistence.CheckTravel(_operation.World, _operation.Catalog, saved, CrimewatchTravel.Warp, Now);
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the pilot from leaving.");
        }
        _work = Work with { WaitingReason = null };
        _work = Work with { WarpReadyAt = CapsuleerWarpRules.Begin(_navigation, WarpOrder(_target!), Work.ActiveTicks) };
    }

    private void EndSortie(RattingProgress activity) =>
        _work = Work with
        {
            Ratting = activity with { Phase = RattingPhase.Returning, TargetId = 0 },
            Retreating = Work.Retreating || activity.Patrol?.Kind != PatrolKind.Transit,
        };

    private void RattingStep()
    {
        var activity = Work.Ratting!;
        if (Work.Activity == HaulerActivity.Waiting && Work.ActiveTicks < Work.DueTicks)
        {
            return;
        }
        if (Pilot.Position is null)
        {
            return;
        }
        if (Work.Retreating || activity.RemainingTicks <= 0)
        {
            _work = Work with { Ratting = activity with { Phase = RattingPhase.Returning } };
        }
        if (Work.Ratting is { Phase: not RattingPhase.Looting, Salvage: { } pending })
        {
            StopRattingSalvage(pending);
        }
        activity = Work.Ratting!;
        switch (activity.Phase)
        {
            case RattingPhase.TravellingToBelt:
                Fly();
                break;
            case RattingPhase.Returning when activity.Patrol?.Kind == PatrolKind.Transit:
                _work = Work with { Ratting = null };
                BeginTravel();
                break;
            case RattingPhase.Returning:
                if (Work.TargetId != activity.StationId || _target?.Id != activity.StationId || Work.WaitingReason is not null)
                {
                    _target = Objects.FirstOrDefault(o => o.Id == activity.StationId);
                    if (_target is null)
                    {
                        // A remote patrol cannot warp directly to its home station in another system.
                        _work = Work with
                        {
                            Ratting = null,
                        };
                        BeginTravel();
                        break;
                    }
                    _work = Work with
                    {
                        TargetId = _target.Id,
                        DestinationStationId = activity.StationId,
                        Activity = HaulerActivity.Flying,
                    };
                    StartActivityWarp();
                }
                Fly();
                if (Pilot.Position is null)
                {
                    _work = Work with { Ratting = null };
                }
                break;
            case RattingPhase.Searching:
            case RattingPhase.Fighting:
                if (activity.Patrol is { } patrol)
                {
                    long leaveAt = patrol.WatchingForGang
                        ? checked(patrol.LeaveGridAt + RoamingGangRules.RendezvousTicks)
                        : patrol.LeaveGridAt;
                    if (
                        patrol.LeaveGridAt > 0 && Work.ActiveTicks >= leaveAt
                        || patrol.Kind == PatrolKind.Transit
                            && activity.Phase == RattingPhase.Searching
                            && _target is not null
                            && GangContactBeyondGate(_target)
                    )
                    {
                        NextBelt();
                    }
                    break;
                }
                if (activity.AnomalyPosition is not null && activity.Phase == RattingPhase.Searching)
                {
                    ratting.Arrive(Pilot, Now);
                }
                var enemies = ratting.Pirates(Pilot, _target!, Now);
                if (enemies.Length > 0 && !_background && ratting.Assess(Pilot, enemies, Now, FreshCombat).Capable)
                {
                    _work = Work with { Ratting = activity with { Phase = RattingPhase.Fighting, TargetId = enemies[0].Id } };
                }
                else if (enemies.Length > 0)
                {
                    NextBelt();
                }
                else
                {
                    _work = Work with { Ratting = activity with { Phase = RattingPhase.Looting, TargetId = 0 } };
                }
                break;
            case RattingPhase.Looting:
                if (ratting.Pirates(Pilot, _target!, Now).Length > 0)
                {
                    NextBelt();
                    break;
                }
                if (
                    activity.Salvage is not { } salvage
                    || !database.Settings.Capsuleers.WreckSalvage
                    || Work.ActiveTicks >= salvage.DueTicks
                )
                {
                    CollectRattingLoot();
                }
                break;
        }
    }

    private void NextBelt()
    {
        if (Work.Ratting?.Salvage is { } pending)
        {
            StopRattingSalvage(pending);
        }
        var activity = Work.Ratting!;
        if (activity.Patrol?.Kind == PatrolKind.Transit)
        {
            EndSortie(activity);
            return;
        }
        _work = Work with
        {
            VisitedBelts = PilotAwareness.Remember(Work.VisitedBelts, activity.Belts[activity.BeltIndex], Work.ActiveTicks),
        };
        if (activity.BeltIndex + 1 >= activity.Belts.Length || activity.RemainingTicks <= 0 || Work.Retreating)
        {
            _work = Work with { Ratting = activity with { Phase = RattingPhase.Returning, TargetId = 0 } };
        }
        else
        {
            _work = Work with
            {
                Ratting = activity with
                {
                    BeltIndex = activity.BeltIndex + 1,
                    Patrol = activity.Patrol is { } patrol ? patrol with { LeaveGridAt = 0, NextLook = 0, WatchingForGang = false } : null,
                },
            };
            FlyToBelt();
        }
    }

    private void CollectRattingLoot()
    {
        var activity = Work.Ratting!;
        var contacts = ratting.Wrecks(Pilot, _target!, Now, Live is not null);
        if (contacts.Count == 0)
        {
            NextBelt();
            return;
        }
        var world = _operation.World;
        var catalog = _operation.Catalog;
        var salvager = Salvager(world, catalog);
        var wreck = contacts
            .Where(w => !activity.VisitedWrecks.Contains(w.Id) && (!w.Empty || CanSalvage(world, catalog, w.Id, salvager)))
            .OrderByDescending(w => w.Id == activity.Salvage?.WreckId)
            .ThenBy(w => Pilot.Position!.DistanceTo(w.Position))
            .ThenBy(w => w.Id)
            .FirstOrDefault();
        if (wreck is null)
        {
            NextBelt();
            return;
        }
        if (activity.Salvage is { } pending && (pending.WreckId != wreck.Id || pending.ModuleId != salvager?.ItemId))
        {
            StopRattingSalvage(pending);
            activity = Work.Ratting!;
        }
        StaticSpaceObject wreckTarget = new(
            wreck.Id,
            wreck.TypeId,
            WreckDefinitions.Group,
            WreckDefinitions.Category,
            0,
            wreck.Position,
            wreck.Radius
        )
        {
            IsGlobal = false,
            Solid = false,
        };
        double range = wreck.Empty ? salvager!.Range : WreckDefinitions.TransferRange;
        if (Pilot.Position!.DistanceTo(wreck.Position) - Pilot.Flight.ShipRadius - wreck.Radius > range)
        {
            _navigation.Follow(Pilot.ShipId, Work.Stamp, Pilot.Position, Pilot.Flight.ShipRadius, wreckTarget, range / 2, false, true);
            var moved = _navigation.Advance(
                Pilot.ShipId,
                Work.Stamp,
                Pilot.Flight,
                Pilot.Position,
                id => id == wreck.Id ? wreckTarget : null,
                _obstacles
            );
            _pilot = Pilot with { Position = moved.Position };
            RestorePosition(Pilot);
            return;
        }
        var items = wreck.Empty
            ? []
            : world.Filter<OwnedItem>(i => i.LocationId == wreck.Id).Where(i => i.OwnerId == pilotId).OrderBy(i => i.Id).ToArray();
        List<long> collected = activity.LootIds.ToList();
        foreach (var item in items)
        {
            double free =
                PilotInventoryStore.HoldCapacity(world, catalog, pilotId, Pilot.ShipId, (int)InventoryFlag.Cargo)
                - InventoryPersistence.UsedVolume(world, catalog, pilotId, Pilot.ShipId, (int)InventoryFlag.Cargo);
            double volume = PilotInventoryStore.Volume(catalog, item.TypeId);
            int count = volume > 0 ? (int)Math.Min(item.Quantity, Math.Floor(Math.Max(0, free) / volume)) : 0;
            if (count > 0)
            {
                var changes = WreckStore.Take(world, catalog, pilotId, Pilot.ShipId, wreck.Id, [item.Id], count, Now);
                collected.AddRange(changes.Where(c => c.Item.LocationId == Pilot.ShipId).Select(c => c.Item.Id));
            }
        }
        _work = Work with { Ratting = activity with { LootIds = collected.Distinct().ToArray() } };
        if (salvager is not null && CanSalvage(world, catalog, wreck.Id, salvager) && RecoverSalvage(wreck, salvager))
        {
            return;
        }
        activity = Work.Ratting!;
        if (activity.Salvage is { } finished)
        {
            StopRattingSalvage(finished);
        }
        _work = Work with { Ratting = Work.Ratting! with { VisitedWrecks = activity.VisitedWrecks.Append(wreck.Id).ToArray() } };
    }

    private void UnloadRattingLoot()
    {
        var ids = Work.RattingLoot;
        if (ids.Length == 0)
        {
            return;
        }
        var available = _operation
            .World.Set<OwnedItem>()
            .Where(i =>
                Enumerable.Contains(ids, i.Id)
                && i.OwnerId == pilotId
                && i.LocationId == Pilot.ShipId
                && i.FlagId == (int)InventoryFlag.Cargo
            )
            .Select(i => i.Id)
            .ToArray();
        if (available.Length > 0)
        {
            var unloaded = PilotInventoryStore.Move(
                _operation.World,
                _operation.Catalog,
                pilotId,
                Pilot.StationId,
                new(available, Pilot.ShipId, (int)InventoryFlag.Hangar, null),
                Now
            );
            _operation.MarketChanged |= ammunitionSupply.SellRecoveredSalvage(_operation.World, _operation.Catalog, Pilot, unloaded, Now);
        }
        _work = Work with { RattingLoot = [] };
    }
}
