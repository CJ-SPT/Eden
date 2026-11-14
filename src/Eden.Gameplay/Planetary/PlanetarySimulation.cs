using Eden.Gameplay.Configuration;

namespace Eden.Gameplay.Planetary;

public static class PlanetarySimulation
{
    public static PlanetaryColony Advance(PlanetaryColony colony, PlanetaryCatalog catalog, long now, PlanetaryPolicy? policy = null)
    {
        if (now < colony.SimulatedAt)
        {
            throw new InvalidDataException("Colony time cannot move backwards.");
        }
        var pins = colony.Pins.ToDictionary(p => p.Id);
        void Start(long time)
        {
            // A processor keeps at most one cycle's ingredients while its current cycle
            // runs. Buffered storage routes are pulled only when that input buffer has room.
            foreach (var original in pins.Values.OrderBy(p => p.Id).ToArray())
            {
                var pin = pins[original.Id];
                if (pin.SchematicId is not int recipe)
                {
                    continue;
                }
                void PullInputs()
                {
                    foreach (var route in colony.Routes.Where(r => r.Path[^1] == pin.Id).OrderBy(r => r.Id))
                    {
                        var source = pins[route.Path[0]];
                        if (!PlanetaryRules.IsStorage(PlanetaryRules.PinType(catalog, source.TypeId).Kind))
                        {
                            continue;
                        }
                        int amount = Math.Min(route.Quantity, Quantity(source, route.TypeId));
                        int accepted = Accept(pin, catalog, route.TypeId, amount);
                        if (accepted > 0)
                        {
                            pins[source.Id] = Add(source, route.TypeId, -accepted);
                            pin = Add(pin, route.TypeId, accepted);
                        }
                    }
                }
                PullInputs();
                var schematic = catalog.Schematics[recipe];
                if (pin.NextRunAt is null && schematic.Inputs.All(i => Quantity(pin, i.TypeId) >= i.Quantity))
                {
                    foreach (var ingredient in schematic.Inputs)
                    {
                        pin = Add(pin, ingredient.TypeId, -ingredient.Quantity);
                    }
                    pin = pin with { NextRunAt = checked(time + schematic.CycleSeconds * TimeSpan.TicksPerSecond), LastRunAt = time };
                    // Establish the next-cycle buffer in this same event. A second
                    // read at the same timestamp must not pull an additional cycle.
                    PullInputs();
                }
                pins[pin.Id] = pin;
            }
        }
        void Produce(PlanetaryPin source, int type, int quantity)
        {
            foreach (
                var route in colony
                    .Routes.Where(r => r.Path[0] == source.Id && r.TypeId == type)
                    .OrderBy(r => PlanetaryRules.IsStorage(PlanetaryRules.PinType(catalog, pins[r.Path[^1]].TypeId).Kind))
                    .ThenBy(r => r.Id)
            )
            {
                var destination = pins[route.Path[^1]];
                int routed = Math.Min(quantity, route.Quantity);
                int accepted = Accept(destination, catalog, type, routed);
                pins[destination.Id] = Add(destination, type, accepted);
                // Overflow on a producer route is lost rather than silently redirected.
                quantity -= routed;
                if (quantity <= 0)
                {
                    break;
                }
            }
        }
        Start(colony.SimulatedAt);
        while (true)
        {
            long next = pins.Values.Where(p => p.NextRunAt.HasValue).Select(p => p.NextRunAt!.Value).DefaultIfEmpty(long.MaxValue).Min();
            if (next > now || next == long.MaxValue)
            {
                break;
            }
            // Complete all work at this deadline before starting the next processor cycles.
            foreach (var original in pins.Values.Where(p => p.NextRunAt == next).OrderBy(p => p.Id).ToArray())
            {
                var pin = pins[original.Id];
                if (pin.Program is { } program)
                {
                    if (next <= program.ExpiresAt)
                    {
                        Produce(pin, program.TypeId, PlanetaryRules.ExtractionOutput(program, catalog.Pins[pin.TypeId], next));
                    }
                    pins[pin.Id] = pin with
                    {
                        LastRunAt = next,
                        NextRunAt = next < program.ExpiresAt ? checked(next + program.CycleTicks) : null,
                    };
                }
                else if (pin.SchematicId is int recipe)
                {
                    var schematic = catalog.Schematics[recipe];
                    Produce(
                        pin,
                        schematic.Output.TypeId,
                        PlanetaryRules.ScaleOutput(schematic.Output.Quantity, policy?.FactoryOutputMultiplier ?? 1)
                    );
                    // Produce may have delivered inputs back to this pin through another
                    // same-deadline producer; preserve that updated buffer.
                    pins[pin.Id] = pins[pin.Id] with
                    {
                        LastRunAt = next,
                        NextRunAt = null,
                    };
                }
            }
            Start(next);
        }
        return colony with { SimulatedAt = now, Pins = pins.Values.OrderBy(p => p.Id).ToArray() };
    }

