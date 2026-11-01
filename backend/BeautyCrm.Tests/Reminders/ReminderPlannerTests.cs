using BeautyCrm.Application.Features.BeautyReminders;

namespace BeautyCrm.Tests.Reminders;

/// <summary>TASK-737 (§22.4, §22.12 сценарії 1, 3, 4, 5): чисте ядро планування нагадувань.</summary>
public class ReminderPlannerTests
{
    private const string Kyiv = "Europe/Kyiv";
    private static readonly TimeZoneInfo KyivTz = TimeZoneInfo.FindSystemTimeZoneById(Kyiv);

    // Зима: Київ = UTC+2. Понеділок 2030-01-07 - візити; "зараз" значно раніше, якщо не вказано інше.
    private static readonly DateTimeOffset Far = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset KyivTime(int y, int mo, int d, int h, int mi = 0) =>
        ReminderPlanner.LocalToUtc(new DateOnly(y, mo, d), new TimeOnly(h, mi), KyivTz);

    private static TimeOnly LocalOf(DateTimeOffset utc) => ReminderPlanner.LocalTime(utc, KyivTz);

    private static PlannedReminders Plan(
        DateTimeOffset startsAt, string method, short[] offsets, DateTimeOffset? now = null, ReminderSettings? settings = null,
        bool phone = true, string tz = Kyiv) =>
        ReminderPlanner.Plan(new ReminderPlanInput(startsAt, tz, method, offsets, settings ?? ReminderSettings.Default, now ?? Far, phone));

    // ---------- сценарій 1: кілька точок ----------

    [Fact]
    public void telegram_with_two_offsets_gives_two_message_points_with_exact_due_times()
    {
        var start = KyivTime(2030, 1, 9, 15); // середа 15:00
        var plan = Plan(start, "telegram", [120, 1440]);

        Assert.Equal(2, plan.Points.Count);
        Assert.All(plan.Points, p => { Assert.Equal(ReminderPointKind.Message, p.Kind); Assert.False(p.IsSkipped); });
        Assert.Equal(start.AddHours(-24), plan.Points.Single(p => p.OffsetMinutes == 1440).DueAt);
        Assert.Equal(start.AddHours(-2), plan.Points.Single(p => p.OffsetMinutes == 120).DueAt);
        Assert.Equal("custom", plan.Mirror);
        Assert.Equal([(short)120, (short)1440], plan.Offsets);
    }

    [Theory]
    [InlineData(new short[] { 60 }, "1h")]
    [InlineData(new short[] { 120 }, "2h")]
    [InlineData(new short[] { }, "none")]
    [InlineData(new short[] { 1440 }, "custom")]
    [InlineData(new short[] { 60, 120 }, "custom")]
    public void mirror_maps_offsets_to_legacy_reminder_option(short[] offsets, string expected) =>
        Assert.Equal(expected, ReminderOffsets.Mirror(offsets));

    [Fact]
    public void none_method_or_empty_offsets_gives_no_points()
    {
        var start = KyivTime(2030, 1, 9, 15);
        Assert.Empty(Plan(start, "none", []).Points);
        Assert.Empty(Plan(start, "telegram", []).Points);
    }

    [Fact]
    public void past_points_are_not_created_and_the_remaining_ones_are_kept()
    {
        var start = KyivTime(2030, 1, 9, 15);
        var now = start.AddMinutes(-90); // 120 і 1440 уже в минулому
        var plan = Plan(start, "telegram", [60, 120, 1440], now);
        var only = Assert.Single(plan.Points);
        Assert.Equal(60, only.OffsetMinutes);
        Assert.Equal(start.AddHours(-1), only.DueAt);
    }

    [Fact]
    public void point_exactly_at_now_is_not_created()
    {
        var start = KyivTime(2030, 1, 9, 15);
        Assert.Empty(Plan(start, "auto", [60], now: start.AddHours(-1)).Points);
        Assert.Single(Plan(start, "auto", [60], now: start.AddHours(-1).AddSeconds(-1)).Points);
    }

