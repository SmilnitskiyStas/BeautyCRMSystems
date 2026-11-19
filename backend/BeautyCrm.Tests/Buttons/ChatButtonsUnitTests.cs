using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Tests.Buttons;

/// <summary>TASK-752 (§23.3, §23.5, §23.8): формат callback_data, розкладка, підписи, налаштування, ліміт натискань. Без БД.</summary>
public sealed class ChatButtonsUnitTests
{
    private const string Tok = "AbCdEfGhIjKlMnOpQrStUv"; // 22 символи base64url

    [Theory]
    [InlineData("by:AbCdEfGhIjKlMnOpQrStUv", "by", null)]
    [InlineData("sp:AbCdEfGhIjKlMnOpQrStUv:3", "sp", 3)]
    [InlineData("rc:AbCdEfGhIjKlMnOpQrStUv:0", "rc", 0)]
    [InlineData("ac:AbCdEfGhIjKlMnOpQrStUv:9", "ac", 9)]
    [InlineData("cn:AbCdEfGhIjKlMnOpQrStUv", "cn", null)]
    public void callback_address_parses_stateful_formats(string data, string verb, int? index)
    {
        Assert.True(CallbackAddress.TryParse(data, out var a));
        Assert.Equal((verb, Tok, index), (a.Verb, a.Token, a.Index));
        Assert.False(a.IsStateless);
    }

