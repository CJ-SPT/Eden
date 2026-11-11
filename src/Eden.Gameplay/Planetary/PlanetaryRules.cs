namespace Eden.Gameplay.Planetary;

public static class PlanetaryRules
{
    public const int CommandCenterUpgradesSkill = 2505;
    public const int InterplanetaryConsolidationSkill = 2495;
    public const int RemoteSensingSkill = 13279;
    public const int PlanetologySkill = 2406;
    public const int AdvancedPlanetologySkill = 2403;
    public const double InterbusTaxRate = 0.17;
    public const int CustomsExpertiseSkill = 33467;
    public const int MaximumHeads = 10;
    public const int MaximumLinkLevel = 10;
    public const int MaximumPathPins = 7;
    public const int MaximumCommands = 2048;
    public const int MaximumPins = 512;
    public const double MinimumHeadRadius = 0.01;
    public const double MaximumHeadRadius = 0.05;
    public const long LaunchCooldown = TimeSpan.TicksPerMinute;
    public const long LaunchLifetime = 5 * TimeSpan.TicksPerDay;
    public const int LaunchContainerType = 2263;
    public const int CustomsOfficeType = 2233;
    public const int LinkType = 2280;
    public const int OrbitalIdleState = 0;
    public const long FirstOfficeId = 17_000_000_000_000;
    public const long FirstLaunchId = 1_700_000_000;
    private const long FirstPinId = 18_000_000_000_000;
    private static readonly int[] s_cpu = [1675, 7057, 12136, 17215, 21315, 25415];
    private static readonly int[] s_power = [6000, 9000, 12000, 15000, 17000, 19000];
    private static readonly long[] s_upgradeIsk = [0, 580000, 1510000, 2710000, 4210000, 6310000];

    public static long OfficeId(int planetId) => checked(FirstOfficeId + planetId);

    public static bool IsOffice(long id) => id > FirstOfficeId && id <= FirstOfficeId + int.MaxValue;

    public static bool IsLaunch(long id) => id > FirstLaunchId && id <= int.MaxValue;

    public static int OfficePlanet(long id) => IsOffice(id) ? checked((int)(id - FirstOfficeId)) : 0;

    public static PlanetaryColony Empty(long owner, int planet, long now) => new(owner, planet, 0, now, FirstPinId, 1, [], [], []);

    public static void Coordinates(double latitude, double longitude)
    {
        // Native SurfacePoint uses colatitude [0, pi], longitude [0, 2*pi].
        if (
            !double.IsFinite(latitude)
            || !double.IsFinite(longitude)
            || latitude < 0
            || latitude > Math.PI
            || longitude < 0
            || longitude > Math.Tau
        )
        {
            throw new InvalidDataException("Choose a valid point on the planet.");
        }
    }

    public static PlanetaryType PinType(PlanetaryCatalog catalog, int id) =>
        catalog.Pins.GetValueOrDefault(id) ?? throw new InvalidDataException("This planetary building is unavailable.");

    public static PlanetaryPin Pin(PlanetaryColony colony, long id) =>
        colony.Pins.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("The colony does not contain that pin.");

