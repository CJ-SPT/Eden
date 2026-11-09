using System.Collections.Immutable;
using System.Linq.Expressions;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Server.Administration;
using Eden.Server.Administration.Components;
using Eden.World.Catalog;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eden.Protocol.Tests.Administration;

public sealed class SkillEditorUsabilityTests
{
    [Fact]
    public void SearchCombinesWordsRanksNamesBeforeDescriptionsAndFindsGameplayAliases()
    {
        var options = Enumerable
            .Range(1, 100)
            .Select(id => new SkillEditorOption(id, $"Unrelated {id}", "A mining yield description"))
            .Concat([new(500, "Mining yield"), new(501, "Ore mining yield"), new(502, "Mining yield", Available: false)])
            .ToArray();
        var matches = SkillChoiceSearch.Find(options, "  MINING - yield  ", null);
        Assert.Equal(30, matches.Count);
        Assert.Equal(500, matches[0].Id);
        Assert.Equal(501, matches[1].Id);
        Assert.Equal(500, SkillChoiceSearch.Find(options, "yield mining", null)[0].Id);
        Assert.Equal(500, SkillChoiceSearch.Find(options, "500", null)[0].Id);
        Assert.DoesNotContain(matches, option => option.Id == 502);
        Assert.DoesNotContain(SkillChoiceSearch.Find(options, "mining yield", 500), option => option.Id == 500);
        Assert.Empty(SkillChoiceSearch.Find(options, "mining impossible", null));
        Assert.Equal(102, SkillChoiceSearch.Find(options, "mining yield", null, resultLimit: null).Count);

        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        PassiveCatalogFixture.Load(database.CatalogPath);
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        var choices = new SkillEditorStore(database, TimeProvider.System).Choices();
        using var warm = SessionPerformance.Begin("gameplay-search", _ => { }, force: true)!;
        var mining = SkillChoiceSearch.Find(choices.Effects, "mining yield", null);
        Assert.Contains(mining, option => option.Id == 389);
        Assert.Contains(mining, option => option.Id == 391);
        Assert.DoesNotContain(mining, option => option.Id == 1882);
        Assert.Contains(SkillChoiceSearch.Find(choices.Attributes, "mining yield", null), option => option.Id == 434);
        Assert.Equal(0, warm.Commands);
        Assert.Equal(0, warm.Connections);
    }

    [Fact]
    public void ColdSkillLoadingUsesBoundedBatchReadsAndWarmNavigationDoesNoCatalogWork()
    {
        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        using (CatalogDbContext fixture = new(database.OriginalCatalogPath, writable: true))
        {
            int group = fixture.Set<ItemGroup>().First(group => group.CategoryId == 16).Id;
            for (int index = 0; index < 200; index++)
            {
                int id = 800000 + index;
                fixture.Add(
                    new ItemType
                    {
                        Id = id,
                        GroupId = group,
                        Name = $"Batch skill {index}",
                    }
                );
                fixture.Add(
                    new TypeAttribute
                    {
                        ItemTypeId = id,
                        AttributeId = (int)DogmaAttributeId.SkillTimeConstant,
                        Value = 3,
                    }
                );
            }
            fixture.SaveChanges();
        }
        SkillEditorStore editor = new(database, TimeProvider.System);
        using (var cold = SessionPerformance.Begin("cold-skill-editor", _ => { }, force: true)!)
        {
            var snapshot = editor.Read();
            Assert.Equal(200, snapshot.Skills.Count(entry => entry.Definition.Name.StartsWith("Batch skill", StringComparison.Ordinal)));
            Assert.All(
                snapshot.Skills.Where(entry => entry.Definition.TypeId is >= 800000 and < 800200),
                entry => Assert.Equal(3, entry.Definition.Rank)
            );
            Assert.InRange(cold.Commands, 1, 20);
            Assert.InRange(cold.Connections, 1, 5);
            Assert.Equal(0, cold.FittingCalculations);
        }
        var choices = editor.Choices();
        var catalog = CatalogProvider.Open(database.OriginalCatalogPath);
        long loads = catalog.Statistics.Loads;
        using var warm = SessionPerformance.Begin("warm-skill-editor", _ => { }, force: true)!;
        for (int navigation = 0; navigation < 3; navigation++)
        {
            Assert.Same(choices, editor.Choices());
            Assert.Same(choices.Attributes, editor.Attributes());
            var snapshot = editor.Read();
            Assert.Empty(editor.History(snapshot.Skills[0].Definition.TypeId));
        }
        Assert.Equal(loads, catalog.Statistics.Loads);
        Assert.Equal(0, warm.Commands);
        Assert.Equal(0, warm.Connections);
        Assert.Equal(0, warm.FittingCalculations);
    }

