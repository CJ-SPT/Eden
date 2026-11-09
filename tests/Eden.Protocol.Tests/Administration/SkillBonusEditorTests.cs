using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Server.Administration;
using Eden.Server.Administration.Components;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eden.Protocol.Tests.Administration;

public sealed class SkillBonusEditorTests
{
    [Theory]
    [InlineData(25)]
    [InlineData(12.5)]
    [InlineData(0)]
    public void MiningRecipePersistsAndAppliesConfiguredPercentPerLevelToMatchingModulesOnly(double percent)
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        SkillEditorStore editor = new(database, TimeProvider.System);
        var options = editor.Choices();
        Assert.True(SkillBonusEditing.SupportsMining(options));
        var form = SkillForm.From(
            editor.Read().Skills.First().Definition with
            {
                TypeId = 0,
                Name = "Custom ore yield",
                IconId = null,
                MarketGroupId = null,
                Attributes = [],
                Effects = [],
                Prerequisites = [],
            }
        );
        SkillBonusEditing.AddMining(form, options);
        SkillBonusEditing.AddMining(form, options);
        Assert.Equal(2, form.Effects.Count);
        Assert.Equal(25, Assert.Single(form.Attributes).Value);
        form.Attributes[0].Value = percent;
        var created = editor.Create(editor.Read().Generation, form.Build(), "Admin");
        SkillEditorStore reopened = new(new(fixture.Path), TimeProvider.System);
        var saved = reopened.Read().Skills.Single(entry => entry.Definition.TypeId == created.Definition.TypeId);
        Assert.True(SkillEditingRules.SameDefinition(created.Definition, saved.Definition));
        string package = Path.Combine(fixture.Path + ".skill-packages", new string('a', 64));
        Directory.CreateDirectory(package);
        string projected = Path.Combine(package, "catalog.sqlite");
        SkillCatalogProjection.Create(database.OriginalCatalogPath, projected, reopened.Read());
        File.WriteAllText(
            Path.Combine(package, "skills.json"),
            JsonSerializer.Serialize(new[] { new PublishedSkillDefinition(saved.Definition, true, false) }, SkillEditorStore.Json)
        );
        const long pilotId = 90000001;
        long moduleId;
        long otherModuleId;
        using (WorldDbContext world = new(fixture.Path))
        {
            long ship = world.Pilots.Single(pilot => pilot.Id == pilotId).ActiveShipId;
            moduleId = InventoryPersistence.NextId(world);
            otherModuleId = moduleId + 1;
            world.AddRange(
                new OwnedItem
                {
                    Id = moduleId,
                    OwnerId = pilotId,
                    LocationId = ship,
                    TypeId = 483,
                    FlagId = (int)InventoryFlag.HighSlot0,
                    Quantity = 1,
                    Singleton = true,
                    Online = true,
                },
                new OwnedItem
                {
                    Id = otherModuleId,
                    OwnerId = pilotId,
                    LocationId = ship,
                    TypeId = 3651,
                    FlagId = (int)InventoryFlag.HighSlot1,
                    Quantity = 1,
                    Singleton = true,
                    Online = true,
                }
            );
            world.Add(new PilotSkill { PilotId = pilotId, TypeId = saved.Definition.TypeId });
            world.SaveChanges();
        }
        // Keep the same mining group but remove the required-skill selector from
        // the second module, proving the effect does not indiscriminately apply.
        using (CatalogDbContext catalog = new(projected, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("DELETE FROM TypeAttribute WHERE ItemTypeId=3651 AND AttributeId=182");
        }
        double Read(long id)
        {
            using WorldDbContext world = new(fixture.Path);
            using CatalogDbContext catalog = new(projected);
            long ship = world.Pilots.Single(pilot => pilot.Id == pilotId).ActiveShipId;
            var module = PilotInventoryStore.List(world, catalog, pilotId, ship).Single(item => item.Id == id);
            return PilotMiningStore.ModuleAttributes(world, catalog, pilotId, module)[DogmaAttributeId.MiningAmount];
        }
        double baseline = Read(moduleId);
        double otherBaseline = Read(otherModuleId);
        Assert.True(baseline > 0);
        foreach (int level in new[] { 1, 3, 5, 0 })
        {
            using (WorldDbContext world = new(fixture.Path))
            {
                world.Set<PilotSkill>().Find(pilotId, saved.Definition.TypeId)!.Points = TrainedSkill.PointsForLevel(
                    saved.Definition.Rank,
                    level
                );
                world.SaveChanges();
            }
            Assert.Equal(baseline * (1 + percent * level / 100), Read(moduleId), 6);
            Assert.Equal(otherBaseline, Read(otherModuleId), 6);
        }
    }

