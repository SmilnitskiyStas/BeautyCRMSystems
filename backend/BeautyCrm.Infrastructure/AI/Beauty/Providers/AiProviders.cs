using System.Text;
using System.Text.Json;

namespace BeautyCrm.Infrastructure.AI.Beauty.Providers;

public sealed record AiProviderCall(AiRequest Request, string Model, int MaxOutputTokens, AiSecret Key);
public sealed record AiProviderResult(AiResponse Response, AiUsage Usage);

/// <summary>Результат перевірки ключа. <see cref="Reason"/>: key_rejected|insufficient_permissions|provider_rate_limited|provider_unavailable|model_unavailable.</summary>
public sealed record AiKeyCheck(bool Ok, string? Reason);

/// <summary>
/// Адаптер LLM-провайдера (ADR-010 §2.4). Єдине місце, що знає формат провайдера. Endpoint-и захардкоджені в адаптері:
/// кастомного base URL немає (SSRF). Помилки — <see cref="AiProviderException"/> без тіла відповіді й без ключа.
/// </summary>
public interface IAiProvider
{
    string Id { get; }
    Task<AiProviderResult> CompleteAsync(AiProviderCall call, CancellationToken ct);
    Task<AiKeyCheck> CheckKeyAsync(AiSecret key, string model, CancellationToken ct);
}

/// <summary>
/// Реєстр доступних провайдерів. Провайдер «доступний», лише якщо його адаптер зареєстровано в коді
/// (конфіг не може увімкнути провайдера без адаптера: <c>provider_not_available</c>).
/// </summary>
public sealed class AiProviderRegistry
{
    public const string MockId = "mock";
    private readonly Dictionary<string, IAiProvider> _providers;

    public AiProviderRegistry(IEnumerable<IAiProvider> providers)
    {
        _providers = new Dictionary<string, IAiProvider>(StringComparer.Ordinal);
        foreach (var p in providers) _providers[p.Id] = p;
    }

    public IReadOnlyCollection<string> AvailableIds => _providers.Keys;
    public bool IsAvailable(string? providerId) => providerId is not null && _providers.ContainsKey(providerId);
    public IAiProvider? Get(string? providerId) => providerId is not null && _providers.TryGetValue(providerId, out var p) ? p : null;
}

/// <summary>Anthropic Messages API (адаптер). Ключ приходить у виклику (tenant або платформа), не з env.</summary>
public sealed class AnthropicAiProvider(HttpClient http) : IAiProvider
{
    public const string ProviderId = "anthropic";
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string ModelsEndpoint = "https://api.anthropic.com/v1/models?limit=1";
    internal const string ApiVersion = "2023-06-01";

    public string Id => ProviderId;

