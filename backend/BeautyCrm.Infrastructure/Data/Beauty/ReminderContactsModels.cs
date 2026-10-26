using System.Text.Json.Serialization;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// TASK-738 (§22.6.2-§22.6.4): DTO вподобань клієнта, задач «зателефонувати» і налаштувань нагадувань.
// Телефон віддається лише в CallTaskClientDto (береться з клієнта в момент запиту, у задачі не зберігається).

public sealed record UserRefDto(Guid Id, string Name);

public sealed record ContactPreferencesDto(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Method,
    IReadOnlyList<int> OffsetsMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UserRefDto? UpdatedBy,
    bool TelegramAvailable, bool PhoneAvailable, bool TelegramEnabled, bool CallEnabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ReplannedAppointments = null);

/// <summary>PUT: <c>applyToUpcoming</c> за замовчуванням true; відсутній <c>offsetsMinutes</c> = порожній список.</summary>
public sealed record PutContactPreferencesRequest(string? Method, int[]? OffsetsMinutes, bool? ApplyToUpcoming);

public sealed record CallTaskClientDto(Guid Id, string Name, string? Phone);
public sealed record CallTaskAppointmentDto(Guid Id, DateTimeOffset StartsAt, string LocalStartsAt, string ServiceName, string SpecialistName, string Status);
public sealed record CallTaskLocationDto(Guid Id, string Name, string Timezone);

public sealed record CallTaskDto(
    Guid Id, DateTimeOffset DueAt, string LocalDueTime, string Status, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OffsetMinutes,
    CallTaskClientDto Client, CallTaskAppointmentDto Appointment, CallTaskLocationDto Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CompletedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UserRefDto? CompletedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note);

public sealed record CallTaskCountsDto(int Open, int Done, int Skipped);
public sealed record CallTaskListDto(IReadOnlyList<CallTaskDto> Items, CallTaskCountsDto Counts);

/// <summary>PATCH: <c>status</c> done|skipped|open; <c>note</c> ≤ 300 (не логується).</summary>
public sealed record PatchCallTaskRequest(string? Status, string? Note);

/// <summary>PUT/GET налаштувань: усі поля обов'язкові (відсутнє поле = 422 settings_incomplete).</summary>
public sealed record ReminderSettingsDto(bool? TelegramEnabled, bool? CallEnabled, string? QuietHoursStart, string? QuietHoursEnd);

public static class ReminderContactsErrors
{
    public const string ForbiddenRole = "forbidden_role";
    public const string ClientNotFound = "client_not_found";
    public const string CallTaskNotFound = "call_task_not_found";
    public const string TaskAlreadyClosed = "task_already_closed";
    public const string TaskCancelled = "task_cancelled";
    /// <summary>Повторне відкриття неможливе: для запису вже є інша відкрита задача-нагадування (частковий UNIQUE).</summary>
    public const string TaskSuperseded = "task_superseded";
}
