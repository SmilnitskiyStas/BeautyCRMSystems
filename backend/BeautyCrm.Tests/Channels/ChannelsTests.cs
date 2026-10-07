using System.Net;
using System.Security.Cryptography;
using System.Text;
using BeautyCrm.Infrastructure.Integrations.Channels;

namespace BeautyCrm.Tests.Channels;

internal sealed class ChCreds(string? token = "123456:ABCDEFtoken", string? secret = "whsecret") : IChannelCredentialsProvider
{
    public ChannelCredentials? Get(string channel) => new(token, secret);
}

internal sealed class ChFakeRepo : IChannelMessageRepository
{
    public HashSet<string> Seen = new();
    public OutboxItem? Item;
    public bool Sent; public int Attempts; public bool FinalFailed;
    public Task<IReadOnlyList<InboundMessage>> SaveInboundAsync(IReadOnlyList<InboundMessage> m, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InboundMessage>>(m.Where(x => Seen.Add(x.IdempotencyKey)).ToList());
    public Task<OutboxItem?> GetOutboxItemAsync(Guid id, CancellationToken ct) => Task.FromResult(Item);
    public Task MarkSentAsync(Guid id, CancellationToken ct) { Sent = true; Item = Item! with { Status = OutboxStatus.Sent }; return Task.CompletedTask; }
    public Task MarkAttemptFailedAsync(Guid id, int attempts, string error, bool final, CancellationToken ct)
    {
        Attempts = attempts; FinalFailed = final;
        Item = Item! with { Attempts = attempts, Status = final ? OutboxStatus.Failed : OutboxStatus.Pending };
        return Task.CompletedTask;
    }
}

internal sealed class ChFakeQueue : IChannelQueue
{
    public int Inbound; public List<TimeSpan> Retries = new();
    public Task EnqueueInboundAsync(IReadOnlyList<InboundMessage> m, CancellationToken ct) { Inbound += m.Count; return Task.CompletedTask; }
    public Task ScheduleOutboxRetryAsync(Guid id, TimeSpan d, CancellationToken ct) { Retries.Add(d); return Task.CompletedTask; }
}

internal sealed class ChStubHandler(HttpStatusCode code) : HttpMessageHandler
{
    public HttpRequestMessage? Last;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    { Last = r; return Task.FromResult(new HttpResponseMessage(code)); }
}

public class ChannelsTests
{
    private static WebhookRequest Req(string k, string v) => new(new Dictionary<string, string> { [k] = v });
    private static string Sig(string secret, string body) =>
        "sha256=" + Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private const string IgBody = """{"entry":[{"messaging":[{"sender":{"id":"u1"},"recipient":{"id":"p"},"timestamp":1700000000000,"message":{"mid":"m1","text":"Hi"}},{"sender":{"id":"p"},"message":{"mid":"m2","text":"echo","is_echo":true}}]}]}""";
    private const string TgBody = """{"update_id":1,"message":{"message_id":7,"from":{"id":42,"first_name":"Ann","last_name":"K"},"chat":{"id":42},"date":1700000000,"text":"Hello"}}""";

    private static InboundWebhookService Svc(ChFakeRepo r, ChFakeQueue q, params IChannelAdapter[] a) => new(new ChannelRegistry(a), r, q);

    [Fact]
    public void telegramVerify_rejects_when_secret_header_wrong_or_missing()
    {
        var a = new TelegramAdapter(new HttpClient(), new ChCreds());
        Assert.False(a.VerifySignature(Req("X-Telegram-Bot-Api-Secret-Token", "bad"), TgBody));
        Assert.False(a.VerifySignature(new WebhookRequest(new Dictionary<string, string>()), TgBody));
        Assert.True(a.VerifySignature(Req("x-telegram-bot-api-secret-token", "whsecret"), TgBody));
    }

    [Fact]
    public void instagramVerify_checks_hmac_and_rejects_tampered_body()
    {
        var a = new InstagramAdapter(new HttpClient(), new ChCreds());
        Assert.True(a.VerifySignature(Req("X-Hub-Signature-256", Sig("whsecret", IgBody)), IgBody));
        Assert.False(a.VerifySignature(Req("X-Hub-Signature-256", Sig("whsecret", IgBody)), IgBody + " "));
        Assert.False(a.VerifySignature(Req("X-Hub-Signature-256", "sha256=00"), IgBody));
        Assert.False(a.VerifySignature(Req("X-Hub-Signature-256", Sig("other", IgBody)), IgBody));
    }

