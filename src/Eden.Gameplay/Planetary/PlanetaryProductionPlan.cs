namespace Eden.Gameplay.Planetary;

public sealed record PlanetaryProductionStep(int TypeId, int Quantity, int? SchematicId, PlanetaryCommodity[] Inputs);

public static class PlanetaryProductionPlan
{
    private const int MaximumCyclesPerBatch = 10;

    public static PlanetaryProductionStep? Next(
        PlanetaryCatalog catalog,
        int type,
        int quantity,
        IReadOnlyDictionary<int, long> inventory,
        decimal factoryOutputMultiplier
    )
    {
        if (quantity <= 0 || !catalog.CommodityTiers.ContainsKey(type))
        {
            return null;
        }
        var recipes = catalog.Schematics.Values.OrderBy(recipe => recipe.Id).ToLookup(recipe => recipe.Output.TypeId);
        var stock = inventory.ToDictionary();
        HashSet<int> path = [];
        return Require(type, quantity).Step;

        (bool Possible, PlanetaryProductionStep? Step) Require(int product, int units)
        {
            long available = stock.GetValueOrDefault(product);
            stock[product] = Math.Max(0, available - units);
            int missing = (int)Math.Max(0, units - available);
            if (missing == 0)
            {
                return (true, null);
            }
            if (!catalog.CommodityTiers.TryGetValue(product, out int tier) || !path.Add(product))
            {
                return (false, null);
            }
            if (tier == 0)
            {
                path.Remove(product);
                return (true, new(product, missing, null, []));
            }
            if (recipes[product].FirstOrDefault() is not { } schematic)
            {
                return (false, null);
            }
            int output = PlanetaryRules.ScaleOutput(schematic.Output.Quantity, factoryOutputMultiplier);
            int cycles = (int)Math.Min(MaximumCyclesPerBatch, ((long)missing + output - 1) / output);
            var inputs = schematic.Inputs.Select(input => input with { Quantity = checked(input.Quantity * cycles) }).ToArray();
            foreach (var input in inputs)
            {
                var dependency = Require(input.TypeId, input.Quantity);
                if (!dependency.Possible || dependency.Step is not null)
                {
                    return dependency;
                }
            }
            path.Remove(product);
            return (true, new(product, checked(output * cycles), schematic.Id, inputs));
        }
    }
}