    // ---------- сценарій 3: тиша ----------

    [Fact]
    public void quiet_hours_shift_the_point_to_08_00_local_when_enough_time_is_left()
    {
        var start = KyivTime(2030, 1, 7, 9, 30); // 09:30; 2 год раніше = 07:30 - тиша
        var p = Assert.Single(Plan(start, "telegram", [120]).Points);
        Assert.False(p.IsSkipped);
        Assert.Equal(KyivTime(2030, 1, 7, 8), p.DueAt);
        Assert.Equal(new TimeOnly(8, 0), LocalOf(p.DueAt));
    }

    [Fact]
    public void quiet_hours_skip_the_point_when_less_than_60_minutes_would_remain_after_the_shift()
    {
        var start = KyivTime(2030, 1, 7, 8, 45); // 06:45 -> 08:00, до візиту 45 хв
        var p = Assert.Single(Plan(start, "telegram", [120]).Points);
        Assert.Equal(ReminderSkipReasons.QuietHours, p.SkipReason);
    }

    [Fact]
    public void shifted_point_is_kept_when_exactly_60_minutes_remain()
    {
        // Межа правила §22.4 «startsAt - shifted >= 60 хв»: 09:00, 2 год -> 07:00 -> 08:00, лишається рівно 60 хв.
        var start = KyivTime(2030, 1, 7, 9);
        var p = Assert.Single(Plan(start, "telegram", [120]).Points);
        Assert.False(p.IsSkipped);
        Assert.Equal(KyivTime(2030, 1, 7, 8), p.DueAt);
    }