    public static PlanetaryProgramQuote Quote(
        PlanetaryPlanet planet,
        PlanetaryCatalog catalog,
        PlanetaryPin pin,
        int typeId,
        IReadOnlyList<PlanetaryHead> heads,
        double radius,
        IReadOnlyList<PlanetaryPin> peers,
        bool validateRange = true
    )
    {
        var profile = PinType(catalog, pin.TypeId);
        if (
            profile.Kind != PlanetaryPinKind.Extractor
            || !PlanetaryResources.Types(planet.TypeId).Contains(typeId)
            || heads.Count is < 1 or > MaximumHeads
            || heads.Select(h => h.Id).Distinct().Count() != heads.Count
            || !double.IsFinite(radius)
            || radius < MinimumHeadRadius
            || radius > MaximumHeadRadius
        )
        {
            throw new InvalidDataException("Choose a planetary resource, one to ten heads and a valid extraction area.");
        }
        foreach (var head in heads)
        {
            ValidateHead(pin, validateRange ? profile : profile with { HeadRange = Math.PI }, head);
        }
        // planetCommon.GetProgramLengthFromHeadRadius / GetCycleTimeFromProgramLength, hours.
        double hours = 1 + 335 * (radius - MinimumHeadRadius) / (MaximumHeadRadius - MinimumHeadRadius);
        double cycleHours = 0.25 * Math.Pow(2, Math.Max(0, Math.Floor(Math.Log2(hours / 25)) + 1));
        long cycle = checked((long)(cycleHours * TimeSpan.TicksPerHour));
        int cycles = Math.Max(1, (int)Math.Floor(hours / cycleHours));
        double quantity = 0;
        var otherHeads = peers
            .Where(p => p.Id != pin.Id && p.Program?.TypeId == typeId)
            .SelectMany(p => (p.Heads ?? []).Select(h => (Head: h, Radius: p.Program!.HeadRadius)))
            .ToArray();
        foreach (var head in heads)
        {
            double overlap = 0;
            foreach (var other in heads.Where(h => h.Id != head.Id).Select(h => (Head: h, Radius: radius)).Concat(otherHeads))
            {
                double distance = PlanetaryResources.AngularDistance(
                    head.Latitude,
                    head.Longitude,
                    other.Head.Latitude,
                    other.Head.Longitude
                );
                overlap += Math.Max(0, 1 - distance / (radius + other.Radius)) * 0.5;
            }
            // Eden yield policy: the surveyed field, footprint and overlap determine the
            // base units per fifteen minutes; the client's decay/noise formula applies per cycle.
            quantity +=
                12000
                * PlanetaryResources.Density(planet.Id, typeId, head.Latitude, head.Longitude)
                * Math.Sqrt(radius / MinimumHeadRadius)
                * Math.Max(0.1, 1 - overlap);
        }
        return new(Math.Max(1, checked((int)Math.Floor(quantity))), cycle, cycles);
    }

    public static int ExtractionOutput(PlanetaryProgram program, PlanetaryType type, long time)
    {
        // ecuPin.GetProgramOutput: sample the middle of each 15-minute-scaled bar.
        double cycle = Math.Max(0, (time - program.InstalledAt + TimeSpan.TicksPerSecond) / program.CycleTicks - 1);
        double width = (double)program.CycleTicks / TimeSpan.TicksPerSecond / 900;
        double t = (cycle + 0.5) * width;
        double phase = Math.Pow(program.BaseQuantity, 0.7);
        double wave = Math.Max(0, (Math.Cos(phase + t / 12) + Math.Cos(phase / 2 + t / 5) + Math.Cos(t / 2)) / 3);
        return checked((int)Math.Max(0, Math.Floor(width * program.BaseQuantity / (1 + t * type.Decay) * (1 + type.Noise * wave))));
    }

