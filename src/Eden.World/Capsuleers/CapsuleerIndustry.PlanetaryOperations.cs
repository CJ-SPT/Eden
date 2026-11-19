using Eden.Data.World.Inventory;
using Eden.Data.World.Planetary;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Planetary;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Planetary;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal sealed record PlanetaryVisitResult(FactoryPlanetaryProduction? Pending, FreightStack? Cargo);

    private static readonly TimeSpan PlanetaryExportRetry = TimeSpan.FromMinutes(5);

    internal static StaticSpaceObject PlanetaryOffice(WorldAction world, CatalogDbContext catalog, int planetId)
    {
        var office =
            world.Set<SavedPlanetaryOffice>().Find(planetId) ?? throw new InvalidDataException("The customs office is unavailable.");
        var type = catalog.RequireItem(PlanetaryRules.CustomsOfficeType);
        return new(
            PlanetaryRules.OfficeId(planetId),
            type.Id,
            type.GroupId,
            type.CategoryId,
            PlanetaryOrbitStore.InterbusCorporation,
            new(office.X, office.Y, office.Z),
            (float)(type.Radius ?? 0)
        );
    }

    internal PlanetaryVisitResult VisitPlanet(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        FactoryPlanetaryVisit visit,
        long now
    )
    {
        var job = visit.Production;
        long pilot = courier.PilotId;
        if (Employer(world, pilot, job.CorporationId) is not { } employer)
        {
            return new(null, null);
        }
        var planet = PlanetaryCatalogReader.Planet(catalog, job.PlanetId);
        var definitions = PlanetaryCatalogReader.Read(catalog);
        var policy = world.Settings.PlanetaryIndustry;
        long officeId = PlanetaryRules.OfficeId(planet.Id);
        var saved = world.Set<SavedPlanetaryColony>().Find(pilot, planet.Id);
        var colony = saved is null ? PlanetaryRules.Empty(pilot, planet.Id, now) : planetary.Advance(saved, definitions, now);
        var port = colony.Pins.FirstOrDefault(pin => definitions.Pins[pin.TypeId].Kind == PlanetaryPinKind.Spaceport);

        if (port is not null && visit.Inputs is not { Length: > 0 })
        {
            int available = port.Contents.FirstOrDefault(item => item.TypeId == job.Step.TypeId)?.Quantity ?? 0;
            if (available > 0)
            {
                double space = FreeFreightVolume(world, catalog, courier);
                int quantity = (int)
                    Math.Clamp(Math.Floor(space / definitions.Volumes[job.Step.TypeId]), 0, Math.Min(available, job.Step.Quantity));
                if (quantity == 0)
                {
                    return RetryExport();
                }
                PlanetaryCommodity[] exports = [new(job.Step.TypeId, quantity)];
                if (PlanetaryRules.TaxCents(definitions, exports, PlanetaryRules.CustomsTaxRate(policy)) > employer.BalanceCents)
                {
                    return RetryExport();
                }
                var exported = planetary.ImportExport(world, catalog, pilot, officeId, port.Id, [], exports, now, employer.Id);
                long item = exported.Items.Single().Item.Id;
                var moved = PlanetaryOrbitStore.Move(
                    world,
                    catalog,
                    pilot,
                    officeId,
                    false,
                    new([item], officeId, (int)InventoryFlag.Cargo, quantity),
                    now,
                    policy
                );
                long cargo = moved.Single(change => change.Item.LocationId == courier.ShipId).Item.Id;
                return new(null, new(cargo, job.Step.TypeId, quantity));
            }
        }
        if (job.ReadyAt > 0)
        {
            long? next = colony.Pins.FirstOrDefault(pin => pin.TypeId == job.MachineType)?.NextRunAt;
            return next > now ? new(job with { ReadyAt = next.Value }, null) : new(null, null);
        }

        var center = saved is null ? PlanetaryCenter(world, courier, job.CenterType) : null;
        if (saved is null && center is null || !QualifyPlanetary(world, catalog, pilot, job, colony, definitions))
        {
            return new(null, null);
        }
        var input = visit.Inputs ?? [];
        if (
            input.Any(stack =>
                world.Set<OwnedItem>().Find(stack.ItemId) is not { } cargo
                || cargo.OwnerId != pilot
                || cargo.LocationId != courier.ShipId
                || cargo.TypeId != stack.TypeId
                || cargo.Quantity < stack.Quantity
            )
        )
        {
            return new(null, null);
        }
        long tax = PlanetaryRules.TaxCents(definitions, job.Step.Inputs, PlanetaryRules.CustomsTaxRate(policy), import: true);
        var layout = PreparePlanetaryLayout(planet, definitions, policy, colony, port, center, job, now);
        if (layout is null || (decimal)layout.Cost + tax > employer.BalanceCents)
        {
            return new(null, null);
        }

        var deployed = planetary.Update(world, catalog, pilot, planet.Id, layout.Commands, now, employer.Id).Colony;
        if (input.Length > 0)
        {
            var customs = PlanetaryOrbitStore.Move(
                world,
                catalog,
                pilot,
                officeId,
                true,
                new(input.Select(stack => stack.ItemId).ToArray(), courier.ShipId, (int)InventoryFlag.Hangar, null),
                now,
                policy
            );
            var imports = input
                .Select(stack => new PlanetaryCargoRequest(
                    customs.Single(change => change.Item.LocationId == officeId && change.Item.TypeId == stack.TypeId).Item.Id,
                    stack.Quantity
                ))
                .ToArray();
            port = deployed.Pins.Single(pin => definitions.Pins[pin.TypeId].Kind == PlanetaryPinKind.Spaceport);
            deployed = planetary.ImportExport(world, catalog, pilot, officeId, port.Id, imports, [], now, employer.Id).Colony;
        }
        var machine = deployed.Pins.Single(pin => pin.TypeId == job.MachineType);
        long ready = machine.Program?.ExpiresAt ?? checked(now + layout.Duration);
        return new(job with { ReadyAt = ready }, null);

        PlanetaryVisitResult RetryExport() => new(job with { ReadyAt = checked(now + PlanetaryExportRetry.Ticks) }, null);
    }

    private static bool QualifyPlanetary(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilot,
        FactoryPlanetaryProduction job,
        PlanetaryColony colony,
        PlanetaryCatalog definitions
    )
    {
        const int upgrades = PlanetaryRules.CommandCenterUpgradesSkill;
        const int consolidation = PlanetaryRules.InterplanetaryConsolidationSkill;
        int count = world.Filter<SavedPlanetaryColony>(colony => colony.PilotId == pilot).Count() + (colony.Pins.Count == 0 ? 1 : 0);
        int level = PlanetaryRules.RequiredConsolidationLevel(world.Settings.PlanetaryIndustry, count);
        if (
            level > PlanetaryCommandCenterSkillLevel
            || catalog.FittingAttributes(upgrades).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) <= 0
            || level > 0 && catalog.FittingAttributes(consolidation).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) <= 0
        )
        {
            return false;
        }
        CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, upgrades, []);
        Qualify(world, catalog, pilot, new(upgrades, PlanetaryCommandCenterSkillLevel));
        if (level > 0)
        {
            CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, consolidation, []);
            Qualify(world, catalog, pilot, new(consolidation, level));
        }
        int port = definitions
            .Pins.Values.First(pin =>
                pin.PlanetTypeId == definitions.Pins[job.CenterType].PlanetTypeId && pin.Kind == PlanetaryPinKind.Spaceport
            )
            .TypeId;
        foreach (int type in new[] { job.CenterType, job.MachineType, port })
        {
            CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, type, []);
        }
        return true;
    }

    private sealed record PlanetaryLayout(PlanetaryCommand[] Commands, long Cost, long Duration);

    private static PlanetaryLayout? PreparePlanetaryLayout(
        PlanetaryPlanet planet,
        PlanetaryCatalog definitions,
        PlanetaryPolicy policy,
        PlanetaryColony colony,
        PlanetaryPin? existingPort,
        OwnedItem? centerItem,
        FactoryPlanetaryProduction job,
        long now
    )
    {
        const double latitude = Math.PI / 2;
        const double pinSpacing = 0.000001;
        List<PlanetaryCommand> commands = colony.Routes.Select(route => (PlanetaryCommand)new RemovePlanetaryRoute(new(route.Id))).ToList();
        PlanetaryId center = centerItem is not null
            ? new(centerItem.Id)
            : new(colony.Pins.Single(pin => definitions.Pins[pin.TypeId].Kind == PlanetaryPinKind.Command).Id);
        if (centerItem is not null)
        {
            commands.Add(new CreatePlanetaryPin(center, job.CenterType, latitude, 0));
        }
        int nextPin = 1;
        var portType = definitions
            .Pins.Values.Where(pin => pin.PlanetTypeId == planet.TypeId && pin.Kind == PlanetaryPinKind.Spaceport)
            .MinBy(pin => pin.TypeId)!;
        var port = EnsurePin(existingPort, portType.TypeId);
        var existingMachine = colony.Pins.FirstOrDefault(pin => pin.TypeId == job.MachineType);
        var machine = EnsurePin(existingMachine, job.MachineType);
        var link = colony.Links.FirstOrDefault(row => PlanetaryRules.Matches(row, port.Value, machine.Value));
        if (link is null)
        {
            commands.Add(new CreatePlanetaryLink(port, machine, 0));
        }
        int route = 1;
        long cycle;
        long duration;
        int sent;
        double incoming = 0;
        if (job.Step.SchematicId is int recipe)
        {
            var schematic = definitions.Schematics[recipe];
            if (existingMachine?.SchematicId != recipe)
            {
                commands.Add(new SetPlanetarySchematic(machine, recipe));
            }
            foreach (var ingredient in schematic.Inputs)
            {
                commands.Add(new CreatePlanetaryRoute(new(route++, 2), [port, machine], ingredient.TypeId, ingredient.Quantity));
                incoming += ingredient.Quantity * definitions.Volumes[ingredient.TypeId];
            }
            cycle = PlanetaryRules.FactoryCycleTicks(schematic, policy);
            duration = checked(cycle * (job.Step.Inputs[0].Quantity / schematic.Inputs[0].Quantity));
            sent = PlanetaryRules.ScaleOutput(schematic.Output.Quantity, policy.FactoryOutputMultiplier);
        }
        else
        {
            var coordinates =
                existingMachine ?? new PlanetaryPin(machine.Value, job.MachineType, latitude, (nextPin - 1) * pinSpacing, now, []);
            if (coordinates.Heads is not { Count: > 0 })
            {
                commands.Add(new AddPlanetaryHead(machine, new(0, coordinates.Latitude, coordinates.Longitude)));
            }
            var heads = coordinates.Heads is { Count: > 0 } installed ? installed : [new(0, coordinates.Latitude, coordinates.Longitude)];
            double radius = PlanetaryRules.MinimumHeadRadius;
            var quote = PlanetaryRules.Quote(planet, definitions, coordinates, job.Step.TypeId, heads, radius, colony.Pins, policy: policy);
            commands.Add(new InstallPlanetaryProgram(machine, job.Step.TypeId, radius));
            cycle = quote.CycleTicks;
            duration = checked(cycle * quote.Cycles);
            sent = Math.Min(job.Step.Quantity, quote.BaseQuantity);
        }
        commands.Add(new CreatePlanetaryRoute(new(route, 2), [machine, port], job.Step.TypeId, sent));
        int bandwidth = link?.Level ?? 0;
        double volume = (incoming + sent * definitions.Volumes[job.Step.TypeId]) * TimeSpan.TicksPerHour / cycle;
        while (bandwidth < PlanetaryRules.MaximumLinkLevel && PlanetaryRules.Bandwidth(definitions.Link, bandwidth) < volume)
        {
            bandwidth++;
        }
        if (bandwidth > (link?.Level ?? 0))
        {
            commands.Add(new UpgradePlanetaryLink(port, machine, bandwidth));
        }
        for (int level = colony.Level; level <= PlanetaryCommandCenterSkillLevel; level++)
        {
            PlanetaryCommand[] candidate = level > colony.Level ? [.. commands, new UpgradePlanetaryCommand(center, level)] : [.. commands];
            try
            {
                var preview = PlanetaryRules.Edit(colony, planet, definitions, candidate, PlanetaryCommandCenterSkillLevel, now, policy);
                return new(candidate, preview.CostCents, duration);
            }
            catch (InvalidDataException)
            {
                // Try a legal command center upgrade before selecting another production turn.
            }
        }
        return null;

        PlanetaryId EnsurePin(PlanetaryPin? existing, int type)
        {
            if (existing is not null)
            {
                return new(existing.Id);
            }
            PlanetaryId pin = new(nextPin++, 1);
            commands.Add(new CreatePlanetaryPin(pin, type, latitude, pin.Value * pinSpacing));
            return pin;
        }
    }
}