    public static int Quantity(PlanetaryPin pin, int type) => pin.Contents.SingleOrDefault(c => c.TypeId == type)?.Quantity ?? 0;

    public static PlanetaryPin Add(PlanetaryPin pin, int type, int amount)
    {
        int result = checked(Quantity(pin, type) + amount);
        if (result < 0)
        {
            throw new InvalidDataException("The planetary building does not contain those commodities.");
        }
        return pin with
        {
            Contents = pin
                .Contents.Where(c => c.TypeId != type)
                .Concat(result == 0 ? [] : new[] { new PlanetaryCommodity(type, result) })
                .OrderBy(c => c.TypeId)
                .ToArray(),
        };
    }

    public static int Accept(PlanetaryPin pin, PlanetaryCatalog catalog, int type, int quantity)
    {
        if (quantity <= 0 || !catalog.Volumes.TryGetValue(type, out double volume) || volume <= 0)
        {
            return 0;
        }
        var profile = catalog.Pins[pin.TypeId];
        if (pin.SchematicId is int recipe)
        {
            var ingredient = catalog.Schematics[recipe].Inputs.SingleOrDefault(i => i.TypeId == type);
            return ingredient is null ? 0 : Math.Min(quantity, Math.Max(0, ingredient.Quantity - Quantity(pin, type)));
        }
        if (!PlanetaryRules.IsStorage(profile.Kind))
        {
            return 0;
        }
        double used = pin.Contents.Sum(c => catalog.Volumes[c.TypeId] * c.Quantity);
        return (int)Math.Min(quantity, Math.Max(0, Math.Floor((profile.Capacity - used + 1e-8) / volume)));
    }

    public static IReadOnlyList<PlanetaryCommodity> Commodities(IReadOnlyList<PlanetaryCommodity> commodities)
    {
        if (
            commodities.Count is < 1 or > 128
            || commodities.Any(c => c.Quantity <= 0)
            || commodities.Select(c => c.TypeId).Distinct().Count() != commodities.Count
        )
        {
            throw new InvalidDataException("Select distinct planetary commodities with positive quantities.");
        }
        return commodities;
    }

    public static PlanetaryColony Remove(PlanetaryColony colony, long pinId, IReadOnlyList<PlanetaryCommodity> commodities)
    {
        var pin = PlanetaryRules.Pin(colony, pinId);
        foreach (var commodity in Commodities(commodities))
        {
            pin = Add(pin, commodity.TypeId, -commodity.Quantity);
        }
        return colony with { Pins = colony.Pins.Select(p => p.Id == pinId ? pin : p).ToArray() };
    }

    public static PlanetaryColony Deposit(
        PlanetaryColony colony,
        PlanetaryCatalog catalog,
        long pinId,
        IReadOnlyList<PlanetaryCommodity> commodities
    )
    {
        var pin = PlanetaryRules.Pin(colony, pinId);
        foreach (var commodity in Commodities(commodities))
        {
            if (Accept(pin, catalog, commodity.TypeId, commodity.Quantity) != commodity.Quantity)
            {
                throw new InvalidDataException("The planetary building cannot hold those commodities.");
            }
            pin = Add(pin, commodity.TypeId, commodity.Quantity);
        }
        return colony with { Pins = colony.Pins.Select(p => p.Id == pinId ? pin : p).ToArray() };
    }

    public static PlanetaryTransfer Transfer(
        PlanetaryColony colony,
        PlanetaryCatalog catalog,
        IReadOnlyList<long> path,
        IReadOnlyList<PlanetaryCommodity> commodities,
        long now
    )
    {
        PlanetaryRules.Path(colony, path);
        var source = PlanetaryRules.Pin(colony, path[0]);
        var destination = PlanetaryRules.Pin(colony, path[^1]);
        if (
            source.TransferReadyAt > now
            || !PlanetaryRules.IsStorage(catalog.Pins[source.TypeId].Kind)
            || !PlanetaryRules.IsStorage(catalog.Pins[destination.TypeId].Kind)
        )
        {
            throw new InvalidDataException("Choose two storage buildings and wait for the source's transfer cooldown.");
        }
        colony = Remove(colony, source.Id, commodities);
        colony = Deposit(colony, catalog, destination.Id, commodities);
        double volume = commodities.Sum(c => catalog.Volumes[c.TypeId] * c.Quantity);
        double bandwidth = PlanetaryRules
            .Edges(path)
            .Min(e => PlanetaryRules.Bandwidth(catalog.Link, colony.Links.Single(l => PlanetaryRules.Matches(l, e.First, e.Second)).Level));
        long ready = checked(now + Math.Max(5 * TimeSpan.TicksPerMinute, (long)Math.Ceiling(volume / bandwidth * TimeSpan.TicksPerHour)));
        colony = colony with { Pins = colony.Pins.Select(p => p.Id == source.Id ? p with { TransferReadyAt = ready } : p).ToArray() };
        return new(colony, ready);
    }
}