    public static PlanetaryEditResult Edit(
        PlanetaryColony colony,
        PlanetaryPlanet planet,
        PlanetaryCatalog catalog,
        IReadOnlyList<PlanetaryCommand> commands,
        int upgradeSkill,
        long now
    )
    {
        if (commands.Count is < 1 or > MaximumCommands || colony.PlanetId != planet.Id)
        {
            throw new InvalidDataException("Submit a bounded set of colony edits for this planet.");
        }
        List<PlanetaryPin> pins = [.. colony.Pins];
        List<PlanetaryLink> links = [.. colony.Links];
        List<PlanetaryRoute> routes = [.. colony.Routes];
        Dictionary<PlanetaryId, long> temporary = [];
        Dictionary<long, InstallPlanetaryProgram> programs = [];
        long nextPin = colony.NextPin;
        long nextRoute = colony.NextRoute;
        long cost = 0;
        int level = colony.Level;
        long Resolve(PlanetaryId id) =>
            temporary.TryGetValue(id, out long resolved) ? resolved
            : id.TemporaryKind == 0 ? id.Value
            : throw new InvalidDataException("Unknown temporary planetary identity.");
        long Allocate(PlanetaryId id, int kind)
        {
            if (id.TemporaryKind != kind || id.Value < 0 || temporary.ContainsKey(id))
            {
                throw new InvalidDataException("Choose a new temporary planetary identity.");
            }
            long value = kind == 1 ? nextPin++ : nextRoute++;
            temporary.Add(id, value);
            return value;
        }
        PlanetaryPin Find(PlanetaryId id) =>
            pins.SingleOrDefault(p => p.Id == Resolve(id)) ?? throw new InvalidDataException("The colony does not contain that pin.");
        void Replace(PlanetaryPin pin) => pins[pins.FindIndex(p => p.Id == pin.Id)] = pin;
        void RemoveRoutes(long id) => routes.RemoveAll(r => r.Path.Contains(id));
        foreach (var command in commands)
        {
            switch (command)
            {
                case CreatePlanetaryPin create:
                {
                    Coordinates(create.Latitude, create.Longitude);
                    var type = PinType(catalog, create.TypeId);
                    if (type.PlanetTypeId != 0 && type.PlanetTypeId != planet.TypeId)
                    {
                        throw new InvalidDataException("This building requires a different planet type.");
                    }
                    bool center = type.Kind == PlanetaryPinKind.Command;
                    if (center == pins.Any(p => PinType(catalog, p.TypeId).Kind == PlanetaryPinKind.Command))
                    {
                        throw new InvalidDataException("Deploy exactly one command center before placing other buildings.");
                    }
                    long id;
                    if (center && create.Pin.TemporaryKind == 0 && create.Pin.Value > 0 && !temporary.ContainsKey(create.Pin))
                    {
                        id = nextPin++;
                        temporary.Add(create.Pin, id);
                    }
                    else
                    {
                        id = Allocate(create.Pin, 1);
                    }
                    if (id <= 0 || pins.Any(p => p.Id == id) || pins.Count >= MaximumPins)
                    {
                        throw new InvalidDataException("Invalid or duplicate planetary pin.");
                    }
                    pins.Add(new(id, type.TypeId, create.Latitude, create.Longitude, now, []));
                    cost = checked(cost + (center ? 0 : type.CostCents));
                    break;
                }
                case RemovePlanetaryPin remove:
                {
                    var pin = Find(remove.Pin);
                    if (PinType(catalog, pin.TypeId).Kind == PlanetaryPinKind.Command)
                    {
                        throw new InvalidDataException("Abandon the planet to remove its command center.");
                    }
                    pins.Remove(pin);
                    links.RemoveAll(l => l.First == pin.Id || l.Second == pin.Id);
                    RemoveRoutes(pin.Id);
                    programs.Remove(pin.Id);
                    break;
                }
                case CreatePlanetaryLink create:
                {
                    long first = Find(create.First).Id;
                    long second = Find(create.Second).Id;
                    if (first == second || links.Any(l => Matches(l, first, second)) || create.Level != 0)
                    {
                        throw new InvalidDataException("Create a new level-zero link between different pins.");
                    }
                    links.Add(new(Math.Min(first, second), Math.Max(first, second), 0));
                    break;
                }
                case RemovePlanetaryLink remove:
                {
                    long first = Find(remove.First).Id;
                    long second = Find(remove.Second).Id;
                    if (links.RemoveAll(l => Matches(l, first, second)) != 1)
                    {
                        throw new InvalidDataException("This link does not exist.");
                    }
                    routes.RemoveAll(r => Edges(r.Path).Any(e => Matches(new(first, second, 0), e.First, e.Second)));
                    break;
                }
                case UpgradePlanetaryLink upgrade:
                {
                    long first = Find(upgrade.First).Id;
                    long second = Find(upgrade.Second).Id;
                    int index = links.FindIndex(l => Matches(l, first, second));
                    if (index < 0 || upgrade.Level is < 0 or > MaximumLinkLevel)
                    {
                        throw new InvalidDataException("Choose an existing link and a supported level.");
                    }
                    links[index] = links[index] with { Level = upgrade.Level };
                    break;
                }
                case CreatePlanetaryRoute create:
                {
                    long id = Allocate(create.Route, 2);
                    routes.Add(new(id, create.Path.Select(Resolve).ToArray(), create.TypeId, create.Quantity));
                    break;
                }
                case RemovePlanetaryRoute remove:
                {
                    if (routes.RemoveAll(r => r.Id == Resolve(remove.Route)) != 1)
                    {
                        throw new InvalidDataException("This route does not exist.");
                    }
                    break;
                }
                case SetPlanetarySchematic set:
                {
                    var pin = Find(set.Pin);
                    if (
                        PinType(catalog, pin.TypeId).Kind != PlanetaryPinKind.Processor
                        || catalog.Schematics.GetValueOrDefault(set.SchematicId) is not { } schematic
                        || !schematic.PinTypes.Contains(pin.TypeId)
                    )
                    {
                        throw new InvalidDataException("This schematic cannot run in that processor.");
                    }
                    if (pin.SchematicId != set.SchematicId)
                    {
                        Replace(pin with { SchematicId = set.SchematicId, Contents = [], NextRunAt = null, LastRunAt = now });
                        RemoveRoutes(pin.Id);
                    }
                    break;
                }
                case UpgradePlanetaryCommand upgrade:
                {
                    if (
                        PinType(catalog, Find(upgrade.Pin).TypeId).Kind != PlanetaryPinKind.Command
                        || upgrade.Level <= level
                        || upgrade.Level > 5
                        || upgrade.Level > upgradeSkill
                    )
                    {
                        throw new InvalidDataException("Train Command Center Upgrades before upgrading this command center.");
                    }
                    cost = checked(cost + (s_upgradeIsk[upgrade.Level] - s_upgradeIsk[level]) * 100);
                    level = upgrade.Level;
                    break;
                }
                case AddPlanetaryHead add:
                {
                    var pin = EditableHeads(Find(add.Pin), catalog, now);
                    ValidateHead(pin, PinType(catalog, pin.TypeId), add.Head);
                    if ((pin.Heads?.Count ?? 0) >= MaximumHeads || (pin.Heads ?? []).Any(h => h.Id == add.Head.Id))
                    {
                        throw new InvalidDataException("Choose an unused extractor head slot.");
                    }
                    Replace(pin with { Heads = [.. pin.Heads ?? [], add.Head] });
                    break;
                }
                case RemovePlanetaryHead remove:
                {
                    var pin = EditableHeads(Find(remove.Pin), catalog, now);
                    if (!(pin.Heads ?? []).Any(h => h.Id == remove.HeadId))
                    {
                        throw new InvalidDataException("This extractor head does not exist.");
                    }
                    Replace(pin with { Heads = (pin.Heads ?? []).Where(h => h.Id != remove.HeadId).ToArray() });
                    break;
                }
                case MovePlanetaryHead move:
                {
                    var pin = EditableHeads(Find(move.Pin), catalog, now);
                    ValidateHead(pin, PinType(catalog, pin.TypeId), move.Head);
                    if (!(pin.Heads ?? []).Any(h => h.Id == move.Head.Id))
                    {
                        throw new InvalidDataException("This extractor head does not exist.");
                    }
                    Replace(pin with { Heads = (pin.Heads ?? []).Select(h => h.Id == move.Head.Id ? move.Head : h).ToArray() });
                    break;
                }
                case InstallPlanetaryProgram install:
                {
                    var pin = Find(install.Pin);
                    if (PinType(catalog, pin.TypeId).Kind != PlanetaryPinKind.Extractor)
                    {
                        throw new InvalidDataException("Install a program on an extractor control unit.");
                    }
                    if (pin.Program is { } previous && now < previous.InstalledAt + TimeSpan.TicksPerMinute)
                    {
                        throw new InvalidDataException("Wait one minute before replacing this extraction program.");
                    }
                    if (install.TypeId is null)
                    {
                        programs.Remove(pin.Id);
                        Replace(pin with { Program = null, NextRunAt = null });
                        routes.RemoveAll(r => r.Path[0] == pin.Id);
                    }
                    else
                    {
                        programs[pin.Id] = install;
                    }
                    break;
                }
                default:
                    throw new InvalidDataException("Unknown planetary command.");
            }
        }
        // All newly installed ECUs see the same final layout, independent of command order.
        foreach (var (id, install) in programs)
        {
            var pin = pins.Single(p => p.Id == id);
            Replace(pin with { Program = new(install.TypeId!.Value, now, now, 1, 0, install.HeadRadius) });
        }
        var peers = pins.ToArray();
        foreach (var (id, install) in programs)
        {
            var pin = pins.Single(p => p.Id == id);
            var quote = Quote(planet, catalog, pin, install.TypeId!.Value, pin.Heads ?? [], install.HeadRadius, peers);
            var program = new PlanetaryProgram(
                install.TypeId.Value,
                now,
                checked(now + quote.CycleTicks * quote.Cycles),
                quote.CycleTicks,
                quote.BaseQuantity,
                install.HeadRadius
            );
            Replace(pin with { Program = program, LastRunAt = now, NextRunAt = checked(now + program.CycleTicks) });
            routes.RemoveAll(r => r.Path[0] == pin.Id && r.TypeId != program.TypeId);
        }
        var result = colony with
        {
            Level = level,
            Pins = pins.ToArray(),
            Links = links.ToArray(),
            Routes = routes.ToArray(),
            NextPin = nextPin,
            NextRoute = nextRoute,
        };
        Validate(result, planet, catalog);
        return new(PlanetarySimulation.Advance(result, catalog, now), cost);
    }

