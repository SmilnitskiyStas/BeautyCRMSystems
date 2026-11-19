using System.Text.Json.Serialization;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyClients;

public sealed record ClientDto(
    Guid Id, string FullName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phone,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email, DateOnly? BirthDate, bool MarketingConsent, bool Unsubscribed,
    int Visits, DateTimeOffset? LastVisitAt,
    int CancelledCount = 0, int CancelledByClientCount = 0, // TASK-697: зведення скасувань (усі / ініційовані клієнтом)
    // TASK-769 (§25.7): лише в картці GET /clients/{id} (у списку не заповнюються); повний номер бачать owner/admin (політика Management)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasPhone = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PhoneSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CallConsentAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClientPhonePendingDto? PhonePending = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClientDuplicateDto? PossibleDuplicate = null,
    // TASK-777 (§26.11.2): рівень маркетингової згоди (telegram) лише в картці; MarketingConsent вище - похідний прапорець
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClientMarketingDto? Marketing = null,
    // §30.6: без clients.contacts = full phone/email відсутні, натомість маски
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PhoneMasked = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EmailMasked = null)
{
    /// <summary>Маскування контактів за <c>clients.contacts</c> актора (сервер, у ВСІХ клієнтських DTO).</summary>
    public ClientDto ForViewer(Actor actor) => ContactMasking.CanSeeContacts(actor)
        ? this
        : this with
        {
            Phone = null, Email = null,
            PhoneMasked = ContactMasking.MaskPhone(Phone), EmailMasked = ContactMasking.MaskEmail(Email),
            PhonePending = PhonePending is null ? null : PhonePending with { Phone = null, PhoneMasked = ContactMasking.MaskPhone(PhonePending.Phone) },
        };
}
/// <summary>Level: full | selective | none (рядка згоди немає = none, UpdatedAt відсутній).</summary>
public sealed record ClientMarketingDto(string Level, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? UpdatedAt);
/// <summary>Інший номер, наданий у чаті й не підтверджений адміністратором (§25.7). Source: telegram_contact | manual_chat.</summary>
public sealed record ClientPhonePendingDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phone, string Source, DateTimeOffset At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PhoneMasked = null);
public sealed record ClientDuplicateDto(Guid Id, string Name);
public sealed record ClientNoteDto(Guid Id, Guid? AuthorUserId, string Body, DateTimeOffset CreatedAt);
public sealed record ClientDetailDto(ClientDto Client, IReadOnlyList<ClientNoteDto> Notes, IReadOnlyList<AppointmentDto> History)
{
    public ClientDetailDto ForViewer(Actor actor) => this with { Client = Client.ForViewer(actor), History = History.Select(h => h.ForList(actor)).ToList() };
}

/// <summary>Обмеження видимості для clients.view = own: лише записи цього specialist_id (історія/лічильники/пошук); null = усі.</summary>
public sealed record ClientScopeFilter(Guid SpecialistId, Guid ActorUserId);
public sealed record CreateClientRequest(
    string FullName, string? Phone, string? Email, DateOnly? BirthDate, bool MarketingConsent = false);
public sealed record AddNoteRequest(string Body);

public interface IClientStore
{
    Task<IReadOnlyList<ClientDto>> ListAsync(string? search, int skip, int take, ClientScopeFilter? own, bool searchContacts, CancellationToken ct);
    Task<ClientDetailDto?> GetDetailAsync(Guid id, ClientScopeFilter? own, CancellationToken ct);
    Task<bool> PhoneExistsAsync(string phone, CancellationToken ct);
    Task<ClientDto> CreateAsync(CreateClientRequest req, CancellationToken ct);
    Task<ClientNoteDto?> AddNoteAsync(Guid clientId, Guid? authorUserId, string body, CancellationToken ct);
}

public sealed class ClientService(IClientStore store, IClientScope scope)
{
    /// <summary>null - усі клієнти; інакше обмеження own. none/неактивний профіль - порожньо (<c>empty = true</c>).</summary>
    private static (bool Empty, ClientScopeFilter? Own) Filter(Actor actor)
    {
        if (actor.Access.AtLeast(PermissionKeys.ClientsView, PermissionLevels.All)) return (false, null);
        if (!actor.Access.AtLeast(PermissionKeys.ClientsView, PermissionLevels.Own)) return (true, null);
        return actor.SpecialistId is { } sp && !actor.Access.SpecialistInactive ? (false, new ClientScopeFilter(sp, actor.UserId)) : (true, null);
    }

    public async Task<IReadOnlyList<ClientDto>> ListAsync(Actor actor, string? search, int page, int pageSize, CancellationToken ct)
    {
        var (empty, own) = Filter(actor);
        if (empty) return [];
        var size = Math.Clamp(pageSize, 1, 200);
        var rows = await store.ListAsync(search?.Trim(), (Math.Max(page, 1) - 1) * size, size, own, ContactMasking.CanSeeContacts(actor), ct);
        return rows.Select(r => r.ForViewer(actor)).ToList();
    }

    /// <summary>Чужий (поза scope) клієнт - 404 client_not_found, не 403 (M3: існування не розкривається).</summary>
    public async Task<Result<ClientDetailDto>> GetAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var (empty, own) = Filter(actor);
        if (empty || (own is not null && !await scope.InScopeAsync(actor, id, ct))) return Error.NotFound("client_not_found", "Client not found.");
        return await store.GetDetailAsync(id, own, ct) is { } d ? d.ForViewer(actor) : Error.NotFound("client_not_found", "Client not found.");
    }

    public async Task<Result<ClientDto>> CreateAsync(Actor actor, CreateClientRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.FullName) || req.FullName.Length > 200)
            return Error.Validation("invalid_name", "Full name is required (max 200).");
        if (req.Phone is { Length: > 32 }) return Error.Validation("invalid_phone", "Phone is too long.");
        if (!string.IsNullOrWhiteSpace(req.Phone) && await store.PhoneExistsAsync(req.Phone.Trim(), ct))
            return Error.Conflict("phone_exists", "A client with this phone already exists.");
        return (await store.CreateAsync(req with { FullName = req.FullName.Trim(), Phone = req.Phone?.Trim() }, ct)).ForViewer(actor);
    }

    public async Task<Result<ClientNoteDto>> AddNoteAsync(Actor actor, Guid clientId, AddNoteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Body) || req.Body.Length > 4000)
            return Error.Validation("invalid_note", "Note body is required (max 4000).");
        if (!actor.Access.AtLeast(PermissionKeys.ClientsEdit, PermissionLevels.All) && !await scope.InScopeAsync(actor, clientId, ct))
            return Error.NotFound("client_not_found", "Client not found.");
        return await store.AddNoteAsync(clientId, actor.UserId, req.Body.Trim(), ct) is { } n
            ? n : Error.NotFound("client_not_found", "Client not found.");
    }
}
