using System.Text.Json.Serialization;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// TASK-777 (§26.4, §26.11.2, ADR-015): DTO і константи маркетингової згоди. ПД немає: лише id, рівні й коди.

public static class MarketingConsentLevels
{
    public const string Full = "full", Selective = "selective", None = "none";
    public static bool IsValid(string? v) => v is Full or Selective or None;
    /// <summary>Порядок "ширини" згоди: підвищення = зростання рангу.</summary>
    public static int Rank(string? v) => v switch { Full => 2, Selective => 1, _ => 0 };
}

public static class MarketingConsentChannels
{
    public const string Telegram = "telegram", Push = "push";
}

public static class MarketingConsentSources
{
    public const string Chat = "chat", Admin = "admin", Stop = "stop";
}

public static class MarketingConsentVias
{
    public const string Button = "button", Command = "command", Text = "text", Api = "api";
}

public static class MarketingConsentErrors
{
    public const string ForbiddenRole = "forbidden_role";
    public const string ClientNotFound = "client_not_found";
    public const string InvalidLevel = "invalid_level";
    public const string ChannelUnsupported = "channel_unsupported";
    public const string BasisRequired = "basis_required";
    public const string InvalidBasis = "invalid_basis";
    public const string InvalidBasisNote = "invalid_basis_note";
    public const string AdminGrantDisabled = "admin_grant_disabled";
    public const string MarketingDisabled = "marketing_disabled";
    public const string TextVersionRequired = "text_version_required";
}

/// <summary>Параметри однієї зміни згоди (єдиний шлях запису: <see cref="MarketingConsentService.SetConsentAsync"/>).</summary>
/// <param name="Basis">written | in_person | other; лише для source=admin.</param>
public sealed record SetConsentCommand(
    Guid ClientId, string Channel, string Level, string Source, string? Via = null, string? TextVersion = null,
    Guid? ActorUserId = null, string? Basis = null, string? BasisNote = null, Guid? CampaignId = null, Guid? ConversationId = null);

/// <param name="Changed">false = рівень не змінився, нічого не записано (ідемпотентно).</param>
public sealed record ConsentChange(bool Changed, string? LevelFrom, string LevelTo);

public sealed record MarketingConsentChannelDto(
    string Channel, string Level, string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TextVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UserRefDto? UpdatedBy);

public sealed record MarketingConsentEventDto(
    DateTimeOffset At, string Channel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LevelFrom,
    string LevelTo, string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Via,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UserRefDto? Actor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Basis,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BasisNote,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? CampaignId);

public sealed record MarketingConsentDto(
    IReadOnlyList<MarketingConsentChannelDto> Channels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? OfferedAt,
    bool TelegramAvailable,
    IReadOnlyList<MarketingConsentEventDto> History);

/// <summary>PUT /clients/{id}/marketing-consent. <c>basisNote</c> не логується.</summary>
public sealed record PutMarketingConsentRequest(string? Channel, string? Level, string? Basis, string? BasisNote);
