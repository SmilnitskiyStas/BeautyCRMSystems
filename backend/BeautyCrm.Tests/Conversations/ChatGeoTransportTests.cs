using System.Text.Json;
using BeautyCrm.Application.Features.BeautyChatFlow;
using BeautyCrm.Infrastructure.AI.Beauty.Conversations;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Loc = (string Name, decimal? Lat, decimal? Lon);

namespace BeautyCrm.Tests.Conversations;

/// <summary>TASK-837 (ADR-020, §28.9): транспорт геолокації Telegram. Чисті перевірки без БД: парсер, reply-клавіатура, haversine, пам'ять.</summary>
public sealed class ChatGeoTransportUnitTests
{
    private sealed class Creds : IChannelCredentialsProvider
    {
        public ChannelCredentials? Get(string channel) => new("111:TOKEN", "sec");
    }

    private static TelegramAdapter Adapter(FakeBotApi? bot = null) => new(new HttpClient(bot ?? new FakeBotApi()), new Creds());

    private static string Update(string messageBody, string chatType = "private") =>
        "{\"update_id\":1,\"message\":{\"message_id\":7,\"date\":1790000000,\"chat\":{\"id\":100,\"type\":\"" + chatType
        + "\"},\"from\":{\"id\":100,\"is_bot\":false,\"first_name\":\"Anna\"}," + messageBody + "}}";

    [Fact]
    public async Task location_message_becomes_kind_location_with_marker_body_and_rounded_transient_point()
    {
        var m = Assert.Single(await Adapter().ParseInboundAsync(Update(
            "\"location\":{\"latitude\":50.123456789,\"longitude\":30.987654321,\"live_period\":600,\"horizontal_accuracy\":12.5}")));
        Assert.Equal(InboundKinds.Location, m.Kind);
        Assert.Equal("[локація]", m.Text);
        Assert.Equal(50.1235, m.TransientLatitude);
        Assert.Equal(30.9877, m.TransientLongitude);
        // координати не з'являються в текстових представленнях повідомлення
        Assert.DoesNotContain("50.12", m.ToString());
        Assert.DoesNotContain("50.12", m.Text);
    }

    [Fact]
    public async Task venue_uses_its_location()
    {
        var m = Assert.Single(await Adapter().ParseInboundAsync(Update(
            "\"venue\":{\"location\":{\"latitude\":49.5,\"longitude\":24.25},\"title\":\"X\",\"address\":\"Y\"},\"location\":{\"latitude\":49.5,\"longitude\":24.25}")));
        Assert.Equal(InboundKinds.Location, m.Kind);
        Assert.Equal(49.5, m.TransientLatitude);
    }

    [Theory]
    [InlineData("""{"forward_origin":{"type":"user"},"location":{"latitude":50.1,"longitude":30.1}}""")]
    [InlineData("""{"forward_date":1700000000,"location":{"latitude":50.1,"longitude":30.1}}""")]
    [InlineData("""{"forward_from":{"id":5},"location":{"latitude":50.1,"longitude":30.1}}""")]
    [InlineData("""{"location":{"latitude":91.0,"longitude":30.1}}""")]
    [InlineData("""{"location":{"latitude":50.1,"longitude":181.0}}""")]
    [InlineData("""{"location":{"latitude":"50.1","longitude":30.1}}""")]
    [InlineData("""{"location":{"latitude":50.1}}""")]
    public async Task forwarded_or_invalid_location_is_unsupported_and_carries_no_coordinates(string fragment)
    {
        var m = Assert.Single(await Adapter().ParseInboundAsync(Update(fragment[1..^1])));
        Assert.Equal(InboundKinds.Unsupported, m.Kind);
        Assert.Equal("[location]", m.Text);
        Assert.Null(m.TransientLatitude);
        Assert.Null(m.TransientLongitude);
    }