    [Theory]
    [InlineData("i:book")]
    [InlineData("i:my")]
    [InlineData("i:help")]
    [InlineData("i:human")]
    [InlineData("i:stop")]
    public void callback_address_parses_stateless_formats(string data)
    {
        Assert.True(CallbackAddress.TryParse(data, out var a));
        Assert.Equal((data, null), (a.Verb, a.Token));
        Assert.True(a.IsStateless);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("by")]
    [InlineData("by:")]
    [InlineData("zz:AbCdEfGhIjKlMnOpQrStUv")] // невідомий verb
    [InlineData("by:AbCdEfGhIjKlMnOpQrStU")] // токен 21 символ
    [InlineData("by:AbCdEfGhIjKlMnOpQrStUvX")] // токен 23 символи
    [InlineData("by:AbCdEfGhIjKlMnOpQrSt+v")] // не base64url
    [InlineData("by:AbCdEfGhIjKlMnOpQrStUv:10")] // index не однозначний
    [InlineData("by:AbCdEfGhIjKlMnOpQrStUv:a")]
    [InlineData("by:AbCdEfGhIjKlMnOpQrStUv\n")] // кінцевий перенос рядка
    [InlineData(" by:AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("BY:AbCdEfGhIjKlMnOpQrStUv")] // регістр verb
    [InlineData("i:other")]
    [InlineData("i:stop\n")]
    [InlineData("i:stop:AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("by:AbCdEfGhIjKlMnOpQrStUv:1:2")]
    public void callback_address_rejects_everything_else(string? data) => Assert.False(CallbackAddress.TryParse(data, out _));

    [Fact]
    public void callback_address_rejects_data_over_the_byte_limit()
    {
        var data = "by:" + new string('A', 70); // 73 байти
        Assert.False(CallbackAddress.TryParse(data, out _));
        Assert.False(CallbackAddress.TryParse("by:" + new string('я', 22), out _)); // не ASCII
    }

    [Fact]
    public void build_and_parse_round_trip_and_stateless_has_no_token()
    {
        Assert.Equal($"sp:{Tok}:2", CallbackAddress.Build("sp", Tok, 2));
        Assert.Equal($"cy:{Tok}", CallbackAddress.Build("cy", Tok));
        Assert.Equal("i:stop", CallbackAddress.Build("i:stop", Tok));
        Assert.True(CallbackAddress.TryParse(CallbackAddress.Build("ac", Tok, 4), out var a));
        Assert.Equal(("ac", 4), (a.Verb, a.Index));
    }

    [Fact]
    public void verbs_are_allowed_per_kind_per_the_contract_table()
    {
        Assert.True(ButtonVerbs.AllowedFor(KeyboardKinds.BookingConfirm, ButtonVerbs.ConfirmBooking));
        Assert.True(ButtonVerbs.AllowedFor(KeyboardKinds.BookingConfirm, ButtonVerbs.OtherTime));
        Assert.False(ButtonVerbs.AllowedFor(KeyboardKinds.BookingConfirm, ButtonVerbs.CancelYes));
        Assert.False(ButtonVerbs.AllowedFor(KeyboardKinds.SlotPick, ButtonVerbs.ConfirmBooking));
        Assert.True(ButtonVerbs.AllowedFor(KeyboardKinds.AppointmentList, ButtonVerbs.Reschedule));
        Assert.False(ButtonVerbs.AllowedFor(KeyboardKinds.AppointmentList, ButtonVerbs.Attend));
        Assert.True(ButtonVerbs.AllowedFor(KeyboardKinds.AppointmentActions, ButtonVerbs.Attend));
        Assert.True(ButtonVerbs.AllowedFor(KeyboardKinds.CancelConfirm, ButtonVerbs.CancelNo));
        Assert.True(ButtonVerbs.AllowedFor(KeyboardKinds.AppointmentActions, ButtonVerbs.Stop)); // безстанова кнопка воркера
        Assert.True(ButtonVerbs.NeedsIndex(KeyboardKinds.SlotPick, ButtonVerbs.SlotPick));
        Assert.True(ButtonVerbs.NeedsIndex(KeyboardKinds.AppointmentList, ButtonVerbs.CancelAsk));
        Assert.False(ButtonVerbs.NeedsIndex(KeyboardKinds.AppointmentActions, ButtonVerbs.CancelAsk));
        Assert.False(ButtonVerbs.NeedsIndex(KeyboardKinds.BookingConfirm, ButtonVerbs.ConfirmBooking));
        Assert.All(new[] { "by", "bn", "bo", "sp", "rc", "cy", "cn" }, v => Assert.True(ButtonVerbs.Consuming(v)));
        Assert.All(new[] { "ac", "aa", "ar" }, v => Assert.False(ButtonVerbs.Consuming(v)));
        Assert.All(new[] { "i:stop", "i:my", "rc", "aa", "ac", "cy", "cn" }, v => Assert.True(ButtonVerbs.AllowedInHandoff(v)));
        Assert.All(new[] { "by", "bn", "bo", "sp", "ar", "i:book", "i:human", "i:help" }, v => Assert.False(ButtonVerbs.AllowedInHandoff(v)));
    }

    private static ActionButton B(string label, string data) => new(label, data);

    private static IReadOnlyList<IReadOnlyList<ActionButton>> Rows(params IReadOnlyList<ActionButton>[] rows) => rows;

    [Fact]
    public void layout_validation_enforces_buttons_rows_label_and_callback_data_limits()
    {
        var ok = Rows([B("✅ Так, записатися", $"by:{Tok}"), B("Ні", $"bn:{Tok}")], [B("🕐 Інший час", $"bo:{Tok}")]);
        Assert.Null(ChatKeyboardLayout.Validate(ok));

        Assert.NotNull(ChatKeyboardLayout.Validate(Rows())); // порожня
        Assert.NotNull(ChatKeyboardLayout.Validate(Rows([B("a", "i:my"), B("b", "i:my"), B("c", "i:my"), B("d", "i:my")]))); // 4 у рядку
        Assert.Null(ChatKeyboardLayout.Validate(Rows([B("a", "i:my"), B("b", "i:my")]), maxPerRow: 2));
        Assert.NotNull(ChatKeyboardLayout.Validate(Rows([B("a", "i:my"), B("b", "i:my"), B("c", "i:my")]), maxPerRow: 2));
        Assert.NotNull(ChatKeyboardLayout.Validate(Enumerable.Range(0, 7).Select(i => (IReadOnlyList<ActionButton>)[new ActionButton("x", "i:my")]).ToList())); // 7 рядків
        var eleven = Enumerable.Range(0, 4).Select(_ => (IReadOnlyList<ActionButton>)[new ActionButton("a", "i:my"), new ActionButton("b", "i:my"), new ActionButton("c", "i:my")]).ToList();
        Assert.NotNull(ChatKeyboardLayout.Validate(eleven)); // 12 кнопок
        Assert.NotNull(ChatKeyboardLayout.Validate(Rows([B(new string('я', 33), "i:my")]))); // підпис 33 > 32
        Assert.Null(ChatKeyboardLayout.Validate(Rows([B(new string('я', 32), "i:my")])));
        Assert.NotNull(ChatKeyboardLayout.Validate(Rows([B("", "i:my")])));
        Assert.NotNull(ChatKeyboardLayout.Validate(Rows([B("a\nb", "i:my")]))); // керівний символ
        Assert.NotNull(ChatKeyboardLayout.Validate(Rows([B("a", "by:" + new string('A', 70))]))); // > 64 байти
        Assert.NotNull(ChatKeyboardLayout.Validate(Rows([B("a", "sp:short")]))); // не за форматом
    }

    [Fact]
    public void labels_are_sanitized_to_one_line_without_control_characters()
    {
        Assert.Equal("Олена Іванівна", ChatKeyboardLayout.SanitizeLabel("  Олена\n\t Іванівна\u0007 "));
        Assert.Equal(32, ChatKeyboardLayout.SanitizeLabel(new string('ж', 100)).Length);
        Assert.Equal("", ChatKeyboardLayout.SanitizeLabel("  \n "));
        Assert.Equal("a b", ChatKeyboardLayout.SanitizeLabel("a" + (char)0x200B + "b")); // формат-символи не потрапляють у підпис
        var emoji = ChatKeyboardLayout.SanitizeLabel(string.Concat(Enumerable.Repeat("😀", 40)));
        Assert.Equal(32, emoji.EnumerateRunes().Count()); // за кодовими точками, сурогатні пари не рвуться
    }

    [Fact]
    public void actions_json_uses_the_worker_shape_and_inbox_reads_only_labels()
    {
        var rows = Rows([B("✅ Підтверджую візит", $"aa:{Tok}"), B("❌ Скасувати візит", $"ac:{Tok}")], [B("🔕 Не нагадувати", "i:stop")]);
        var json = ChatKeyboardLayout.ToActionsJson(rows);
        Assert.StartsWith("{\"rows\":[[{\"label\":\"✅ Підтверджую візит\",\"data\":\"aa:", json);
        Assert.Equal(["✅ Підтверджую візит", "❌ Скасувати візит", "🔕 Не нагадувати"], ChatKeyboardLayout.ReadLabels(json));
        // Розкладка в PascalCase (серіалізація ChatActionLayout) читається так само.
        Assert.Equal(["a"], ChatKeyboardLayout.ReadLabels("{\"Rows\":[[{\"Label\":\"a\",\"Data\":\"i:my\"}]]}"));
        Assert.Empty(ChatKeyboardLayout.ReadLabels(null));
        Assert.Empty(ChatKeyboardLayout.ReadLabels("not json"));
        Assert.Empty(ChatKeyboardLayout.ReadLabels("[1,2]"));
    }

    [Fact]
    public void options_json_round_trips_with_camel_case_and_without_nulls()
    {
        var o = new KeyboardOption("sp", "14:30", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateTimeOffset(2026, 11, 3, 12, 30, 0, TimeSpan.Zero));
        var json = KeyboardJson.Serialize([o, new KeyboardOption("rc", "За 24 і 2 год", Method: "telegram", Offsets: [120, 1440])]);
        Assert.Contains("\"verb\":\"sp\"", json);
        Assert.DoesNotContain("appointmentId", json);
        var back = KeyboardJson.TryParse(json)!;
        Assert.Equal(o, back[0]);
        Assert.Equal(new short[] { 120, 1440 }, back[1].Offsets);
        Assert.Null(KeyboardJson.TryParse("{broken"));
        // options воркера (TASK-751): verb + label + appointmentId, безстанова кнопка з verb "i:stop"
        var worker = KeyboardJson.TryParse("[{\"verb\":\"aa\",\"label\":\"x\",\"appointmentId\":\"7a1c63a6-6f0a-4d8c-9f34-0c5a4d2f9d11\"},{\"verb\":\"i:stop\",\"label\":\"y\"}]")!;
        Assert.Equal(Guid.Parse("7a1c63a6-6f0a-4d8c-9f34-0c5a4d2f9d11"), worker[0].AppointmentId);
        Assert.Equal("i:stop", worker[1].Verb);
    }

    [Fact]
    public void callback_body_is_a_label_template_never_raw_callback_data()
    {
        Assert.Equal("[кнопка] Так, скасувати", CallbackBody.Build("Так, скасувати"));
        Assert.Equal("[кнопка]", CallbackBody.Build(null));
        Assert.Equal("[кнопка]", CallbackBody.Build("  \n"));
        Assert.True(CallbackBody.Build(new string('щ', 100)).Length <= 64);
        Assert.Equal("Так, скасувати", CallbackBody.LabelOf("[кнопка] Так, скасувати"));
        Assert.Null(CallbackBody.LabelOf("[кнопка]"));
        Assert.Null(CallbackBody.LabelOf("by:" + Tok));
    }

    [Fact]
    public void options_read_defaults_and_clamp_to_contract_ranges()
    {
        var d = ChatButtonsOptions.Read(new ConfigurationBuilder().Build());
        Assert.Equal((false, 6, 30, 60, 10, 20, 4, true, 0, 3),
            (d.Enabled, d.SlotButtons, d.SlotPickTtlMinutes, d.ListTtlMinutes, d.CancelConfirmTtlMinutes, d.MaxCallbacksPerConversationPerMinute,
                d.MyAppointmentsMax, d.CancelEnabled, d.CancelMinLeadMinutes, d.CancelMaxPerClientPerDay));

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChatButtons:Enabled"] = "true", ["ChatButtons:SlotButtons"] = "9", ["ChatButtons:SlotPickTtlMinutes"] = "4",
            ["ChatButtons:MyAppointmentsMax"] = "5", ["ChatButtons:MaxCallbacksPerConversationPerMinute"] = "121",
            ["ChatCancel:Enabled"] = "false", ["ChatCancel:MinLeadMinutes"] = "60", ["ChatCancel:MaxPerClientPerDay"] = "0",
        }).Build();
        var o = ChatButtonsOptions.Read(cfg);
        Assert.Equal((true, 6, 30, 5, 20, false, 60, 3), (o.Enabled, o.SlotButtons, o.SlotPickTtlMinutes, o.MyAppointmentsMax,
            o.MaxCallbacksPerConversationPerMinute, o.CancelEnabled, o.CancelMinLeadMinutes, o.CancelMaxPerClientPerDay));
    }

    [Fact]
    public void callback_rate_limiter_allows_the_configured_count_per_minute_per_conversation()
    {
        var clock = new Conversations.TestClock();
        var limiter = new ChatCallbackRateLimiter(new ChatButtonsOptions(), clock);
        var a = Guid.NewGuid();
        for (var i = 0; i < 20; i++) Assert.True(limiter.TryAcquire(a), $"press {i + 1}");
        Assert.False(limiter.TryAcquire(a)); // 21-ше за хвилину
        Assert.True(limiter.TryAcquire(Guid.NewGuid())); // інша розмова
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(limiter.TryAcquire(a));
    }

    [Fact]
    public void created_keyboard_does_not_print_its_token()
    {
        var k = new CreatedKeyboard(Guid.NewGuid(), Tok, DateTimeOffset.UtcNow, []);
        Assert.DoesNotContain(Tok, k.ToString());
        Assert.DoesNotContain("100500", new PendingClear(Guid.NewGuid(), Guid.NewGuid(), "100500", "77").ToString());
        Assert.Equal($"by:{Tok}", k.Data("by"));
        Assert.Equal($"sp:{Tok}:1", k.Data("sp", 1));
    }

    // ------------------------------------------------------------------ settingsJson.assistant.buttons

    private sealed class Store : IChannelSettingsStore
    {
        public Task<IReadOnlyList<ChannelRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ChannelRecord>>([]);
        public Task<ChannelRecord?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult<ChannelRecord?>(null);
        public Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct) => Task.FromResult(true);
        public Task<ChannelRecord?> UpsertAsync(Guid id, UpsertChannelRequest req, CancellationToken ct) =>
            Task.FromResult<ChannelRecord?>(new ChannelRecord(id, req.Type ?? "telegram", req.Name, req.LocationId, req.IsActive, null, false, req.SettingsJson));
    }

    [Theory]
    [InlineData("{\"assistant\":{\"enabled\":true,\"buttons\":\"yes\"}}")]
    [InlineData("{\"assistant\":{\"buttons\":1}}")]
    [InlineData("{\"assistant\":{\"buttons\":null}}")]
    [InlineData("{\"assistant\":{\"buttons\":{}}}")]
    [InlineData("{\"assistant\":{\"buttons\":[true]}}")]
    public async Task channel_settings_reject_non_boolean_buttons_with_invalid_settings(string settings)
    {
        var r = await new ChannelSettingsService(new Store()).UpsertAsync(Guid.NewGuid(),
            new UpsertChannelRequest("telegram", "bot", null, true, null, null, settings), default);
        Assert.False(r.IsOk);
        Assert.Equal("invalid_settings", r.Error!.Code);
    }

    [Theory]
    [InlineData("{\"assistant\":{\"enabled\":true,\"buttons\":false}}")]
    [InlineData("{\"assistant\":{\"enabled\":true,\"buttons\":true}}")]
    [InlineData("{\"assistant\":{\"enabled\":true}}")]
    [InlineData("{\"other\":1}")]
    [InlineData("{}")]
    public async Task channel_settings_accept_boolean_or_missing_buttons(string settings)
    {
        var r = await new ChannelSettingsService(new Store()).UpsertAsync(Guid.NewGuid(),
            new UpsertChannelRequest("telegram", "bot", null, true, null, null, settings), default);
        Assert.True(r.IsOk, r.Error?.Code);
    }
}
