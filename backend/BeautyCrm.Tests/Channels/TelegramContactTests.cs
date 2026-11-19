using System.Text.Json;
using System.Text.Json.Nodes;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

namespace BeautyCrm.Tests.Channels;

/// <summary>TASK-768 (§25.4, §25.9, §25.10 сценарії 1-2, 19): парсер message.contact і reply-keyboard / remove_keyboard без БД.</summary>
public class TelegramContactTests
{
    private const string Token = "999111:SECRETTOKENVALUE";
    private static TelegramAdapter A(TgRecorder rec) => new(new HttpClient(rec), new ChCreds(Token));

    private static string Upd(string contact, string chat = "{\"id\":42,\"type\":\"private\"}",
        string from = "{\"id\":42,\"is_bot\":false,\"first_name\":\"Ann\"}", string extra = "") =>
        $"{{\"update_id\":1,\"message\":{{\"message_id\":7,\"from\":{from},\"chat\":{chat},\"date\":1700000000,{extra}\"contact\":{contact}}}}}";

    private static string Own(string phone, string extra = "") =>
        Upd($"{{\"phone_number\":\"{phone}\",\"first_name\":\"Ann\",\"last_name\":\"Secret\",\"user_id\":42,\"vcard\":\"BEGIN:VCARD\\nTEL:{phone}\\nEND:VCARD\"}}", extra: extra);

    private static Task<IReadOnlyList<InboundMessage>> P(string body) => A(new TgRecorder()).ParseInboundAsync(body);

    // ------------------------------------------------------------------ парсер

    [Fact]
    public async Task own_contact_is_kind_contact_with_normalized_phone_and_marker_body()
    {
        var m = Assert.Single(await P(Own("380501112233")));
        Assert.Equal((InboundKinds.Contact, ContactVerdicts.Own, "+380501112233", "[контакт]"), (m.Kind, m.ContactVerdict, m.ContactPhone, m.Text));
        Assert.Equal(("42:7", "42", "42", "telegram:42:7"), (m.ExternalMessageId, m.ExternalConversationId, m.SenderId, m.IdempotencyKey));
        Assert.False(m.AckOnly);
        Assert.DoesNotContain("380501112233", m.Text);
        Assert.DoesNotContain("Secret", m.Text);
    }

    [Theory]
    [InlineData("380501112233", "+380501112233")]
    [InlineData("+380501112233", "+380501112233")]
    [InlineData("12025550143", "+12025550143")]
    [InlineData("+442079460958", "+442079460958")]
    public async Task own_contact_phone_formats_are_normalized_to_e164(string raw, string expected) =>
        Assert.Equal(expected, Assert.Single(await P(Own(raw))).ContactPhone);

    [Theory]
    [InlineData("12345")]                    // закоротко
    [InlineData("0501112233")]               // національний формат без + (11..15 цифр без +)
    [InlineData("+38050111223")]             // +380 = рівно 12 цифр
    [InlineData("3805011122334")]
    [InlineData("+0501112233")]              // перша цифра 0
    [InlineData("+380 50 111 22 33")]        // роздільники
    [InlineData("+38050abc2233")]
    [InlineData("١٢٣٤٥٦٧٨٩٠١٢٣")]            // арабські цифри
    [InlineData("+1234567890123456")]        // 16 цифр
    [InlineData("")]
    public async Task own_contact_with_unparseable_number_is_invalid_and_keeps_no_number(string raw)
    {
        var m = Assert.Single(await P(Own(raw)));
        Assert.Equal((InboundKinds.Contact, ContactVerdicts.Invalid, null, "[контакт]"), (m.Kind, m.ContactVerdict, m.ContactPhone, m.Text));
    }

    [Fact]
    public async Task own_contact_without_phone_number_field_is_invalid()
    {
        var m = Assert.Single(await P(Upd("{\"first_name\":\"Ann\",\"user_id\":42}")));
        Assert.Equal((ContactVerdicts.Invalid, null), (m.ContactVerdict, m.ContactPhone));
    }