    [Fact]
    public async Task edited_message_and_group_chat_locations_are_ignored()
    {
        var edited = """{"update_id":2,"edited_message":{"message_id":7,"chat":{"id":100,"type":"private"},"from":{"id":100,"is_bot":false},"location":{"latitude":50.1,"longitude":30.1}}}""";
        Assert.Empty(await Adapter().ParseInboundAsync(edited));
        Assert.Empty(await Adapter().ParseInboundAsync(Update("\"location\":{\"latitude\":50.1,\"longitude\":30.1}", chatType: "group")));
    }

    [Fact]
    public void flow_callback_data_is_valid_for_telegram_and_requires_index()
    {
        const string tok = "AAAAAAAAAAAAAAAAAAAAAA";
        foreach (var verb in new[] { "fl", "fs", "fp", "fd", "ft", "fn" })
        {
            Assert.True(TelegramAdapter.IsValidCallbackData($"{verb}:{tok}:3"));
            Assert.False(TelegramAdapter.IsValidCallbackData($"{verb}:{tok}"));
        }
        Assert.False(TelegramAdapter.IsValidCallbackData($"fx:{tok}:3"));
    }

    [Fact]
    public async Task location_request_keyboard_is_sent_as_reply_keyboard_with_request_location()
    {
        var bot = new FakeBotApi();
        var layout = ChatActionLayout.ForReply(ChatReplyKeyboard.LocationRequest());
        Assert.True(layout.IsValid());
        await Adapter(bot).SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "100", FlowTemplates.GeoOffer, null, layout), default);
        var markup = JsonDocument.Parse(Assert.Single(bot.Messages).Markup!).RootElement;
        Assert.True(markup.GetProperty("one_time_keyboard").GetBoolean());
        Assert.True(markup.GetProperty("resize_keyboard").GetBoolean());
        var rows = markup.GetProperty("keyboard");
        Assert.True(rows[0][0].GetProperty("request_location").GetBoolean());
        Assert.Equal("📍 Поділитись локацією", rows[0][0].GetProperty("text").GetString());
        Assert.False(rows[1][0].TryGetProperty("request_location", out _));
        Assert.Equal("Обрати зі списку", rows[1][0].GetProperty("text").GetString());
    }

    [Fact]
    public void location_request_actions_json_round_trips()
    {
        var parsed = ChatActionLayout.TryParse(ChatReplyKeyboard.LocationRequest().ToActionsJson());
        Assert.NotNull(parsed?.Reply);
        Assert.True(parsed!.Reply!.Rows[0][0].RequestLocation);
        Assert.False(parsed.Reply.Rows[1][0].RequestLocation);
        Assert.False(new ChatReplyKeyboard([[new ChatReplyButton("x", true, true)]]).IsValid()); // contact і location разом - заборонено
    }

    [Fact]
    public void haversine_matches_known_distance_and_sorts_with_coordinate_less_locations_last()
    {
        // Київ - Львів ≈ 468 км
        Assert.InRange(GeoDistance.Km(50.4501, 30.5234, 49.8397, 24.0297), 465, 472);
        Assert.Equal(0, GeoDistance.Km(50, 30, 50, 30), 6);
        FlowLocationItem L(string n, double? la, double? lo) => new(Guid.NewGuid(), n, null, "UTC", la, lo);
        var far = L("Дальній", 50.6, 30.9);
        var near = L("Ближній", 50.46, 30.52);
        var none = L("Аа без координат", null, null);
        var sorted = GeoDistance.Sort([none, far, near], 50.45, 30.52).Select(x => x.Location.Name).ToList();
        Assert.Equal(["Ближній", "Дальній", "Аа без координат"], sorted);
        Assert.Null(GeoDistance.Sort([none], 1, 1)[0].Km);
    }

    [Theory]
    [InlineData(0.04, "0,1 км")]
    [InlineData(1.24, "1,2 км")]
    [InlineData(99.94, "99,9 км")]
    [InlineData(100.4, "100 км")]
    [InlineData(468.7, "469 км")]
    public void distance_is_formatted_with_comma_and_whole_km_from_100(double km, string expected) =>
        Assert.Equal(expected, FlowTemplates.Distance(km));

    [Fact]
    public void near_label_fits_the_button_limit_and_keeps_the_distance()
    {
        var label = FlowTemplates.NearLabel(new string('Ж', 60), 12.34);
        Assert.True(label.EnumerateRunes().Count() <= ChatKeyboardLayout.MaxLabel);
        Assert.EndsWith(" · 12,3 км", label);
        Assert.Equal("Центр", FlowTemplates.NearLabel("Центр", null));
    }

    [Fact]
    public void transient_location_is_taken_once_and_expires_by_ttl()
    {
        var clock = new TestClock();
        var signals = new ConversationSignals(clock);
        var (conv, msg) = (Guid.NewGuid(), Guid.NewGuid());
        signals.StoreLocation(conv, msg, 50.1234, 30.5678, TimeSpan.FromSeconds(300));
        Assert.Equal(1, signals.TransientLocationCount);
        Assert.Null(signals.TakeLocation(conv, Guid.NewGuid())); // інше повідомлення
        var p = signals.TakeLocation(conv, msg);
        Assert.Equal(50.1234, p!.Value.Latitude);
        Assert.Null(signals.TakeLocation(conv, msg)); // читання видаляє
        Assert.Equal(0, signals.TransientLocationCount);
        Assert.DoesNotContain("50.12", p.Value.ToString());

        signals.StoreLocation(conv, msg, 50.1, 30.5, TimeSpan.FromSeconds(300));
        clock.Advance(TimeSpan.FromSeconds(301));
        Assert.Null(signals.TakeLocation(conv, msg)); // TTL минув
    }
}

