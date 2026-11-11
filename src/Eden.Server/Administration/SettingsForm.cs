using System.ComponentModel.DataAnnotations;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;

namespace Eden.Server.Administration;

internal sealed class SettingsForm
{
    public bool ServiceCosts { get; set; }
    public bool ProgressiveMarkets { get; set; }

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
    public bool GlobalProvisioning { get; set; }
    public bool SimulatedPilots { get; set; }
    public bool SimulatedMiners { get; set; }

    [Range(typeof(decimal), "0", "10")]
    [Display(Name = "Station traffic density")]
    public decimal TrafficMultiplier { get; set; }

    [Range(0, 16)]
    [Display(Name = "Visitors per watched gate")]
    public int GateVisitors { get; set; }

    [Range(1, 300)]
    public int CheckpointSeconds { get; set; }

    [Range(100, 60_000)]
    public int MarketPollMilliseconds { get; set; }

    [Range(1, 3600)]
    public int ResourceCheckSeconds { get; set; }

    [Range(0, (int)NavigationState.MaximumWarpRange)]
    [Display(Name = "Autopilot gate landing distance (metres)")]
    public int AutopilotGateLandingMeters { get; set; } = NavigationDefinitions.AutopilotLandingMeters;

    [Range(typeof(decimal), "0.1", "100")]
    [Display(Name = "Ore mining yield")]
    public decimal OreYieldMultiplier { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    [Display(Name = "Ice harvesting yield")]
    public decimal IceYieldMultiplier { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    [Display(Name = "Gas harvesting yield")]
    public decimal GasYieldMultiplier { get; set; } = 1;

    [Range(typeof(decimal), "0", "100")]
    [Display(Name = "NPC bounty ISK")]
    public decimal BountyMultiplier { get; set; } = 1;

    [Range(typeof(decimal), "0", "100")]
    [Display(Name = "Mission ISK")]
    public decimal MissionIskMultiplier { get; set; } = 1;

    [Range(typeof(decimal), "0", "100")]
    [Display(Name = "Mission loyalty points")]
    public decimal MissionLpMultiplier { get; set; } = 1;

    [Range(typeof(decimal), "0", "100")]
    [Display(Name = "Positive mission standings")]
    public decimal MissionStandingMultiplier { get; set; } = 1;

    [Range(typeof(decimal), "0", "100")]
    [Display(Name = "NPC kill skill points")]
    public decimal KillSkillPointMultiplier { get; set; } = 1;

    [Range(typeof(decimal), "0", "100")]
    [Display(Name = "Mining skill points")]
    public decimal MiningSkillPointMultiplier { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    [Display(Name = "Manufacturing speed")]
    public decimal ManufacturingSpeed { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    [Display(Name = "Research speed")]
    public decimal ResearchSpeed { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    [Display(Name = "Copying speed")]
    public decimal CopyingSpeed { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    [Display(Name = "Invention speed")]
    public decimal InventionSpeed { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    [Display(Name = "Reprocessing efficiency")]
    public decimal ReprocessingEfficiency { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    public decimal StructureAnchoringSpeed { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    public decimal StructureOnliningSpeed { get; set; } = 1;

    [Range(typeof(decimal), "0.1", "100")]
    public decimal StructureUnanchoringSpeed { get; set; } = 1;

    internal static SettingsForm From(WorldSettings settings) =>
        new()
        {
            OreYieldMultiplier = settings.Resources.OreYieldMultiplier,
            IceYieldMultiplier = settings.Resources.IceYieldMultiplier,
            GasYieldMultiplier = settings.Resources.GasYieldMultiplier,
            BountyMultiplier = settings.Rewards.BountyMultiplier,
            MissionIskMultiplier = settings.Rewards.MissionIskMultiplier,
            MissionLpMultiplier = settings.Rewards.MissionLpMultiplier,
            MissionStandingMultiplier = settings.Rewards.MissionStandingMultiplier,
            KillSkillPointMultiplier = settings.Progression.KillSkillPointMultiplier,
            MiningSkillPointMultiplier = settings.Progression.MiningSkillPointMultiplier,
            ManufacturingSpeed = settings.Industry.ManufacturingSpeed,
            ResearchSpeed = settings.Industry.ResearchSpeed,
            CopyingSpeed = settings.Industry.CopyingSpeed,
            InventionSpeed = settings.Industry.InventionSpeed,
            ReprocessingEfficiency = settings.Industry.ReprocessingEfficiency,
            StructureAnchoringSpeed = settings.Structures.AnchoringSpeed,
            StructureOnliningSpeed = settings.Structures.OnliningSpeed,
            StructureUnanchoringSpeed = settings.Structures.UnanchoringSpeed,
            ServiceCosts = settings.Economy.ChargeServiceCosts,
            ProgressiveMarkets = settings.Economy.ProgressiveMarkets ?? !settings.DevelopmentSupply.TestingMarket,
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
            GlobalProvisioning = settings.Capsuleers.GlobalProvisioning,
            SimulatedPilots = settings.Capsuleers.Enabled,
            SimulatedMiners = settings.Capsuleers.Miners,
            TrafficMultiplier = settings.Capsuleers.TrafficMultiplier,
            GateVisitors = settings.Capsuleers.GateVisitors,
            CheckpointSeconds = settings.Simulation.CheckpointSeconds,
            MarketPollMilliseconds = settings.Simulation.MarketPollMilliseconds,
            ResourceCheckSeconds = settings.Simulation.ResourceCheckSeconds,
            AutopilotGateLandingMeters = settings.Navigation.AutopilotGateLandingMeters,
        };

    internal WorldSettings Build() =>
        new()
        {
            Economy = new(ServiceCosts, ManufacturingIndex, ProgressiveMarkets),
            DevelopmentSupply = new(
                TestingMarket,
                TestingGrants,
                InstitutionalStock,
                TestingStock,
                Cents(TestingPriceIsk),
                Cents(GrantIsk)
            ),
            Resources = new(RenewBelts, RenewalHourUtc, OreYieldMultiplier, IceYieldMultiplier, GasYieldMultiplier),
            Rewards = new(BountyMultiplier, MissionIskMultiplier, MissionLpMultiplier, MissionStandingMultiplier),
            Industry = new(ManufacturingSpeed, ResearchSpeed, CopyingSpeed, InventionSpeed, ReprocessingEfficiency),
            Structures = new(StructureAnchoringSpeed, StructureOnliningSpeed, StructureUnanchoringSpeed),
            NpcPopulation = new(Pirates, Guards, Convoys, Mining, MajorThreatAnomalies),
            Progression = new(StartingSkills, KillSkillPointMultiplier, MiningSkillPointMultiplier),
            Capsuleers = new(GlobalProvisioning, SimulatedPilots, SimulatedMiners, TrafficMultiplier, GateVisitors),
            Simulation = new(CheckpointSeconds, MarketPollMilliseconds, ResourceCheckSeconds),
            Navigation = new(AutopilotGateLandingMeters),
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