    public static IEnumerable<object[]> ForeignCases()
    {
        yield return ["no user_id", Upd("{\"phone_number\":\"380509998877\",\"first_name\":\"Bob\"}"), null!];
        yield return ["other user_id", Upd("{\"phone_number\":\"380509998877\",\"first_name\":\"Bob\",\"user_id\":43}"), null!];
        yield return ["user_id as string", Upd("{\"phone_number\":\"380509998877\",\"user_id\":\"42\"}"), null!];
        yield return ["user_id null", Upd("{\"phone_number\":\"380509998877\",\"user_id\":null}"), null!];
        yield return ["forward_origin", Upd("{\"phone_number\":\"380509998877\",\"user_id\":42}", extra: "\"forward_origin\":{\"type\":\"user\"},"), null!];
        yield return ["forward_from (legacy)", Upd("{\"phone_number\":\"380509998877\",\"user_id\":42}", extra: "\"forward_from\":{\"id\":9},"), null!];
        yield return ["forward_date (legacy)", Upd("{\"phone_number\":\"380509998877\",\"user_id\":42}", extra: "\"forward_date\":1,"), null!];
        yield return ["via_bot", Upd("{\"phone_number\":\"380509998877\",\"user_id\":42}", extra: "\"via_bot\":{\"id\":9},"), null!];
        yield return ["chat.id != from.id", Upd("{\"phone_number\":\"380509998877\",\"user_id\":42}", chat: "{\"id\":43,\"type\":\"private\"}"), null!];
        yield return ["from.id missing numeric", Upd("{\"phone_number\":\"380509998877\",\"user_id\":42}", from: "{\"id\":\"42\",\"is_bot\":false}"), null!];
    }

    [Theory]
    [MemberData(nameof(ForeignCases))]
    public async Task foreign_or_forged_contact_is_marked_foreign_and_the_number_is_dropped(string _, string json, string? __)
    {
        var m = Assert.Single(await P(json));
        Assert.Equal((InboundKinds.Contact, ContactVerdicts.Foreign, null, "[контакт]"), (m.Kind, m.ContactVerdict, m.ContactPhone, m.Text));
        Assert.DoesNotContain("509998877", m.ToString());
    }

    [Theory]
    [InlineData("{\"id\":-100,\"type\":\"supergroup\"}")]
    [InlineData("{\"id\":-100,\"type\":\"group\"}")]
    [InlineData("{\"id\":-100,\"type\":\"channel\"}")]
    [InlineData("{\"id\":-100}")]
    public async Task contact_in_groups_and_channels_is_ignored(string chat) =>
        Assert.Empty(await P(Upd("{\"phone_number\":\"380501112233\",\"user_id\":42}", chat: chat)));

    [Fact]
    public async Task contact_from_a_bot_is_ignored() =>
        Assert.Empty(await P(Upd("{\"phone_number\":\"380501112233\",\"user_id\":42}", from: "{\"id\":42,\"is_bot\":true,\"first_name\":\"Bot\"}")));

    [Fact]
    public async Task contact_that_is_not_an_object_falls_back_to_unsupported_placeholder()
    {
        var m = Assert.Single(await P(Upd("\"380501112233\"")));
        Assert.Equal(InboundKinds.Unsupported, m.Kind);
        Assert.Null(m.ContactPhone);
        Assert.DoesNotContain("380501112233", m.Text);
    }

    [Fact]
    public async Task text_next_to_a_contact_object_stays_text_and_never_gets_a_phone()
    {
        var m = Assert.Single(await P(Upd("{\"phone_number\":\"380501112233\",\"user_id\":42}", extra: "\"text\":\"привіт\",")));
        Assert.Equal((InboundKinds.Text, "привіт", null, null), (m.Kind, m.Text, m.ContactPhone, m.ContactVerdict));
    }

    [Fact]
    public async Task inbound_message_to_string_and_equality_print_no_phone_text_or_name()
    {
        var m = Assert.Single(await P(Own("380501112233")));
        var s = m.ToString();
        Assert.DoesNotContain("380501112233", s);
        Assert.DoesNotContain("Ann", s);
        Assert.DoesNotContain("42", s.Replace("telegram", ""));
        Assert.Contains("contact", s);
    }

    [Fact]
    public async Task contact_redelivery_has_the_same_idempotency_key_as_a_message()
    {
        var a = Assert.Single(await P(Own("380501112233")));
        var b = Assert.Single(await P(Own("380501112233")));
        Assert.Equal(a.IdempotencyKey, b.IdempotencyKey);
    }

    [Theory]
    [InlineData("i:phone", true)]
    [InlineData("i:phones", false)]
    [InlineData("i:phone\\n", false)]
    [InlineData("I:phone", false)]
    public void phone_stateless_callback_data_is_valid_only_exactly(string data, bool valid)
    {
        var raw = JsonDocument.Parse($"\"{data}\"").RootElement.GetString();
        Assert.Equal(valid, TelegramAdapter.IsValidCallbackData(raw));
        Assert.Equal(valid, CallbackAddress.TryParse(raw, out var addr) && addr.IsStateless && addr.Verb == ButtonVerbs.Phone);
    }

