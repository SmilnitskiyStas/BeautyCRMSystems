using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <summary>Адаптер Anthropic Messages API. Єдине місце, що знає про провайдера.</summary>
public sealed class AnthropicAiClient : IAiClient
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    private readonly HttpClient _http;
    private readonly AiSettings _settings;

    public AnthropicAiClient(HttpClient http, AiSettings settings)
    {
        _http = http; _settings = settings;
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable(_settings.ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"{_settings.ApiKeyEnvVar} is not set.");

        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-api-key", key);
        msg.Headers.Add("anthropic-version", "2023-06-01");

        using var res = await _http.SendAsync(msg, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API error {(int)res.StatusCode}");
        return ParseResponse(json);
    }

    public string BuildBody(AiRequest r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", _settings.Model);
            w.WriteNumber("max_tokens", _settings.MaxTokens);
            w.WriteString("system", r.System);
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

    public static AiResponse ParseResponse(string json)
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
        return new AiResponse(blocks);
    }
}
