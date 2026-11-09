using System.Net;
using System.Text.RegularExpressions;
using Eden.Gameplay.Skills;
using Eden.Server.Administration;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Eden.Protocol.Tests.Administration.SkillBonusEditorTests;

namespace Eden.Protocol.Tests.Administration;

public sealed class SkillBonusPresentationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GuidedActionsKeepValuesAndOtherRulesAndSwitchViewsWithoutMutatingDraft(bool editable)
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        var options = new SkillEditorStore(database, TimeProvider.System).Choices();
        SkillForm form = new()
        {
            Name = "Keep this draft",
            Attributes = [new() { AttributeId = 434, Value = 20 }, new() { AttributeId = 20, Value = 3 }],
            Effects = [new() { EffectId = 132, IsDefault = true }],
        };
        var before = form.Build();
        int changes = 0;
        TestableBonuses? panel = null;
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<BonusHost>(
                ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        [nameof(BonusHost.Form)] = form,
                        [nameof(BonusHost.Options)] = options,
                        [nameof(BonusHost.Editable)] = editable,
                        [nameof(BonusHost.Capture)] = (Action<TestableBonuses>)(value => panel = value),
                        [nameof(BonusHost.Changed)] = EventCallback.Factory.Create(this, () => changes++),
                    }
                )
            );
            Assert.Contains("Add bonus", component.ToHtmlString());
            panel!.SwitchView();
            Assert.Contains("Technical rules", component.ToHtmlString());
            Assert.True(SkillEditingRules.SameDefinition(before, form.Build()));
            Assert.Equal(0, changes);
            panel.SwitchView();
            await panel.Add();
            Assert.Equal(editable ? 1 : 0, changes);
            Assert.Equal(editable, SkillBonusEditing.HasMiningBonus(form, options));
            if (editable)
            {
                panel.SwitchView();
                string technical = WebUtility.HtmlDecode(component.ToHtmlString());
                Assert.Contains("Effect rule", technical);
                // One effect input plus the explicit advanced stored-value editor.
                Assert.Equal(2, Regex.Matches(technical, "<input[^>]*type=\"number\"[^>]*value=\"20\"").Count);
                panel.SwitchView();
                Assert.Contains("+100%", WebUtility.HtmlDecode(component.ToHtmlString()));
            }
            await panel.Remove();
            Assert.Equal(editable ? 2 : 0, changes);
            Assert.True(SkillEditingRules.SameDefinition(before, form.Build()));
            Assert.True(Assert.Single(form.Effects).IsDefault);
        });
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("shared")]
    [InlineData("shared-target")]
    [InlineData("duplicate-effect")]
    [InlineData("duplicate-value")]
    public void AmbiguousRulesCannotBeGroupedRemovedOrMutatedByInspection(string variant)
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        var options = new SkillEditorStore(database, TimeProvider.System).Choices();
        SkillForm form = new();
        SkillBonusEditing.AddMining(form, options);
        switch (variant)
        {
            case "unknown":
                form.Effects.Add(new() { EffectId = 999999 });
                break;
            case "shared":
            case "shared-target":
                var shared = options.Effects.Single(effect => effect.Id == 391) with { Id = 999999 };
                shared = shared with { Modifiers = [shared.Modifiers.Single() with { RequiredSkillId = 1 }] };
                if (variant == "shared-target")
                {
                    shared = shared with
                    {
                        Modifiers = [shared.Modifiers.Single() with { SourceAttributeId = 20, TargetAttributeId = 434 }],
                    };
                }
                options = options with { Effects = options.Effects.Append(shared).ToArray() };
                form.Effects.Add(new() { EffectId = shared.Id });
                break;
            case "duplicate-effect":
                form.Effects.Add(new() { EffectId = 389 });
                break;
            case "duplicate-value":
                form.Attributes.Add(new() { AttributeId = 434, Value = 50 });
                break;
        }
        var before = form.Build();
        Assert.False(SkillBonusEditing.CanConfigureMining(form, options));
        Assert.False(SkillBonusEditing.HasMiningBonus(form, options));
        Assert.Throws<InvalidOperationException>(() => SkillBonusEditing.RemoveMining(form, options));
        Assert.Throws<InvalidOperationException>(() => SkillBonusEditing.AddMining(form, options));
        Assert.True(SkillEditingRules.SameDefinition(before, form.Build()));
    }

    [Fact]
    public async Task LayoutFixtureRendersOneBonusInputAndThreeDistinctPrerequisiteRows()
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        var options = new SkillEditorStore(database, TimeProvider.System).Choices() with
        {
            Groups = [new(1, "Mining"), new(2, "Science"), new(3, "Astrogeology")],
        };
        options = options with
        {
            Attributes = options.Attributes.Concat(SkillBonusBuilderTests.Choices().Attributes).DistinctBy(option => option.Id).ToArray(),
            Effects = options
                .Effects.Concat(
                    SkillBonusBuilderTests
                        .Choices()
                        .Effects.Where(option => option.Id is 10 or 11)
                        .Select(option => option with { Id = option.Id + 900000 })
                )
                .ToArray(),
        };
        SkillForm form = new()
        {
            Prerequisites = [new() { TypeId = 1, Level = 5 }, new() { TypeId = 2, Level = 4 }, new() { TypeId = 3, Level = 3 }],
        };
        SkillBonusEditing.AddMining(form, options);
        form.Attributes.Single().Value = 20;
        var before = form.Build();
        TestableBonuses? panel = null;
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<BonusHost>(
                ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        [nameof(BonusHost.Form)] = form,
                        [nameof(BonusHost.Options)] = options,
                        [nameof(BonusHost.Editable)] = true,
                        [nameof(BonusHost.ShowPrerequisites)] = true,
                        [nameof(BonusHost.Capture)] = (Action<TestableBonuses>)(value => panel = value),
                    }
                )
            );
            string html = component.ToHtmlString();
            Assert.Equal(3, html.Split("skill-input-row skill-control-row", StringSplitOptions.None).Length - 1);
            Assert.Equal(5, html.Split("type=\"number\"", StringSplitOptions.None).Length - 1);
            Assert.Contains("value=\"5\"", html);
            Assert.Contains("value=\"4\"", html);
            Assert.Contains("value=\"3\"", html);
            Assert.Contains("+100%", WebUtility.HtmlDecode(html));
            Assert.True(SkillEditingRules.SameDefinition(before, form.Build()));
            if (Environment.GetEnvironmentVariable("EDEN_SKILL_LAYOUT_DIRECTORY") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "skill-editor.html"), html);
                panel!.SwitchView();
                await File.WriteAllTextAsync(Path.Combine(output, "skill-editor-advanced.html"), component.ToHtmlString());
                Assert.True(SkillEditingRules.SameDefinition(before, form.Build()));
                var empty = await renderer.RenderComponentAsync<BonusHost>(
                    ParameterView.FromDictionary(
                        new Dictionary<string, object?>
                        {
                            [nameof(BonusHost.Form)] = new SkillForm(),
                            [nameof(BonusHost.Options)] = options,
                            [nameof(BonusHost.Editable)] = true,
                        }
                    )
                );
                await File.WriteAllTextAsync(Path.Combine(output, "skill-bonus-empty.html"), empty.ToHtmlString());
            }
        });
    }

    [Theory]
    [InlineData(0, null, null, false, "Original skills are active")]
    [InlineData(2L, 1L, null, false, "Saved draft")]
    [InlineData(2L, 1L, 1L, false, "Saved draft")]
    [InlineData(2L, 1L, 2L, false, "restart required")]
    [InlineData(2L, 2L, 2L, false, "active on the server")]
    [InlineData(2L, 2L, 2L, true, "Unsaved changes")]
    public void PublicationStatusDistinguishesDraftsPendingRestartAndActiveChanges(
        long generation,
        long? active,
        long? pending,
        bool dirty,
        string expected
    )
    {
        SkillPublicationStatus status = new(
            active is long activeGeneration ? new("active", activeGeneration, "hash", 1) : null,
            pending is long pendingGeneration ? new("pending", pendingGeneration, "hash", 1) : null,
            null
        );
        Assert.Contains(expected, SkillPublicationState.Describe(generation, status, dirty));
        Assert.Contains("unavailable", SkillPublicationState.Describe(generation, null, false));
    }
}
