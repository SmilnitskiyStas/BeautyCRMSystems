using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyClients;

public sealed record ClientDto(
    Guid Id, string FullName, string? Phone, string? Email, DateOnly? BirthDate, bool MarketingConsent, bool Unsubscribed,
    int Visits, DateTimeOffset? LastVisitAt,
    int CancelledCount = 0, int CancelledByClientCount = 0); // TASK-697: зведення скасувань (усі / ініційовані клієнтом)
public sealed record ClientNoteDto(Guid Id, Guid? AuthorUserId, string Body, DateTimeOffset CreatedAt);
public sealed record ClientDetailDto(ClientDto Client, IReadOnlyList<ClientNoteDto> Notes, IReadOnlyList<AppointmentDto> History);
public sealed record CreateClientRequest(
    string FullName, string? Phone, string? Email, DateOnly? BirthDate, bool MarketingConsent = false);
public sealed record AddNoteRequest(string Body);

public interface IClientStore
{
    Task<IReadOnlyList<ClientDto>> ListAsync(string? search, int skip, int take, CancellationToken ct);
    Task<ClientDetailDto?> GetDetailAsync(Guid id, CancellationToken ct);
    Task<bool> PhoneExistsAsync(string phone, CancellationToken ct);
    Task<ClientDto> CreateAsync(CreateClientRequest req, CancellationToken ct);
    Task<ClientNoteDto?> AddNoteAsync(Guid clientId, Guid? authorUserId, string body, CancellationToken ct);
}

public sealed class ClientService(IClientStore store)
{
    public Task<IReadOnlyList<ClientDto>> ListAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        return store.ListAsync(search?.Trim(), (Math.Max(page, 1) - 1) * size, size, ct);
    }

    public async Task<Result<ClientDetailDto>> GetAsync(Guid id, CancellationToken ct) =>
        await store.GetDetailAsync(id, ct) is { } d ? d : Error.NotFound("client_not_found", "Client not found.");

    public async Task<Result<ClientDto>> CreateAsync(CreateClientRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.FullName) || req.FullName.Length > 200)
            return Error.Validation("invalid_name", "Full name is required (max 200).");
        if (req.Phone is { Length: > 32 }) return Error.Validation("invalid_phone", "Phone is too long.");
        if (!string.IsNullOrWhiteSpace(req.Phone) && await store.PhoneExistsAsync(req.Phone.Trim(), ct))
            return Error.Conflict("phone_exists", "A client with this phone already exists.");
        return await store.CreateAsync(req with { FullName = req.FullName.Trim(), Phone = req.Phone?.Trim() }, ct);
    }

    public async Task<Result<ClientNoteDto>> AddNoteAsync(Guid clientId, Guid? authorUserId, AddNoteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Body) || req.Body.Length > 4000)
            return Error.Validation("invalid_note", "Note body is required (max 4000).");
        return await store.AddNoteAsync(clientId, authorUserId, req.Body.Trim(), ct) is { } n
            ? n : Error.NotFound("client_not_found", "Client not found.");
    }
}
