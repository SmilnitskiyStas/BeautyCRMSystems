namespace BeautyCrm.Application.Features.BeautyReminders;

// TASK-737 (§22.4, ADR-012 §2.2): спільне ядро планування нагадувань. Чисте, без БД і без годинника:
// усі шляхи запису (staff, публічний, чат), перенос, переплан вподобань і «Стоп» викликають один і той самий Plan.

/// <summary>Способи нагадування на записі. <c>auto</c> - лише спадкове/публічне (канал визначає воркер під час надсилання).</summary>
public static class ReminderMethods
{
    public const string None = "none";
    public const string Auto = "auto";
    public const string Telegram = "telegram";
    public const string Call = "call";

    /// <summary>Те, що може явно задати клієнт API (staff) або вподобання клієнта.</summary>
    public static readonly string[] Requestable = [None, Telegram, Call];
}

/// <summary>Коди <c>skip_reason</c> / <c>closed_reason</c> (§22.4). Лише коди, без текстів і ПД.</summary>
public static class ReminderSkipReasons
{
    public const string QuietHours = "quiet_hours";
    public const string Merged = "merged";
    public const string NoPhone = "no_phone";
    public const string CallDisabled = "call_disabled";
    public const string TelegramDisabled = "telegram_disabled";
    public const string AppointmentInactive = "appointment_inactive";
    public const string Replanned = "replanned";
}

public static class ReminderOffsets
{
    /// <summary>Допустимі точки: за 1 год, 2 год, 24 год (CHECK у БД).</summary>
    public static readonly short[] Allowed = [60, 120, 1440];

    /// <summary>Верхня межа сирого списку з запиту (захист від роздутого тіла); далі масив нормалізується до унікальних.</summary>
    public const int MaxRawCount = 10;

    /// <summary>Унікальні значення за зростанням або null, якщо є значення поза <see cref="Allowed"/>.</summary>
    public static short[]? TryNormalize(IEnumerable<int>? raw)
    {
        if (raw is null) return [];
        var list = raw.Take(MaxRawCount + 1).ToList();
        if (list.Count > MaxRawCount) return null;
        var result = new SortedSet<short>();
        foreach (var v in list)
        {
            if (v is not (60 or 120 or 1440)) return null;
            result.Add((short)v);
        }
        return [.. result];
    }

    /// <summary>Спадкове дзеркало <c>reminder_option</c>: {60}->1h, {120}->2h, {}->none, решта->custom (§22.4).</summary>
    public static string Mirror(IReadOnlyCollection<short> offsets) => offsets.Count switch
    {
        0 => "none",
        1 when offsets.First() == 60 => "1h",
        1 when offsets.First() == 120 => "2h",
        _ => "custom",
    };

    /// <summary>Спадкове значення reminder -> (method, offsets): none -> (none, {}), 1h -> (auto, {60}), 2h -> (auto, {120}).</summary>
    public static (string Method, short[] Offsets) FromLegacy(string? option) => option switch
    {
        "1h" => (ReminderMethods.Auto, [60]),
        "2h" => (ReminderMethods.Auto, [120]),
        _ => (ReminderMethods.None, []),
    };
}

/// <summary>Налаштування tenant (відсутній рядок = за замовчуванням: тиша 22:00-08:00, обидва канали увімкнено).</summary>
public sealed record ReminderSettings(bool TelegramEnabled, bool CallEnabled, TimeOnly QuietStart, TimeOnly QuietEnd)
{
    public static readonly ReminderSettings Default = new(true, true, new TimeOnly(22, 0), new TimeOnly(8, 0));
}

public enum ReminderPointKind { Message, Call }

/// <param name="DueAt">Ефективний час: для надісланих точок - коли, для пропущених - номінальний (або зсунутий) час, лише для показу.</param>
/// <param name="SkipReason">null = точка активна (рядок scheduled / задача open); інакше код §22.4.</param>
public sealed record ReminderPoint(ReminderPointKind Kind, int OffsetMinutes, DateTimeOffset DueAt, string? SkipReason = null)
{
    public bool IsSkipped => SkipReason is not null;
}

public sealed record ReminderPlanInput(
    DateTimeOffset StartsAt, string Timezone, string Method, IReadOnlyList<short> Offsets,
    ReminderSettings Settings, DateTimeOffset Now, bool PhoneAvailable);

/// <summary>Результат планування: знімок плану запису + точки, які store пише в транзакції запису/переносу.</summary>
public sealed record PlannedReminders(string Method, IReadOnlyList<short> Offsets, DateTimeOffset StartsAt, IReadOnlyList<ReminderPoint> Points)
{
    public string Mirror => ReminderOffsets.Mirror(Offsets);

    /// <summary>Спадкова єдина точка (сумісність зі старими портами/fakes): найраніша активна точка-повідомлення.</summary>
    public DateTimeOffset? LegacyReminderAt =>
        Points.Where(p => p.Kind == ReminderPointKind.Message && !p.IsSkipped).Select(p => (DateTimeOffset?)p.DueAt).Min();

    public static PlannedReminders Empty(DateTimeOffset startsAt) => new(ReminderMethods.None, [], startsAt, []);
}

public static class ReminderPlanner
{
    /// <summary>Повідомлення ближче цього інтервалу зливаються; після зсуву з тиші до візиту має лишатися не менше (§22.4).</summary>
    public const int MinGapMinutes = 60;

