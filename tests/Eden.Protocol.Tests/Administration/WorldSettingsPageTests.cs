using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Mining;
using Eden.Server.Administration;
using Eden.Server.Administration.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eden.Protocol.Tests.Administration;

public sealed class WorldSettingsPageTests
{
    [Fact]
    public void DecimalRangesAcceptFractionalLimitsInFrenchServerCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            // Read fresh attributes because DataAnnotations caches the limits after first use.
            var ranges = typeof(SettingsForm)
                .GetProperties()
                .Select(property => property.GetCustomAttribute<RangeAttribute>())
                .OfType<RangeAttribute>()
                .Where(range => range.OperandType == typeof(decimal));
            foreach (var range in ranges)
            {
                decimal minimum = decimal.Parse((string)range.Minimum, CultureInfo.InvariantCulture);
                decimal maximum = decimal.Parse((string)range.Maximum, CultureInfo.InvariantCulture);
                Assert.True(range.IsValid(minimum));
                Assert.True(range.IsValid(maximum));
                Assert.False(range.IsValid(minimum - .01m));
                Assert.False(range.IsValid(maximum + .01m));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void CustomsTaxControlAcceptsZeroAndFractionalPercentagesAndReportsRevisionChanges()
    {
        var form = SettingsForm.From(new());
        Assert.Equal(17, form.PlanetaryCustomsTaxPercent);
        foreach (decimal value in new[] { 0m, 2.5m, 17m, 100m })
        {
            form.PlanetaryCustomsTaxPercent = value;
            Assert.True(Validator.TryValidateObject(form, new(form), [], true));
            Assert.Equal(value, SettingsForm.From(form.Build()).PlanetaryCustomsTaxPercent);
        }
        Assert.Contains(
            ConfigurationDisplay.Compare(new(), new() { Settings = form.Build() }),
            change => change.Field == "Customs tax (%)" && change.Before == "17" && change.After == "100"
        );
        foreach (decimal value in new[] { -.1m, 100.1m })
        {
            form.PlanetaryCustomsTaxPercent = value;
            List<ValidationResult> errors = [];
            Assert.False(Validator.TryValidateObject(form, new(form), errors, true));
            Assert.Contains(errors, error => error.MemberNames.Contains(nameof(form.PlanetaryCustomsTaxPercent)));
        }
    }

    [Fact]
    public void PlanetaryStorageAndSpeedControlsValidateRoundTripAndReportRevisionChanges()
    {
        var form = SettingsForm.From(new());
        Assert.Equal(1, form.PlanetaryStorageCapacityMultiplier);
        Assert.Equal(1, form.PlanetaryFactorySpeedMultiplier);
        foreach (decimal value in new[] { .1m, 1m, 100m })
        {
            form.PlanetaryStorageCapacityMultiplier = value;
            form.PlanetaryFactorySpeedMultiplier = value;
            Assert.True(Validator.TryValidateObject(form, new(form), [], true));
            var saved = SettingsForm.From(form.Build());
            Assert.Equal(value, saved.PlanetaryStorageCapacityMultiplier);
            Assert.Equal(value, saved.PlanetaryFactorySpeedMultiplier);
        }
        var changes = ConfigurationDisplay.Compare(new(), new() { Settings = form.Build() });
        Assert.Contains(changes, change => change.Field == "Planetary storage capacity");
        Assert.Contains(changes, change => change.Field == "Planetary factory speed");
        foreach (decimal value in new[] { 0m, -.1m, 101m })
        {
            form.PlanetaryStorageCapacityMultiplier = value;
            form.PlanetaryFactorySpeedMultiplier = value;
            List<ValidationResult> errors = [];
            Assert.False(Validator.TryValidateObject(form, new(form), errors, true));
            Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.PlanetaryStorageCapacityMultiplier)));
            Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.PlanetaryFactorySpeedMultiplier)));
        }
    }

    [Fact]
    public void MaximumPlanetControlValidatesAndAppearsInRevisionChanges()
    {
        var form = SettingsForm.From(new());
        Assert.Equal(0, form.PlanetaryMaximumPlanets);
        foreach (int maximum in new[] { 1, 8, 100 })
        {
            form.PlanetaryMaximumPlanets = maximum;
            Assert.True(Validator.TryValidateObject(form, new(form), [], true));
            Assert.Equal(maximum, SettingsForm.From(form.Build()).PlanetaryMaximumPlanets);
        }
        Assert.Contains(
            ConfigurationDisplay.Compare(new(), new() { Settings = form.Build() }),
            change => change.Field == "Maximum planets (0 = skill-based)" && change.Before == "0" && change.After == "100"
        );
        foreach (int maximum in new[] { -1, 101 })
        {
            form.PlanetaryMaximumPlanets = maximum;
            List<ValidationResult> errors = [];
            Assert.False(Validator.TryValidateObject(form, new(form), errors, true));
            Assert.Contains(errors, error => error.MemberNames.Contains(nameof(form.PlanetaryMaximumPlanets)));
        }
    }

    [Fact]
    public void CustomPolicyControlsRoundTripAndReportEveryDisabledOption()
    {
        var form = SettingsForm.From(new());
        Assert.True(form.EmpireLpDatacores);
        Assert.True(form.SimulatedMarketHistory);
        Assert.True(form.RegionalHubSupply);
        Assert.True(form.BeginnerMarketSupply);
        Assert.True(form.StartingEquipment);
        Assert.True(form.SimulatedPilots);
        form.EmpireLpDatacores = false;
        form.SimulatedMarketHistory = false;
        form.RegionalHubSupply = false;
        form.BeginnerMarketSupply = false;
        form.StartingEquipment = false;
        form.SimulatedPilots = false;
        var settings = form.Build();
        Assert.Equal(settings, SettingsForm.From(settings).Build());
        var changes = ConfigurationDisplay.Compare(new(), new() { Settings = settings });
        foreach (
            string label in new[]
            {
                "Empire LP-store datacores",
                "Simulated market history",
                "Regional hub supply",
                "Beginner market supply",
                "Starting equipment for new pilots",
                "Player bots",
            }
        )
        {
            Assert.Contains(changes, change => change.Field == label && change.Before == "On" && change.After == "Off");
        }
        SettingsWorkspace workspace = new();
        workspace.Load(new() { Settings = settings });
        workspace.Form.BeginnerMarketSupply = true;
        Assert.True(workspace.Unsaved);
        workspace.Discard();
        Assert.False(workspace.Form.BeginnerMarketSupply);
    }

    [Fact]
    public void AutomaticSignatureControlRoundTripsAndAppearsInRevisionChanges()
    {
        Assert.False(SettingsForm.From(new()).AlwaysResolvedSignatures);
        var form = SettingsForm.From(new() { Exploration = new(true) });
        Assert.True(form.AlwaysResolvedSignatures);
        Assert.True(form.Build().Exploration.AlwaysResolvedSignatures);
        Assert.Contains(
            ConfigurationDisplay.Compare(new(), new() { Settings = form.Build() }),
            change => change.Field == "Always resolve cosmic signatures" && change.Before == "Off" && change.After == "On"
        );
        SettingsWorkspace workspace = new();
        workspace.Load(new());
        workspace.Form.AlwaysResolvedSignatures = true;
        Assert.True(workspace.Unsaved);
        workspace.Discard();
        Assert.False(workspace.Form.AlwaysResolvedSignatures);
    }

    [Fact]
    public void TravelControlsRoundTripValidateAndAppearInRevisionChanges()
    {
        NavigationPolicy policy = new(0, 2, 1.25m);
        var form = SettingsForm.From(new() { Navigation = policy });
        Assert.Equal(policy, form.Build().Navigation);
        Assert.Equal(new NavigationPolicy(), SettingsForm.From(new()).Build().Navigation);
        form.WarpSpeedMultiplier = 0;
        form.AlignSpeedMultiplier = 100.01m;
        List<ValidationResult> errors = [];
        Assert.False(Validator.TryValidateObject(form, new(form), errors, true));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(form.WarpSpeedMultiplier)));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(form.AlignSpeedMultiplier)));
        form.WarpSpeedMultiplier = 2;
        form.AlignSpeedMultiplier = 1.25m;
        Assert.True(Validator.TryValidateObject(form, new(form), [], true));
        var changes = ConfigurationDisplay.Compare(new(), new() { Settings = form.Build() });
        Assert.Contains(changes, change => change.Field == "Warp speed" && change.After == "2×");
        Assert.Contains(changes, change => change.Field == "Align speed" && change.After == "1.25×");
        SettingsWorkspace workspace = new();
        workspace.Load(new());
        workspace.Activity = SettingsActivity.Travel;
        workspace.Form.WarpSpeedMultiplier = 2;
        workspace.Activity = SettingsActivity.Advanced;
        Assert.Equal(2, workspace.Current.Settings.Navigation.WarpSpeedMultiplier);
        workspace.Discard();
        Assert.Equal(1, workspace.Current.Settings.Navigation.WarpSpeedMultiplier);
    }

    [Fact]
    public void AutopilotDistanceRoundTripsValidatesAndAppearsInRevisionChanges()
    {
        Assert.Equal(15000, SettingsForm.From(new()).AutopilotGateLandingMeters);
        var form = SettingsForm.From(new() { Navigation = new(0) });
        Assert.Equal(0, form.Build().Navigation.AutopilotGateLandingMeters);
        foreach (int distance in new[] { -1, 100001 })
        {
            form.AutopilotGateLandingMeters = distance;
            List<ValidationResult> errors = [];
            Assert.False(Validator.TryValidateObject(form, new(form), errors, true));
            Assert.Contains(errors, error => error.MemberNames.Contains(nameof(form.AutopilotGateLandingMeters)));
        }
        form.AutopilotGateLandingMeters = 100000;
        Assert.True(Validator.TryValidateObject(form, new(form), [], true));
        Assert.Contains(
            ConfigurationDisplay.Compare(new(), new() { Settings = form.Build() }),
            change => change.Field == "Autopilot gate landing distance (metres)" && change.After == "100000"
        );
    }

    [Fact]
    public void PopulationControlsRoundTripPreserveLegacyPreferencesAndValidateRanges()
    {
        CapsuleerPopulationPolicy policy = new(false, false, false, 2.5m, 9) { Exploration = false, WreckSalvage = false };
        var form = SettingsForm.From(new() { Capsuleers = policy });
        Assert.Equal(policy, form.Build().Capsuleers);
        Assert.Equal(new CapsuleerPopulationPolicy(), SettingsForm.From(new()).Build().Capsuleers);
        form.TrafficMultiplier = -0.1m;
        form.GateVisitors = 17;
        List<ValidationResult> errors = [];
        Assert.False(Validator.TryValidateObject(form, new(form), errors, true));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.TrafficMultiplier)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.GateVisitors)));
        form.TrafficMultiplier = 10;
        form.GateVisitors = 16;
        Assert.True(Validator.TryValidateObject(form, new(form), [], true));
        var changes = ConfigurationDisplay.Compare(new(), new() { Settings = form.Build() });
        Assert.Contains(changes, c => c.Field == "Player bots" && c.After == "Off");
        Assert.Contains(changes, c => c.Field == "Station traffic density" && c.After == "10×");
        Assert.Contains(changes, c => c.Field == "Visitors per watched gate" && c.After == "16");
        Assert.Contains(changes, c => c.Field == "Explore data and relic sites" && c.Before == "On" && c.After == "Off");
        Assert.Contains(changes, c => c.Field == "Salvage NPC wrecks" && c.Before == "On" && c.After == "Off");
    }

    [Fact]
    public void ActivitySwitchesRetainEditsAndDiscardRestoresTheSavedPendingDraft()
    {
        WorldConfigurationDraft pending = new()
        {
            Settings = new() { Resources = new(OreYieldMultiplier: 3), Rewards = new(BountyMultiplier: 4) },
        };
        SettingsWorkspace workspace = new();
        workspace.Load(pending);
        Assert.False(workspace.Unsaved);
        workspace.Form.OreYieldMultiplier = 2;
        workspace.Activity = SettingsActivity.Rewards;
        workspace.Form.BountyMultiplier = 0;
        workspace.Activity = SettingsActivity.Gathering;
        Assert.Equal(2, workspace.Form.OreYieldMultiplier);
        Assert.True(workspace.Unsaved);
        Assert.Equal(2, workspace.Changes.Count);
        workspace.Discard();
        Assert.Equal(3, workspace.Form.OreYieldMultiplier);
        Assert.Equal(4, workspace.Form.BountyMultiplier);
        Assert.False(workspace.Unsaved);
        workspace.RestoreOriginal(new());
        Assert.Equal(1, workspace.Form.OreYieldMultiplier);
        Assert.True(workspace.Unsaved);
        workspace.Load(workspace.Current);
        Assert.False(workspace.Unsaved);
        Assert.Equal(SettingsActivity.Gathering, workspace.Activity);
    }

    [Fact]
    public void FormRoundTripsAllRatesAndReportsErrorsForHiddenActivityFields()
    {
        WorldSettings settings = new()
        {
            Resources = new(OreYieldMultiplier: 2, IceYieldMultiplier: 3, GasYieldMultiplier: 4),
            MiningHolds = new(1.5m, .5m, 2),
            Rewards = new(5, 6, 7, 8, BountyTickSeconds: 300),
            Progression = new(
                KillSkillPointMultiplier: 9,
                MiningSkillPointMultiplier: 2.5m,
                KillSkillPointPercent: 1.25m,
                MiningIskPerSkillPoint: 125.5m,
                TrainingSpeedMultiplier: 2.5m
            ),
            Industry = new(10, 11, 12, 13, 14),
            Structures = new(2, 3, 4, MinimumCorporationAgeDays: 0),
        };
        var form = SettingsForm.From(settings);
        var result = form.Build();
        Assert.Equal(settings.Resources, result.Resources);
        Assert.Equal(settings.MiningHolds, result.MiningHolds);
        Assert.Equal(settings.Rewards, result.Rewards);
        Assert.Equal(settings.Industry, result.Industry);
        Assert.Equal(settings.Progression, result.Progression);
        Assert.Equal(settings.Structures, result.Structures);
        form.OreYieldMultiplier = 0;
        form.IceHoldMultiplier = 0;
        form.BountyMultiplier = -1;
        form.BountyTickSeconds = -1;
        form.MiningSkillPointMultiplier = 101;
        form.KillSkillPointPercent = 100.01m;
        form.MiningIskPerSkillPoint = 0;
        form.TrainingSpeedMultiplier = 0;
        form.InventionSpeed = 101;
        form.StructureAnchoringSpeed = 0;
        form.StructureOnliningSpeed = 101;
        form.StructureUnanchoringSpeed = -1;
        form.StructureMinimumCorporationAgeDays = -1;
        List<ValidationResult> errors = [];
        Assert.False(Validator.TryValidateObject(form, new(form), errors, true));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.OreYieldMultiplier)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.IceHoldMultiplier)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.BountyMultiplier)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.BountyTickSeconds)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.MiningSkillPointMultiplier)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.KillSkillPointPercent)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.MiningIskPerSkillPoint)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.TrainingSpeedMultiplier)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.InventionSpeed)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.StructureAnchoringSpeed)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.StructureOnliningSpeed)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.StructureUnanchoringSpeed)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.StructureMinimumCorporationAgeDays)));
        form.OreYieldMultiplier = 0.1m;
        form.IceHoldMultiplier = .1m;
        form.BountyMultiplier = 0;
        form.BountyTickSeconds = 0;
        form.MiningSkillPointMultiplier = 0;
        form.KillSkillPointPercent = 0;
        form.MiningIskPerSkillPoint = 0.01m;
        form.TrainingSpeedMultiplier = 0.1m;
        form.InventionSpeed = 100;
        form.StructureAnchoringSpeed = .1m;
        form.StructureOnliningSpeed = 100;
        form.StructureUnanchoringSpeed = 1;
        form.StructureMinimumCorporationAgeDays = 0;
        Assert.True(Validator.TryValidateObject(form, new(form), [], true));
        Assert.Equal("2.5×", ConfigurationDisplay.Settings(new(), settings, null).Single(r => r.Label == "Mining skill points").Active);
        Assert.Contains(
            ConfigurationDisplay.Settings(new(), new(), settings),
            row => row.Label == "Skill training speed" && row.Original == "1×" && row.Active == "1×" && row.Pending == "2.5×"
        );
        Assert.Contains(
            ConfigurationDisplay.Compare(new(), new() { Settings = settings }),
            change => change.Field == "Skill training speed" && change.Before == "1×" && change.After == "2.5×"
        );
        Assert.Contains(
            ConfigurationDisplay.Compare(new(), new() { Settings = settings }),
            c => c.Field == "Mining skill points" && c.After == "2.5×"
        );
        Assert.Equal("2×", ConfigurationDisplay.Settings(new(), settings, null).Single(r => r.Label == "Ore mining yield").Active);
        Assert.Contains(
            ConfigurationDisplay.Compare(new(), new() { Settings = settings }),
            c => c.Field == "General mining hold capacity" && c.After == "1.5×"
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiplierControlShowsAccessibleUnitsRangeResetExampleAndValidation(bool excessive)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        var form = SettingsForm.From(new());
        form.OreYieldMultiplier = excessive ? decimal.MaxValue : 2;
        Expression<Func<decimal>> expression = () => form.OreYieldMultiplier;
        EditContext context = new(form);
        ValidationMessageStore validation = new(context);
        validation.Add(FieldIdentifier.Create(expression), "Choose a supported mining multiplier.");
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder => RenderMultiplier(builder, expression, form.OreYieldMultiplier);
            var rendered = await renderer.RenderComponentAsync<CascadingValue<EditContext>>(
                ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        [nameof(CascadingValue<EditContext>.Value)] = context,
                        [nameof(CascadingValue<EditContext>.ChildContent)] = content,
                    }
                )
            );
            string html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Ore mining yield", html);
            Assert.Contains("aria-describedby", html);
            Assert.Contains("0.1–100×", html);
            Assert.Contains("Reset Ore mining yield to 1×", html);
            Assert.Contains(excessive ? "Enter a multiplier within the supported range" : "100 units becomes 200 units", html);
            Assert.Contains("Choose a supported mining multiplier.", html);
        });
    }

    [Theory]
    [InlineData(MiningFamily.Ore, 2000000, 200)]
    [InlineData(MiningFamily.Ice, 4000000, 400)]
    [InlineData(MiningFamily.Gas, 6000000, 600)]
    public void ProgressionPreviewsApplyTheConfiguredBaseRatesAndResourceYield(MiningFamily family, long expectedSp, decimal expectedScale)
    {
        WorldSettings settings = new()
        {
            Resources = new(OreYieldMultiplier: 10, IceYieldMultiplier: 20, GasYieldMultiplier: 30),
            Rewards = new(BountyMultiplier: 10),
            Progression = new(
                KillSkillPointMultiplier: 20,
                MiningSkillPointMultiplier: 20,
                KillSkillPointPercent: 1,
                MiningIskPerSkillPoint: 100
            ),
        };
        Assert.Equal(new ProgressionPreview(10_000_000, 2_000_000, 200), ProgressionPreview.Combat(settings, 1_000_000));
        var mining = ProgressionPreview.Mining(settings, 1_000_000, family);
        Assert.Equal(expectedSp, mining.SkillPoints);
        Assert.Equal(expectedScale, mining.BaseValueMultiplier);
        Assert.Throws<InvalidDataException>(() => ProgressionPreview.Combat(settings, -1));
        Assert.Throws<InvalidDataException>(() => ProgressionPreview.Mining(settings, -1, family));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ProgressionControlsRenderBaseUnitsActiveDraftReadoutsAndInvalidRateGuidance(bool invalid, bool tinyMultiplier)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        var form = SettingsForm.From(
            new()
            {
                Rewards = new(BountyMultiplier: 10),
                Resources = new(OreYieldMultiplier: 10),
                Progression = new(KillSkillPointMultiplier: 20, MiningSkillPointMultiplier: 20),
            }
        );
        if (invalid)
        {
            form.KillSkillPointPercent = decimal.MaxValue;
            form.MiningIskPerSkillPoint = 0;
        }
        if (tinyMultiplier)
        {
            form.MiningSkillPointMultiplier = 0.0000000000000000000000000001m;
            form.MiningIskPerSkillPoint = 1_000_000_000;
        }
        EditContext context = new(form);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent<ProgressionControls>(0);
                builder.AddAttribute(1, nameof(ProgressionControls.Form), form);
                builder.AddAttribute(2, nameof(ProgressionControls.ActiveSettings), new WorldSettings());
                builder.CloseComponent();
            };
            var rendered = await renderer.RenderComponentAsync<CascadingValue<EditContext>>(
                ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        [nameof(CascadingValue<EditContext>.Value)] = context,
                        [nameof(CascadingValue<EditContext>.ChildContent)] = content,
                    }
                )
            );
            string html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Base SP rate (% of gross bounty)", html);
            Assert.Contains("Reference ISK per SP", html);
            Assert.Contains("Reset to 0.25%", html);
            Assert.Contains("Reset to 400 ISK", html);
            Assert.Contains("aria-live=\"polite\"", html);
            Assert.Contains("Active now: 2,500 SP", html);
            Assert.Contains("NPC kill skill points presets", html);
            Assert.Contains("Mining skill points presets", html);
            Assert.DoesNotContain("independently of bounty", html);
            Assert.Contains(invalid ? "Enter supported rates" : "500,000", html);
            if (tinyMultiplier)
            {
                Assert.Contains("0.0000000000000000000000000001 SP per 1,000,000,000 ISK", html);
            }
        });
    }

    private static void RenderMultiplier(RenderTreeBuilder builder, Expression<Func<decimal>> expression, decimal value)
    {
        builder.OpenComponent<MultiplierControl>(0);
        builder.AddAttribute(1, nameof(MultiplierControl.Label), "Ore mining yield");
        builder.AddAttribute(2, nameof(MultiplierControl.Description), "Player extraction.");
        builder.AddAttribute(3, nameof(MultiplierControl.Value), value);
        builder.AddAttribute(4, nameof(MultiplierControl.ValueExpression), expression);
        builder.CloseComponent();
    }
}