    [Fact]
    public void instagramVerifyChallenge_returns_challenge_only_with_valid_token()
    {
        var a = new InstagramAdapter(new HttpClient(), new ChCreds());
        var ok = new WebhookRequest(new Dictionary<string, string>(), new Dictionary<string, string>
            { ["hub.mode"] = "subscribe", ["hub.verify_token"] = "whsecret", ["hub.challenge"] = "abc" });
        var bad = new WebhookRequest(new Dictionary<string, string>(), new Dictionary<string, string>
            { ["hub.mode"] = "subscribe", ["hub.verify_token"] = "x", ["hub.challenge"] = "abc" });
        Assert.Equal("abc", a.VerifyChallenge(ok));
        Assert.Null(a.VerifyChallenge(bad));
    }

    [Fact]
    public async Task webhook_rejects_invalid_signature_and_stores_nothing()
    {
        var repo = new ChFakeRepo(); var q = new ChFakeQueue();
        var s = Svc(repo, q, new InstagramAdapter(new HttpClient(), new ChCreds()));
        var res = await s.HandleAsync("instagram", Req("X-Hub-Signature-256", "sha256=deadbeef"), IgBody, default);
        Assert.Equal(WebhookOutcome.InvalidSignature, res.Outcome);
        Assert.Empty(repo.Seen); Assert.Equal(0, q.Inbound);
    }

    [Fact]
    public async Task webhook_normalizes_instagram_message_skips_echo_and_dedups()
    {
        var repo = new ChFakeRepo(); var q = new ChFakeQueue();
        var s = Svc(repo, q, new InstagramAdapter(new HttpClient(), new ChCreds()));
        var req = Req("X-Hub-Signature-256", Sig("whsecret", IgBody));
        var r1 = await s.HandleAsync("INSTAGRAM", req, IgBody, default);
        var r2 = await s.HandleAsync("instagram", req, IgBody, default);
        Assert.Equal(1, r1.NewMessages); Assert.Equal(0, r2.NewMessages);
        Assert.Equal(1, q.Inbound);
        Assert.Contains("instagram:m1", repo.Seen);
    }

    [Fact]
    public async Task telegramParse_normalizes_fields()
    {
        var m = (await new TelegramAdapter(new HttpClient(), new ChCreds()).ParseInboundAsync(TgBody)).Single();
        Assert.Equal("telegram", m.Channel); Assert.Equal("42", m.ExternalConversationId);
        Assert.Equal("Ann K", m.SenderName); Assert.Equal("Hello", m.Text);
        Assert.Equal("42:7", m.ExternalMessageId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), m.ReceivedAt);
    }

    [Fact]
    public async Task webhook_returns_bad_payload_for_malformed_json_and_unknown_for_missing_channel()
    {
        var repo = new ChFakeRepo(); var q = new ChFakeQueue();
        var s = Svc(repo, q, new MockTelegramAdapter());
        Assert.Equal(WebhookOutcome.BadPayload, (await s.HandleAsync("telegram", Req("X-Mock-Signature", "mock"), "{not json", default)).Outcome);
        Assert.Equal(WebhookOutcome.UnknownChannel, (await s.HandleAsync("tiktok", Req("a", "b"), "{}", default)).Outcome);
    }

