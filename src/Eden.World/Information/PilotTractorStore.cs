using Eden.Data.World.Fittings;
using Eden.Data.World.Information;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Information;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Information;

internal sealed partial class PilotInformationStore
{
    public void AdvanceTractor(InformationCycle cycle, long now)
    {
        if (cycle.TargetOwnerId is not long owner || !CanTractor(cycle.PilotId, owner))
        {
            throw new InvalidDataException("The tractor beam no longer has access to this container.");
        }
        if (LiveWorld.Find(database.Path) is { } live)
        {
            if (!live.Tractors.Has(cycle))
            {
                using WorldDbContext reload = new(database.Path);
                using CatalogDbContext catalog = new(database.CatalogPath);
                var row = reload.Set<SavedInformationCycle>().Find(cycle.Module.ItemId);
                var item = reload.Set<OwnedItem>().Find(cycle.Module.ItemId);
                var source = Pilot(reload, cycle.PilotId);
                var target = reload.Set<WorldWreck>().Find(cycle.TargetId);
                if (
                    row?.ReadyAt != cycle.ReadyAt
                    || item is not { Online: true }
                    || item.LocationId != cycle.ShipId
                    || source.ActiveShipId != cycle.ShipId
                    || source.ActiveShip.Position is null
                    || target is null
                    || !reload.Set<ActiveFittedModule>().Any(m => m.ModuleId == cycle.Module.ItemId && m.ShipId == cycle.ShipId)
                )
                {
                    throw new InvalidDataException("The tractor beam is no longer active.");
                }
                CheckTarget(reload, catalog, source, cycle.TargetId, cycle.Module, now);
                live.Tractors.Start(
                    cycle with
                    {
                        StartedAt = Math.Max(row.StartedAt, Math.Min(now, cycle.ReadyAt)),
                    },
                    Position(source),
                    new(target.X, target.Y, target.Z),
                    (catalog.FittingType(source.ActiveShip.TypeId)!.Radius ?? 0) + (catalog.FittingType(target.TypeId)!.Radius ?? 0),
                    fleets
                );
            }
            live.Tractors.Advance(now);
            return;
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext definitions = new(database.CatalogPath);
        var saved = world.Set<SavedInformationCycle>().Find(cycle.Module.ItemId);
        if (saved is null || saved.PilotId != cycle.PilotId || saved.ReadyAt != cycle.ReadyAt)
        {
            return;
        }
        var pilot = Pilot(world, cycle.PilotId);
        if (pilot.ActiveShipId != cycle.ShipId || pilot.ActiveShip.Position is null)
        {
            return;
        }
        var wreck = world.Set<WorldWreck>().Find(cycle.TargetId);
        if (wreck is null || wreck.ExpiresAt <= now)
        {
            return;
        }
        CheckTarget(world, definitions, pilot, cycle.TargetId, cycle.Module, now);
        long until = Math.Min(now, cycle.ReadyAt);
        if (until > saved.StartedAt)
        {
            double radii =
                (definitions.FittingType(pilot.ActiveShip.TypeId)!.Radius ?? 0) + (definitions.FittingType(wreck.TypeId)!.Radius ?? 0);
            var point = InformationRules.Pull(
                new(wreck.X, wreck.Y, wreck.Z),
                Position(pilot),
                InformationRules.TractorStopDistance + radii,
                cycle.Module.TractorVelocity,
                (until - saved.StartedAt) / (double)TimeSpan.TicksPerSecond
            );
            wreck.X = point.X;
            wreck.Y = point.Y;
            wreck.Z = point.Z;
            saved.StartedAt = until;
            world.SaveChanges();
        }
        transaction.Commit();
    }
}
