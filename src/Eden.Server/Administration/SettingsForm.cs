using System.ComponentModel.DataAnnotations;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;

namespace Eden.Server.Administration;

internal sealed class SettingsForm
{
    public bool ServiceCosts { get; set; }

    [Range(typeof(decimal), "0", "1")]
    public decimal ManufacturingIndex { get; set; }
    public bool TestingMarket { get; set; }
    public bool TestingGrants { get; set; }

    [Range(1, NpcSupplyRules.MaximumStock)]
    public int InstitutionalStock { get; set; }

    [Range(1, 1_000_000_000)]
    public int TestingStock { get; set; }
    public decimal TestingPriceIsk { get; set; }
    public decimal GrantIsk { get; set; }
    public bool RenewBelts { get; set; }

    [Range(0, 23)]
    public int RenewalHourUtc { get; set; }
    public bool Pirates { get; set; }
    public bool Guards { get; set; }
    public bool Convoys { get; set; }
    public bool Mining { get; set; }
    public bool MajorThreatAnomalies { get; set; }
    public bool StartingSkills { get; set; }

    [Range(1, 300)]
    public int CheckpointSeconds { get; set; }

    [Range(100, 60_000)]
    public int MarketPollMilliseconds { get; set; }

    [Range(1, 3600)]
    public int ResourceCheckSeconds { get; set; }

    internal static SettingsForm From(WorldSettings settings) =>
        new()
        {
            ServiceCosts = settings.Economy.ChargeServiceCosts,
            ManufacturingIndex = settings.Economy.DefaultManufacturingIndex,
            TestingMarket = settings.DevelopmentSupply.TestingMarket,
            TestingGrants = settings.DevelopmentSupply.TestingGrants,
            InstitutionalStock = settings.DevelopmentSupply.InstitutionalStock,
            TestingStock = settings.DevelopmentSupply.TestingStock,
            TestingPriceIsk = settings.DevelopmentSupply.TestingPriceCents / 100m,
            GrantIsk = settings.DevelopmentSupply.GrantCents / 100m,
            RenewBelts = settings.Resources.RenewBelts,
            RenewalHourUtc = settings.Resources.RenewalHourUtc,
            Pirates = settings.NpcPopulation.Pirates,
            Guards = settings.NpcPopulation.Guards,
            Convoys = settings.NpcPopulation.Convoys,
            Mining = settings.NpcPopulation.Mining,
            MajorThreatAnomalies = settings.NpcPopulation.MajorThreatAnomalies,
            StartingSkills = settings.Progression.GrantStartingSkills,
            CheckpointSeconds = settings.Simulation.CheckpointSeconds,
            MarketPollMilliseconds = settings.Simulation.MarketPollMilliseconds,
            ResourceCheckSeconds = settings.Simulation.ResourceCheckSeconds,
        };

    internal WorldSettings Build() =>
        new()
        {
            Economy = new(ServiceCosts, ManufacturingIndex),
            DevelopmentSupply = new(
                TestingMarket,
                TestingGrants,
                InstitutionalStock,
                TestingStock,
                Cents(TestingPriceIsk),
                Cents(GrantIsk)
            ),
            Resources = new(RenewBelts, RenewalHourUtc),
            NpcPopulation = new(Pirates, Guards, Convoys, Mining, MajorThreatAnomalies),
            Progression = new(StartingSkills),
            Simulation = new(CheckpointSeconds, MarketPollMilliseconds, ResourceCheckSeconds),
        };

    private static long Cents(decimal isk)
    {
        decimal cents = isk * MarketDefinitions.CentsPerIsk;
        if (cents != decimal.Truncate(cents) || cents < 0 || cents > long.MaxValue)
        {
            throw new InvalidDataException("ISK amounts require at most two decimal places and a supported positive value.");
        }
        return (long)cents;
    }
}