    [Fact]
    public async Task telegramSend_posts_to_bot_api_and_error_does_not_leak_token()
    {
        var h = new ChStubHandler(HttpStatusCode.OK);
        await new TelegramAdapter(new HttpClient(h), new ChCreds()).SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "x"), default);
        Assert.Contains("/sendMessage", h.Last!.RequestUri!.ToString());

        var bad = new TelegramAdapter(new HttpClient(new ChStubHandler(HttpStatusCode.Unauthorized)), new ChCreds());
        var ex = await Assert.ThrowsAsync<PermanentChannelException>(() => bad.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "x"), default));
        Assert.DoesNotContain("ABCDEFtoken", ex.Message);
        var tr = new TelegramAdapter(new HttpClient(new ChStubHandler(HttpStatusCode.BadGateway)), new ChCreds());
        await Assert.ThrowsAsync<TransientChannelException>(() => tr.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "x"), default));
    }

    [Fact]
    public async Task instagramSend_fails_permanently_when_24h_window_closed()
    {
        var a = new InstagramAdapter(new HttpClient(new ChStubHandler(HttpStatusCode.OK)), new ChCreds());
        var old = new OutboundMessage(Guid.NewGuid(), "instagram", "u1", "x", DateTimeOffset.UtcNow.AddHours(-25));
        await Assert.ThrowsAsync<PermanentChannelException>(() => a.SendAsync(old, default));
        var h = new ChStubHandler(HttpStatusCode.OK);
        var ok = new InstagramAdapter(new HttpClient(h), new ChCreds());
        await ok.SendAsync(old with { LastInboundAt = DateTimeOffset.UtcNow.AddHours(-1) }, default);
        Assert.Equal("Bearer", h.Last!.Headers.Authorization!.Scheme);
    }

    private static (OutboxProcessor p, ChFakeRepo r, ChFakeQueue q, MockTelegramAdapter a) Outbox()
    {
        var a = new MockTelegramAdapter(); var r = new ChFakeRepo(); var q = new ChFakeQueue();
        r.Item = new OutboxItem(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "hi"), OutboxStatus.Pending, 0);
        return (new OutboxProcessor(new ChannelRegistry([a]), r, q), r, q, a);
    }

    [Fact]
    public async Task outbox_sends_and_is_idempotent_on_second_call()
    {
        var (p, r, _, a) = Outbox();
        Assert.Equal(OutboxResult.Sent, await p.ProcessAsync(r.Item!.Message.MessageId, default));
        Assert.Equal(OutboxResult.AlreadyDone, await p.ProcessAsync(r.Item!.Message.MessageId, default));
        Assert.Single(a.Sent);
    }

    [Fact]
    public async Task outbox_retries_transient_failures_then_succeeds()
    {
        var (p, r, q, a) = Outbox(); a.FailNextSends = 2;
        var id = r.Item!.Message.MessageId;
        Assert.Equal(OutboxResult.RetryScheduled, await p.ProcessAsync(id, default));
        Assert.Equal(OutboxResult.RetryScheduled, await p.ProcessAsync(id, default));
        Assert.Equal(OutboxResult.Sent, await p.ProcessAsync(id, default));
        Assert.Equal(2, q.Retries.Count); Assert.True(q.Retries[1] > q.Retries[0]);
    }

    [Fact]
    public async Task outbox_marks_failed_after_three_attempts()
    {
        var (p, r, q, a) = Outbox(); a.FailNextSends = 99;
        var id = r.Item!.Message.MessageId;
        await p.ProcessAsync(id, default); await p.ProcessAsync(id, default);
        Assert.Equal(OutboxResult.Failed, await p.ProcessAsync(id, default));
        Assert.True(r.FinalFailed); Assert.Equal(3, r.Attempts); Assert.Equal(2, q.Retries.Count);
        Assert.Equal(OutboxResult.AlreadyDone, await p.ProcessAsync(id, default));
    }

    [Fact]
    public async Task outbox_does_not_retry_permanent_failure_and_stub_channels_fail_closed()
    {
        var r = new ChFakeRepo(); var q = new ChFakeQueue();
        r.Item = new OutboxItem(new OutboundMessage(Guid.NewGuid(), "viber", "1", "x"), OutboxStatus.Pending, 0);
        var p = new OutboxProcessor(new ChannelRegistry([new ViberAdapter()]), r, q);
        Assert.Equal(OutboxResult.Failed, await p.ProcessAsync(r.Item.Message.MessageId, default));
        Assert.Empty(q.Retries);
        Assert.False(new ViberAdapter().VerifySignature(Req("a", "b"), "{}"));
        Assert.Equal(OutboxResult.NotFound, await new OutboxProcessor(new ChannelRegistry([]), new ChFakeRepo(), q).ProcessAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public void secretMasker_shows_only_last_four_chars()
    {
        Assert.Equal("********oken", SecretMasker.Mask("123456:ABCDEFtoken"));
        Assert.Equal("****", SecretMasker.Mask("abc"));
        Assert.Equal("", SecretMasker.Mask(null));
        var s = new ChannelCredentials("123456:ABCDEFtoken", "whsecret").ToString();
        Assert.DoesNotContain("ABCDEF", s); Assert.DoesNotContain("whsecret", s);
    }
}
