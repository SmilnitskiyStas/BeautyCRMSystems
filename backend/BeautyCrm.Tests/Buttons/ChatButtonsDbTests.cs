using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using BeautyCrm.Infrastructure.Integrations.Channels.Inbox;
using BeautyCrm.Tests.Conversations;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static BeautyCrm.Tests.Conversations.ChatHarness;

namespace BeautyCrm.Tests.Buttons;

/// <summary>
/// TASK-752 (§23.3, §23.5, §23.7, §23.11, §23.13 тести 7, 10, 20 + IDOR): сервіс клавіатур, перевірки callback у порядку §23.5, CAS,
/// дії клієнта над власними записами, inbox-поля. Реальний PostgreSQL з RLS і роллю без BYPASSRLS, ChatHarness (mock ШІ, fake Telegram).
/// Транспорту й конвеєра (TASK-750/753) тут немає: вхідні callback-рядки створюються напряму.
/// </summary>
public sealed class ChatButtonsDbTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public ChatButtonsDbTests(PostgresRlsFixture db) => _db = db;

    private static readonly IDictionary<string, string?> On = new Dictionary<string, string?> { ["ChatButtons:Enabled"] = "true" };

    private sealed class Env
    {
        public required ChatHarness H;
        public required ChatSeed S;
        public Guid Conv;
        public Guid ClientId;
        public Guid Tenant => S.TenantId;
    }

    private async Task<Env> NewAsync(bool enabled = true, bool withClient = true, IDictionary<string, string?>? extra = null, Action<IServiceCollection>? configure = null)
    {
        var cfg = new Dictionary<string, string?>(extra ?? On) { ["ChatButtons:Enabled"] = enabled ? "true" : "false" };
        var h = ChatHarness.Create(_db, extraConfig: cfg, configure: configure);
        var s = await h.SeedAsync();
        var conv = (await h.IngestAsync(s, "Привіт")).Conversation!.Value;
        var e = new Env { H = h, S = s, Conv = conv };
        if (withClient) e.ClientId = await SetClientAsync(e, conv);
        return e;
    }

    private async Task<Guid> SetClientAsync(Env e, Guid conv, string name = "Анна")
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var c = new Client { FullName = name };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        await ctx.Conversations.Where(x => x.Id == conv).ExecuteUpdateAsync(u => u.SetProperty(x => x.ClientId, (Guid?)c.Id));
        return c.Id;
    }


    private static async Task<T> Run<T>(ChatHarness h, Guid tenant, Func<IServiceProvider, Task<T>> f)
    {
        using var scope = h.Sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        return await f(scope.ServiceProvider);
    }

    private Task<T> Scope<T>(Env e, Func<IServiceProvider, Task<T>> f) => Run(e.H, e.Tenant, f);

    private static int _slot;

    private async Task<Appointment> AddApptAsync(Env e, Guid? client = null, TimeSpan? fromNow = null, AppointmentStatus status = AppointmentStatus.Confirmed,
        decimal price = 500m)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var a = new Appointment
        {
            LocationId = e.S.LocationId, SpecialistId = e.S.SpecialistId, ServiceId = e.S.ServiceId, ClientId = client ?? e.ClientId,
            StartsAt = DateTimeOffset.UtcNow.Add(fromNow ?? TimeSpan.FromDays(2) + TimeSpan.FromHours(3 * (Interlocked.Increment(ref _slot) % 60))), DurationMinutes = 60, Status = status, Source = AppointmentSource.Telegram,
            PriceOriginal = price, PriceFinal = price,
        };
        if (status == AppointmentStatus.Cancelled) { a.CancelledAt = DateTimeOffset.UtcNow; a.CancelledByType = "staff"; }
        ctx.Add(a);
        await ctx.SaveChangesAsync();
        return a;
    }

    private async Task<Guid> AddOutboundAsync(Env e, Guid? conv = null, string? ext = "500", string status = "sent")
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var m = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv ?? e.Conv, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System,
            Body = "Меню", Status = status, ExternalMessageId = ext, SentAt = DateTimeOffset.UtcNow,
        };
        ctx.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private async Task<Guid> AddInboundCallbackAsync(Env e, Guid? conv = null)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var m = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv ?? e.Conv, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client,
            Body = "[кнопка]", Kind = "callback", Status = "received", SentAt = DateTimeOffset.UtcNow,
        };
        ctx.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private async Task<CreatedKeyboard> KbAsync(Env e, string kind, IReadOnlyList<KeyboardOption> options, Guid? appt = null, Guid? proposal = null,
        DateTimeOffset? exp = null, Guid? conv = null, Guid? msg = null, string? ext = "500")
    {
        var m = msg ?? await AddOutboundAsync(e, conv, ext);
        var r = await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().CreateAsync(new NewKeyboard(conv ?? e.Conv, m, kind, options, exp, proposal, appt), default));
        Assert.True(r.IsOk, r.Error?.Code);
        return r.Value!;
    }

    private Task<CreatedKeyboard> ActionsKbAsync(Env e, Guid appt, DateTimeOffset? exp = null) => KbAsync(e, KeyboardKinds.AppointmentActions,
        [new("aa", "✅ Підтверджую візит", AppointmentId: appt), new("ac", "❌ Скасувати візит", AppointmentId: appt), new("i:stop", "🔕 Не нагадувати")], appt: appt, exp: exp);

    private Task<CreatedKeyboard> ConfirmKbAsync(Env e, Guid appt) => KbAsync(e, KeyboardKinds.CancelConfirm,
        [new("cy", "Так, скасувати", AppointmentId: appt), new("cn", "Ні, залишити", AppointmentId: appt)], appt: appt);

    private Task<CallbackResolution> ResolveAsync(Env e, string? data, string? messageRef = "500", Guid? conv = null, Guid? inbound = null) =>
        Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().ResolveCallbackAsync(conv ?? e.Conv, data, messageRef, inbound, default));

    private sealed record Pressed(CallbackResolution Res, ButtonActionResult? Act, Guid Inbound);

    private async Task<Pressed> PressAsync(Env e, CreatedKeyboard kb, string verb, int? idx = null, string? messageRef = "500", Guid? conv = null, TelegramProfile? profile = null)
    {
        var inbound = await AddInboundCallbackAsync(e, conv);
        var res = await ResolveAsync(e, kb.Data(verb, idx), messageRef, conv, inbound);
        return new Pressed(res, res.IsValid ? await ExecuteAsync(e, res, inbound, conv, profile) : null, inbound);
    }

    private Task<ButtonActionResult> ExecuteAsync(Env e, CallbackResolution res, Guid inbound, Guid? conv = null, TelegramProfile? profile = null) =>
        Scope(e, sp => sp.GetRequiredService<ChatButtonActions>().ExecuteAsync(new ButtonActionContext(conv ?? e.Conv, inbound, res, profile), default));

    private async Task<ChatKeyboard> LoadKbAsync(Env e, Guid id)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.ChatKeyboards.AsNoTracking().SingleAsync(k => k.Id == id);
    }

    private async Task<Appointment> LoadApptAsync(Env e, Guid id)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.Appointments.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private async Task SetHandoffAsync(Env e, Guid? conv = null)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        var id = conv ?? e.Conv;
        await ctx.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiState, "handoff")
            .SetProperty(c => c.HandoffReason, "human_requested").SetProperty(c => c.HandoffAt, (DateTimeOffset?)DateTimeOffset.UtcNow));
    }

    // =============================================================== створення

    [SkippableFact]
    public async Task create_stores_a_22_char_token_options_ttl_and_binds_message_conversation_and_appointment()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var kb = await ActionsKbAsync(e, appt.Id);

        Assert.Matches("^[A-Za-z0-9_-]{22}$", kb.Token);
        var row = await LoadKbAsync(e, kb.Id);
        Assert.Equal((KeyboardKinds.AppointmentActions, "active", e.Conv, appt.Id), (row.Kind, row.Status, row.ConversationId, row.AppointmentId));
        Assert.Equal(appt.StartsAt, row.ExpiresAt, TimeSpan.FromMilliseconds(1)); // TTL appointment_actions = початок візиту
        var opts = KeyboardJson.TryParse(row.Options)!;
        Assert.Equal(["aa", "ac", "i:stop"], opts.Select(o => o.Verb).ToArray());
        Assert.DoesNotContain(kb.Token, kb.ToString());

        var second = await KbAsync(e, KeyboardKinds.AppointmentActions, [new("aa", "x", AppointmentId: appt.Id)], appt: appt.Id);
        Assert.NotEqual(kb.Token, second.Token);
    }

    [SkippableFact]
    public async Task create_uses_default_ttl_per_kind_and_revokes_the_previous_keyboard_of_the_same_kind_only()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var slot = new KeyboardOption("sp", "14:30", e.S.LocationId, e.S.ServiceId, e.S.SpecialistId, e.S.Start);
        var s1 = await KbAsync(e, KeyboardKinds.SlotPick, [slot]);
        Assert.InRange((await LoadKbAsync(e, s1.Id)).ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));
        var list1 = await KbAsync(e, KeyboardKinds.AppointmentList, [new("ac", "x", AppointmentId: appt.Id), new("ar", "y", AppointmentId: appt.Id)]);
        Assert.InRange((await LoadKbAsync(e, list1.Id)).ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));
        var c1 = await ConfirmKbAsync(e, appt.Id);
        Assert.InRange((await LoadKbAsync(e, c1.Id)).ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));

        var s2 = await KbAsync(e, KeyboardKinds.SlotPick, [slot]);
        var list2 = await KbAsync(e, KeyboardKinds.AppointmentList, [new("ac", "x", AppointmentId: appt.Id)]);
        var c2 = await ConfirmKbAsync(e, appt.Id);

        Assert.Equal([s1.Id], s2.Revoked);
        Assert.Equal([c1.Id], c2.Revoked);
        Assert.Equal([list1.Id], list2.Revoked); // TASK-759 (M3): одна активна appointment_list на розмову (раніше не відкликалась)
        Assert.Equal("revoked", (await LoadKbAsync(e, list1.Id)).Status);
        Assert.Equal("revoked", (await LoadKbAsync(e, s1.Id)).Status);
        Assert.Equal("revoked", (await LoadKbAsync(e, c1.Id)).Status);
        Assert.Equal("active", (await LoadKbAsync(e, list2.Id)).Status);
        Assert.Equal("active", (await LoadKbAsync(e, s2.Id)).Status);
    }

    [SkippableFact]
    public async Task create_rejects_invalid_layouts_and_foreign_references()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var otherClient = await SetClientAsync(e, (await e.H.IngestAsync(e.S, "Hi", chat: "200")).Conversation!.Value, "Інна");
        var foreign = await AddApptAsync(e, otherClient);
        var msg = await AddOutboundAsync(e);
        var otherConv = (await e.H.IngestAsync(e.S, "Hi", chat: "200")).Conversation!.Value;
        var otherMsg = await AddOutboundAsync(e, otherConv);

        async Task<string?> Code(NewKeyboard k) =>
            (await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().CreateAsync(k, default))).Error?.Code;

        KeyboardOption Ok() => new("cy", "Так", AppointmentId: appt.Id);
        Assert.Equal("keyboard_invalid", await Code(new(e.Conv, msg, "nope", [Ok()])));
        Assert.Equal("keyboard_invalid", await Code(new(e.Conv, msg, KeyboardKinds.CancelConfirm, [])));
        Assert.Equal("keyboard_invalid", await Code(new(e.Conv, msg, KeyboardKinds.CancelConfirm, Enumerable.Repeat(Ok(), 11).ToList(), AppointmentId: appt.Id)));
        Assert.Equal("keyboard_invalid", await Code(new(e.Conv, msg, KeyboardKinds.CancelConfirm, [new("by", "Так")], AppointmentId: appt.Id))); // verb не цього виду
        Assert.Equal("keyboard_invalid", await Code(new(e.Conv, msg, KeyboardKinds.CancelConfirm, [new("cy", "  ")], AppointmentId: appt.Id)));
        Assert.Equal("keyboard_invalid", await Code(new(e.Conv, msg, KeyboardKinds.CancelConfirm, [Ok()]))); // немає appointment_id
        Assert.Equal("keyboard_invalid", await Code(new(e.Conv, msg, KeyboardKinds.BookingConfirm, [new("by", "Так")]))); // немає proposal_id
        Assert.Equal("keyboard_invalid", await Code(new(e.Conv, msg, KeyboardKinds.CancelConfirm, [Ok()], DateTimeOffset.UtcNow.AddMinutes(-1), AppointmentId: appt.Id)));
        Assert.Equal("appointment_not_found", await Code(new(e.Conv, msg, KeyboardKinds.CancelConfirm, [new("cy", "Так", AppointmentId: foreign.Id)], AppointmentId: foreign.Id)));
        Assert.Equal("appointment_not_found", await Code(new(e.Conv, msg, KeyboardKinds.AppointmentList, [new("ac", "Так", AppointmentId: foreign.Id)])));
        Assert.Equal("proposal_not_found", await Code(new(e.Conv, msg, KeyboardKinds.BookingConfirm, [new("by", "Так")], ProposalId: Guid.NewGuid())));
        Assert.Equal("message_not_found", await Code(new(e.Conv, otherMsg, KeyboardKinds.CancelConfirm, [Ok()], AppointmentId: appt.Id))); // повідомлення чужої розмови
        Assert.Equal("message_not_found", await Code(new(e.Conv, Guid.NewGuid(), KeyboardKinds.CancelConfirm, [Ok()], AppointmentId: appt.Id)));
        Assert.Equal("conversation_not_found", await Code(new(Guid.NewGuid(), msg, KeyboardKinds.CancelConfirm, [Ok()], AppointmentId: appt.Id)));
        Assert.Empty(await ListKeyboardsAsync(e));
    }

    private async Task<List<ChatKeyboard>> ListKeyboardsAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.ChatKeyboards.AsNoTracking().ToListAsync();
    }

    [SkippableFact]
    public async Task one_keyboard_per_message_and_label_is_sanitized_in_options()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var msg = await AddOutboundAsync(e);
        var kb = await KbAsync(e, KeyboardKinds.AppointmentActions, [new("aa", "  Підтверджую\n\tвізит  " + new string('я', 80), AppointmentId: appt.Id)], appt: appt.Id, msg: msg);
        var label = KeyboardJson.TryParse((await LoadKbAsync(e, kb.Id)).Options)![0].Label;
        Assert.True(label.Length <= 32);
        Assert.DoesNotContain('\n', label);
        Assert.StartsWith("Підтверджую візит", label);

        await Assert.ThrowsAnyAsync<Exception>(() => Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().CreateAsync(
            new NewKeyboard(e.Conv, msg, KeyboardKinds.AppointmentActions, [new("aa", "x", AppointmentId: appt.Id)], AppointmentId: appt.Id), default)));
    }

    // =============================================================== порядок перевірок §23.5

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zz:AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("by:AbCdEfGhIjKlMnOpQrStU")] // токен 21 символ
    [InlineData("by:AbCdEfGhIjKlMnOpQrStUv:12")]
    public async Task step1_invalid_format_never_touches_the_database(string? data)
    {
        var e = await NewAsync();
        var r = await ResolveAsync(e, data);
        Assert.Equal((CallbackOutcome.InvalidFormat, "invalid_format", CallbackClass.Stale), (r.Outcome, r.Code, r.Class));
        Assert.Null(r.Keyboard);
        Assert.False(r.ClearMarkup); // клавіатуру не знайдено - нічого прибирати
    }

    [SkippableFact]
    public async Task step3_random_token_and_a_token_from_another_tenant_are_unknown_without_disclosure()
    {
        var a = await NewAsync();
        var b = await NewAsync();
        var apptB = await AddApptAsync(b);
        var kbB = await ActionsKbAsync(b, apptB.Id);

        var random = await ResolveAsync(a, "aa:" + FakeToken());
        var cross = await ResolveAsync(a, kbB.Data("aa")); // токен tenant B у розмові tenant A (RLS)
        var crossWithOwnTenantConv = await Run(b.H, a.Tenant, sp => sp.GetRequiredService<ChatKeyboardService>().ResolveCallbackAsync(a.Conv, kbB.Data("aa"), "500", null, default));

        Assert.All(new[] { random, cross, crossWithOwnTenantConv }, r =>
        {
            Assert.Equal((CallbackOutcome.UnknownToken, "unknown_token"), (r.Outcome, r.Code));
            Assert.Null(r.Keyboard);
            Assert.False(r.ClearMarkup);
        });
        Assert.Equal("active", (await LoadKbAsync(b, kbB.Id)).Status); // чужа клавіатура не змінилась
    }

    private static string FakeToken() => Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_').PadRight(22, 'A')[..22];

    [SkippableFact]
    public async Task step4_foreign_conversation_of_the_same_tenant_is_rejected_and_the_keyboard_is_left_alone()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var kb = await ActionsKbAsync(e, appt.Id);
        var otherConv = (await e.H.IngestAsync(e.S, "Hi", chat: "200")).Conversation!.Value;

        var r = await ResolveAsync(e, kb.Data("aa"), conv: otherConv);

        Assert.Equal((CallbackOutcome.ForeignConversation, CallbackClass.Mismatch), (r.Outcome, r.Class));
        Assert.Null(r.Keyboard); // чужу клавіатуру не розкриваємо
        Assert.False(r.ClearMarkup);
        Assert.Equal("active", (await LoadKbAsync(e, kb.Id)).Status);
        Assert.Contains(e.H.Logs.Lines, l => l.StartsWith("Warning") && l.Contains("result=foreign_conversation"));
    }

    [SkippableFact]
    public async Task step4_runs_before_the_status_checks_so_a_used_foreign_keyboard_is_still_foreign()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var kb = await ConfirmKbAsync(e, appt.Id);
        var first = await PressAsync(e, kb, "cn");
        Assert.Equal(ButtonResultCodes.CancelKept, first.Act!.Code);
        var otherConv = (await e.H.IngestAsync(e.S, "Hi", chat: "200")).Conversation!.Value;

        Assert.Equal(CallbackOutcome.ForeignConversation, (await ResolveAsync(e, kb.Data("cn"), conv: otherConv)).Outcome);
    }

    [SkippableFact]
    public async Task step5_message_ref_must_match_when_known_and_the_message_must_be_sent()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var kb = await ActionsKbAsync(e, appt.Id); // external_message_id = "500"

        var wrong = await ResolveAsync(e, kb.Data("aa"), messageRef: "501");
        Assert.Equal((CallbackOutcome.RefMismatch, CallbackClass.Mismatch), (wrong.Outcome, wrong.Class));
        Assert.True(wrong.Keyboard is not null);
        Assert.Equal(CallbackOutcome.RefMismatch, (await ResolveAsync(e, kb.Data("aa"), messageRef: null)).Outcome);
        Assert.Equal(CallbackOutcome.Valid, (await ResolveAsync(e, kb.Data("aa"), messageRef: "500")).Outcome);
        Assert.Contains(e.H.Logs.Lines, l => l.StartsWith("Warning") && l.Contains("result=ref_mismatch"));

        // ref невідомий (повідомлення ще без external_message_id) - довіряємо розмові й токену
        var noRef = await KbAsync(e, KeyboardKinds.AppointmentActions, [new("aa", "x", AppointmentId: appt.Id)], appt: appt.Id, ext: null);
        Assert.Equal(CallbackOutcome.Valid, (await ResolveAsync(e, noRef.Data("aa"), messageRef: "777")).Outcome);

        // повідомлення не `sent` (pending/failed) - кнопки там не могло бути
        var pending = await KbAsync(e, KeyboardKinds.AppointmentActions, [new("aa", "x", AppointmentId: appt.Id)], appt: appt.Id, msg: await AddOutboundAsync(e, null, null, "pending"));
        Assert.Equal(CallbackOutcome.RefMismatch, (await ResolveAsync(e, pending.Data("aa"), messageRef: "500")).Outcome);
    }

    [SkippableFact]
    public async Task step6_verb_must_belong_to_the_kind_and_index_must_exist_in_options()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var a2 = await AddApptAsync(e, fromNow: TimeSpan.FromDays(3));
        var list = await KbAsync(e, KeyboardKinds.AppointmentList, [new("ac", "Скасувати 1", AppointmentId: appt.Id), new("ar", "Змінити 1", AppointmentId: appt.Id), new("ac", "Скасувати 2", AppointmentId: a2.Id)]);
        var slot = await KbAsync(e, KeyboardKinds.SlotPick, [new("sp", "14:30", e.S.LocationId, e.S.ServiceId, e.S.SpecialistId, e.S.Start)]);
        var confirm = await ConfirmKbAsync(e, appt.Id);
        var actions = await ActionsKbAsync(e, appt.Id);

        // verb не цього виду
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, confirm.Data("by"))).Outcome);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, slot.Data("cy"))).Outcome);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, actions.Data("cn"))).Outcome);
        // verb дозволений для виду, але такої кнопки в options немає (воркер не додає `ar`)
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, actions.Data("ar"))).Outcome);
        // index поза options / відсутній, де потрібен / зайвий, де не потрібен / verb опції не збігається
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, slot.Data("sp", 1))).Outcome);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, slot.Data("sp"))).Outcome);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, confirm.Data("cy", 0))).Outcome);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, list.Data("ac", 1))).Outcome); // options[1] - `ar`
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, list.Data("ac", 3))).Outcome);
        Assert.Equal(CallbackOutcome.InvalidOption, (await ResolveAsync(e, list.Data("ac"))).Outcome);
        Assert.True((await ResolveAsync(e, list.Data("ac", 1))).ClearMarkup); // клавіатуру цієї розмови знайдено - прибрати

        var ok = await ResolveAsync(e, list.Data("ac", 2));
        Assert.Equal((CallbackOutcome.Valid, "ok", 2, a2.Id), (ok.Outcome, ok.Code, ok.OptionPosition, ok.Option!.AppointmentId));
        var okNoIndex = await ResolveAsync(e, confirm.Data("cn"));
        Assert.Equal((CallbackOutcome.Valid, 1), (okNoIndex.Outcome, okNoIndex.OptionPosition));
        Assert.Equal(KeyboardKinds.CancelConfirm, okNoIndex.Keyboard!.Kind);
    }

    [SkippableFact]
    public async Task step7_used_revoked_expired_and_ttl_statuses()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var used = await ConfirmKbAsync(e, appt.Id);
        await PressAsync(e, used, "cn");
        var u = await ResolveAsync(e, used.Data("cn"));
        Assert.Equal((CallbackOutcome.Used, CallbackClass.Used, true), (u.Outcome, u.Class, u.ClearMarkup));
        Assert.True(u.Quiet); // ≤ 30 с після використання - без відповіді

        var revoked = await ConfirmKbAsync(e, appt.Id);
        await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().RevokeActiveAsync(e.Conv, [KeyboardKinds.CancelConfirm], default));
        var rv = await ResolveAsync(e, revoked.Data("cy"));
        Assert.Equal((CallbackOutcome.Revoked, CallbackClass.Stale, true), (rv.Outcome, rv.Class, rv.ClearMarkup));

        var short1 = await KbAsync(e, KeyboardKinds.SlotPick, [new("sp", "14:30", e.S.LocationId, e.S.ServiceId, e.S.SpecialistId, e.S.Start)], exp: DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(CallbackOutcome.Valid, (await ResolveAsync(e, short1.Data("sp", 0))).Outcome);
        e.H.Clock.Advance(TimeSpan.FromMinutes(2));
        var ex = await ResolveAsync(e, short1.Data("sp", 0));
        Assert.Equal((CallbackOutcome.Expired, CallbackClass.Expired, true), (ex.Outcome, ex.Class, ex.ClearMarkup));
        Assert.Equal("expired", (await LoadKbAsync(e, short1.Id)).Status); // ставиться expired
        Assert.Equal(CallbackOutcome.Expired, (await ResolveAsync(e, short1.Data("sp", 0))).Outcome);

        e.H.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await ResolveAsync(e, used.Data("cn"))).Quiet); // > 30 с -> відповідь T-action-stale
    }

    [SkippableFact]
    public async Task buttons_disabled_makes_old_keyboards_stale_but_still_clearable()
    {
        var e = await NewAsync(enabled: false);
        var appt = await AddApptAsync(e);
        var kb = await ActionsKbAsync(e, appt.Id);

        var r = await ResolveAsync(e, kb.Data("aa"));
        Assert.Equal((CallbackOutcome.Disabled, CallbackClass.Stale, true), (r.Outcome, r.Class, r.ClearMarkup));
        Assert.Equal("buttons_disabled", r.Code);
        Assert.Equal(CallbackOutcome.Disabled, (await ResolveAsync(e, "i:my")).Outcome);
        Assert.Null((await LoadAppt(e, appt.Id)).ClientConfirmedAt);
    }

    private Task<Appointment> LoadAppt(Env e, Guid id) => LoadApptAsync(e, id);

    [SkippableFact]
    public async Task step8_handoff_allows_only_the_contract_set_and_blocks_the_rest()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var confirm = await KbAsync(e, KeyboardKinds.CancelConfirm, [new("cy", "Так", AppointmentId: appt.Id)], appt: appt.Id);
        var actions = await ActionsKbAsync(e, appt.Id);
        var list = await KbAsync(e, KeyboardKinds.AppointmentList, [new("ar", "Змінити", AppointmentId: appt.Id)]);
        var slot = await KbAsync(e, KeyboardKinds.SlotPick, [new("sp", "14:30", e.S.LocationId, e.S.ServiceId, e.S.SpecialistId, e.S.Start)]);
        await SetHandoffAsync(e);

        Assert.Equal(CallbackOutcome.Valid, (await ResolveAsync(e, confirm.Data("cy"))).Outcome);
        Assert.Equal(CallbackOutcome.Valid, (await ResolveAsync(e, actions.Data("aa"))).Outcome);
        Assert.Equal(CallbackOutcome.Valid, (await ResolveAsync(e, actions.Data("ac"))).Outcome);
        Assert.Equal(CallbackOutcome.Stateless, (await ResolveAsync(e, "i:stop")).Outcome);
        Assert.Equal(CallbackOutcome.Stateless, (await ResolveAsync(e, "i:my")).Outcome);

        var ar = await ResolveAsync(e, list.Data("ar", 0));
        var sp = await ResolveAsync(e, slot.Data("sp", 0));
        Assert.Equal((CallbackOutcome.HandoffBlocked, CallbackClass.Handoff), (ar.Outcome, ar.Class));
        Assert.Equal(CallbackOutcome.HandoffBlocked, sp.Outcome);
        Assert.True(sp.RevokesAiKeyboards);
        Assert.False(sp.ClearMarkup); // handoff_blocked: клавіатури booking_confirm|slot_pick відкликає конвеєр
        foreach (var d in new[] { "i:book", "i:human", "i:help" })
            Assert.Equal(CallbackOutcome.HandoffBlocked, (await ResolveAsync(e, d)).Outcome);
    }

    [SkippableFact]
    public async Task handoff_with_a_finite_pause_that_is_still_running_blocks_and_an_expired_pause_does_not()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var slot = await KbAsync(e, KeyboardKinds.SlotPick, [new("sp", "14:30", e.S.LocationId, e.S.ServiceId, e.S.SpecialistId, e.S.Start)]);
        await using (var ctx = _db.CreateContext(e.Tenant))
            await ctx.Conversations.Where(c => c.Id == e.Conv).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiState, "handoff")
                .SetProperty(c => c.AiPausedUntil, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.Equal(CallbackOutcome.HandoffBlocked, (await ResolveAsync(e, slot.Data("sp", 0))).Outcome);
        e.H.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(CallbackOutcome.Valid, (await ResolveAsync(e, slot.Data("sp", 0))).Outcome);
        _ = appt;
    }

    // =============================================================== CAS, відкликання, sweeper

    [SkippableFact]
    public async Task parallel_consume_has_exactly_one_winner()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var kb = await ConfirmKbAsync(e, appt.Id);
        var inbound = new List<Guid>();
        for (var i = 0; i < 8; i++) inbound.Add(await AddInboundCallbackAsync(e));

        var results = await Task.WhenAll(inbound.Select(m => Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().TryConsumeAsync(kb.Id, 0, m, default))));

        Assert.Equal(1, results.Count(x => x));
        var row = await LoadKbAsync(e, kb.Id);
        Assert.Equal(("used", (short)0), (row.Status, row.UsedOption!.Value));
        Assert.Contains(row.UsedByMessageId!.Value, inbound);
        Assert.NotNull(row.UsedAt);
        Assert.False(await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().TryConsumeAsync(kb.Id, 0, inbound[0], default)));
    }

    [SkippableFact]
    public async Task consume_fails_for_revoked_expired_and_unknown_keyboards()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var kb = await ConfirmKbAsync(e, appt.Id);
        var inbound = await AddInboundCallbackAsync(e);
        await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().RevokeActiveAsync(e.Conv, [KeyboardKinds.CancelConfirm], default));
        Assert.False(await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().TryConsumeAsync(kb.Id, 0, inbound, default)));
        Assert.False(await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().TryConsumeAsync(Guid.NewGuid(), 0, inbound, default)));

        var short1 = await KbAsync(e, KeyboardKinds.SlotPick, [new("sp", "x", e.S.LocationId, e.S.ServiceId, e.S.SpecialistId, e.S.Start)], exp: DateTimeOffset.UtcNow.AddMinutes(1));
        e.H.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().TryConsumeAsync(short1.Id, 0, inbound, default)));
    }

    [SkippableFact]
    public async Task a_replay_of_the_same_inbound_press_is_recognised_but_another_press_is_used()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var kb = await ConfirmKbAsync(e, appt.Id);
        var first = await PressAsync(e, kb, "cn");
        Assert.Equal(ButtonResultCodes.CancelKept, first.Act!.Code);

        var replay = await ResolveAsync(e, kb.Data("cn"), inbound: first.Inbound);
        Assert.Equal((CallbackOutcome.Valid, true), (replay.Outcome, replay.Replay));
        Assert.Equal(ButtonResultCodes.CancelKept, (await ExecuteAsync(e, replay, first.Inbound)).Code);

        var other = await ResolveAsync(e, kb.Data("cn"), inbound: await AddInboundCallbackAsync(e));
        Assert.Equal(CallbackOutcome.Used, other.Outcome);
    }

    [SkippableFact]
    public async Task revoke_for_proposal_and_expire_due_and_sweeper_listing()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var kb = await ConfirmKbAsync(e, appt.Id);
        var shortLived = await KbAsync(e, KeyboardKinds.SlotPick, [new("sp", "x", e.S.LocationId, e.S.ServiceId, e.S.SpecialistId, e.S.Start)], exp: DateTimeOffset.UtcNow.AddMinutes(1));
        var noRef = await KbAsync(e, KeyboardKinds.AppointmentActions, [new("aa", "x", AppointmentId: appt.Id)], appt: appt.Id, ext: null);

        e.H.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().ExpireDueAsync(10, default)));
        Assert.Equal("expired", (await LoadKbAsync(e, shortLived.Id)).Status);
        Assert.Equal("active", (await LoadKbAsync(e, kb.Id)).Status);
        await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().RevokeActiveAsync(e.Conv, [KeyboardKinds.CancelConfirm], default));
        await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().RevokeActiveAsync(e.Conv, [KeyboardKinds.AppointmentActions], default));

        var pending = await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().ListPendingClearAsync(50, default));
        // shortLived (expired) без external_message_id? - ні, з "500"; noRef (revoked) без нього закривається одразу
        Assert.Equal(new[] { kb.Id, shortLived.Id }.OrderBy(x => x), pending.Select(p => p.KeyboardId).OrderBy(x => x));
        Assert.All(pending, p => Assert.Equal("500", p.ExternalMessageId));
        Assert.NotNull((await LoadKbAsync(e, noRef.Id)).MarkupClearedAt);

        await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().RecordClearFailureAsync(kb.Id, default));
        await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().RecordClearFailureAsync(kb.Id, default));
        await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().RecordClearFailureAsync(kb.Id, default));
        Assert.Equal((short)3, (await LoadKbAsync(e, kb.Id)).ClearAttempts);
        Assert.DoesNotContain(kb.Id, (await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().ListPendingClearAsync(50, default))).Select(p => p.KeyboardId)); // ≤ 3 спроб

        await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().MarkClearedAsync([shortLived.Id], default));
        Assert.Empty(await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().ListPendingClearAsync(50, default)));
    }

    [SkippableFact]
    public async Task keyboard_table_is_invisible_from_another_tenant_scope()
    {
        var a = await NewAsync();
        var b = await NewAsync();
        var kb = await ActionsKbAsync(a, (await AddApptAsync(a)).Id);
        await using var ctxB = _db.CreateContext(b.Tenant);
        Assert.Empty(await ctxB.ChatKeyboards.AsNoTracking().Where(k => k.Id == kb.Id).ToListAsync());
        Assert.Equal(0, await ctxB.ChatKeyboards.Where(k => k.Id == kb.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.Status, "revoked")));
        Assert.Equal("active", (await LoadKbAsync(a, kb.Id)).Status);
    }

    // =============================================================== «Так» кнопкою

    private async Task<(Env E, ChatBookingProposal P, CreatedKeyboard Kb)> SummaryWithKeyboardAsync(ChatHarness h, ChatSeed s)
    {
        var conv = await h.ShowSummaryAsync(s);
        var p = Assert.Single(await h.ProposalsAsync(s));
        Assert.Equal("shown", p.Status);
        var e = new Env { H = h, S = s, Conv = conv };
        // TASK-753: конвеєр сам створює booking_confirm на повідомленні підсумку (ChatButtons:Enabled + Telegram) - беремо його з БД.
        await using var ctx = _db.CreateContext(s.TenantId);
        var row = await ctx.ChatKeyboards.AsNoTracking().SingleAsync(k => k.ProposalId == p.Id);
        // тестовий Telegram повертає власні message_id; ці тести тиснуть із умовним ref "500"
        await ctx.Messages.Where(m => m.Id == row.MessageId).ExecuteUpdateAsync(u => u.SetProperty(m => m.ExternalMessageId, "500"));
        return (e, p, new CreatedKeyboard(row.Id, row.Token, row.ExpiresAt, []));
    }

    [SkippableFact]
    public async Task yes_button_creates_exactly_one_booking_identical_to_the_text_yes_without_the_llm()
    {
        var h = ChatHarness.Create(_db, extraConfig: On);
        var s = await h.SeedAsync();
        var (e, p, kb) = await SummaryWithKeyboardAsync(h, s);
        var calls = h.Ai.Calls.Count;

        var press = await PressAsync(e, kb, "by", profile: new TelegramProfile("Anna", null, null));

        Assert.Equal(ButtonResultCodes.Confirmed, press.Act!.Code);
        Assert.Equal(calls, h.Ai.Calls.Count); // LLM не викликався
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(press.Act.Confirmed!.Appointment.Id, appt.Id);
        Assert.True(press.Act.Confirmed.ReminderChoiceNeeded); // вподобання ще немає -> меню нагадувань (конвеєр)
        Assert.Equal("confirmed", (await h.ProposalsAsync(s)).Single().Status);
        var row = await LoadKbAsync(e, kb.Id);
        Assert.Equal(("used", press.Inbound), (row.Status, row.UsedByMessageId!.Value));
        var journal = Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "confirm_booking"));
        Assert.Matches(@"""via"":[ ]*""button""", journal.Payload);
        Assert.Contains(p.Id.ToString(), journal.Payload, StringComparison.OrdinalIgnoreCase);

        // те саме текстовим «Так» (інший tenant): ті самі поля запису
        var s2 = await h.SeedAsync();
        await h.ShowSummaryAsync(s2);
        await h.SayAsync(s2, "Так");
        var text = Assert.Single(await h.AppointmentsAsync(s2));
        Assert.Equal(
            (text.Status, text.Source, text.StartsAt, text.DurationMinutes, text.PriceOriginal, text.PriceFinal, text.PaymentMethod, text.ReminderMethod, text.ReminderOption, text.ClientConfirmedAt),
            (appt.Status, appt.Source, appt.StartsAt, appt.DurationMinutes, appt.PriceOriginal, appt.PriceFinal, appt.PaymentMethod, appt.ReminderMethod, appt.ReminderOption, appt.ClientConfirmedAt));

        // повторне натискання тієї самої кнопки: запису вдруге немає
        var again = await PressAsync(e, kb, "by");
        Assert.Equal(CallbackOutcome.Used, again.Res.Outcome);
        Assert.Null(again.Act);
        Assert.Single(await h.AppointmentsAsync(s));
    }

    [SkippableFact]
    public async Task two_simultaneous_yes_presses_create_one_booking_and_one_winner()
    {
        var h = ChatHarness.Create(_db, extraConfig: On);
        var s = await h.SeedAsync();
        var (e, _, kb) = await SummaryWithKeyboardAsync(h, s);
        var i1 = await AddInboundCallbackAsync(e);
        var i2 = await AddInboundCallbackAsync(e);
        var r1 = await ResolveAsync(e, kb.Data("by"), inbound: i1);
        var r2 = await ResolveAsync(e, kb.Data("by"), inbound: i2);
        Assert.True(r1.IsValid && r2.IsValid); // обидва бачать active

        var results = await Task.WhenAll(ExecuteAsync(e, r1, i1), ExecuteAsync(e, r2, i2));

        Assert.Equal(1, results.Count(r => r.Code == ButtonResultCodes.Confirmed));
        Assert.Equal(1, results.Count(r => r.Code == ButtonResultCodes.Used));
        Assert.Single(await h.AppointmentsAsync(s));
        Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "confirm_booking"));
    }

    [SkippableFact]
    public async Task a_text_between_the_summary_and_the_press_supersedes_the_proposal_and_nothing_is_booked()
    {
        var h = ChatHarness.Create(_db, extraConfig: On);
        var s = await h.SeedAsync();
        var (e, p, kb) = await SummaryWithKeyboardAsync(h, s);
        h.Ai.Enqueue(Text("Перевірю 15:00."));
        await h.SayAsync(s, "а можна о 15?");
        Assert.Equal("superseded", (await h.ProposalsAsync(s)).Single(x => x.Id == p.Id).Status);

        var press = await PressAsync(e, kb, "by");

        // TASK-753: текст відкликає клавіатуру пропозиції (§23.6), тож натискання відсікається ще на кроці 7 (revoked)
        Assert.Equal(CallbackOutcome.Revoked, press.Res.Outcome);
        Assert.Null(press.Act);
        Assert.Empty(await h.AppointmentsAsync(s));
        Assert.Equal("revoked", (await LoadKbAsync(e, kb.Id)).Status);
    }

    [SkippableFact]
    public async Task yes_button_on_a_summary_that_is_not_the_keyboards_message_is_stale()
    {
        var h = ChatHarness.Create(_db, extraConfig: On);
        var s = await h.SeedAsync();
        var conv = await h.ShowSummaryAsync(s);
        var p = Assert.Single(await h.ProposalsAsync(s));
        var e = new Env { H = h, S = s, Conv = conv };
        // клавіатура на ІНШОМУ повідомленні, не на підсумку пропозиції
        var kb = await KbAsync(e, KeyboardKinds.BookingConfirm, [new("by", "Так")], proposal: p.Id, conv: conv);

        var press = await PressAsync(e, kb, "by");

        Assert.Equal(ButtonResultCodes.ProposalNotActive, press.Act!.Code);
        Assert.Empty(await h.AppointmentsAsync(s));
    }

    [SkippableFact]
    public async Task expired_booking_keyboard_resolves_as_expired_and_creates_nothing()
    {
        var h = ChatHarness.Create(_db, extraConfig: On);
        var s = await h.SeedAsync();
        var (e, _, kb) = await SummaryWithKeyboardAsync(h, s);
        h.Clock.Advance(TimeSpan.FromMinutes(20)); // TTL пропозиції 15 хв

        var r = await ResolveAsync(e, kb.Data("by"));

        Assert.Equal((CallbackOutcome.Expired, true), (r.Outcome, r.ClearMarkup));
        Assert.Equal(kb.Id, r.Keyboard!.Id);
        Assert.Equal(r.Keyboard.ProposalId, (await h.ProposalsAsync(s)).Single().Id); // конвеєр ставить пропозицію expired за цим id
        Assert.Empty(await h.AppointmentsAsync(s));
    }

    [SkippableFact]
    public async Task no_and_other_time_buttons_reject_the_proposal_once()
    {
        var h = ChatHarness.Create(_db, extraConfig: On);
        var s = await h.SeedAsync();
        var (e, p, kb) = await SummaryWithKeyboardAsync(h, s);

        var no = await PressAsync(e, kb, "bn");
        Assert.Equal(ButtonResultCodes.Declined, no.Act!.Code);
        Assert.Equal("rejected", (await h.ProposalsAsync(s)).Single().Status);
        Assert.Equal(CallbackOutcome.Used, (await PressAsync(e, kb, "bo")).Res.Outcome);

        var s2 = await h.SeedAsync();
        var (e2, _, kb2) = await SummaryWithKeyboardAsync(h, s2);
        var other = await PressAsync(e2, kb2, "bo");
        Assert.Equal(ButtonResultCodes.OtherTime, other.Act!.Code);
        Assert.Equal("rejected", (await h.ProposalsAsync(s2)).Single().Status);
        Assert.Empty(await h.AppointmentsAsync(s));
        Assert.Empty(await h.AppointmentsAsync(s2));
        _ = p;
    }

    // =============================================================== вибір слота

    [SkippableFact]
    public async Task slot_pick_prepares_a_proposal_without_llm_and_unoffered_slots_are_taken()
    {
        var h = ChatHarness.Create(_db, extraConfig: On);
        var s = await h.SeedAsync();
        var conv = (await h.IngestAsync(s, "Привіт")).Conversation!.Value;
        var e = new Env { H = h, S = s, Conv = conv };
        var good = new KeyboardOption("sp", "10:00", s.LocationId, s.ServiceId, s.SpecialistId, s.Start);
        var night = new KeyboardOption("sp", "03:00", s.LocationId, s.ServiceId, s.SpecialistId, s.Start.AddHours(-7)); // поза графіком
        var kb = await KbAsync(e, KeyboardKinds.SlotPick, [night, good]);
        var calls = h.Ai.Calls.Count;

        var bad = await PressAsync(e, kb, "sp", 0);
        Assert.Equal(ButtonResultCodes.SlotTaken, bad.Act!.Code);
        Assert.Empty(await h.ProposalsAsync(s));

        var kb2 = await KbAsync(e, KeyboardKinds.SlotPick, [night, good]);
        var ok = await PressAsync(e, kb2, "sp", 1);
        Assert.Equal(ButtonResultCodes.SummaryReady, ok.Act!.Code);
        Assert.Equal("Стрижка", ok.Act.Prepared!.Summary.ServiceName);
        var p = Assert.Single(await h.ProposalsAsync(s));
        Assert.Equal(("prepared", ok.Act.Prepared.ProposalId, s.Start.ToUniversalTime()), (p.Status, p.Id, p.StartsAt));
        Assert.Equal(calls, h.Ai.Calls.Count);
        Assert.Equal("used", (await LoadKbAsync(e, kb2.Id)).Status);
        Assert.Empty(await h.AppointmentsAsync(s)); // запис лише після підтвердження

        // новий вибір супersedes попередню пропозицію
        var kb3 = await KbAsync(e, KeyboardKinds.SlotPick, [good]);
        await PressAsync(e, kb3, "sp", 0);
        var all = await h.ProposalsAsync(s);
        Assert.Equal(["superseded", "prepared"], all.Select(x => x.Status).ToArray());
    }

    // =============================================================== «Мої записи»

    [SkippableFact]
    public async Task my_appointments_lists_only_own_future_active_ones_up_to_the_limit()
    {
        var e = await NewAsync();
        var other = await SetClientAsync(e, (await e.H.IngestAsync(e.S, "Hi", chat: "200")).Conversation!.Value, "Інна");
        for (var i = 1; i <= 6; i++) await AddApptAsync(e, fromNow: TimeSpan.FromDays(i));
        await AddApptAsync(e, fromNow: TimeSpan.FromHours(-5));                                   // минулий
        await AddApptAsync(e, status: AppointmentStatus.Cancelled, fromNow: TimeSpan.FromHours(30)); // скасований
        await AddApptAsync(e, status: AppointmentStatus.Completed, fromNow: TimeSpan.FromHours(27)); // не pending|confirmed
        var foreign = await AddApptAsync(e, other, TimeSpan.FromHours(1));                         // чужий клієнт: найближчий за часом

        var r = await Scope(e, sp => sp.GetRequiredService<ClientChatAppointments>().ListUpcomingAsync(e.Conv, default));

        Assert.Equal(4, r.Items.Count); // ChatButtons:MyAppointmentsMax
        Assert.DoesNotContain(r.Items, i => i.Id == foreign.Id);
        Assert.True(r.Items.Zip(r.Items.Skip(1)).All(p => p.First.StartsAt <= p.Second.StartsAt));
        Assert.All(r.Items, i => Assert.Equal(("Стрижка", "Марина", "Салон Центр", "Europe/Kyiv"), (i.ServiceName, i.SpecialistName, i.LocationName, i.Timezone)));

        var press = await ExecuteAsync(e, new CallbackResolution(CallbackOutcome.Stateless, "i:my"), await AddInboundCallbackAsync(e));
        Assert.Equal((ButtonResultCodes.MyList, 4), (press.Code, press.Appointments!.Count));
        Assert.DoesNotContain(press.Appointments, i => i.Id == foreign.Id);
    }

    [SkippableFact]
    public async Task my_appointments_without_a_client_or_appointments_is_empty()
    {
        var noClient = await NewAsync(withClient: false);
        var r = await Scope(noClient, sp => sp.GetRequiredService<ClientChatAppointments>().ListUpcomingAsync(noClient.Conv, default));
        Assert.Equal((true, 0), (r.NoClient, r.Items.Count));
        Assert.Equal(ButtonResultCodes.MyEmpty, (await ExecuteAsync(noClient, new CallbackResolution(CallbackOutcome.Stateless, "i:my"), await AddInboundCallbackAsync(noClient))).Code);

        var e = await NewAsync();
        Assert.Equal(ButtonResultCodes.MyEmpty, (await ExecuteAsync(e, new CallbackResolution(CallbackOutcome.Stateless, "i:my"), await AddInboundCallbackAsync(e))).Code);
    }

    [SkippableFact]
    public async Task stateless_actions_carry_only_facts_for_the_pipeline()
    {
        var e = await NewAsync();
        Task<ButtonActionResult> Do(string v) => ExecuteAsync(e, new CallbackResolution(CallbackOutcome.Stateless, v), Guid.NewGuid());
        Assert.Equal(ButtonResultCodes.BookStart, (await Do("i:book")).Code);
        Assert.Equal(ButtonResultCodes.Help, (await Do("i:help")).Code);
        Assert.Equal(ButtonResultCodes.Stop, (await Do("i:stop")).Code);
        var human = await Do("i:human");
        Assert.Equal((ButtonResultCodes.Human, "human_requested"), (human.Code, human.HandoffReason));
        // не-Valid результат дії не виконує
        Assert.Equal(ButtonResultCodes.Stale, (await ExecuteAsync(e, new CallbackResolution(CallbackOutcome.Used), Guid.NewGuid())).Code);
    }

    // =============================================================== скасування

    private async Task<Env> BookedViaChatAsync(ChatHarness h, ChatSeed s, string reminderAnswer = "2 3")
    {
        var conv = await h.ShowSummaryAsync(s);
        await h.SayAsync(s, "Так");
        await h.SayAsync(s, reminderAnswer);
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        return new Env { H = h, S = s, Conv = conv, ClientId = appt.ClientId };
    }

    [SkippableFact]
    public async Task cancel_in_two_steps_cancels_the_own_booking_as_client_and_closes_reminders_and_journals_it()
    {
        var h = ChatHarness.Create(_db, extraConfig: On);
        var s = await h.SeedAsync();
        var e = await BookedViaChatAsync(h, s);
        var appt = Assert.Single(await h.AppointmentsAsync(s));
        Assert.Equal(2, (await RemindersAsync(e)).Count(r => r.Status == ReminderStatus.Scheduled));

        var actions = await ActionsKbAsync(e, appt.Id);
        var ask = await PressAsync(e, actions, "ac");
        Assert.Equal(ButtonResultCodes.CancelScreen, ask.Act!.Code);
        Assert.Equal(appt.Id, ask.Act.Appointment!.Id);
        Assert.Equal("Оплата в закладі, тож нічого повертати не потрібно.", ask.Act.Policy!.Text);
        Assert.Equal("active", (await LoadKbAsync(e, actions.Id)).Status); // ac не споживає
        Assert.Equal("confirmed", EnumText(await LoadApptAsync(e, appt.Id)));

        var confirm = await ConfirmKbAsync(e, appt.Id);
        var yes = await PressAsync(e, confirm, "cy");

        Assert.Equal(ButtonResultCodes.Cancelled, yes.Act!.Code);
        var after = await LoadApptAsync(e, appt.Id);
        Assert.Equal((AppointmentStatus.Cancelled, "client", "Скасовано клієнтом у Telegram", (Guid?)null), (after.Status, after.CancelledByType, after.CancelReason, after.CancelledByUserId));
        Assert.NotNull(after.CancelledAt);
        var reminders = await RemindersAsync(e);
        Assert.Empty(reminders.Where(r => r.Status == ReminderStatus.Scheduled));
        Assert.All(reminders.Where(r => r.Status == ReminderStatus.Cancelled), r => Assert.Equal("appointment_inactive", r.SkipReason));
        Assert.Equal(2, reminders.Count(r => r.Status == ReminderStatus.Cancelled));
        var journal = Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "client_cancel"));
        Assert.Equal(e.Conv, journal.ConversationId);
        Assert.Matches(@"""via"":[ ]*""button""", journal.Payload);
        Assert.Contains($"appointment:{appt.Id}", journal.Result);
        Assert.Equal("used", (await LoadKbAsync(e, confirm.Id)).Status);

        // в історії/календарі: скасував клієнт
        var dto = await Scope(e, sp => sp.GetRequiredService<IBookingStore>().GetAppointmentAsync(appt.Id, default));
        Assert.Equal("client", dto!.CancelledBy!.Type);
        Assert.Equal("Скасовано клієнтом у Telegram", dto.CancelReason);
        // повторне натискання - нічого
        Assert.Equal(CallbackOutcome.Used, (await PressAsync(e, confirm, "cy")).Res.Outcome);
        Assert.Single((await h.ActionsAsync(s)).Where(a => a.ToolName == "client_cancel"));
    }

    private static string EnumText(Appointment a) => a.Status.ToString().ToLowerInvariant();

    private async Task<List<Reminder>> RemindersAsync(Env e)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        return await ctx.Reminders.AsNoTracking().ToListAsync();
    }

    [SkippableFact]
    public async Task keep_button_leaves_the_booking_and_consumes_the_confirm_screen()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var confirm = await ConfirmKbAsync(e, appt.Id);

        var no = await PressAsync(e, confirm, "cn");

        Assert.Equal((ButtonResultCodes.CancelKept, appt.Id), (no.Act!.Code, no.Act.Appointment!.Id));
        Assert.Equal(AppointmentStatus.Confirmed, (await LoadApptAsync(e, appt.Id)).Status);
        Assert.Equal("used", (await LoadKbAsync(e, confirm.Id)).Status);
        Assert.Equal(CallbackOutcome.Used, (await PressAsync(e, confirm, "cy")).Res.Outcome); // після «Ні» «Так» вже не діє
        Assert.Equal(AppointmentStatus.Confirmed, (await LoadApptAsync(e, appt.Id)).Status);
    }

    [SkippableFact]
    public async Task cancel_screen_expires_after_ten_minutes()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var confirm = await ConfirmKbAsync(e, appt.Id);
        e.H.Clock.Advance(TimeSpan.FromMinutes(11));

        var press = await PressAsync(e, confirm, "cy");

        Assert.Equal((CallbackOutcome.Expired, null), (press.Res.Outcome, (object?)press.Act));
        Assert.Equal(AppointmentStatus.Confirmed, (await LoadApptAsync(e, appt.Id)).Status);
    }

    [SkippableFact]
    public async Task another_clients_appointment_cannot_be_cancelled_or_confirmed_even_with_a_forged_keyboard()
    {
        var e = await NewAsync();
        var otherClient = await SetClientAsync(e, (await e.H.IngestAsync(e.S, "Hi", chat: "200")).Conversation!.Value, "Інна");
        var foreign = await AddApptAsync(e, otherClient);

        // 1) сервіс не створює клавіатуру з чужим записом
        var msg = await AddOutboundAsync(e);
        var created = await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().CreateAsync(
            new NewKeyboard(e.Conv, msg, KeyboardKinds.CancelConfirm, [new("cy", "Так", AppointmentId: foreign.Id)], AppointmentId: foreign.Id), default));
        Assert.Equal("appointment_not_found", created.Error!.Code);

        // 2) підроблений рядок у БД (обхід сервісу): дія все одно відмовляє, запис не змінюється
        var forgedToken = FakeToken();
        await using (var ctx = _db.CreateContext(e.Tenant))
        {
            ctx.Add(new ChatKeyboard
            {
                ConversationId = e.Conv, MessageId = await AddOutboundAsync(e), Kind = KeyboardKinds.CancelConfirm, Token = forgedToken,
                Options = KeyboardJson.Serialize([new("cy", "Так", AppointmentId: foreign.Id), new("cn", "Ні", AppointmentId: foreign.Id)]),
                AppointmentId = foreign.Id, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
            ctx.Add(new ChatKeyboard
            {
                ConversationId = e.Conv, MessageId = await AddOutboundAsync(e), Kind = KeyboardKinds.AppointmentActions, Token = FakeToken(),
                Options = KeyboardJson.Serialize([new("aa", "Так", AppointmentId: foreign.Id), new("ac", "Ні", AppointmentId: foreign.Id)]),
                AppointmentId = foreign.Id, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
            await ctx.SaveChangesAsync();
        }
        await using var rd = _db.CreateContext(e.Tenant);
        var kbs = await rd.ChatKeyboards.AsNoTracking().ToListAsync();
        foreach (var k in kbs)
        {
            var created2 = new CreatedKeyboard(k.Id, k.Token, k.ExpiresAt, []);
            var verbs = k.Kind == KeyboardKinds.CancelConfirm ? new[] { "cy", "cn" } : new[] { "aa", "ac" };
            foreach (var v in verbs)
            {
                var press = await PressAsync(e, created2, v);
                Assert.True(press.Res.IsValid);
                Assert.Contains(press.Act!.Code, new[] { ButtonResultCodes.CancelUnavailable, ButtonResultCodes.AppointmentInactive });
                Assert.Null(press.Act.Appointment);
            }
        }
        var f = await LoadApptAsync(e, foreign.Id);
        Assert.Equal((AppointmentStatus.Confirmed, null, null), (f.Status, f.CancelledByType, (object?)f.ClientConfirmedAt));
        Assert.Empty((await e.H.ActionsAsync(e.S)).Where(a => a.ToolName is "client_cancel" or "client_attend_confirm"));
    }

    [SkippableFact]
    public async Task started_past_cancelled_and_missing_appointments_are_unavailable_and_stay_unchanged()
    {
        var e = await NewAsync();
        var started = await AddApptAsync(e, fromNow: TimeSpan.FromMinutes(-10));
        var completed = await AddApptAsync(e, status: AppointmentStatus.Completed);
        var staffCancelled = await AddApptAsync(e, status: AppointmentStatus.Cancelled);

        foreach (var a in new[] { started, completed, staffCancelled })
        {
            var kb = await ConfirmKbAsync(e, a.Id);
            Assert.Equal(ButtonResultCodes.CancelUnavailable, (await PressAsync(e, kb, "cy")).Act!.Code);
            var actions = await ActionsKbAsync(e, a.Id, DateTimeOffset.UtcNow.AddHours(1));
            Assert.Equal(ButtonResultCodes.CancelUnavailable, (await PressAsync(e, actions, "ac")).Act!.Code);
            Assert.Equal("active", (await LoadKbAsync(e, kb.Id)).Status); // CAS не виконано до доменної перевірки
        }
        Assert.Equal(AppointmentStatus.Confirmed, (await LoadApptAsync(e, started.Id)).Status);
        Assert.Equal(AppointmentStatus.Completed, (await LoadApptAsync(e, completed.Id)).Status);
        Assert.Empty((await e.H.ActionsAsync(e.S)).Where(a => a.ToolName == "client_cancel"));
    }

    [SkippableFact]
    public async Task staff_cancelling_between_the_screen_and_yes_gives_unavailable()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var confirm = await ConfirmKbAsync(e, appt.Id);
        var staffId = Guid.NewGuid();
        await using (var uc = _db.CreateContext(e.Tenant))
        {
            uc.Add(new User { Id = staffId, Email = $"{staffId:N}@t.test", FullName = "Олена", PasswordHash = "x", Role = Roles.Admin });
            await uc.SaveChangesAsync();
        }
        await Scope(e, sp => sp.GetRequiredService<CancellationService>().CancelAsync(appt.Id, default, CancelOrigin.Staff(staffId), "перенесли"));

        var yes = await PressAsync(e, confirm, "cy");

        Assert.Equal(ButtonResultCodes.CancelUnavailable, yes.Act!.Code);
        var row = await LoadApptAsync(e, appt.Id);
        Assert.Equal("staff", row.CancelledByType); // автора скасування не перезаписано
    }

    [SkippableFact]
    public async Task cancellation_in_chat_can_be_disabled_or_require_a_minimum_lead_time()
    {
        var off = await NewAsync(extra: new Dictionary<string, string?> { ["ChatCancel:Enabled"] = "false" });
        var a1 = await AddApptAsync(off);
        Assert.Equal(ButtonResultCodes.CancelUnavailable, (await PressAsync(off, await ActionsKbAsync(off, a1.Id), "ac")).Act!.Code);
        Assert.Equal(AppointmentStatus.Confirmed, (await LoadApptAsync(off, a1.Id)).Status);

        var lead = await NewAsync(extra: new Dictionary<string, string?> { ["ChatCancel:MinLeadMinutes"] = "180" });
        var soon = await AddApptAsync(lead, fromNow: TimeSpan.FromHours(2));
        var later = await AddApptAsync(lead, fromNow: TimeSpan.FromHours(4));
        Assert.Equal(ButtonResultCodes.CancelUnavailable, (await PressAsync(lead, await ConfirmKbAsync(lead, soon.Id), "cy")).Act!.Code);
        Assert.Equal(ButtonResultCodes.Cancelled, (await PressAsync(lead, await ConfirmKbAsync(lead, later.Id), "cy")).Act!.Code);
        Assert.Equal(AppointmentStatus.Confirmed, (await LoadApptAsync(lead, soon.Id)).Status);
    }

    [SkippableFact]
    public async Task the_fourth_cancellation_in_24_hours_hands_off_and_leaves_the_booking_active()
    {
        var e = await NewAsync();
        for (var i = 1; i <= 3; i++)
        {
            var a = await AddApptAsync(e, fromNow: TimeSpan.FromDays(i));
            Assert.Equal(ButtonResultCodes.Cancelled, (await PressAsync(e, await ConfirmKbAsync(e, a.Id), "cy")).Act!.Code);
        }
        Assert.Equal(3, (await e.H.ActionsAsync(e.S)).Count(a => a.ToolName == "client_cancel"));

        var fourth = await AddApptAsync(e, fromNow: TimeSpan.FromDays(5));
        var kb = await ConfirmKbAsync(e, fourth.Id);
        var press = await PressAsync(e, kb, "cy");

        Assert.Equal((ButtonResultCodes.CancelLimit, "cancel_limit"), (press.Act!.Code, press.Act.HandoffReason));
        Assert.Equal(AppointmentStatus.Confirmed, (await LoadApptAsync(e, fourth.Id)).Status);
        Assert.Equal(3, (await e.H.ActionsAsync(e.S)).Count(a => a.ToolName == "client_cancel"));
        // ліміт рахується по КЛІЄНТУ: друга розмова того самого клієнта його не обходить
        var conv2 = (await e.H.IngestAsync(e.S, "Hi", chat: "300")).Conversation!.Value;
        await using (var ctx = _db.CreateContext(e.Tenant))
            await ctx.Conversations.Where(c => c.Id == conv2).ExecuteUpdateAsync(u => u.SetProperty(c => c.ClientId, (Guid?)e.ClientId));
        var kb2 = await KbAsync(e, KeyboardKinds.CancelConfirm, [new("cy", "Так", AppointmentId: fourth.Id)], appt: fourth.Id, conv: conv2);
        Assert.Equal(ButtonResultCodes.CancelLimit, (await PressAsync(e, kb2, "cy", conv: conv2)).Act!.Code);
        // записи минулої доби не рахуються
        e.H.Clock.Advance(TimeSpan.FromHours(25));
        var kb3 = await KbAsync(e, KeyboardKinds.CancelConfirm, [new("cy", "Так", AppointmentId: fourth.Id)], appt: fourth.Id);
        Assert.Equal(ButtonResultCodes.Cancelled, (await PressAsync(e, kb3, "cy")).Act!.Code);
    }

    [SkippableFact]
    public async Task two_simultaneous_yes_presses_cancel_once()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var confirm = await ConfirmKbAsync(e, appt.Id);
        var i1 = await AddInboundCallbackAsync(e);
        var i2 = await AddInboundCallbackAsync(e);
        var r1 = await ResolveAsync(e, confirm.Data("cy"), inbound: i1);
        var r2 = await ResolveAsync(e, confirm.Data("cy"), inbound: i2);

        var results = await Task.WhenAll(ExecuteAsync(e, r1, i1), ExecuteAsync(e, r2, i2));

        Assert.Equal(1, results.Count(r => r.Code == ButtonResultCodes.Cancelled));
        Assert.Equal(1, results.Count(r => r.Code == ButtonResultCodes.Used));
        Assert.Single((await e.H.ActionsAsync(e.S)).Where(a => a.ToolName == "client_cancel"));
    }

    [SkippableFact]
    public async Task a_replayed_yes_after_a_crash_reports_the_cancel_without_a_second_journal_entry()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var confirm = await ConfirmKbAsync(e, appt.Id);
        var first = await PressAsync(e, confirm, "cy");
        Assert.Equal(ButtonResultCodes.Cancelled, first.Act!.Code);

        var replay = await ResolveAsync(e, confirm.Data("cy"), inbound: first.Inbound);
        Assert.True(replay.Replay);
        var again = await ExecuteAsync(e, replay, first.Inbound);

        Assert.Equal(ButtonResultCodes.Cancelled, again.Code);
        Assert.Single((await e.H.ActionsAsync(e.S)).Where(a => a.ToolName == "client_cancel"));
    }

    [SkippableFact]
    public async Task paid_booking_shows_the_refund_policy_as_text_and_refunds_through_the_cancellation_service()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e, price: 400m);
        await using (var ctx = _db.CreateContext(e.Tenant))
        {
            ctx.Add(new Payment { AppointmentId = appt.Id, Amount = 400m, Method = BeautyCrm.Infrastructure.Data.Entities.PaymentMethod.Card, Status = PaymentStatus.Paid, ProviderPaymentId = "mock_p" });
            await ctx.SaveChangesAsync();
        }
        var expected = await Scope(e, async sp => CancellationPolicy.Calculate(appt.StartsAt, DateTimeOffset.UtcNow, 400m, await sp.GetRequiredService<CancellationSettingsService>().GetAsync(default)));

        var ask = await PressAsync(e, await ActionsKbAsync(e, appt.Id), "ac");

        var policy = ask.Act!.Policy!;
        Assert.Equal((expected.Percent, expected.Amount, expected.FeePercent), (policy.RefundPercent, policy.RefundAmount, policy.FeePercent));
        Assert.StartsWith($"Повернемо {expected.Percent}% передоплати ({expected.Amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} грн)", policy.Text);
        Assert.True(expected.Amount > 0);

        var yes = await PressAsync(e, await ConfirmKbAsync(e, appt.Id), "cy");
        Assert.Equal(ButtonResultCodes.Cancelled, yes.Act!.Code);
        Assert.Equal(expected.Amount, yes.Act.Policy!.RefundAmount);
        await using var rd = _db.CreateContext(e.Tenant);
        Assert.Equal(PaymentStatus.Refunded, (await rd.Payments.AsNoTracking().SingleAsync()).Status);
    }

    private sealed class FailingRefunds : IPaymentService
    {
        public Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct) => Task.FromResult(new PaymentResult(true, "x", null));
        public Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct) => Task.FromResult(new RefundResult(false, null, "provider down"));
    }

    [SkippableFact]
    public async Task failed_refund_keeps_the_booking_and_signals_a_cancel_failed_handoff()
    {
        var e = await NewAsync(configure: s => { s.RemoveAll<IPaymentService>(); s.AddSingleton<IPaymentService>(new FailingRefunds()); });
        var appt = await AddApptAsync(e, price: 400m);
        await using (var ctx = _db.CreateContext(e.Tenant))
        {
            ctx.Add(new Payment { AppointmentId = appt.Id, Amount = 400m, Method = BeautyCrm.Infrastructure.Data.Entities.PaymentMethod.Card, Status = PaymentStatus.Paid, ProviderPaymentId = "mock_p" });
            await ctx.SaveChangesAsync();
        }

        var yes = await PressAsync(e, await ConfirmKbAsync(e, appt.Id), "cy");

        Assert.Equal((ButtonResultCodes.CancelFailed, "cancel_failed"), (yes.Act!.Code, yes.Act.HandoffReason));
        Assert.Equal(AppointmentStatus.Confirmed, (await LoadApptAsync(e, appt.Id)).Status);
        // TASK-759 (L2): резерв місця під ліміт звільнено (status failed), успішного скасування в журналі немає
        Assert.Empty((await e.H.ActionsAsync(e.S)).Where(a => a.ToolName == "client_cancel" && a.Status != BeautyCrm.Infrastructure.Data.Entities.AiActionStatus.Failed));
    }

    // =============================================================== «Підтверджую візит», «Змінити час»

    [SkippableFact]
    public async Task attend_confirms_once_is_idempotent_and_keeps_the_keyboard_active()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var actions = await ActionsKbAsync(e, appt.Id);

        var first = await PressAsync(e, actions, "aa");
        var second = await PressAsync(e, actions, "aa");

        Assert.Equal(ButtonResultCodes.AttendOk, first.Act!.Code);
        Assert.Equal(ButtonResultCodes.AttendNoop, second.Act!.Code); // без відповіді
        var row = await LoadApptAsync(e, appt.Id);
        Assert.NotNull(row.ClientConfirmedAt);
        Assert.Equal(row.ClientConfirmedAt!.Value, first.Act.Appointment!.ClientConfirmedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal("active", (await LoadKbAsync(e, actions.Id)).Status); // не споживається
        var journal = Assert.Single((await e.H.ActionsAsync(e.S)).Where(a => a.ToolName == "client_attend_confirm"));
        Assert.Equal(e.Conv, journal.ConversationId);
        // у DTO запису/календаря
        var dto = await Scope(e, sp => sp.GetRequiredService<IBookingStore>().GetAppointmentAsync(appt.Id, default));
        Assert.Equal(row.ClientConfirmedAt, dto!.ClientConfirmedAt);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"clientConfirmedAt\":", json);
        var plain = await AddApptAsync(e);
        var plainJson = JsonSerializer.Serialize(await Scope(e, sp => sp.GetRequiredService<IBookingStore>().GetAppointmentAsync(plain.Id, default)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("clientConfirmedAt", plainJson); // відсутнє, якщо NULL
    }

    [SkippableFact]
    public async Task attend_ignores_cancelled_and_past_visits()
    {
        var e = await NewAsync();
        var past = await AddApptAsync(e, fromNow: TimeSpan.FromMinutes(-5));
        var cancelled = await AddApptAsync(e, status: AppointmentStatus.Cancelled);
        foreach (var a in new[] { past, cancelled })
            Assert.Equal(ButtonResultCodes.AppointmentInactive, (await PressAsync(e, await ActionsKbAsync(e, a.Id, DateTimeOffset.UtcNow.AddHours(1)), "aa")).Act!.Code);
        Assert.Null((await LoadApptAsync(e, past.Id)).ClientConfirmedAt);
    }

    [SkippableFact]
    public async Task reschedule_is_a_handoff_signal_for_the_own_booking_only()
    {
        var e = await NewAsync();
        var mine = await AddApptAsync(e);
        var otherClient = await SetClientAsync(e, (await e.H.IngestAsync(e.S, "Hi", chat: "200")).Conversation!.Value, "Інна");
        var foreign = await AddApptAsync(e, otherClient);
        var list = await KbAsync(e, KeyboardKinds.AppointmentList, [new("ac", "x", AppointmentId: mine.Id), new("ar", "🕐 Змінити час", AppointmentId: mine.Id)]);

        var press = await PressAsync(e, list, "ar", 1);

        Assert.Equal((ButtonResultCodes.RescheduleHandoff, "reschedule_request", mine.Id), (press.Act!.Code, press.Act.HandoffReason, press.Act.Appointment!.Id));
        Assert.Equal("active", (await LoadKbAsync(e, list.Id)).Status);
        Assert.Equal(AppointmentStatus.Confirmed, (await LoadApptAsync(e, mine.Id)).Status);

        var forged = await ExecuteAsync(e, new CallbackResolution(CallbackOutcome.Valid, "ar", null, new KeyboardInfo(Guid.NewGuid(), KeyboardKinds.AppointmentList, "active", Guid.NewGuid(), null, null, DateTimeOffset.UtcNow.AddHours(1), null, null),
            new KeyboardOption("ar", "x", AppointmentId: foreign.Id)), Guid.NewGuid());
        Assert.Equal(ButtonResultCodes.AppointmentInactive, forged.Code);
    }

    [SkippableFact]
    public async Task reminder_choice_is_validated_and_consumed_for_the_pipeline_to_apply()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var kb = await KbAsync(e, KeyboardKinds.ReminderChoice,
            [new("rc", "За 24 і 2 год", Method: "telegram", Offsets: [120, 1440]), new("rc", "🔕 Не нагадувати", Method: "none", Offsets: [])], appt: appt.Id);

        var press = await PressAsync(e, kb, "rc", 0);

        Assert.Equal(ButtonResultCodes.ReminderChoice, press.Act!.Code);
        Assert.Equal("telegram", press.Act.Option!.Method);
        Assert.Equal(new short[] { 120, 1440 }, press.Act.Option.Offsets!.ToArray());
        Assert.Equal(appt.Id, press.Act.Appointment!.Id);
        Assert.Equal("used", (await LoadKbAsync(e, kb.Id)).Status);
        Assert.Equal(CallbackOutcome.Used, (await PressAsync(e, kb, "rc", 1)).Res.Outcome);

        var cancelled = await AddApptAsync(e, status: AppointmentStatus.Cancelled);
        var kb2 = await KbAsync(e, KeyboardKinds.ReminderChoice, [new("rc", "x", Method: "none", Offsets: [])], appt: cancelled.Id);
        Assert.Equal(ButtonResultCodes.AppointmentInactive, (await PressAsync(e, kb2, "rc", 0)).Act!.Code);
    }

    // =============================================================== inbox (§23.10)

    [SkippableFact]
    public async Task inbox_shows_presses_as_label_templates_button_captions_and_client_confirmation_without_secrets()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var actions = await ActionsKbAsync(e, appt.Id);
        var confirm = await ConfirmKbAsync(e, appt.Id);

        var outMsg = await AddOutboundAsync(e);
        var layoutJson = ChatKeyboardLayout.ToActionsJson([[new ActionButton("✅ Підтверджую візит", actions.Data("aa")), new ActionButton("❌ Скасувати візит", actions.Data("ac"))], [new ActionButton("🔕 Не нагадувати", "i:stop")]]);
        await using (var ctx = _db.CreateContext(e.Tenant))
            await ctx.Messages.Where(m => m.Id == outMsg).ExecuteUpdateAsync(u => u.SetProperty(m => m.Actions, layoutJson));

        await PressAsync(e, actions, "aa");
        var yes = await PressAsync(e, confirm, "cy");
        Assert.Equal(ButtonResultCodes.Cancelled, yes.Act!.Code);
        await Scope(e, sp => sp.GetRequiredService<ChatKeyboardService>().SetCallbackBodyAsync(yes.Inbound, "Так, скасувати", default));
        var appt2 = await AddApptAsync(e);

        await using (var ctx = _db.CreateContext(e.Tenant))
        {
            var admin = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@t.test", FullName = "Олена", PasswordHash = "x", Role = Roles.Admin };
            ctx.Add(admin);
            await ctx.SaveChangesAsync();
            var actor = new Actor(admin.Id, e.Tenant, Roles.Admin, null);
            var detail = await Scope(e, sp => sp.GetRequiredService<InboxService>().GetAsync(actor, e.Conv, null, null, default));
            Assert.True(detail.IsOk, detail.Error?.Code);
            var d = detail.Value!;

            var cb = d.Messages.Single(m => m.Id == yes.Inbound);
            Assert.Equal(("callback", "[кнопка] Так, скасувати", "Так, скасувати"), (cb.Kind, cb.Body, cb.PressedLabel));
            var raw = d.Messages.Single(m => m.Kind == "callback" && m.Id != yes.Inbound && m.Body == "[кнопка]");
            Assert.Null(raw.PressedLabel);
            var buttons = d.Messages.Single(m => m.Id == outMsg).Buttons!;
            Assert.Equal(["✅ Підтверджую візит", "❌ Скасувати візит", "🔕 Не нагадувати"], buttons);
            Assert.All(d.Messages.Where(m => m.Id != outMsg), m => Assert.Null(m.Buttons));

            var upcoming = d.UpcomingAppointments.Single(a => a.Id == appt.Id || a.Id == appt2.Id);
            Assert.Equal(appt2.Id, upcoming.Id);
            Assert.Null(upcoming.ClientConfirmedAt);

            var json = JsonSerializer.Serialize(d, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.DoesNotContain(actions.Token, json);
            Assert.DoesNotContain(confirm.Token, json);
            Assert.DoesNotContain("callback_data", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aa:", json);
        }
    }

    [SkippableFact]
    public async Task inbox_upcoming_appointments_carry_client_confirmed_at()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        await PressAsync(e, await ActionsKbAsync(e, appt.Id), "aa");
        await using var ctx = _db.CreateContext(e.Tenant);
        var admin = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@t.test", FullName = "Олена", PasswordHash = "x", Role = Roles.Admin };
        ctx.Add(admin);
        await ctx.SaveChangesAsync();

        var detail = await Scope(e, sp => sp.GetRequiredService<InboxService>().GetAsync(new Actor(admin.Id, e.Tenant, Roles.Admin, null), e.Conv, null, null, default));

        var a = detail.Value!.UpcomingAppointments.Single();
        Assert.NotNull(a.ClientConfirmedAt);
        Assert.Contains("\"clientConfirmedAt\":", JsonSerializer.Serialize(a, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    // =============================================================== ПД у логах

    [SkippableFact]
    public async Task logs_contain_ids_and_codes_but_no_tokens_callback_data_names_or_chat_ids()
    {
        var e = await NewAsync();
        var appt = await AddApptAsync(e);
        var actions = await ActionsKbAsync(e, appt.Id);
        var confirm = await ConfirmKbAsync(e, appt.Id);
        var slot = await KbAsync(e, KeyboardKinds.SlotPick, [new("sp", "14:30", e.S.LocationId, e.S.ServiceId, e.S.SpecialistId, e.S.Start)]);
        var otherConv = (await e.H.IngestAsync(e.S, "Hi", chat: "200")).Conversation!.Value;

        await PressAsync(e, actions, "aa");
        await PressAsync(e, actions, "ac");
        await PressAsync(e, actions, "aa", messageRef: "999");        // ref_mismatch
        await ResolveAsync(e, "by:" + FakeToken());                   // unknown_token
        await ResolveAsync(e, actions.Data("aa"), conv: otherConv);   // foreign_conversation
        await ResolveAsync(e, "garbage");                             // invalid_format
        await PressAsync(e, slot, "sp", 0);
        await PressAsync(e, confirm, "cy");
        await PressAsync(e, confirm, "cy");                           // used

        var logs = e.H.Logs.All;
        Assert.Contains("chat_callback conversation=", logs);
        Assert.Contains("result=ok", logs);
        Assert.Contains("result=unknown_token", logs);
        Assert.Contains("result=used", logs);
        Assert.Contains("client_cancel appointment=", logs);
        Assert.Contains("chat_keyboard_created conversation=", logs);
        foreach (var secret in new[] { actions.Token, confirm.Token, slot.Token, "Анна", "Підтверджую", "Скасувати", "Так, скасувати", ChatHarness.BotToken, "garbage" })
            Assert.DoesNotContain(secret, logs);
        Assert.DoesNotMatch(@"\b(by|bn|bo|sp|rc|ac|aa|ar|cy|cn):[A-Za-z0-9_-]{22}", logs);
        Assert.DoesNotContain(e.H.Logs.Lines.Where(l => l.Contains("chat_callback") || l.Contains("chat_keyboard") || l.Contains("client_cancel")), l => l.Contains("callback_data", StringComparison.OrdinalIgnoreCase));
    }

    // =============================================================== TASK-759 (L2, L10)

    [SkippableFact]
    public async Task parallel_cancellations_from_two_conversations_of_one_client_respect_the_daily_limit_atomically()
    {
        var e = await NewAsync(extra: new Dictionary<string, string?> { ["ChatCancel:MaxPerClientPerDay"] = "1" });
        for (var round = 0; round < 3; round++)
        {
            e.H.Clock.Advance(TimeSpan.FromHours(25)); // нова доба: журнал минулого раунду не рахується
            var conv2 = (await e.H.IngestAsync(e.S, "Hi", chat: "4" + round)).Conversation!.Value;
            await using (var ctx = _db.CreateContext(e.Tenant))
                await ctx.Conversations.Where(c => c.Id == conv2).ExecuteUpdateAsync(u => u.SetProperty(c => c.ClientId, (Guid?)e.ClientId));
            var a1 = await AddApptAsync(e, fromNow: TimeSpan.FromDays(10 + round * 2));
            var a2 = await AddApptAsync(e, fromNow: TimeSpan.FromDays(11 + round * 2));
            var k1 = await ConfirmKbAsync(e, a1.Id);
            var k2 = await KbAsync(e, KeyboardKinds.CancelConfirm, [new("cy", "Так", AppointmentId: a2.Id), new("cn", "Ні", AppointmentId: a2.Id)], appt: a2.Id, conv: conv2);

            var results = await Task.WhenAll(PressAsync(e, k1, "cy"), PressAsync(e, k2, "cy", conv: conv2));

            Assert.Single(results, r => r.Act!.Code == ButtonResultCodes.Cancelled);
            Assert.Single(results, r => r.Act!.Code == ButtonResultCodes.CancelLimit);
            var cancelled = new[] { await LoadApptAsync(e, a1.Id), await LoadApptAsync(e, a2.Id) }.Count(a => a.Status == AppointmentStatus.Cancelled);
            Assert.Equal(1, cancelled);
            // накопичувально: рівно один ефективний (не failed) запис журналу за раунд
            Assert.Equal(round + 1, (await e.H.ActionsAsync(e.S)).Count(a => a.ToolName == "client_cancel"
                && a.Status != BeautyCrm.Infrastructure.Data.Entities.AiActionStatus.Failed));
        }
    }

    private async Task SetChannelSettingsAsync(Env e, string settings)
    {
        await using var ctx = _db.CreateContext(e.Tenant);
        await ctx.Channels.Where(c => c.Id == e.S.ChannelId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Settings, settings));
    }

    [SkippableFact]
    public async Task channel_buttons_off_disables_old_keyboards_and_stateless_buttons_except_stop_with_markup_cleanup()
    {
        var e = await NewAsync(); // ChatButtons:Enabled = true глобально
        var appt = await AddApptAsync(e);
        var kb = await ActionsKbAsync(e, appt.Id);
        Assert.Equal(CallbackOutcome.Valid, (await ResolveAsync(e, kb.Data("aa"))).Outcome);

        await SetChannelSettingsAsync(e, "{\"assistant\":{\"enabled\":true,\"buttons\":false}}");
        var r = await ResolveAsync(e, kb.Data("ac"));
        Assert.Equal((CallbackOutcome.Disabled, CallbackClass.Stale, true), (r.Outcome, r.Class, r.ClearMarkup));
        Assert.Equal("buttons_disabled", r.Code);
        var my = await ResolveAsync(e, "i:my");
        Assert.Equal(CallbackOutcome.Disabled, my.Outcome);
        Assert.Equal(CallbackOutcome.Disabled, (await ResolveAsync(e, "i:help")).Outcome);
        // безстанова кнопка зі старої клавіатури: клавіатуру знайдено за message_ref -> розмітку можна прибрати
        var viaRef = await ResolveAsync(e, "i:my", messageRef: "500");
        Assert.True(viaRef.ClearMarkup);
        Assert.Equal(kb.Id, viaRef.Keyboard!.Id);
        // «Стоп» діє
        Assert.Equal(CallbackOutcome.Stateless, (await ResolveAsync(e, "i:stop")).Outcome);

        // порожні/зіпсовані налаштування й buttons=true не вимикають кнопки
        await SetChannelSettingsAsync(e, "{\"assistant\":{\"enabled\":true,\"buttons\":true}}");
        Assert.Equal(CallbackOutcome.Valid, (await ResolveAsync(e, kb.Data("aa"))).Outcome);
    }

    [SkippableFact]
    public async Task global_buttons_off_keeps_stop_working_and_everything_else_disabled()
    {
        var e = await NewAsync(enabled: false);
        Assert.Equal(CallbackOutcome.Stateless, (await ResolveAsync(e, "i:stop")).Outcome);
        Assert.Equal(CallbackOutcome.Disabled, (await ResolveAsync(e, "i:human")).Outcome);
        Assert.Equal(CallbackOutcome.Disabled, (await ResolveAsync(e, "i:book")).Outcome);
    }
}