    private static PlanetaryPin EditableHeads(PlanetaryPin pin, PlanetaryCatalog catalog, long now)
    {
        if (PinType(catalog, pin.TypeId).Kind != PlanetaryPinKind.Extractor || pin.Program?.ExpiresAt > now)
        {
            throw new InvalidDataException("Stop the extractor program before changing its heads.");
        }
        return pin;
    }

    private static void ValidateHead(PlanetaryPin pin, PlanetaryType type, PlanetaryHead head)
    {
        Coordinates(head.Latitude, head.Longitude);
        if (
            head.Id is < 0 or >= MaximumHeads
            || PlanetaryResources.AngularDistance(pin.Latitude, pin.Longitude, head.Latitude, head.Longitude) > type.HeadRange
        )
        {
            throw new InvalidDataException("Place the extractor head within the control unit's area of influence.");
        }
    }

    public static bool Matches(PlanetaryLink link, long first, long second) =>
        link.First == first && link.Second == second || link.First == second && link.Second == first;

    public static IEnumerable<(long First, long Second)> Edges(IReadOnlyList<long> path) =>
        path.Zip(path.Skip(1), (first, second) => (first, second));

    public static double LinkLength(PlanetaryColony colony, PlanetaryPlanet planet, PlanetaryLink link)
    {
        var first = Pin(colony, link.First);
        var second = Pin(colony, link.Second);
        return planet.Radius
            / 1000
            * PlanetaryResources.AngularDistance(first.Latitude, first.Longitude, second.Latitude, second.Longitude);
    }