    public static PlannedReminders Plan(ReminderPlanInput i)
    {
        var startsUtc = i.StartsAt.ToUniversalTime();
        var offsets = i.Offsets.Distinct().OrderByDescending(o => o).ToList(); // найраніша точка першою
        if (i.Method == ReminderMethods.None || offsets.Count == 0)
            return new PlannedReminders(i.Method, [], startsUtc, []);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(i.Timezone);
        var points = i.Method == ReminderMethods.Call
            ? PlanCall(i, startsUtc, offsets[0])
            : PlanMessages(i, startsUtc, offsets, tz);
        return new PlannedReminders(i.Method, [.. offsets.OrderBy(o => o)], startsUtc, points);
    }

    private static List<ReminderPoint> PlanCall(ReminderPlanInput i, DateTimeOffset startsUtc, short offset)
    {
        var nominal = startsUtc.AddMinutes(-offset);
        if (!i.Settings.CallEnabled) return [new(ReminderPointKind.Call, offset, nominal, ReminderSkipReasons.CallDisabled)];
        if (!i.PhoneAvailable) return [new(ReminderPointKind.Call, offset, nominal, ReminderSkipReasons.NoPhone)];
        if (nominal > i.Now) return [new(ReminderPointKind.Call, offset, nominal)];
        // Прострочено: подзвонити якнайшвидше, якщо до візиту ще є час; інакше задачі немає.
        return startsUtc - i.Now >= TimeSpan.FromMinutes(MinGapMinutes)
            ? [new(ReminderPointKind.Call, offset, i.Now)]
            : [];
    }

    private static List<ReminderPoint> PlanMessages(ReminderPlanInput i, DateTimeOffset startsUtc, List<short> offsets, TimeZoneInfo tz)
    {
        var result = new List<ReminderPoint>();
        var active = new List<ReminderPoint>();
        foreach (var offset in offsets)
        {
            var nominal = startsUtc.AddMinutes(-offset); // абсолютна тривалість, не «та сама година вчора» (DST)
            if (nominal <= i.Now) continue; // точка в минулому не створюється
            if (!i.Settings.TelegramEnabled)
            {
                result.Add(new(ReminderPointKind.Message, offset, nominal, ReminderSkipReasons.TelegramDisabled));
                continue;
            }
            var due = nominal;
            if (IsQuiet(LocalTime(nominal, tz), i.Settings))
            {
                var shifted = NextQuietEnd(nominal, tz, i.Settings);
                if (startsUtc - shifted < TimeSpan.FromMinutes(MinGapMinutes))
                {
                    result.Add(new(ReminderPointKind.Message, offset, nominal, ReminderSkipReasons.QuietHours));
                    continue;
                }
                due = shifted;
            }
            active.Add(new(ReminderPointKind.Message, offset, due));
        }

        // Злиття: точки ближче 60 хв одна до одної - лишається ближча до візиту, інша skipped:merged.
        var kept = new List<ReminderPoint>();
        foreach (var p in active.OrderBy(p => p.DueAt).ThenByDescending(p => p.OffsetMinutes))
        {
            if (kept.Count > 0 && p.DueAt - kept[^1].DueAt < TimeSpan.FromMinutes(MinGapMinutes))
            {
                result.Add(kept[^1] with { SkipReason = ReminderSkipReasons.Merged });
                kept[^1] = p;
            }
            else kept.Add(p);
        }
        result.AddRange(kept);
        return [.. result.OrderByDescending(p => p.OffsetMinutes)];
    }

    // ---------- тиша і локальний час ----------

    public static TimeOnly LocalTime(DateTimeOffset instant, TimeZoneInfo tz) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, tz).DateTime);

    /// <summary>Вікно [start, end); якщо start &gt; end - через північ.</summary>
    public static bool IsQuiet(TimeOnly t, ReminderSettings s) =>
        s.QuietStart > s.QuietEnd ? t >= s.QuietStart || t < s.QuietEnd : t >= s.QuietStart && t < s.QuietEnd;

    /// <summary>Найближче наступне quietHoursEnd (локальний час закладу) для моменту, що потрапив у тишу.</summary>
    public static DateTimeOffset NextQuietEnd(DateTimeOffset nominal, TimeZoneInfo tz, ReminderSettings s)
    {
        var local = TimeZoneInfo.ConvertTime(nominal, tz).DateTime;
        var date = DateOnly.FromDateTime(local);
        if (s.QuietStart > s.QuietEnd && TimeOnly.FromDateTime(local) >= s.QuietStart) date = date.AddDays(1);
        return LocalToUtc(date, s.QuietEnd, tz);
    }

    /// <summary>
    /// Локальна дата й час -> UTC. Неіснуючий час переходу DST замінюється першою валідною миттю після нього;
    /// неоднозначний (повторюваний) - першим входженням.
    /// </summary>
    public static DateTimeOffset LocalToUtc(DateOnly date, TimeOnly time, TimeZoneInfo tz)
    {
        var dt = date.ToDateTime(time, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(dt)) dt = dt.AddMinutes(1);
        var offset = tz.IsAmbiguousTime(dt) ? tz.GetAmbiguousTimeOffsets(dt).Max() : tz.GetUtcOffset(dt);
        return new DateTimeOffset(dt, offset).ToUniversalTime();
    }
}
