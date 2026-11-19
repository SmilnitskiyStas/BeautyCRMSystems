using System.Text.Json;
using System.Text.Json.Nodes;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// Контракт §30.6.3 (ADR-023): поля собівартості (§24.6) віддаються лише за <c>revenue.costs >= view</c>; інакше їх немає в JSON
/// (не 0 і не null). Прибираються за іменем на будь-якій глибині відповіді аналітики: ці імена в DTO аналітики означають лише
/// фінансові поля собівартості.
/// </summary>
public static class CostFieldRedaction
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "cost", "masterPercent", "masterPayout", "net", "netIncome", "marginPercent", "withoutCost", "revenueWithoutCost", "costMissing",
    };

    public static IActionResult Redacted(this ControllerBase c, object? value, Actor actor)
    {
        if (value is null || actor.Access.AtLeast(PermissionKeys.RevenueCosts, PermissionLevels.View)) return c.Ok(value);
        var options = c.HttpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options);
        Strip(node);
        return c.Ok(node);
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).Where(Fields.Contains).ToList()) o.Remove(key);
                foreach (var (_, v) in o.ToList()) Strip(v);
                break;
            case JsonArray a:
                foreach (var v in a) Strip(v);
                break;
        }
    }
}