    public static double Bandwidth(PlanetaryLinkProfile profile, int level) => profile.CapacityPerHour * Math.Pow(2, level);

    public static PlanetaryLinkUsage LinkUsage(PlanetaryLinkProfile profile, double kilometers, int level) =>
        new(
            profile.Cpu + Math.Ceiling(profile.CpuPerKm * kilometers * Math.Pow(level + 1, profile.CpuLevelMultiplier)),
            profile.Power + Math.Ceiling(profile.PowerPerKm * kilometers * Math.Pow(level + 1, profile.PowerLevelMultiplier))
        );

    public static void Path(PlanetaryColony colony, IReadOnlyList<long> path)
    {
        if (
            path.Count is < 2 or > MaximumPathPins
            || path.Distinct().Count() != path.Count
            || path.Any(id => !colony.Pins.Any(p => p.Id == id))
            || Edges(path).Any(e => !colony.Links.Any(l => Matches(l, e.First, e.Second)))
        )
        {
            throw new InvalidDataException("Choose a connected, non-repeating route of at most seven pins.");
        }
    }

    public static void Validate(PlanetaryColony colony, PlanetaryPlanet planet, PlanetaryCatalog catalog)
    {
        double cpu = 0;
        double power = 0;
        foreach (var pin in colony.Pins)
        {
            var type = PinType(catalog, pin.TypeId);
            cpu += type.Cpu + (pin.Heads?.Count ?? 0) * type.HeadCpu;
            power += type.Power + (pin.Heads?.Count ?? 0) * type.HeadPower;
        }
        foreach (var link in colony.Links)
        {
            double length = LinkLength(colony, planet, link);
            var linkUsage = LinkUsage(catalog.Link, length, link.Level);
            cpu += linkUsage.Cpu;
            power += linkUsage.Power;
        }
        if (cpu > s_cpu[colony.Level] || power > s_power[colony.Level])
        {
            throw new InvalidDataException("The colony exceeds its command center CPU or power supply.");
        }
        Dictionary<PlanetaryLink, double> usage = [];
        foreach (var route in colony.Routes)
        {
            Path(colony, route.Path);
            if (route.Quantity <= 0 || !catalog.Volumes.TryGetValue(route.TypeId, out double volume))
            {
                throw new InvalidDataException("Route a positive amount of a planetary commodity.");
            }
            var source = Pin(colony, route.Path[0]);
            var destination = Pin(colony, route.Path[^1]);
            var sourceType = PinType(catalog, source.TypeId);
            var destinationType = PinType(catalog, destination.TypeId);
            long cycle;
            if (IsStorage(sourceType.Kind))
            {
                if (destination.SchematicId is not int recipe || !catalog.Schematics[recipe].Inputs.Any(i => i.TypeId == route.TypeId))
                {
                    throw new InvalidDataException("Storage routes must feed a processor's schematic inputs.");
                }
                cycle = catalog.Schematics[recipe].CycleSeconds * TimeSpan.TicksPerSecond;
            }
            else
            {
                int output = source.Program?.TypeId ?? (source.SchematicId is int recipe ? catalog.Schematics[recipe].Output.TypeId : 0);
                if (output != route.TypeId)
                {
                    throw new InvalidDataException("Route the source building's installed product.");
                }
                cycle = source.Program?.CycleTicks ?? catalog.Schematics[source.SchematicId!.Value].CycleSeconds * TimeSpan.TicksPerSecond;
            }
            if (
                !IsStorage(destinationType.Kind)
                && (
                    destination.SchematicId is not int schematic || !catalog.Schematics[schematic].Inputs.Any(i => i.TypeId == route.TypeId)
                )
            )
            {
                throw new InvalidDataException("The destination cannot accept this product.");
            }
            foreach (var edge in Edges(route.Path))
            {
                var link = colony.Links.Single(l => Matches(l, edge.First, edge.Second));
                double total = usage.GetValueOrDefault(link) + route.Quantity * volume * TimeSpan.TicksPerHour / cycle;
                if (total > Bandwidth(catalog.Link, link.Level))
                {
                    throw new InvalidDataException("Upgrade the link to carry this route's bandwidth.");
                }
                usage[link] = total;
            }
        }
        foreach (var pin in colony.Pins.Where(p => p.SchematicId.HasValue))
        {
            var schematic = catalog.Schematics[pin.SchematicId!.Value];
            long outgoing = colony.Routes.Where(r => r.Path[0] == pin.Id).Sum(r => (long)r.Quantity);
            if (outgoing > schematic.Output.Quantity)
            {
                throw new InvalidDataException("The processor's routes exceed its output per cycle.");
            }
            foreach (var ingredient in schematic.Inputs)
            {
                long incoming = colony.Routes.Where(r => r.Path[^1] == pin.Id && r.TypeId == ingredient.TypeId).Sum(r => (long)r.Quantity);
                if (incoming > ingredient.Quantity)
                {
                    throw new InvalidDataException("The processor's incoming routes exceed its input per cycle.");
                }
            }
        }
    }

    public static bool IsStorage(PlanetaryPinKind kind) =>
        kind is PlanetaryPinKind.Command or PlanetaryPinKind.Storage or PlanetaryPinKind.Spaceport;

    public static long TaxCents(PlanetaryCatalog catalog, IReadOnlyList<PlanetaryCommodity> commodities, double rate, bool import = false)
    {
        // Pinned commodity-tier taxable values, ISK/unit. Command launches use 15%,
        // Interbus customs exports 17%; imports cost half exports.
        int[] values = [5, 400, 7200, 60000, 1200000];
        decimal total = 0;
        foreach (var commodity in commodities)
        {
            if (!catalog.CommodityTiers.TryGetValue(commodity.TypeId, out int tier) || commodity.Quantity <= 0)
            {
                throw new InvalidDataException("Select positive planetary commodity quantities.");
            }
            total += (decimal)values[tier] * commodity.Quantity * (decimal)rate * (import ? 0.5m : 1m) * 100;
        }
        return checked((long)decimal.Ceiling(total));
    }
}