    [Fact]
    public void phone_button_is_stateless_allowed_in_handoff_and_for_every_keyboard_kind()
    {
        Assert.Contains(ButtonVerbs.Phone, ButtonVerbs.Stateless);
        Assert.True(ButtonVerbs.AllowedInHandoff(ButtonVerbs.Phone));
        Assert.All(KeyboardKinds.All, k => Assert.True(ButtonVerbs.AllowedFor(k, ButtonVerbs.Phone)));
        Assert.Equal("i:phone", CallbackAddress.Build(ButtonVerbs.Phone, null));
    }

    // ------------------------------------------------------------------ відправка

    private static JsonNode Markup(TgRecorder.Call call) => JsonNode.Parse(JsonDocument.Parse(call.Body).RootElement.GetProperty("reply_markup").GetRawText())!;

    [Fact]
    public async Task sendMessage_sends_exact_reply_keyboard_with_request_contact_and_no_parse_mode()
    {
        var rec = new TgRecorder();
        var layout = ChatActionLayout.ForReply(ChatReplyKeyboard.ContactRequest());
        await A(rec).SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "Потрібен номер", Actions: layout), default);
        var call = Assert.Single(rec.Of("sendMessage"));
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal(["chat_id", "text", "reply_markup"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain("parse_mode", call.Body);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"keyboard":[[{"text":"📱 Поділитися контактом","request_contact":true}],[{"text":"Не треба"}]],
             "one_time_keyboard":true,"resize_keyboard":true,"input_field_placeholder":"+380XXXXXXXXX"}
            """), Markup(call)));
    }

    [Fact]
    public async Task reply_keyboard_without_placeholder_omits_the_field_and_honours_flags()
    {
        var rec = new TgRecorder();
        var kb = new ChatReplyKeyboard([[new ChatReplyButton("📱 Поділитися контактом", true)]], OneTime: false, Resize: false);
        await A(rec).SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "x", Actions: ChatActionLayout.ForReply(kb)), default);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"keyboard":[[{"text":"📱 Поділитися контактом","request_contact":true}]],"one_time_keyboard":false,"resize_keyboard":false}
            """), Markup(Assert.Single(rec.Of("sendMessage")))));
    }

    [Fact]
    public async Task sendMessage_sends_remove_keyboard()
    {
        var rec = new TgRecorder();
        await A(rec).SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "Дякуємо", Actions: ChatActionLayout.ForRemoveReply()), default);
        var call = Assert.Single(rec.Of("sendMessage"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"chat_id":"42","text":"Дякуємо","reply_markup":{"remove_keyboard":true}}"""), JsonNode.Parse(call.Body)));
        Assert.DoesNotContain("parse_mode", call.Body);
    }

    [Fact]
    public async Task inline_rows_are_still_sent_as_inline_keyboard()
    {
        var rec = new TgRecorder();
        await A(rec).SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "x", Actions: new ChatActionLayout([[new("A", "i:phone")]])), default);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"inline_keyboard":[[{"text":"A","callback_data":"i:phone"}]]}"""), Markup(Assert.Single(rec.Of("sendMessage")))));
    }

    public static IEnumerable<object[]> InvalidLayouts()
    {
        var ok = ChatReplyKeyboard.ContactRequest();
        yield return ["rows + reply", new ChatActionLayout([[new("A", "i:my")]]) { Reply = ok }];
        yield return ["rows + remove", new ChatActionLayout([[new("A", "i:my")]]) { RemoveReply = true }];
        yield return ["reply + remove", new ChatActionLayout([]) { Reply = ok, RemoveReply = true }];
        yield return ["nothing", new ChatActionLayout([])];
        yield return ["3 rows", ChatActionLayout.ForReply(new ChatReplyKeyboard([[new("a")], [new("b")], [new("c")]]))];
        yield return ["5 buttons", ChatActionLayout.ForReply(new ChatReplyKeyboard([[new("a"), new("b"), new("c")], [new("d"), new("e")]]))];
        yield return ["label 33", ChatActionLayout.ForReply(new ChatReplyKeyboard([[new(new string('я', 33))]]))];
        yield return ["empty label", ChatActionLayout.ForReply(new ChatReplyKeyboard([[new(" ")]]))];
        yield return ["control char", ChatActionLayout.ForReply(new ChatReplyKeyboard([[new("a\nb")]]))];
        yield return ["empty row", ChatActionLayout.ForReply(new ChatReplyKeyboard([[]]))];
        yield return ["placeholder 65", ChatActionLayout.ForReply(new ChatReplyKeyboard([[new("a")]], Placeholder: new string('x', 65)))];
    }

    [Theory]
    [MemberData(nameof(InvalidLayouts))]
    public async Task invalid_layout_goes_out_as_plain_text_without_reply_markup(string _, ChatActionLayout layout)
    {
        Assert.False(layout.IsValid());
        var rec = new TgRecorder();
        await A(rec).SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "42", "x", Actions: layout), default);
        Assert.DoesNotContain("reply_markup", Assert.Single(rec.Of("sendMessage")).Body);
    }

    [Fact]
    public void reply_limits_accept_boundaries()
    {
        Assert.True(ChatActionLayout.ForReply(new ChatReplyKeyboard([[new("a"), new("b")], [new("c"), new("d")]])).IsValid());
        Assert.True(ChatActionLayout.ForReply(new ChatReplyKeyboard([[new(new string('я', 32))]], Placeholder: new string('x', 64))).IsValid());
        Assert.True(ChatActionLayout.ForReply(new ChatReplyKeyboard([[new("📱 " + new string('я', 29))]])).IsValid()); // емодзі = 1 кодова точка
    }

    [Fact]
    public void reply_and_remove_layouts_round_trip_through_beauty_messages_actions_json()
    {
        var json = ChatReplyKeyboard.ContactRequest().ToActionsJson();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"reply":{"rows":[[{"label":"📱 Поділитися контактом","requestContact":true}],[{"label":"Не треба"}]],"oneTime":true,"resize":true,"placeholder":"+380XXXXXXXXX"}}"""), JsonNode.Parse(json)));
        var parsed = ChatActionLayout.TryParse(json)!;
        Assert.True(parsed.IsValid());
        Assert.Empty(parsed.Rows);
        Assert.Equal(ChatReplyKeyboard.ContactRequest().Rows.SelectMany(r => r).Select(b => (b.Label, b.RequestContact)),
            parsed.Reply!.Rows.SelectMany(r => r).Select(b => (b.Label, b.RequestContact)));
        Assert.Equal(("+380XXXXXXXXX", true, true), (parsed.Reply.Placeholder, parsed.Reply.OneTime, parsed.Reply.Resize));

        var remove = ChatActionLayout.TryParse(ChatActionLayout.RemoveReplyJson)!;
        Assert.True(remove.RemoveReply);
        Assert.True(remove.IsValid());

        // PascalCase (серіалізація .NET) читається так само
        Assert.True(ChatActionLayout.TryParse("""{"Reply":{"Rows":[[{"Label":"X","RequestContact":true}]],"OneTime":true,"Resize":true}}""")!.IsValid());
    }

    [Fact]
    public void stored_json_with_rows_and_reply_parses_but_is_invalid_and_garbage_is_null()
    {
        var both = ChatActionLayout.TryParse("""{"rows":[[{"label":"A","data":"i:my"}]],"reply":{"rows":[[{"label":"X"}]]}}""")!;
        Assert.False(both.IsValid());
        Assert.Null(ChatActionLayout.TryParse("""{"removeReply":false}"""));
        Assert.Null(ChatActionLayout.TryParse("""{"removeReply":"yes"}"""));
        Assert.Null(ChatActionLayout.TryParse("""{"reply":"x"}"""));
        Assert.Null(ChatActionLayout.TryParse("""{"reply":{"rows":[[{"label":1}]]}}"""));
        Assert.Null(ChatActionLayout.TryParse("""{"reply":{"rows":[]}}"""));
    }

    [Fact]
    public void inbox_labels_include_reply_keyboard_captions()
    {
        Assert.Equal(["📱 Поділитися контактом", "Не треба"], ChatKeyboardLayout.ReadLabels(ChatReplyKeyboard.ContactRequest().ToActionsJson()));
        Assert.Empty(ChatKeyboardLayout.ReadLabels(ChatActionLayout.RemoveReplyJson));
        Assert.Equal(["A"], ChatKeyboardLayout.ReadLabels("""{"rows":[[{"label":"A","data":"i:my"}]]}"""));
    }

    [Fact]
    public async Task instagram_and_mock_adapters_ignore_reply_and_remove_layouts()
    {
        var mock = new MockTelegramAdapter();
        foreach (var layout in new[] { ChatActionLayout.ForReply(ChatReplyKeyboard.ContactRequest()), ChatActionLayout.ForRemoveReply() })
        {
            Assert.Null((await mock.SendAsync(new OutboundMessage(Guid.NewGuid(), "telegram", "1", "t", Actions: layout), default)).ExternalMessageId);
            var rec = new TgRecorder();
            var ig = new InstagramAdapter(new HttpClient(rec), new ChCreds());
            await ig.SendAsync(new OutboundMessage(Guid.NewGuid(), "instagram", "u", "text", DateTimeOffset.UtcNow, layout), default);
            var body = Assert.Single(rec.Calls).Body;
            Assert.DoesNotContain("keyboard", body);
            Assert.DoesNotContain("request_contact", body);
        }
    }
}
