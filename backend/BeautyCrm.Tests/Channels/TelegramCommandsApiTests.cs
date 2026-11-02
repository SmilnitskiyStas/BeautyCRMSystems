using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Channels;

/// <summary>TASK-750 (§23.4, §23.13 сценарій 14): setMyCommands при збереженні каналу і POST /channels/{id}/telegram/sync-commands. Fake Telegram.</summary>
public sealed class TelegramCommandsApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string BotToken = "700111:BOTTOKENSECRETVALUE";
    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    private sealed record Env(HttpClient Client, TgRecorder Bot);

    private Env NewEnv()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var bot = new TgRecorder();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("Channels:EncryptionKey", Convert.ToBase64String(Enumerable.Range(10, 32).Select(i => (byte)i).ToArray()));
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IChannelAdapter>();
                s.AddSingleton<IChannelAdapter>(sp => new TelegramAdapter(new HttpClient(bot), sp.GetRequiredService<IChannelCredentialsProvider>()));
            });
        });
        _disposables.Add(f);
        return new Env(f.CreateClient(), bot);
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, string? token, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    private async Task<(TenantSeed T, string Owner, string Admin, string Spec)> TenantAsync()
    {
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        return (t, owner.AccessToken, admin.AccessToken, spec.AccessToken);
    }

    private static Task<HttpResponseMessage> Put(Env e, string token, Guid id, object body) =>
        e.Client.SendAsync(Req(HttpMethod.Put, $"/api/beauty/channels/{id}", token, body));

    private static Task<HttpResponseMessage> Sync(Env e, string? token, Guid id) =>
        e.Client.SendAsync(Req(HttpMethod.Post, $"/api/beauty/channels/{id}/telegram/sync-commands", token));

    private static async Task<string> Code(HttpResponseMessage r) => (await Read<JsonElement>(r)).GetProperty("code").GetString()!;

    [SkippableFact]
    public async Task saving_a_telegram_channel_syncs_commands_only_when_token_changes_or_it_becomes_active()
    {
        var e = NewEnv(); var (_, owner, _, _) = await TenantAsync();
        var id = Guid.NewGuid();

        var created = await Put(e, owner, id, new { type = "telegram", name = "bot", isActive = true, token = BotToken, webhookSecret = "s" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(2, e.Bot.Of("setMyCommands").Count);                       // створення з токеном
        Assert.DoesNotContain(BotToken, await created.Content.ReadAsStringAsync()); // секрети не повертаються

        Assert.Equal(HttpStatusCode.OK, (await Put(e, owner, id, new { name = "bot2", isActive = true })).StatusCode);
        Assert.Equal(2, e.Bot.Of("setMyCommands").Count);                       // ні токен, ні активність не змінились

        Assert.Equal(HttpStatusCode.OK, (await Put(e, owner, id, new { name = "bot2", isActive = true, token = BotToken + "x" })).StatusCode);
        Assert.Equal(4, e.Bot.Of("setMyCommands").Count);                       // новий токен

        Assert.Equal(HttpStatusCode.OK, (await Put(e, owner, id, new { name = "bot2", isActive = false })).StatusCode);
        Assert.Equal(4, e.Bot.Of("setMyCommands").Count);                       // вимкнення не синхронізує
        Assert.Equal(HttpStatusCode.OK, (await Put(e, owner, id, new { name = "bot2", isActive = true })).StatusCode);
        Assert.Equal(6, e.Bot.Of("setMyCommands").Count);                       // false -> true

        var ig = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Put(e, owner, ig, new { type = "instagram", name = "ig", isActive = true, token = "igtoken" })).StatusCode);
        Assert.Equal(6, e.Bot.Of("setMyCommands").Count);                       // не Telegram
    }

    [SkippableFact]
    public async Task telegram_failure_on_save_does_not_change_the_put_response()
    {
        var e = NewEnv(); var (_, owner, _, _) = await TenantAsync();
        e.Bot.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var r = await Put(e, owner, Guid.NewGuid(), new { type = "telegram", name = "bot", isActive = true, token = BotToken });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("********" + BotToken[^4..], (await Read<JsonElement>(r)).GetProperty("maskedToken").GetString());
    }

    [SkippableFact]
    public async Task sync_commands_endpoint_roles_and_status_codes()
    {
        var e = NewEnv(); var (_, owner, admin, spec) = await TenantAsync();
        var tg = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await Put(e, owner, tg, new { type = "telegram", name = "bot", isActive = true, token = BotToken })).StatusCode);
        e.Bot.Responder = null;

        Assert.Equal(HttpStatusCode.Unauthorized, (await Sync(e, null, tg)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Sync(e, spec, tg)).StatusCode);

        var before = e.Bot.Of("setMyCommands").Count;
        Assert.Equal(HttpStatusCode.NoContent, (await Sync(e, owner, tg)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Sync(e, admin, tg)).StatusCode);
        Assert.Equal(before + 4, e.Bot.Of("setMyCommands").Count);

        var missing = await Sync(e, owner, Guid.NewGuid());
        Assert.Equal((HttpStatusCode.NotFound, "channel_not_found"), (missing.StatusCode, await Code(missing)));

        var ig = Guid.NewGuid();
        await Put(e, owner, ig, new { type = "instagram", name = "ig", isActive = true, token = "igtoken" });
        var unsupported = await Sync(e, owner, ig);
        Assert.Equal((HttpStatusCode.Conflict, "channel_unsupported"), (unsupported.StatusCode, await Code(unsupported)));

        var off = Guid.NewGuid();
        await Put(e, owner, off, new { type = "telegram", name = "off", isActive = false, token = BotToken });
        var inactive = await Sync(e, owner, off);
        Assert.Equal((HttpStatusCode.Conflict, "channel_inactive"), (inactive.StatusCode, await Code(inactive)));

        var noToken = Guid.NewGuid();
        await Put(e, owner, noToken, new { type = "telegram", name = "nt", isActive = true });
        var tokenMissing = await Sync(e, owner, noToken);
        Assert.Equal((HttpStatusCode.Conflict, "token_missing"), (tokenMissing.StatusCode, await Code(tokenMissing)));

        e.Bot.Responder = _ => TgRecorder.Fail(HttpStatusCode.Forbidden, "Forbidden: bot was blocked by the user " + BotToken);
        var bad = await Sync(e, owner, tg);
        var body = await bad.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadGateway, bad.StatusCode);
        Assert.Contains("telegram_unavailable", body);
        Assert.DoesNotContain("blocked", body);
        Assert.DoesNotContain(BotToken, body);
    }

    [SkippableFact]
    public async Task sync_commands_does_not_reach_a_channel_of_another_tenant()
    {
        var e = NewEnv(); var (_, ownerA, _, _) = await TenantAsync(); var (_, ownerB, _, _) = await TenantAsync();
        var chB = Guid.NewGuid();
        await Put(e, ownerB, chB, new { type = "telegram", name = "b", isActive = true, token = BotToken });
        var before = e.Bot.Of("setMyCommands").Count;
        var r = await Sync(e, ownerA, chB);
        Assert.Equal((HttpStatusCode.NotFound, "channel_not_found"), (r.StatusCode, await Code(r)));
        Assert.Equal(before, e.Bot.Of("setMyCommands").Count);
    }
}
