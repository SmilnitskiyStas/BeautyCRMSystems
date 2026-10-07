using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyChannels;

/// <summary>Секрети ніколи не повертаються: лише маска токена (останні 4 символи) і прапорець наявності webhook-секрету.</summary>
public sealed record ChannelDto(
    Guid Id, string Type, string Name, Guid? LocationId, bool IsActive, string MaskedToken, bool HasWebhookSecret,
    string? SettingsJson, string WebhookPath);

/// <summary>Token/WebhookSecret = null: залишити наявне значення.</summary>
public sealed record UpsertChannelRequest(
    string? Type, string Name, Guid? LocationId, bool IsActive, string? Token, string? WebhookSecret, string? SettingsJson);

/// <summary>Рядок каналу без секретів; Last4 — для маски.</summary>
public sealed record ChannelRecord(
    Guid Id, string Type, string Name, Guid? LocationId, bool IsActive, string? Last4, bool HasWebhookSecret, string? SettingsJson);

public interface IChannelSettingsStore
{
    Task<IReadOnlyList<ChannelRecord>> ListAsync(CancellationToken ct);
    Task<ChannelRecord?> GetAsync(Guid id, CancellationToken ct);
    Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct);
    /// <summary>Шифрує секрети; створює канал, якщо id не існує (тоді Type обов'язковий).</summary>
    Task<ChannelRecord> UpsertAsync(Guid id, UpsertChannelRequest req, CancellationToken ct);
}

public sealed class ChannelSettingsService(IChannelSettingsStore store)
{
    public static readonly string[] Types = ["telegram", "instagram", "facebook", "whatsapp", "viber", "widget"];

    public async Task<IReadOnlyList<ChannelDto>> ListAsync(CancellationToken ct) =>
        (await store.ListAsync(ct)).Select(ToDto).ToList();

    public async Task<Result<ChannelDto>> UpsertAsync(Guid id, UpsertChannelRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200) return Error.Validation("invalid_name", "Name is required (max 200).");
        if (req.SettingsJson is not null && !IsJsonObject(req.SettingsJson))
            return Error.Validation("invalid_settings", "settingsJson must be a JSON object.");
        var existing = await store.GetAsync(id, ct);
        if (existing is null && (req.Type is null || !Types.Contains(req.Type)))
            return Error.Validation("invalid_type", "type is required to create a channel.");
        if (existing is not null && req.Type is not null && req.Type != existing.Type)
            return Error.Validation("type_immutable", "Channel type cannot be changed.");
        if (req.Token is { Length: > 512 } || req.WebhookSecret is { Length: > 512 })
            return Error.Validation("invalid_secret", "Secret is too long.");
        if (req.LocationId is { } loc && !await store.LocationExistsAsync(loc, ct))
            return Error.NotFound("location_not_found", "Location not found.");
        return ToDto(await store.UpsertAsync(id, req, ct));
    }

    private static bool IsJsonObject(string json)
    {
        try { using var d = System.Text.Json.JsonDocument.Parse(json); return d.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static ChannelDto ToDto(ChannelRecord r) => new(
        r.Id, r.Type, r.Name, r.LocationId, r.IsActive,
        string.IsNullOrEmpty(r.Last4) ? string.Empty : "********" + r.Last4,
        r.HasWebhookSecret, r.SettingsJson, $"/api/beauty/webhooks/{r.Type}/{r.Id}");
}
