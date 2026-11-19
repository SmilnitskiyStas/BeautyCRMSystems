using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-839 (ADR-020, §28.11): координати закладу (POST/PUT /locations), assistant.bookingFlow у settingsJson каналу,
/// inbox kind=location без координат. Повний HTTP-конвеєр на реальному PostgreSQL.
/// </summary>
public sealed class ChatFlowApiSurfaceTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    // Секрети каналу шифруються: потрібен Channels:EncryptionKey (як в InboxApiTests).
    private HttpClient ChannelClient()
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
            b.UseSetting("Channels:EncryptionKey", Convert.ToBase64String(Enumerable.Range(10, 32).Select(i => (byte)i).ToArray())));
        _disposables.Add(f);
        return f.CreateClient();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);
    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

    private async Task<(TenantSeed T, string Owner)> NewTenantAsync()
    {
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        return (t, owner.AccessToken);
    }

    private async Task<JsonElement> CreateLocationAsync(string token, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var r = await fx.Send(HttpMethod.Post, "/api/beauty/locations", body, token);
        Assert.Equal(expected, r.StatusCode);
        return await J(r);
    }

    [SkippableFact]
    public async Task location_create_without_coordinates_returns_nulls_and_with_coordinates_rounds_to_6_digits()
    {
        NeedDb();
        var (_, owner) = await NewTenantAsync();
        var plain = await CreateLocationAsync(owner, new { name = "Без гео", timezone = "Europe/Kyiv" });
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("latitude").ValueKind);
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("longitude").ValueKind);

        var geo = await CreateLocationAsync(owner, new { name = "З гео", timezone = "Europe/Kyiv", latitude = 50.4501234567, longitude = 30.5234 });
        Assert.Equal(50.450123m, geo.GetProperty("latitude").GetDecimal());
        Assert.Equal(30.5234m, geo.GetProperty("longitude").GetDecimal());

        var list = await J(await fx.Send(HttpMethod.Get, "/api/beauty/locations", null, owner));
        var row = list.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == geo.GetProperty("id").GetGuid());
        Assert.Equal(50.450123m, row.GetProperty("latitude").GetDecimal());
    }

    [SkippableFact]
    public async Task location_put_absent_keeps_null_clears_and_number_sets_coordinates()
    {
        NeedDb();
        var (_, owner) = await NewTenantAsync();
        var loc = await CreateLocationAsync(owner, new { name = "Центр", timezone = "Europe/Kyiv", latitude = 50.45, longitude = 30.52 });
        var url = $"/api/beauty/locations/{loc.GetProperty("id").GetGuid()}";

        // поля відсутні -> без змін
        var keep = await J(await fx.Send(HttpMethod.Put, url, new { name = "Центр 2", timezone = "Europe/Kyiv" }, owner));
        Assert.Equal(50.45m, keep.GetProperty("latitude").GetDecimal());
        Assert.Equal(30.52m, keep.GetProperty("longitude").GetDecimal());

        // нові числа
        var set = await J(await fx.Send(HttpMethod.Put, url, new { name = "Центр 2", timezone = "Europe/Kyiv", latitude = -33.8688, longitude = 151.2093 }, owner));
        Assert.Equal(-33.8688m, set.GetProperty("latitude").GetDecimal());
        Assert.Equal(151.2093m, set.GetProperty("longitude").GetDecimal());

        // обидва null -> очистити
        var clear = await J(await fx.Send(HttpMethod.Put, url,
            new Dictionary<string, object?> { ["name"] = "Центр 2", ["timezone"] = "Europe/Kyiv", ["latitude"] = null, ["longitude"] = null }, owner));
        Assert.Equal(JsonValueKind.Null, clear.GetProperty("latitude").ValueKind);
        Assert.Equal(JsonValueKind.Null, clear.GetProperty("longitude").ValueKind);
    }

    public static IEnumerable<object[]> BadCoordinates() =>
    [
        [new Dictionary<string, object?> { ["latitude"] = 50.4 }],                                   // лише одне
        [new Dictionary<string, object?> { ["longitude"] = 30.5 }],                                  // лише одне
        [new Dictionary<string, object?> { ["latitude"] = 50.4, ["longitude"] = null }],             // число + null
        [new Dictionary<string, object?> { ["latitude"] = null, ["longitude"] = 30.5 }],             // null + число
        [new Dictionary<string, object?> { ["latitude"] = 90.1, ["longitude"] = 30.5 }],             // поза діапазоном
        [new Dictionary<string, object?> { ["latitude"] = -90.1, ["longitude"] = 30.5 }],
        [new Dictionary<string, object?> { ["latitude"] = 50.4, ["longitude"] = 180.5 }],
        [new Dictionary<string, object?> { ["latitude"] = 50.4, ["longitude"] = -180.5 }],
        [new Dictionary<string, object?> { ["latitude"] = "50.4", ["longitude"] = "30.5" }],         // нечислові
        [new Dictionary<string, object?> { ["latitude"] = "NaN", ["longitude"] = "Infinity" }],
        [new Dictionary<string, object?> { ["latitude"] = true, ["longitude"] = false }],
    ];

    [SkippableTheory]
    [MemberData(nameof(BadCoordinates))]
    public async Task invalid_coordinates_are_422_on_create_and_update_and_change_nothing(Dictionary<string, object?> coords)
    {
        NeedDb();
        var (_, owner) = await NewTenantAsync();
        Dictionary<string, object?> Body(string name)
        {
            var b = new Dictionary<string, object?> { ["name"] = name, ["timezone"] = "Europe/Kyiv" };
            foreach (var kv in coords) b[kv.Key] = kv.Value;
            return b;
        }

        var created = await fx.Send(HttpMethod.Post, "/api/beauty/locations", Body("Новий"), owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);
        Assert.Equal("invalid_coordinates", await Code(created));

        var loc = await CreateLocationAsync(owner, new { name = "Старий", timezone = "Europe/Kyiv", latitude = 50.45, longitude = 30.52 });
        var put = await fx.Send(HttpMethod.Put, $"/api/beauty/locations/{loc.GetProperty("id").GetGuid()}", Body("Старий"), owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("invalid_coordinates", await Code(put));

        var list = await J(await fx.Send(HttpMethod.Get, "/api/beauty/locations", null, owner));
        var row = list.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == loc.GetProperty("id").GetGuid());
        Assert.Equal(50.45m, row.GetProperty("latitude").GetDecimal());
        Assert.Equal(30.52m, row.GetProperty("longitude").GetDecimal());
        Assert.DoesNotContain(list.EnumerateArray(), x => x.GetProperty("name").GetString() == "Новий");
    }

    [SkippableTheory]
    [InlineData("""{"assistant":{"enabled":true,"bookingFlow":true}}""", HttpStatusCode.OK)]
    [InlineData("""{"assistant":{"enabled":true,"bookingFlow":false}}""", HttpStatusCode.OK)]
    [InlineData("""{"assistant":{"enabled":true}}""", HttpStatusCode.OK)]
    [InlineData("""{"assistant":{"bookingFlow":"true"}}""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("""{"assistant":{"bookingFlow":1}}""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("""{"assistant":{"bookingFlow":null}}""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("""{"assistant":{"bookingFlow":{}}}""", HttpStatusCode.UnprocessableEntity)]
    public async Task channel_settings_booking_flow_must_be_boolean_or_absent(string settingsJson, HttpStatusCode expected)
    {
        NeedDb();
        var (_, owner) = await NewTenantAsync();
        var r = await fx.Send(HttpMethod.Put, $"/api/beauty/channels/{Guid.NewGuid()}",
            new { type = "telegram", name = "bot", isActive = true, token = "700111:BOTTOKENSECRETVALUE", webhookSecret = "whsecret", settingsJson }, owner, client: ChannelClient());
        Assert.Equal(expected, r.StatusCode);
        if (expected == HttpStatusCode.UnprocessableEntity) Assert.Equal("invalid_settings", await Code(r));
    }

    [SkippableFact]
    public async Task inbox_location_message_is_shown_as_label_only_in_thread_and_preview()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var client = ChannelClient();
        var channelId = Guid.NewGuid();
        var put = await fx.Send(HttpMethod.Put, $"/api/beauty/channels/{channelId}",
            new { type = "telegram", name = "bot", isActive = true, token = "700111:BOTTOKENSECRETVALUE", webhookSecret = "whsecret" }, owner.AccessToken, client: client);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Guid conv;
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var c = new Conversation { ChannelId = channelId, ExternalChatId = "5550987", LastMessageAt = DateTimeOffset.UtcNow };
            ctx.Add(c);
            await ctx.SaveChangesAsync();
            ctx.Add(new Message
            {
                ConversationId = c.Id, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client, Kind = "location",
                Body = "[локація]", Status = "held", SentAt = DateTimeOffset.UtcNow, ExternalMessageId = "1", IdempotencyKey = $"telegram:{Guid.NewGuid():N}",
            });
            await ctx.SaveChangesAsync();
            conv = c.Id;
        }

        var thread = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/conversations/{conv}", null, owner.AccessToken, client: client));
        var m = thread.GetProperty("messages").EnumerateArray().Single();
        Assert.Equal("location", m.GetProperty("kind").GetString());
        Assert.Equal("[локація]", m.GetProperty("body").GetString());
        Assert.DoesNotContain("latitude", m.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var list = await J(await fx.Send(HttpMethod.Get, "/api/beauty/conversations?filter=all", null, owner.AccessToken, client: client));
        var item = list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == conv);
        Assert.Equal("[локація]", item.GetProperty("lastMessagePreview").GetString());
    }
}