/// <summary>
/// TASK-837 (§28.9, §28.15 сц. 13-14): геолокація на кроці закладу, реальний PostgreSQL з RLS, fake Telegram. Координати клієнта не потрапляють
/// у БД, логи, збережені клавіатури; відстані - лише в inline-версії.
/// </summary>
public sealed class ChatGeoTransportTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public ChatGeoTransportTests(PostgresRlsFixture db) => _db = db;

    private const string Hours =
        """{"mon":[{"from":"08:00","to":"20:00"}],"tue":[{"from":"08:00","to":"20:00"}],"wed":[{"from":"08:00","to":"20:00"}],"thu":[{"from":"08:00","to":"20:00"}],"fri":[{"from":"08:00","to":"20:00"}],"sat":[{"from":"08:00","to":"20:00"}],"sun":[{"from":"08:00","to":"20:00"}]}""";

    private static readonly IDictionary<string, string?> GeoOn = new Dictionary<string, string?>
    {
        ["ChatButtons:Enabled"] = "true", ["ChatFlow:Enabled"] = "true", ["ChatFlow:GeoEnabled"] = "true",
    };

    // Координати клієнта: різке значення, якого немає в інших тестах (перевіряється відсутність у БД і логах).
    private const double ClientLat = 50.4990, ClientLon = 30.5020;
    private static readonly string[] ClientNumbers = ["50.499", "30.502"];

    private static long _id = 70_000;

    private ChatHarness H(IDictionary<string, string?>? cfg = null) => ChatHarness.Create(_db, extraConfig: cfg ?? GeoOn);

    private sealed record Btn(string Text, string Data);

    private static List<Btn> Inline(FakeBotApi.Sent m)
    {
        if (m.Markup is null) return [];
        using var doc = JsonDocument.Parse(m.Markup);
        return doc.RootElement.TryGetProperty("inline_keyboard", out var rows)
            ? rows.EnumerateArray().SelectMany(r => r.EnumerateArray()).Select(b => new Btn(b.GetProperty("text").GetString()!, b.GetProperty("callback_data").GetString()!)).ToList()
            : [];
    }

    private static bool IsReplyMarkup(FakeBotApi.Sent m) => m.Markup is not null && m.Markup.Contains("\"keyboard\"", StringComparison.Ordinal);
    private static bool IsRemoveKeyboard(FakeBotApi.Sent m) => m.Markup is not null && m.Markup.Contains("remove_keyboard", StringComparison.Ordinal);
    private static FakeBotApi.Sent LastInline(ChatHarness h) => h.Bot.Messages.Last(m => Inline(m).Count > 0);
    private static string Ref(FakeBotApi.Sent m) => m.MessageId.ToString();

    /// <summary>Заклади пари «послуга + майстер»: seed-«Салон Центр» + додаткові, канал відкріплений від закладу (крок закладу показується).</summary>
    private async Task<ChatSeed> SeedLocationsAsync(ChatHarness h, Loc[] extra, string? aiMode = null)
    {
        var s = await h.SeedAsync(aiMode: aiMode);
        await using var ctx = _db.CreateContext(s.TenantId);
        await ctx.Channels.Where(c => c.Id == s.ChannelId).ExecuteUpdateAsync(u => u.SetProperty(c => c.LocationId, (Guid?)null));
        await ctx.Locations.Where(l => l.Id == s.LocationId).ExecuteUpdateAsync(u => u.SetProperty(l => l.Latitude, 50.4501m).SetProperty(l => l.Longitude, 30.5234m));
        foreach (var (name, lat, lon) in extra)
        {
            var loc = new Location { Name = name, Address = "вул. Тестова 1", Timezone = "Europe/Kyiv", Latitude = lat, Longitude = lon };
            ctx.Add(loc);
            await ctx.SaveChangesAsync();
            ctx.Add(new SpecialistLocation { SpecialistId = s.SpecialistId, LocationId = loc.Id, WorkingHours = Hours });
            await ctx.SaveChangesAsync();
        }
        return s;
    }

    private static readonly Loc[] FourLocations =
    [
        ("Подол", 50.4660m, 30.5160m), ("Оболонь", 50.5010m, 30.5000m), ("Без координат", null, null),
    ];

    private static Loc[] ManyLocations(int n) =>
        Enumerable.Range(1, n).Select(i => ($"Філія {i}", (decimal?)(50.40m + i / 100m), (decimal?)30.5m)).ToArray();

    /// <summary>/book -> «Стрижка» (єдиний майстер пропускається) -> крок закладу.</summary>
    private async Task<Guid> ToLocationStepAsync(ChatHarness h, ChatSeed s)
    {
        var conv = await h.SayAsync(s, "/book", kind: InboundKinds.Command);
        var start = LastInline(h);
        await h.PressAsync(s, Inline(start).First(b => b.Text.Contains("Стрижка")).Data, Ref(start));
        return conv;
    }

    private async Task<(Guid Conv, InboundMessage Parsed)> SendLocationAsync(ChatHarness h, ChatSeed s, double lat, double lon, string extra = "")
    {
        var id = Interlocked.Increment(ref _id);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var json = "{\"update_id\":" + id + ",\"message\":{\"message_id\":" + id + ",\"date\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            + ",\"chat\":{\"id\":100,\"type\":\"private\"},\"from\":{\"id\":100,\"is_bot\":false,\"first_name\":\"Anna\"}," + extra
            + "\"location\":{\"latitude\":" + lat.ToString(inv) + ",\"longitude\":" + lon.ToString(inv) + "}}}";
        var adapter = new TelegramAdapter(new HttpClient(h.Bot), h.Sp.GetRequiredService<IChannelCredentialsProvider>());
        var parsed = Assert.Single(await adapter.ParseInboundAsync(json));
        using var scope = h.Sp.CreateScope();
        var p = scope.ServiceProvider;
        p.GetRequiredService<BeautyCrm.Infrastructure.Data.Tenancy.TenantContext>().SetTenant(s.TenantId);
        using var _ = p.GetRequiredService<ChannelRequestContext>().Begin(
            new ResolvedChannel(s.TenantId, s.ChannelId, "telegram", new ChannelCredentials(s.Token, "whsecret")));
        await p.GetRequiredService<InboundIngestCore>().IngestAsync([parsed], default);
        var conv = await h.ConversationIdAsync(s);
        await h.TurnAsync(s.TenantId, conv);
        return (conv, parsed);
    }

    private async Task<List<ChatBookingFlow>> FlowsAsync(ChatSeed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await ctx.ChatBookingFlows.AsNoTracking().OrderBy(f => f.CreatedAt).ToListAsync();
    }

    /// <summary>Скільки рядків усіх таблиць beauty_* (крім закладів, де координати закладу легальні) містять підрядок.</summary>
    private async Task<List<string>> TablesContainingAsync(Guid tenantId, params string[] needles)
    {
        var hits = new List<string>();
        await using var conn = new NpgsqlConnection(_db.AdminConnectionString);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT t.table_name FROM information_schema.tables t JOIN information_schema.columns c ON c.table_schema=t.table_schema AND c.table_name=t.table_name AND c.column_name='tenant_id' "
            + "WHERE t.table_schema='public' AND t.table_type='BASE TABLE' AND t.table_name LIKE 'beauty\\_%' AND t.table_name <> 'beauty_locations'", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) tables.Add(r.GetString(0));
        foreach (var t in tables)
            foreach (var needle in needles)
            {
                await using var q = new NpgsqlCommand($"SELECT count(*) FROM \"{t}\" x WHERE x.tenant_id = @t AND x::text LIKE @p", conn);
                q.Parameters.AddWithValue("t", tenantId);
                q.Parameters.AddWithValue("p", "%" + needle + "%");
                if ((long)(await q.ExecuteScalarAsync())! > 0) hits.Add($"{t}:{needle}");
            }
        return hits;
    }

    // ---------------------------------------------------------------- пропозиція

    [SkippableFact]
    public async Task location_step_sends_inline_list_and_then_separate_reply_keyboard_with_request_location()
    {
        var h = H(); var s = await SeedLocationsAsync(h, FourLocations);
        var conv = await ToLocationStepAsync(h, s);

        var list = LastInline(h);
        Assert.Contains("Оберіть заклад", list.Text);
        Assert.Equal(4, Inline(list).Count(b => b.Data.StartsWith("fl:", StringComparison.Ordinal)));

        var offer = Assert.Single(h.Bot.Messages, IsReplyMarkup);
        Assert.Equal(FlowTemplates.GeoOffer, offer.Text);
        Assert.True(JsonDocument.Parse(offer.Markup!).RootElement.GetProperty("keyboard")[0][0].GetProperty("request_location").GetBoolean());
        Assert.True(h.Bot.Messages.ToList().IndexOf(list) < h.Bot.Messages.ToList().IndexOf(offer)); // inline-список першим
        Assert.Empty(Inline(offer)); // inline і reply не змішуються на одному повідомленні

        var c = await h.ConversationAsync(s);
        Assert.NotNull(c.ReplyKeyboardOpenAt);
        Assert.Empty(h.Ai.Calls);
        Assert.Equal(conv, c.Id);
    }

    [SkippableFact]
    public async Task geo_is_not_offered_by_default_for_more_than_eight_locations_or_without_any_coordinates()
    {
        // GeoEnabled = false (за замовчуванням)
        var off = H(new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true", ["ChatFlow:Enabled"] = "true" });
        var so = await SeedLocationsAsync(off, FourLocations);
        await ToLocationStepAsync(off, so);
        Assert.DoesNotContain(off.Bot.Messages, IsReplyMarkup);
        Assert.Null((await off.ConversationAsync(so)).ReplyKeyboardOpenAt);
        // клієнт все одно надіслав локацію: координати навіть у пам'ять не потрапляють
        await SendLocationAsync(off, so, ClientLat, ClientLon);
        Assert.Equal(0, off.Signals.TransientLocationCount);

        // 9 закладів (> 8)
        var h = H();
        var s = await SeedLocationsAsync(h, ManyLocations(8));
        await ToLocationStepAsync(h, s);
        Assert.DoesNotContain(h.Bot.Messages, IsReplyMarkup);

        // жоден заклад без координат
        var nc = H();
        var sn = await SeedLocationsAsync(nc, [("Б", null, null), ("В", null, null)]);
        await using (var ctx = _db.CreateContext(sn.TenantId))
            await ctx.Locations.Where(l => l.Id == sn.LocationId).ExecuteUpdateAsync(u => u.SetProperty(l => l.Latitude, (decimal?)null).SetProperty(l => l.Longitude, (decimal?)null));
        await ToLocationStepAsync(nc, sn);
        Assert.DoesNotContain(nc.Bot.Messages, IsReplyMarkup);
    }

    // ---------------------------------------------------------------- сортування

    [SkippableFact]
    public async Task shared_location_sorts_inline_by_distance_while_stored_data_stays_in_name_order_without_distances_or_coordinates()
    {
        var h = H(); var s = await SeedLocationsAsync(h, FourLocations);
        await ToLocationStepAsync(h, s);
        var before = h.Bot.Messages.Count;

        var (conv, parsed) = await SendLocationAsync(h, s, ClientLat, ClientLon);
        Assert.Equal(ClientLat, parsed.TransientLatitude);

        var sent = h.Bot.Messages.Skip(before).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(FlowTemplates.LocationThanks, sent[0].Text);
        Assert.True(IsRemoveKeyboard(sent[0])); // reply-клавіатуру прибрано окремим повідомленням
        Assert.Equal(FlowTemplates.LocationNear, sent[1].Text);
        Assert.DoesNotContain("Подол", sent[1].Text); // перелік закладів у тіло не потрапляє

        // inline-версія: найближчі першими, відстані в підписах, заклад без координат - в кінці без відстані
        var labels = Inline(sent[1]).Where(b => b.Data.StartsWith("fl:", StringComparison.Ordinal)).Select(b => b.Text).ToList();
        Assert.Equal(4, labels.Count);
        Assert.StartsWith("Оболонь · ", labels[0]); Assert.EndsWith(" км", labels[0]);
        Assert.StartsWith("Подол · ", labels[1]);
        Assert.StartsWith("Салон Центр · ", labels[2]);
        Assert.Equal("Без координат", labels[3]);
        Assert.Contains(Inline(sent[1]), b => b.Text == FlowTemplates.NavLabel(FlowNav.Cancel, false));

        // збережена версія: порядок назви, без відстаней
        var outbound = await h.OutboundAsync(s);
        var near = Assert.Single(outbound, m => m.Body == FlowTemplates.LocationNear);
        Assert.DoesNotContain("км", near.Actions!);
        var stored = ChatKeyboardLayout.ReadLabels(near.Actions).Where(l => !l.StartsWith("✖") && !l.StartsWith("⬅")).ToList();
        Assert.Equal(["Без координат", "Оболонь", "Подол", "Салон Центр"], stored);
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            var kb = await ctx.ChatKeyboards.AsNoTracking().Where(k => k.MessageId == near.Id).SingleAsync();
            Assert.DoesNotContain("км", kb.Options);
            var options = KeyboardJson.TryParse(kb.Options)!;
            Assert.Equal(["Без координат", "Оболонь", "Подол", "Салон Центр"], options.Where(o => o.Verb == "fl").Select(o => o.Label).ToList());
        }

        // inbound: kind=location, тіло-мітка
        var inbound = (await h.MessagesAsync(s)).Single(m => m.Kind == InboundKinds.Location);
        Assert.Equal("[локація]", inbound.Body);
        Assert.Equal("processed", inbound.Status);

        // потік пересортовано: нова версія, крок той самий
        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal("location", flow.Step);
        Assert.Equal(2, flow.Version);

        // координат немає ніде: ні в БД (усі beauty_*, крім координат закладів), ні в логах, ні в пам'яті
        Assert.Empty(await TablesContainingAsync(s.TenantId, ClientNumbers[0], ClientNumbers[1], " км"));
        Assert.DoesNotContain(ClientNumbers[0], h.Logs.All);
        Assert.DoesNotContain(ClientNumbers[1], h.Logs.All);
        Assert.DoesNotContain(" км", h.Logs.All);
        Assert.Contains("result=geo_sorted", h.Logs.All);
        Assert.Equal(0, h.Signals.TransientLocationCount);
        Assert.Null((await h.ConversationAsync(s)).ReplyKeyboardOpenAt);
        Assert.Empty(h.Ai.Calls);
        Assert.Equal(conv, (await h.ConversationAsync(s)).Id);

        // натискання найближчого (індекс з inline-версії відповідає збереженим options): Оболонь -> час
        await h.PressAsync(s, Inline(sent[1]).First(b => b.Text.StartsWith("Оболонь")).Data, Ref(sent[1]));
        Assert.Contains("Оболонь", h.Bot.Messages.Last().Text);
        Assert.Contains("Вільний час", h.Bot.Messages.Last().Text);
        Assert.Equal("time", (await FlowsAsync(s)).Single().Step);
    }

    [SkippableFact]
    public async Task all_locations_button_restores_plain_list_with_reset_notice()
    {
        var h = H();
        var s = await SeedLocationsAsync(h, ManyLocations(6));
        await ToLocationStepAsync(h, s); // 7 закладів: 2 сторінки
        var before = h.Bot.Messages.Count;
        await SendLocationAsync(h, s, ClientLat, ClientLon);
        var near = h.Bot.Messages.Skip(before).Last();
        var inline = Inline(near);
        Assert.Equal(5, inline.Count(b => b.Data.StartsWith("fl:", StringComparison.Ordinal)));
        var all = inline.First(b => b.Text == FlowTemplates.AllLocationsLabel);

        await h.PressAsync(s, all.Data, Ref(near));
        var plain = LastInline(h); // після списку, за контрактом (reply_keyboard_open_at = NULL), може йти повторна пропозиція гео
        Assert.StartsWith(FlowTemplates.GeoReset, plain.Text);
        Assert.Contains("Оберіть заклад", plain.Text);
        Assert.DoesNotContain(" км", string.Join("|", Inline(plain).Select(b => b.Text)));
        Assert.Equal("location", (await FlowsAsync(s)).Single().Step);
    }

    // ---------------------------------------------------------------- відсутні координати, відмова, невикористана локація

    [SkippableFact]
    public async Task missing_coordinates_in_memory_gives_retry_with_same_list_and_removes_reply_keyboard()
    {
        var h = H(); var s = await SeedLocationsAsync(h, FourLocations);
        await ToLocationStepAsync(h, s);
        var before = h.Bot.Messages.Count;
        var versionBefore = (await FlowsAsync(s)).Single().Version;

        // «рестарт між ingest і ходом»: повідомлення збережено, а координат у пам'яті немає
        var id = Interlocked.Increment(ref _id);
        var (conv, _) = await h.IngestAsync(s, InboundKinds.LocationBody, messageId: id, kind: InboundKinds.Location);
        await h.TurnAsync(s.TenantId, conv!.Value);

        var sent = h.Bot.Messages.Skip(before).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(FlowTemplates.LocationRetry, sent[0].Text);
        Assert.True(IsRemoveKeyboard(sent[0]));
        Assert.Contains("Оберіть заклад", sent[1].Text);
        Assert.Equal(4, Inline(sent[1]).Count(b => b.Data.StartsWith("fl:", StringComparison.Ordinal)));
        Assert.DoesNotContain(" км", string.Join("|", Inline(sent[1]).Select(b => b.Text)));
        Assert.Equal(versionBefore + 1, (await FlowsAsync(s)).Single().Version);
        Assert.Contains("result=geo_missing", h.Logs.All);
        Assert.Empty(h.Ai.Calls);
    }

    [SkippableFact]
    public async Task declining_the_location_keeps_the_list_and_removes_the_reply_keyboard()
    {
        var h = H(); var s = await SeedLocationsAsync(h, FourLocations);
        await ToLocationStepAsync(h, s);
        var list = LastInline(h);
        var version = (await FlowsAsync(s)).Single().Version;
        var before = h.Bot.Messages.Count;

        await h.SayAsync(s, ChatReplyKeyboard.DeclineLocationLabel);

        var reply = Assert.Single(h.Bot.Messages.Skip(before));
        Assert.Equal(FlowTemplates.GeoDeclined, reply.Text);
        Assert.True(IsRemoveKeyboard(reply));
        Assert.Equal(version, (await FlowsAsync(s)).Single().Version); // список без змін
        Assert.Null((await h.ConversationAsync(s)).ReplyKeyboardOpenAt);
        Assert.Empty(h.Ai.Calls);

        // стара клавіатура лишається чинною
        await h.PressAsync(s, Inline(list).First(b => b.Text == "Оболонь").Data, Ref(list));
        Assert.Contains("Вільний час", h.Bot.Messages.Last().Text);
    }

    [SkippableFact]
    public async Task choosing_a_location_with_open_reply_keyboard_adds_short_chosen_message_that_removes_it()
    {
        var h = H(); var s = await SeedLocationsAsync(h, FourLocations);
        await ToLocationStepAsync(h, s);
        var list = LastInline(h);
        var before = h.Bot.Messages.Count;

        await h.PressAsync(s, Inline(list).First(b => b.Text == "Подол").Data, Ref(list));

        var sent = h.Bot.Messages.Skip(before).ToList();
        Assert.Equal(2, sent.Count);
        Assert.StartsWith("Обрано: заклад «Подол»", sent[0].Text);
        Assert.True(IsRemoveKeyboard(sent[0]));
        Assert.Contains("Вільний час", sent[1].Text);
        Assert.Null((await h.ConversationAsync(s)).ReplyKeyboardOpenAt);
    }

    [SkippableFact]
    public async Task location_outside_the_location_step_gets_a_throttled_unused_reply_and_nothing_is_stored()
    {
        var h = H(); var s = await SeedLocationsAsync(h, FourLocations);

        await SendLocationAsync(h, s, ClientLat, ClientLon); // потоку немає
        await SendLocationAsync(h, s, ClientLat, ClientLon); // повтор у межах 10 хв - тиша

        Assert.Single(h.Bot.Messages);
        Assert.Equal(FlowTemplates.LocationUnused, h.Bot.Messages[0].Text);
        Assert.Contains("result=geo_unused", h.Logs.All);
        Assert.Equal(0, h.Signals.TransientLocationCount); // координати забрано й відкинуто
        Assert.Empty(await TablesContainingAsync(s.TenantId, ClientNumbers[0], ClientNumbers[1]));
        Assert.Empty(h.Ai.Calls);
    }

    [SkippableFact]
    public async Task forwarded_location_is_rejected_stored_as_unsupported_without_coordinates_and_never_sorts()
    {
        var h = H(); var s = await SeedLocationsAsync(h, FourLocations);
        await ToLocationStepAsync(h, s);
        var version = (await FlowsAsync(s)).Single().Version;

        var (_, parsed) = await SendLocationAsync(h, s, ClientLat, ClientLon, extra: "\"forward_origin\":{\"type\":\"user\"},");

        Assert.Equal(InboundKinds.Unsupported, parsed.Kind);
        Assert.Null(parsed.TransientLatitude);
        var inbound = (await h.MessagesAsync(s)).Single(m => m.Direction == MessageDirection.Inbound && m.Body == "[location]");
        Assert.Equal(InboundKinds.Unsupported, inbound.Kind);
        Assert.Equal(version, (await FlowsAsync(s)).Single().Version); // пересортування не було
        Assert.DoesNotContain(h.Bot.Messages, m => m.Text == FlowTemplates.LocationThanks);
        Assert.Equal(0, h.Signals.TransientLocationCount);
        Assert.Empty(await TablesContainingAsync(s.TenantId, ClientNumbers[0], ClientNumbers[1]));
    }

    [SkippableFact]
    public async Task suggest_mode_ignores_the_location_silently_and_discards_coordinates()
    {
        var h = H(); var s = await SeedLocationsAsync(h, FourLocations, aiMode: "suggest");
        await SendLocationAsync(h, s, ClientLat, ClientLon);

        Assert.Empty(h.Bot.Messages);
        Assert.Equal(0, h.Signals.TransientLocationCount);
        Assert.Equal("processed", (await h.MessagesAsync(s)).Single(m => m.Kind == InboundKinds.Location).Status);
    }
}
