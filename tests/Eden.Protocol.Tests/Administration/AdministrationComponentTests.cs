using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Configuration;
using Eden.Server.Administration;
using Eden.Server.Administration.Components;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eden.Protocol.Tests.Administration;

public sealed class AdministrationComponentTests
{
    [Fact]
    public void MoneyInputsKeepExactCentsAndRejectHiddenFractionalCents()
    {
        var form = SettingsForm.From(new WorldSettings());
        form.TestingPriceIsk = 1.23m;
        form.GrantIsk = 987654.32m;
        var settings = form.Build();
        Assert.Equal(123, settings.DevelopmentSupply.TestingPriceCents);
        Assert.Equal(98_765_432, settings.DevelopmentSupply.GrantCents);
        form.TestingPriceIsk = 1.234m;
        Assert.Throws<InvalidDataException>(() => form.Build());
    }

    [Fact]
    public async Task ComponentsShowPendingRestartErrorsMissingFieldsAndExactIntegers()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        WorldConfigurationDraft draft = new();
        ConfigurationState state = new(2, new(1, null, "Original", 0, draft), new(2, 1, "Admin", 0, draft), "<invalid pending>");
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var status = await renderer.RenderComponentAsync<ConfigurationStatus>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ConfigurationStatus.State)] = state })
            );
            string html = status.ToHtmlString();
            Assert.Contains("Active revision 1", html);
            Assert.Contains("Revision 2", html);
            Assert.Contains("pending restart", html);
            Assert.Contains("&lt;invalid pending&gt;", html);
            using var document = JsonDocument.Parse("{\"wide\":9007199254740993,\"none\":null}");
            CatalogShapeDescriptor schema = new(
                "object",
                false,
                false,
                new Dictionary<string, CatalogShapeDescriptor>
                {
                    ["wide"] = new("long", false, true),
                    ["none"] = new("string", true, false),
                    ["absent"] = new("string", true, false),
                }.ToImmutableDictionary()
            );
            var source = await renderer.RenderComponentAsync<CatalogValue>(
                ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        [nameof(CatalogValue.Value)] = document.RootElement,
                        [nameof(CatalogValue.Schema)] = schema,
                    }
                )
            );
            string values = source.ToHtmlString();
            Assert.Contains("9007199254740993", values);
            Assert.Contains("Explicit null", values);
            Assert.Contains("Missing in original source", values);
            Assert.Contains("read only", values);
        });
    }

    [Fact]
    public void EveryDatasetHasTypedBrowsingWithSearchKeyPagingAndDeclaredRelationships()
    {
        using TestWorld world = new();
        using (CatalogDbContext fixture = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            fixture.Database.ExecuteSqlRaw(
                """
                UPDATE SourceRecord SET Data='{{"volume":15000.0,"groupID":237}}' WHERE Dataset='client/types' AND Key='601';
                """
            );
        }
        CatalogBrowser browser = new(new WorldDatabase(world.Path).Catalog);
        Assert.Equal(442, browser.Datasets.Count);
        foreach (var dataset in browser.Datasets)
        {
            Assert.InRange(browser.Page(dataset.Name, limit: 1).Rows.Length, 0, 1);
        }
        var first = browser.Page("client/types", limit: 1);
        var second = browser.Page("client/types", first.NextCursor, limit: 1);
        Assert.NotEqual(first.Rows[0].Key, second.Rows[0].Key);
        var ibis = Assert.Single(browser.Page("client/types", key: "601").Rows);
        Assert.Contains(browser.Relationships("client/types", ibis), relation => relation.Dataset == "client/groups");
        Assert.NotEmpty(browser.Page("client/types", search: "groupID").Rows);
        Assert.Empty(browser.Page("client/types", search: "value_that_is_not_present").Rows);
        Assert.Throws<InvalidDataException>(() => browser.Page("unknown"));
        Assert.Throws<JsonException>(() => browser.Page("client/types", key: "\"601\""));
        Assert.Throws<InvalidDataException>(() => browser.Page("client/types", limit: 501));
    }
}