    [Fact]
    public void evening_visit_with_24h_offset_moves_the_reminder_to_08_00_of_the_visit_day()
    {
        var start = KyivTime(2030, 1, 8, 23); // 23:00; доба раніше = 23:00 попереднього дня - тиша
        var p = Assert.Single(Plan(start, "telegram", [1440]).Points);
        Assert.False(p.IsSkipped);
        Assert.Equal(KyivTime(2030, 1, 8, 8), p.DueAt);
    }

    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(0, 0, true)]
    [InlineData(7, 59, true)]
    [InlineData(8, 0, false)]
    [InlineData(12, 0, false)]
    public void quiet_window_boundaries_are_start_inclusive_end_exclusive(int h, int m, bool quiet) =>
        Assert.Equal(quiet, ReminderPlanner.IsQuiet(new TimeOnly(h, m), ReminderSettings.Default));

    [Fact]
    public void point_at_exactly_08_00_is_not_shifted_and_at_22_00_is_shifted()
    {
        var at08 = Assert.Single(Plan(KyivTime(2030, 1, 7, 10), "telegram", [120]).Points); // 08:00
        Assert.Equal(KyivTime(2030, 1, 7, 8), at08.DueAt);
        var at22 = Assert.Single(Plan(KyivTime(2030, 1, 8, 22), "telegram", [1440]).Points); // 22:00 попереднього дня -> тиша
        Assert.Equal(KyivTime(2030, 1, 8, 8), at22.DueAt);
    }

    [Fact]
    public void same_day_quiet_window_that_does_not_cross_midnight_is_supported()
    {
        var s = new ReminderSettings(true, true, new TimeOnly(13, 0), new TimeOnly(15, 0)); // обідня тиша
        var inside = Assert.Single(Plan(KyivTime(2030, 1, 7, 17), "telegram", [120], settings: s).Points); // 17:00 - 2 год = 15:00, не в тиші
        Assert.Equal(KyivTime(2030, 1, 7, 15), inside.DueAt);
        var shifted = Assert.Single(Plan(KyivTime(2030, 1, 7, 16, 30), "telegram", [120], settings: s).Points); // 14:30 -> 15:00
        Assert.Equal(KyivTime(2030, 1, 7, 15), shifted.DueAt);
    }

    [Fact]
    public void custom_quiet_hours_from_tenant_settings_are_used()
    {
        var s = new ReminderSettings(true, true, new TimeOnly(23, 0), new TimeOnly(9, 0));
        var p = Assert.Single(Plan(KyivTime(2030, 1, 7, 11), "telegram", [120], settings: s).Points); // 09:00 - уже не тиша
        Assert.Equal(KyivTime(2030, 1, 7, 9), p.DueAt);
        var q = Assert.Single(Plan(KyivTime(2030, 1, 7, 10, 30), "telegram", [120], settings: s).Points); // 08:30 - тиша -> 09:00
        Assert.Equal(KyivTime(2030, 1, 7, 9), q.DueAt);
    }

    // ---------- сценарій 4: злиття ----------

    [Fact]
    public void points_closer_than_60_minutes_are_merged_and_the_one_closer_to_the_visit_stays()
    {
        var start = KyivTime(2030, 1, 7, 9, 30); // 60 -> 08:30; 120 -> 07:30 -> зсув на 08:00
        var plan = Plan(start, "telegram", [60, 120]);
        var kept = Assert.Single(plan.Points, p => !p.IsSkipped);
        Assert.Equal(60, kept.OffsetMinutes);
        Assert.Equal(KyivTime(2030, 1, 7, 8, 30), kept.DueAt);
        var merged = Assert.Single(plan.Points, p => p.IsSkipped);
        Assert.Equal(120, merged.OffsetMinutes);
        Assert.Equal(ReminderSkipReasons.Merged, merged.SkipReason);
    }

    [Fact]
    public void points_exactly_60_minutes_apart_are_not_merged()
    {
        var plan = Plan(KyivTime(2030, 1, 9, 15), "telegram", [60, 120]);
        Assert.Equal(2, plan.Points.Count);
        Assert.All(plan.Points, p => Assert.False(p.IsSkipped));
    }

    [Fact]
    public void three_points_with_a_merge_keep_the_24h_and_the_closest_one()
    {
        var plan = Plan(KyivTime(2030, 1, 8, 9, 30), "telegram", [60, 120, 1440]);
        Assert.Equal([1440, 60], plan.Points.Where(p => !p.IsSkipped).Select(p => p.OffsetMinutes).OrderByDescending(x => x));
        Assert.Equal(ReminderSkipReasons.Merged, plan.Points.Single(p => p.OffsetMinutes == 120).SkipReason);
    }

    [Fact]
    public void skipped_quiet_point_does_not_take_part_in_merging()
    {
        // 120 пропущена тишею (quiet_hours), 60 лишається сама
        var plan = Plan(KyivTime(2030, 1, 7, 8, 45), "telegram", [60, 120]);
        Assert.Equal(ReminderSkipReasons.QuietHours, plan.Points.Single(p => p.OffsetMinutes == 120).SkipReason);
        Assert.Equal(ReminderSkipReasons.QuietHours, plan.Points.Single(p => p.OffsetMinutes == 60).SkipReason); // 07:45 -> 08:00, лишилось 45 хв
    }

    // ---------- сценарій 5: DST ----------

    [Fact]
    public void offset_is_an_absolute_duration_across_the_spring_dst_transition()
    {
        // 2030-03-31 03:00 -> 04:00 (Київ). Візит у неділю 12:00 (UTC+3); доба раніше = субота 11:00 (UTC+2), а не 12:00.
        var start = KyivTime(2030, 3, 31, 12);
        Assert.Equal(TimeSpan.FromHours(3), KyivTz.GetUtcOffset(start));
        var p = Assert.Single(Plan(start, "telegram", [1440]).Points);
        Assert.Equal(start.AddHours(-24), p.DueAt);
        Assert.Equal(new TimeOnly(11, 0), LocalOf(p.DueAt));
    }

    [Fact]
    public void offset_is_an_absolute_duration_across_the_autumn_dst_transition()
    {
        // 2030-10-27 04:00 -> 03:00. Візит у неділю 12:00 (UTC+2); доба раніше = субота 13:00 (UTC+3).
        var start = KyivTime(2030, 10, 27, 12);
        Assert.Equal(TimeSpan.FromHours(2), KyivTz.GetUtcOffset(start));
        var p = Assert.Single(Plan(start, "telegram", [1440]).Points);
        Assert.Equal(start.AddHours(-24), p.DueAt);
        Assert.Equal(new TimeOnly(13, 0), LocalOf(p.DueAt));
    }

    [Fact]
    public void quiet_shift_on_the_spring_transition_day_uses_the_correct_offset()
    {
        var start = KyivTime(2030, 3, 31, 9, 30); // 09:30 (UTC+3); 2 год = 07:30 -> 08:00 за новим зсувом
        var p = Assert.Single(Plan(start, "telegram", [120]).Points);
        Assert.Equal(new DateTimeOffset(2030, 3, 31, 5, 0, 0, TimeSpan.Zero), p.DueAt); // 08:00 EEST = 05:00Z
    }

    [Fact]
    public void nonexistent_local_quiet_end_on_the_dst_gap_becomes_the_first_valid_instant_after_it()
    {
        // Тиша 01:00-03:30; 2030-03-31 03:30 у Києві не існує (03:00 -> 04:00).
        var s = new ReminderSettings(true, true, new TimeOnly(1, 0), new TimeOnly(3, 30));
        var start = new DateTimeOffset(2030, 3, 31, 2, 0, 0, TimeSpan.Zero); // 05:00 EEST
        var p = Assert.Single(Plan(start, "telegram", [120], settings: s).Points); // 00:00Z = 02:00 EET - у тиші
        Assert.False(p.IsSkipped);
        Assert.Equal(new DateTimeOffset(2030, 3, 31, 1, 0, 0, TimeSpan.Zero), p.DueAt); // 04:00 EEST
        Assert.Equal(new TimeOnly(4, 0), LocalOf(p.DueAt));
    }

    [Fact]
    public void ambiguous_local_quiet_end_on_the_autumn_overlap_takes_the_first_occurrence()
    {
        var utc = ReminderPlanner.LocalToUtc(new DateOnly(2030, 10, 27), new TimeOnly(3, 30), KyivTz);
        Assert.Equal(new DateTimeOffset(2030, 10, 27, 0, 30, 0, TimeSpan.Zero), utc); // 03:30 EEST (UTC+3), перше входження
    }

    [Fact]
    public void other_timezone_gives_a_different_instant_for_the_same_local_rule()
    {
        var start = new DateTimeOffset(2030, 1, 9, 20, 0, 0, TimeSpan.Zero);
        var kyiv = Assert.Single(Plan(start, "telegram", [60], tz: Kyiv).Points); // 21:00 за Києвом - не тиша
        var ny = Assert.Single(Plan(start, "telegram", [60], tz: "America/New_York").Points); // 14:00 - не тиша
        Assert.Equal(kyiv.DueAt, ny.DueAt); // абсолютна тривалість
        var nightNy = Plan(new DateTimeOffset(2030, 1, 9, 5, 0, 0, TimeSpan.Zero), "telegram", [60], tz: "America/New_York"); // 00:00 NY, мінус 1 год = 23:00 - тиша; 08:00 NY вже після візиту
        Assert.Equal(ReminderSkipReasons.QuietHours, Assert.Single(nightNy.Points).SkipReason);
    }

    // ---------- налаштування tenant ----------

    [Fact]
    public void disabled_telegram_marks_every_future_message_point_as_skipped()
    {
        var s = ReminderSettings.Default with { TelegramEnabled = false };
        var plan = Plan(KyivTime(2030, 1, 9, 15), "telegram", [60, 1440], settings: s);
        Assert.Equal(2, plan.Points.Count);
        Assert.All(plan.Points, p => Assert.Equal(ReminderSkipReasons.TelegramDisabled, p.SkipReason));
    }

    // ---------- дзвінок ----------

    [Fact]
    public void call_with_future_due_creates_a_single_task_at_the_nominal_time_ignoring_quiet_hours()
    {
        var start = KyivTime(2030, 1, 9, 10);
        var p = Assert.Single(Plan(start, "call", [1440]).Points);
        Assert.Equal(ReminderPointKind.Call, p.Kind);
        Assert.False(p.IsSkipped);
        Assert.Equal(start.AddHours(-24), p.DueAt);

        var early = KyivTime(2030, 1, 9, 7); // 60 -> 06:00 (тиша не діє на задачі для людини)
        Assert.Equal(early.AddHours(-1), Assert.Single(Plan(early, "call", [60]).Points).DueAt);
    }

    [Fact]
    public void call_with_past_due_and_enough_time_is_due_now()
    {
        var start = KyivTime(2030, 1, 9, 15);
        var now = start.AddHours(-5); // доба раніше вже минула, до візиту 5 год
        var p = Assert.Single(Plan(start, "call", [1440], now).Points);
        Assert.Equal(now, p.DueAt);
        Assert.False(p.IsSkipped);
    }

    [Fact]
    public void call_with_past_due_and_less_than_60_minutes_to_the_visit_creates_nothing()
    {
        var start = KyivTime(2030, 1, 9, 15);
        Assert.Empty(Plan(start, "call", [1440], start.AddMinutes(-59)).Points);
        Assert.Single(Plan(start, "call", [1440], start.AddMinutes(-60)).Points); // рівно 60 - ще створюється
    }

    [Fact]
    public void call_without_phone_or_with_calls_disabled_is_skipped_with_a_code()
    {
        var start = KyivTime(2030, 1, 9, 15);
        Assert.Equal(ReminderSkipReasons.NoPhone, Assert.Single(Plan(start, "call", [120], phone: false).Points).SkipReason);
        var off = ReminderSettings.Default with { CallEnabled = false };
        Assert.Equal(ReminderSkipReasons.CallDisabled, Assert.Single(Plan(start, "call", [120], settings: off).Points).SkipReason);
    }

    [Fact]
    public void call_never_produces_message_points()
    {
        var plan = Plan(KyivTime(2030, 1, 9, 15), "call", [1440]);
        Assert.DoesNotContain(plan.Points, p => p.Kind == ReminderPointKind.Message);
    }

    // ---------- нормалізація / валідація ----------

    [Fact]
    public void offsets_are_normalized_to_unique_ascending_values_and_reject_unknown_ones()
    {
        Assert.Equal([(short)60, (short)1440], ReminderOffsets.TryNormalize([1440, 60, 60]));
        Assert.Empty(ReminderOffsets.TryNormalize(null)!);
        Assert.Null(ReminderOffsets.TryNormalize([30]));
        Assert.Null(ReminderOffsets.TryNormalize([60, 61]));
        Assert.Null(ReminderOffsets.TryNormalize(Enumerable.Repeat(60, ReminderOffsets.MaxRawCount + 1)));
        Assert.Null(ReminderOffsets.TryNormalize([60 + 65536])); // не обрізається до short
    }

    [Theory]
    [InlineData("none", new int[] { }, true)]
    [InlineData("none", new[] { 60 }, false)]
    [InlineData("telegram", new[] { 120, 1440 }, true)]
    [InlineData("telegram", new int[] { }, false)]
    [InlineData("call", new[] { 1440 }, true)]
    [InlineData("call", new[] { 60, 120 }, false)]
    [InlineData("call", new int[] { }, false)]
    [InlineData("telegram", new[] { 999 }, false)]
    public void request_offsets_validation_follows_the_method_rules(string method, int[] offsets, bool ok)
    {
        var r = ReminderPlanning.Validate(new ReminderPlanRequest(method, offsets));
        Assert.Equal(ok, r.IsOk);
        if (!ok) Assert.Equal("invalid_reminder_offsets", r.Error!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("sms")]
    [InlineData("")]
    public void request_method_must_be_none_telegram_or_call(string? method) =>
        Assert.Equal("invalid_reminder_method", ReminderPlanning.Validate(new ReminderPlanRequest(method, [60])).Error!.Code);
}
