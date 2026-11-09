using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol.Skills;
using Eden.Server;
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
    [InlineData(25, 483, false)]
    [InlineData(12.5, 483, false)]
    [InlineData(0, 483, false)]
    [InlineData(20, 482, true)]
    public void MiningRecipePersistsAndAppliesConfiguredPercentPerLevelToMatchingModulesOnly(double percent, int miner, bool repair)
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        if (repair)
        {
            PassiveCatalogFixture.Load(database.CatalogPath);
        }
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
        if (repair)
        {
            form.Attributes.Add(new() { AttributeId = 434, Value = percent });
            form.Effects.Add(new() { EffectId = 1882 });
        }
        SkillBonusEditing.AddMining(form, options);
        SkillBonusEditing.AddMining(form, options);
        Assert.Equal(2, form.Effects.Count);
        Assert.Equal(repair ? percent : 25, Assert.Single(form.Attributes).Value);
        Assert.DoesNotContain(form.Effects, effect => effect.EffectId == 1882);
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
        File.WriteAllText(
            Path.Combine(package, "package.json"),
            JsonSerializer.Serialize(
                new SkillClientPackage(new string('a', 64), reopened.Read().Generation, new string('b', 64), 1),
                SkillEditorStore.Json
            )
        );
        File.WriteAllText(fixture.Path + ".skills-active", new string('a', 64));
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
                    TypeId = miner,
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
        using var services = ServerServices.Build(new WorldDatabase(fixture.Path), TimeProvider.System);
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
            long shipId;
            using (WorldDbContext world = new(fixture.Path))
            {
                shipId = world.Pilots.Single(pilot => pilot.Id == pilotId).ActiveShipId;
            }
            var brain = services.GetRequiredService<PilotBrain>().Read(pilotId, shipId);
            var effects = brain.ShipEffects.Where(effect => effect.Skills.Contains(saved.Definition.TypeId)).ToArray();
            if (level == 0)
            {
                Assert.Empty(effects);
            }
            else
            {
                var effect = Assert.Single(effects);
                Assert.Equal(percent * level, effect.Value);
                Assert.Equal(BrainModifier.LocationRequiredSkill, effect.Modifier);
                Assert.Equal(BrainOperation.PostPercent, effect.Operation);
                Assert.Equal(DogmaAttributeId.MiningAmount, effect.Attribute);
                Assert.Equal(new[] { 3386 }, effect.Extras);
            }
        }
    }

    [Fact]
    public void OnlineModuleBonusIsRejectedAndRecipeRepairsItWithoutChangingOtherValues()
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        PassiveCatalogFixture.Load(database.CatalogPath);
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        SkillEditorStore editor = new(database, TimeProvider.System);
        var options = editor.Choices();
        var online = options.Effects.Single(effect => effect.Id == 1882);
        Assert.Equal(PassiveEffectPhase.Online, online.Phase);
        Assert.False(online.Available);
        Assert.DoesNotContain(SkillChoiceSearch.Find(options.Effects, "mining", null), effect => effect.Id == 1882);
        var snapshot = editor.Read();
        var draft = snapshot.Skills.First().Definition with
        {
            TypeId = 0,
            Name = "Mining Techniques",
            IconId = null,
            MarketGroupId = null,
            Prerequisites = [],
            Attributes = [new(434, 20), new(20, 3)],
            Effects = [new(1882, false)],
        };
        var error = Assert.Throws<InvalidDataException>(() => editor.Create(snapshot.Generation, draft, "Admin"));
        Assert.Contains("online, active and targeted", error.Message);
        Assert.Equal(snapshot.Generation, editor.Read().Generation);
        Assert.DoesNotContain(editor.Read().Skills, entry => entry.UserCreated);
        var form = SkillForm.From(draft);
        form.Effects.Add(new() { EffectId = 391 });
        SkillBonusEditing.AddMining(form, options);
        SkillBonusEditing.AddMining(form, options);
        Assert.Equal(new[] { 389, 391 }, form.Effects.Select(effect => effect.EffectId).Order());
        Assert.Equal(20, form.Attributes.Single(attribute => attribute.AttributeId == 434).Value);
        Assert.Equal(3, form.Attributes.Single(attribute => attribute.AttributeId == 20).Value);
        var created = editor.Create(snapshot.Generation, form.Build(), "Admin");
        Assert.Equal("Mining Techniques", created.Definition.Name);
        Assert.Equal(new[] { 389, 391 }, created.Definition.Effects.Select(effect => effect.EffectId));
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
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task BonusPanelShowsEditableValuesScopeAndLevelPreview(bool editable, bool legacy)
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        if (legacy)
        {
            PassiveCatalogFixture.Load(database.CatalogPath);
        }
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        var options = new SkillEditorStore(database, TimeProvider.System).Choices();
        SkillForm form = new();
        if (legacy)
        {
            form.Attributes.Add(new() { AttributeId = 434, Value = 20 });
            form.Effects.Add(new() { EffectId = 1882 });
        }
        else
        {
            SkillBonusEditing.AddMining(form, options);
        }
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
            Assert.DoesNotContain("Effect rule", html);
            Assert.Contains("What changes", html);
            if (legacy)
            {
                Assert.Contains("existing mining rules need repair", html);
                Assert.Contains("Make it scale with skill level", html);
            }
            else
            {
                Assert.Contains("Amount (%)", html);
                Assert.Contains("data-bonus=\"ore-mining-yield\"", html);
                Assert.Equal(2, html.Split("type=\"number\"", StringSplitOptions.None).Length - 1);
                Assert.Contains("&#x2B;125%", html);

                Assert.Contains("Ice harvesters, gas harvesters and mining drones", html);
                Assert.Contains("value=\"25\"", html);
                Assert.Contains("Technical rules", html);
            }
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

        [Parameter]
        public Action<TestableBonuses>? Capture { get; set; }

        [Parameter]
        public EventCallback Changed { get; set; }

        [Parameter]
        public bool ShowPrerequisites { get; set; }

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
                            child.OpenComponent<TestableBonuses>(0);
                            child.AddAttribute(1, "Form", Form);
                            child.AddAttribute(2, "Options", Options);
                            child.AddAttribute(3, "Editable", Editable);
                            child.AddAttribute(4, "Changed", Changed);
                            child.AddComponentReferenceCapture(5, component => Capture?.Invoke((TestableBonuses)component));
                            child.CloseComponent();
                            if (ShowPrerequisites)
                            {
                                child.OpenComponent<SkillPrerequisites>(6);
                                child.AddAttribute(7, "Form", Form);
                                child.AddAttribute(8, "Options", Options.Groups);
                                child.CloseComponent();
                            }
                        }
                )
            );
            builder.CloseComponent();
        }
    }

    public sealed class TestableBonuses : SkillBonuses
    {
        public new Task Add() => AddMining();

        public Task Remove() => RemoveMining();

        public void ChooseStat(string stat)
        {
            StatChanged(new() { Value = stat });
            StateHasChanged();
        }

        public Task Compose(double amount, bool decrease, bool perLevel)
        {
            Amount = amount;
            Decrease = decrease;
            PerLevel = perLevel;
            return base.Add();
        }

        public void Refresh() => StateHasChanged();

        public void SwitchView()
        {
            ToggleTechnical();
            StateHasChanged();
        }
    }
}