    public async Task<AiProviderResult> CompleteAsync(AiProviderCall call, CancellationToken ct)
    {
        using var msg = Build(HttpMethod.Post, Endpoint, call.Key);
        msg.Content = new StringContent(AnthropicWire.BuildBody(call.Request, call.Model, call.MaxOutputTokens), Encoding.UTF8, "application/json");

        var (status, body) = await SendAsync(msg, readBody: true, ct);
        if (status is < 200 or > 299) throw new AiProviderException(MapError(status), status);
        try
        {
            return AnthropicWire.ParseResult(body!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AiProviderException(AiProviderError.BadResponse, status); // без InnerException: тіло відповіді не витікає
        }
    }

    public async Task<AiKeyCheck> CheckKeyAsync(AiSecret key, string model, CancellationToken ct)
    {
        // Перевірка без витрати токенів: GET /v1/models. Достатність цього виклику без реального ключа не верифікована.
        try
        {
            using var msg = Build(HttpMethod.Get, ModelsEndpoint, key);
            var (status, _) = await SendAsync(msg, readBody: false, ct);
            if (status is >= 200 and <= 299) return new AiKeyCheck(true, null);
            return new AiKeyCheck(false, status switch
            {
                401 => "key_rejected",
                403 => "insufficient_permissions",
                429 => "provider_rate_limited",
                _ => "provider_unavailable",
            });
        }
        catch (AiProviderException ex)
        {
            return new AiKeyCheck(false, ex.Error == AiProviderError.KeyRejected ? "key_rejected" : "provider_unavailable");
        }
    }

    private static HttpRequestMessage Build(HttpMethod method, string url, AiSecret key)
    {
        var msg = new HttpRequestMessage(method, url);
        // TryAddWithoutValidation: некоректний ключ не має кидати FormatException із текстом значення.
        var raw = key.Reveal();
        if (!raw.All(c => c is > ' ' and <= '~') || !msg.Headers.TryAddWithoutValidation("x-api-key", raw))
        {
            msg.Dispose();
            throw new AiProviderException(AiProviderError.KeyRejected); // ключ з керівними символами/переносами: запит не відправляється
        }
        msg.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        return msg;
    }

    private async Task<(int Status, string? Body)> SendAsync(HttpRequestMessage msg, bool readBody, CancellationToken ct)
    {
        try
        {
            using var res = await http.SendAsync(msg, ct);
            var status = (int)res.StatusCode;
            // Тіло помилки не читаємо взагалі (воно могло б містити фрагменти запиту/ключа).
            var body = readBody && res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(ct) : null;
            return (status, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiProviderException(AiProviderError.Unavailable); // таймаут HttpClient
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or InvalidOperationException)
        {
            throw new AiProviderException(AiProviderError.Unavailable); // без InnerException: у повідомленні могло бути значення заголовка
        }
    }

    private static AiProviderError MapError(int status) => status switch
    {
        401 => AiProviderError.KeyRejected,
        403 => AiProviderError.InsufficientPermissions,
        404 => AiProviderError.ModelUnavailable,
        429 => AiProviderError.RateLimited,
        400 or 413 or 422 => AiProviderError.BadRequest,
        _ => AiProviderError.Unavailable, // 5xx, 529 overloaded тощо
    };
}

/// <summary>Серіалізація/розбір формату Anthropic Messages. Спільне для адаптера й legacy <c>AnthropicAiClient.BuildBody</c>.</summary>
public static class AnthropicWire
{
    public static string BuildBody(AiRequest r, string model, int maxTokens)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", maxTokens);
            w.WriteString("system", r.System);
            if (r.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (var t in r.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("input_schema");
                    using (var d = JsonDocument.Parse(t.InputSchemaJson)) d.RootElement.WriteTo(w);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteStartArray("messages");
            foreach (var m in r.Messages)
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role);
                w.WriteStartArray("content");
                foreach (var b in m.Blocks)
                {
                    w.WriteStartObject();
                    switch (b)
                    {
                        case AiTextBlock tb:
                            w.WriteString("type", "text"); w.WriteString("text", tb.Text); break;
                        case AiToolUseBlock tu:
                            w.WriteString("type", "tool_use"); w.WriteString("id", tu.Id); w.WriteString("name", tu.Name);
                            w.WritePropertyName("input"); tu.Input.WriteTo(w); break;
                        case AiToolResultBlock tr:
                            w.WriteString("type", "tool_result"); w.WriteString("tool_use_id", tr.ToolUseId);
                            w.WriteString("content", tr.Content); w.WriteBoolean("is_error", tr.IsError); break;
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static AiProviderResult ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var blocks = new List<AiBlock>();
        foreach (var c in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            switch (c.GetProperty("type").GetString())
            {
                case "text": blocks.Add(new AiTextBlock(c.GetProperty("text").GetString() ?? "")); break;
                case "tool_use":
                    blocks.Add(new AiToolUseBlock(c.GetProperty("id").GetString()!, c.GetProperty("name").GetString()!,
                        c.GetProperty("input").Clone()));
                    break;
            }
        }
        long input = 0, output = 0;
        if (doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            if (u.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number) input = i.GetInt64();
            if (u.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number) output = o.GetInt64();
        }
        var usage = new AiUsage(Math.Max(0, input), Math.Max(0, output));
        return new AiProviderResult(new AiResponse(blocks, usage), usage);
    }
}

public sealed record MockAiCall(
    string Provider, string Model, int MaxOutputTokens, string KeyFingerprint, string Role,
    string System, IReadOnlyList<AiMessage> Messages, IReadOnlyList<string> ToolNames);

/// <summary>
/// Mock-провайдер для тестів і Development (<c>Ai:UseMockProvider</c>, поза Development старт падає). Реальних викликів немає.
/// Фіксує модель, відбиток ключа (SHA-256-префікс), роль і запит; відповіді — скрипт або <see cref="Handler"/>.
/// </summary>
public sealed class MockAiProvider(string id = AiProviderRegistry.MockId) : IAiProvider
{
    private readonly object _gate = new();
    private readonly List<MockAiCall> _calls = [];
    private readonly Queue<AiResponse> _script = new();

    public string Id { get; } = id;

    public Func<AiProviderCall, AiProviderResult>? Handler { get; set; }
    public Func<AiSecret, string, AiKeyCheck>? KeyCheckHandler { get; set; }
    public AiUsage DefaultUsage { get; set; } = new(12, 8);

    public IReadOnlyList<MockAiCall> Calls { get { lock (_gate) return _calls.ToList(); } }

    public MockAiProvider Enqueue(params AiResponse[] responses)
    {
        lock (_gate) foreach (var r in responses) _script.Enqueue(r);
        return this;
    }

    public Task<AiProviderResult> CompleteAsync(AiProviderCall call, CancellationToken ct)
    {
        AiResponse? scripted = null;
        lock (_gate)
        {
            _calls.Add(new MockAiCall(Id, call.Model, call.MaxOutputTokens, call.Key.Fingerprint(), call.Request.Role,
                call.Request.System, call.Request.Messages.ToList(), call.Request.Tools.Select(t => t.Name).ToList()));
            if (Handler is null && _script.Count > 0) scripted = _script.Dequeue();
        }
        if (Handler is not null) return Task.FromResult(Handler(call));
        var resp = scripted ?? new AiResponse([new AiTextBlock("Mock-відповідь.")]);
        return Task.FromResult(new AiProviderResult(resp with { Usage = DefaultUsage }, DefaultUsage));
    }

    public Task<AiKeyCheck> CheckKeyAsync(AiSecret key, string model, CancellationToken ct) =>
        Task.FromResult(KeyCheckHandler?.Invoke(key, model) ?? new AiKeyCheck(true, null));
}