    [Fact]
    public void ChoicesExplainNamesUnitsAndMarketPathsAndShareEffectValidation()
    {
        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        using (CatalogDbContext fixture = new(database.OriginalCatalogPath, writable: true))
        {
            fixture.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                ('client/localization_fsd_en-us','7001','{{"text":"Velocity bonus"}}'),
                ('client/localization_fsd_en-us','7002','{{"text":"Bonus per skill level"}}'),
                ('client/localization_fsd_en-us','7003','{{"text":"Percent"}}'),
                ('client/localization_fsd_en-us','7004','{{"text":"Stored as a whole percentage"}}'),
                ('client/localization_fsd_en-us','7005','{{"text":"Skills"}}'),
                ('client/localization_fsd_en-us','7006','{{"text":"Navigation"}}'),
                ('client/dogmaattributes','20','{{"name":"speedFactor","displayNameID":7001,"tooltipDescriptionID":7002,"unitID":108}}'),
                ('client/dogmaunits','108','{{"name":"percent","displayNameID":7003,"descriptionID":7004}}'),
                ('client/dogmaeffects','132','{{"effectName":"skillEffect","effectCategory":0}}'),
                ('client/dogmaeffects','900001','{{"effectName":"skillVelocityBonus","effectCategory":0,
                    "modifierInfo":[{{"domain":"shipID","func":"ItemModifier","modifiedAttributeID":37,
                        "modifyingAttributeID":20,"operation":6}}]}}'),
                ('client/dogmaeffects','900002','{{"effectName":"activeBonus","effectCategory":1}}'),
                ('client/iconids','900001','{{"iconFile":"res:/ui/skill.png"}}'),
                ('client/marketgroups','900001','{{"nameID":7005}}'),
                ('client/marketgroups','900002','{{"nameID":7006,"parentGroupID":900001}}');
                """
            );
        }
        SkillEditorStore editor = new(database, TimeProvider.System);
        var choices = editor.Choices();
        var attribute = Assert.Single(choices.Attributes, option => option.Id == 20);
        Assert.Equal("Velocity bonus", attribute.Name);
        Assert.Contains("Bonus per skill level", attribute.Description);
        Assert.Contains("Percent", attribute.Description);
        Assert.Contains("whole percentage", attribute.Description);
        Assert.DoesNotContain(choices.Attributes, option => option.Id == (int)DogmaAttributeId.PrimaryAttribute);
        Assert.Equal("Skills / Navigation", Assert.Single(choices.MarketGroups, option => option.Id == 900002).Name);
        Assert.Contains(choices.Icons, option => option.Id == 900001 && option.Name.Contains("skill.png", StringComparison.Ordinal));
        Assert.True(Assert.Single(choices.Effects, option => option.Id == 900001).Available);
        Assert.Contains("Velocity bonus", Assert.Single(choices.Effects, option => option.Id == 900001).Description);
        Assert.False(Assert.Single(choices.Effects, option => option.Id == 900002).Available);
        var original = editor.Read().Skills[0].Definition;
        var draft = original with
        {
            TypeId = 0,
            Name = "Named bonus",
            IconId = 900001,
            MarketGroupId = 900002,
            Prerequisites = [],
            Attributes = [new(20, 5)],
            Effects = [new(900001, true)],
        };
        var saved = editor.Create(editor.Read().Generation, draft, "Admin");
        Assert.Equal(draft.Effects.ToArray(), saved.Definition.Effects.ToArray());
        Assert.Throws<InvalidDataException>(() =>
            editor.Create(editor.Read().Generation, draft with { Effects = [new(900002, true)] }, "Admin")
        );
    }

    [Fact]
    public async Task LargeNamedPickersKeepCurrentValuesVisibleWithBoundedEscapedMarkup()
    {
        var options = Enumerable.Range(1, 10000).Select(id => new SkillEditorOption(id, $"Skill {id}", "Find this bonus")).ToArray();
        string longName = "Long effect " + new string('x', 200) + " unique ending";
        options[0] = options[0] with { Name = longName };
        Assert.Equal(30, SkillChoiceSearch.Find(options, "", null).Count);
        Assert.Equal(30, SkillChoiceSearch.Find(options, "bonus", null).Count);
        Assert.DoesNotContain(SkillChoiceSearch.Find(options, "", 1), option => option.Id == 1);
        Assert.Empty(SkillChoiceSearch.Find([new(1, "Unsupported", Available: false)], "", null));
        Assert.Equal(9999, Assert.Single(SkillChoiceSearch.Find(options, "9999", null)).Id);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        int? selected = 10001;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SkillChoice<int?>>(
                ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        [nameof(SkillChoice<int?>.Options)] = options
                            .Append(new(10001, "<Retained bonus>", "<safe description>", false))
                            .ToArray(),
                        [nameof(SkillChoice<int?>.Label)] = "Bonus effect",
                        [nameof(SkillChoice<int?>.Value)] = selected,
                        [nameof(SkillChoice<int?>.ValueExpression)] = (Expression<Func<int?>>)(() => selected),
                    }
                )
            );
            string html = component.ToHtmlString();
            Assert.Equal(32, html.Split("<option", StringSplitOptions.None).Length - 1);
            Assert.Contains("&lt;Retained bonus&gt; (retained)", html);
            Assert.Contains("&lt;safe description&gt;", html);
            Assert.Contains("selected", html);
            Assert.Contains("Search names or descriptions", html);
            Assert.DoesNotContain("Skill 9998", html);
            string decoded = System.Net.WebUtility.HtmlDecode(html);
            Assert.Contains($"{longName[..68]}… · 1</option>", decoded);
            Assert.DoesNotContain($">{longName}</option>", decoded);
            Assert.Contains($"title=\"{longName}\"", decoded);
        });
    }

    [Fact]
    public async Task NamedSelectionAndClearingNotifyTheFormAndPreserveUnrelatedFields()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        SkillForm form = new()
        {
            IconId = 7,
            Name = "Draft name",
            MarketGroupId = 42,
        };
        EditContext context = new(form);
        TestableChoice? choice = null;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ChoiceHost>(
                ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        [nameof(ChoiceHost.Form)] = form,
                        [nameof(ChoiceHost.Context)] = context,
                        [nameof(ChoiceHost.Capture)] = (Action<TestableChoice>)(value => choice = value),
                    }
                )
            );
            Assert.False(context.IsModified());
            choice!.SearchFor("new icon");
            Assert.Contains("Matching choices", component.ToHtmlString());
            Assert.False(context.IsModified());
            choice!.Pick(new(8, "New icon"));
            Assert.Equal(8, form.IconId);
            Assert.True(context.IsModified(new FieldIdentifier(form, nameof(form.IconId))));
            context.MarkAsUnmodified();
            choice.Select("");
            Assert.Null(form.IconId);
            Assert.True(context.IsModified(new FieldIdentifier(form, nameof(form.IconId))));
            Assert.Equal("Draft name", form.Name);
            Assert.Equal(42, form.MarketGroupId);
        });
    }

    public sealed class TestableChoice : SkillChoice<int?>
    {
        public void Select(string value) => CurrentValueAsString = value;

        public void Pick(SkillEditorOption option) => ChooseMatch(option);

        public void SearchFor(string search)
        {
            SearchChanged(new() { Value = search });
            StateHasChanged();
        }
    }

    public sealed class ChoiceHost : ComponentBase
    {
        [Parameter]
        public object Form { get; set; } = null!;

        [Parameter]
        public EditContext Context { get; set; } = null!;

        [Parameter]
        public Action<TestableChoice> Capture { get; set; } = null!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var form = (SkillForm)Form;
            builder.OpenComponent<CascadingValue<EditContext>>(0);
            builder.AddAttribute(1, "Value", Context);
            builder.AddAttribute(
                2,
                "ChildContent",
                (RenderFragment)(
                    child =>
                    {
                        child.OpenComponent<TestableChoice>(0);
                        child.AddAttribute(1, "Options", new SkillEditorOption[] { new(7, "Original icon"), new(8, "New icon") });
                        child.AddAttribute(2, "Label", "Icon");
                        child.AddAttribute(3, "Value", form.IconId);
                        child.AddAttribute(4, "ValueExpression", (Expression<Func<int?>>)(() => form.IconId));
                        child.AddAttribute(5, "ValueChanged", EventCallback.Factory.Create<int?>(this, value => form.IconId = value));
                        child.AddComponentReferenceCapture(6, component => Capture((TestableChoice)component));
                        child.CloseComponent();
                    }
                )
            );
            builder.CloseComponent();
        }
    }
}
