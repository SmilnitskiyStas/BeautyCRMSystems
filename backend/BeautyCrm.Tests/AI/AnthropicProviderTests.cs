using System.Net;
using System.Text.Json;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;

namespace BeautyCrm.Tests.AI;

/// <summary>TASK-715 (§20.11 п. 13): адаптер Anthropic на підставному HTTP-обробнику. Реальної мережі й реального ключа немає.</summary>
public class AnthropicProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri, Dictionary<string, string> Headers, string Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.Method, request.RequestUri, headers, body));
            return await respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string OkBody =
        """{"id":"m1","content":[{"type":"text","text":"Привіт!"},{"type":"tool_use","id":"tu1","name":"get_prices","input":{}}],"usage":{"input_tokens":123,"output_tokens":45}}""";

    private static AiProviderCall Call(string key, int max = 321) =>
        new(new AiRequest("sys", [AiMessage.User("hi")], AiToolCatalog.All), "claude-haiku-5-5", max, new AiSecret(key));

    [Fact]
    public async Task complete_sends_model_max_tokens_key_header_to_the_fixed_endpoint_and_parses_usage()
    {
        var key = AiWorld.NewApiKey();
        var h = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, OkBody)));
        var res = await new AnthropicAiProvider(new HttpClient(h)).CompleteAsync(Call(key), default);

        var seen = Assert.Single(h.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal(AnthropicAiProvider.Endpoint, seen.Uri!.ToString());
        Assert.Equal(key, seen.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", seen.Headers["anthropic-version"]);
        using var doc = JsonDocument.Parse(seen.Body);
        Assert.Equal("claude-haiku-5-5", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(321, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(8, doc.RootElement.GetProperty("tools").GetArrayLength());
        Assert.DoesNotContain(key, seen.Body);

        Assert.Equal(new AiUsage(123, 45), res.Usage);
        Assert.Equal("Привіт!", res.Response.Text);
        Assert.Equal("get_prices", Assert.Single(res.Response.ToolCalls).Name);
    }

    [Fact]
    public async Task empty_tool_list_is_omitted_from_the_body()
    {
        var h = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, OkBody)));
        var call = Call(AiWorld.NewApiKey()) with { Request = new AiRequest("s", [AiMessage.User("x")], []) };
        await new AnthropicAiProvider(new HttpClient(h)).CompleteAsync(call, default);
        using var doc = JsonDocument.Parse(h.Seen[0].Body);
        Assert.False(doc.RootElement.TryGetProperty("tools", out _));
    }

    [Theory]
    [InlineData(401, AiProviderError.KeyRejected)]
    [InlineData(403, AiProviderError.InsufficientPermissions)]
    [InlineData(404, AiProviderError.ModelUnavailable)]
    [InlineData(429, AiProviderError.RateLimited)]
    [InlineData(400, AiProviderError.BadRequest)]
    [InlineData(500, AiProviderError.Unavailable)]
    [InlineData(529, AiProviderError.Unavailable)]
    public async Task http_errors_map_to_codes_without_body_or_key(int status, AiProviderError expected)
    {
        var key = AiWorld.NewApiKey();
        // тіло помилки навмисно містить ключ: воно не має потрапити в виняток
        var h = new StubHandler(_ => Task.FromResult(Json((HttpStatusCode)status, "{\"error\":{\"message\":\"bad key " + key + "\"}}")));
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => new AnthropicAiProvider(new HttpClient(h)).CompleteAsync(Call(key), default));
        Assert.Equal((expected, status), (ex.Error, ex.HttpStatus));
        Assert.DoesNotContain(key, ex.ToString());
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task network_failure_and_timeout_become_unavailable_without_inner_exception()
    {
        var key = AiWorld.NewApiKey();
        var net = new StubHandler(_ => throw new HttpRequestException($"connect failed for header {key}"));
        var ex1 = await Assert.ThrowsAsync<AiProviderException>(() => new AnthropicAiProvider(new HttpClient(net)).CompleteAsync(Call(key), default));
        Assert.Equal(AiProviderError.Unavailable, ex1.Error);
        Assert.DoesNotContain(key, ex1.ToString());
        Assert.Null(ex1.InnerException);

        var timeout = new StubHandler(_ => throw new TaskCanceledException("timeout"));
        var ex2 = await Assert.ThrowsAsync<AiProviderException>(() => new AnthropicAiProvider(new HttpClient(timeout)).CompleteAsync(Call(key), default));
        Assert.Equal(AiProviderError.Unavailable, ex2.Error);
    }

    [Fact]
    public async Task caller_cancellation_is_not_masked_as_provider_error()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var h = new StubHandler(_ => throw new OperationCanceledException(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnthropicAiProvider(new HttpClient(h)).CompleteAsync(Call(AiWorld.NewApiKey()), cts.Token));
    }

    [Fact]
    public async Task malformed_success_body_is_bad_response_without_leaking_it()
    {
        var key = AiWorld.NewApiKey();
        var h = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, $"{{ not json {key}")));
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => new AnthropicAiProvider(new HttpClient(h)).CompleteAsync(Call(key), default));
        Assert.Equal(AiProviderError.BadResponse, ex.Error);
        Assert.DoesNotContain(key, ex.ToString());
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task key_with_control_characters_does_not_leak_through_header_errors()
    {
        var key = "sk-ant-test-bad\r\nX-Evil: 1-" + Guid.NewGuid().ToString("N");
        var h = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, OkBody)));
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => new AnthropicAiProvider(new HttpClient(h)).CompleteAsync(Call(key), default));
        Assert.Equal(AiProviderError.KeyRejected, ex.Error);
        Assert.DoesNotContain("sk-ant-test", ex.ToString());
        Assert.Empty(h.Seen); // запит не відправлено, інʼєкція заголовка неможлива
    }

    [Theory]
    [InlineData(200, true, null)]
    [InlineData(401, false, "key_rejected")]
    [InlineData(403, false, "insufficient_permissions")]
    [InlineData(429, false, "provider_rate_limited")]
    [InlineData(503, false, "provider_unavailable")]
    public async Task check_key_maps_statuses_and_uses_models_endpoint(int status, bool ok, string? reason)
    {
        var key = AiWorld.NewApiKey();
        var h = new StubHandler(_ => Task.FromResult(Json((HttpStatusCode)status, "{}")));
        var r = await new AnthropicAiProvider(new HttpClient(h)).CheckKeyAsync(new AiSecret(key), "claude-haiku-5-5", default);
        Assert.Equal((ok, reason), (r.Ok, r.Reason));
        Assert.Equal(AnthropicAiProvider.ModelsEndpoint, h.Seen[0].Uri!.ToString());
        Assert.Equal(key, h.Seen[0].Headers["x-api-key"]);
    }

    [Fact]
    public async Task check_key_network_failure_is_provider_unavailable()
    {
        var h = new StubHandler(_ => throw new HttpRequestException("down"));
        var r = await new AnthropicAiProvider(new HttpClient(h)).CheckKeyAsync(new AiSecret(AiWorld.NewApiKey()), "m", default);
        Assert.Equal((false, "provider_unavailable"), (r.Ok, r.Reason));
    }
}
