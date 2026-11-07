using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Eden.Server.Administration.Components;

public partial class CatalogValue
{
    private const int PageSize = 50;
    private int Page;
    private JsonElement Previous;

    [Parameter]
    public JsonElement Value { get; set; }

    [Parameter]
    public CatalogShapeDescriptor? Schema { get; set; }

    [Parameter]
    public int Depth { get; set; }
    private string? Text =>
        Value.ValueKind switch
        {
            JsonValueKind.String => Value.GetString(),
            JsonValueKind.Null => "Explicit null",
            JsonValueKind.Undefined => "Missing",
            _ => Value.GetRawText(),
        };
    private CatalogShapeDescriptor? Shape =>
        Schema?.Kind != "union"
            ? Schema
            : Schema.Alternatives?.GetValueOrDefault(
                Value.ValueKind switch
                {
                    JsonValueKind.Object => "object",
                    JsonValueKind.Array => "array",
                    JsonValueKind.String => "string",
                    JsonValueKind.True or JsonValueKind.False => "bool",
                    _ => "long",
                }
            );

    protected override void OnParametersSet()
    {
        if (!Previous.Equals(Value))
        {
            Page = 0;
            Previous = Value;
        }
    }
}