    [Fact]
    public void EffectInputsShareValuesPreserveEditsAndExplainOperations()
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        var options = new SkillEditorStore(database, TimeProvider.System).Choices();
        SkillForm form = new() { Name = "Keep my draft", Effects = [new() { EffectId = 389 }, new() { EffectId = 391 }] };
        SkillBonusEditing.EnsureInputs(form, options);
        var input = Assert.Single(form.Attributes);
        Assert.Equal(434, input.AttributeId);
        input.Value = 12.5;
        SkillBonusEditing.EnsureInputs(form, options);
        SkillBonusEditing.AddMining(form, options);
        Assert.Equal(12.5, Assert.Single(form.Attributes).Value);
        var differentTarget = options with
        {
            Effects = options
                .Effects.Select(effect =>
                    effect.Id == 391 ? effect with { Modifiers = [effect.Modifiers.Single() with { RequiredSkillId = 1 }] } : effect
                )
                .ToArray(),
        };
        Assert.False(SkillBonusEditing.SupportsMining(differentTarget));
        Assert.Equal("Keep my draft", form.Name);
        Assert.Contains("trained skill level", options.Effects.Single(effect => effect.Id == 389).Modifiers.Single().Description);
        Assert.Contains("requiring Mining", options.Effects.Single(effect => effect.Id == 391).Modifiers.Single().Description);
        Assert.Contains("25 means +25%", options.Effects.Single(effect => effect.Id == 391).Modifiers.Single().Description);
        var unavailable = options with { Effects = [] };
        Assert.False(SkillBonusEditing.SupportsMining(unavailable));
        Assert.Throws<InvalidOperationException>(() => SkillBonusEditing.AddMining(form, unavailable));
        Assert.Equal(12.5, Assert.Single(form.Attributes).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BonusPanelShowsEditableValuesScopeAndLevelPreview(bool editable)
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        var options = new SkillEditorStore(database, TimeProvider.System).Choices();
        SkillForm form = new();
        SkillBonusEditing.AddMining(form, options);
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
                    }
                )
            );
            string html = component.ToHtmlString();
            Assert.Contains("An <strong>attribute</strong> stores a number", html);
            Assert.Contains("% per level", html);
            Assert.Contains("2.25", html);
            Assert.Contains("Ice harvesters, gas harvesters and mining drones", html);
            Assert.Contains("value=\"25\"", html);
            Assert.Contains("Catalog details", html);
            Assert.Equal(!editable, html.Contains("bonuses are read-only", StringComparison.Ordinal));
            Assert.Equal(!editable, html.Contains("<fieldset disabled", StringComparison.Ordinal));
        });
    }

    public sealed class BonusHost : ComponentBase
    {
        [Parameter]
        public SkillForm Form { get; set; } = null!;

        [Parameter]
        public SkillEditorChoices Options { get; set; } = null!;

        [Parameter]
        public bool Editable { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddAttribute(1, "Model", Form);
            builder.AddAttribute(
                2,
                "ChildContent",
                (RenderFragment<EditContext>)(
                    _ =>
                        child =>
                        {
                            child.OpenComponent<SkillBonuses>(0);
                            child.AddAttribute(1, "Form", Form);
                            child.AddAttribute(2, "Options", Options);
                            child.AddAttribute(3, "Editable", Editable);
                            child.CloseComponent();
                        }
                )
            );
            builder.CloseComponent();
        }
    }
}
