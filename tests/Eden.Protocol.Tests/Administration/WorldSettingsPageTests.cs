using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Net;
using Eden.Gameplay.Configuration;
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
    public void PopulationControlsRoundTripPreserveLegacyPreferencesAndValidateRanges()
    {
        CapsuleerPopulationPolicy policy = new(false, false, false, 2.5m, 9);
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
        Assert.Contains(changes, c => c.Field == "Simulated pilots" && c.After == "Off");
        Assert.Contains(changes, c => c.Field == "Station traffic density" && c.After == "10×");
        Assert.Contains(changes, c => c.Field == "Visitors per watched gate" && c.After == "16");
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
            Rewards = new(5, 6, 7, 8),
            Progression = new(KillSkillPointMultiplier: 9),
            Industry = new(10, 11, 12, 13, 14),
        };
        var form = SettingsForm.From(settings);
        var result = form.Build();
        Assert.Equal(settings.Resources, result.Resources);
        Assert.Equal(settings.Rewards, result.Rewards);
        Assert.Equal(settings.Industry, result.Industry);
        Assert.Equal(settings.Progression, result.Progression);
        form.OreYieldMultiplier = 0;
        form.BountyMultiplier = -1;
        form.InventionSpeed = 101;
        List<ValidationResult> errors = [];
        Assert.False(Validator.TryValidateObject(form, new(form), errors, true));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.OreYieldMultiplier)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.BountyMultiplier)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(form.InventionSpeed)));
        form.OreYieldMultiplier = 0.1m;
        form.BountyMultiplier = 0;
        form.InventionSpeed = 100;
        Assert.True(Validator.TryValidateObject(form, new(form), [], true));
        Assert.Equal("2×", ConfigurationDisplay.Settings(new(), settings, null).Single(r => r.Label == "Ore mining yield").Active);
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
