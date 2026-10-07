using System.Text.Json;
using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

public sealed partial class EfBeautyStore
{
    public async Task<IReadOnlyList<ChannelRecord>> ListAsync(CancellationToken ct) =>
        (await db.Channels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)).Select(ToRecord).ToList();

    async Task<ChannelRecord?> IChannelSettingsStore.GetAsync(Guid id, CancellationToken ct) =>
        await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is { } c ? ToRecord(c) : null;

    public async Task<ChannelRecord> UpsertAsync(Guid id, UpsertChannelRequest r, CancellationToken ct)
    {
        var ch = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (ch is null)
            db.Channels.Add(ch = new Channel { Id = id, Type = EnumText<ChannelType>.FromDb(r.Type!) });
        ch.Name = r.Name.Trim(); ch.LocationId = r.LocationId; ch.IsActive = r.IsActive;
        if (r.SettingsJson is not null) ch.Settings = r.SettingsJson;

        if (r.Token is not null || r.WebhookSecret is not null)
        {
            var current = ChannelSecrets.Read(secrets, ch.CredentialsEncrypted);
            var token = r.Token ?? current.Token;
            var secret = r.WebhookSecret ?? current.WebhookSecret;
            ch.CredentialsEncrypted = ChannelSecrets.Write(secrets, token, secret);
            ch.CredentialsLast4 = token is { Length: >= 4 } ? token[^4..] : null;
        }
        await db.SaveChangesAsync(ct);
        return ToRecord(ch);
    }

    private ChannelRecord ToRecord(Channel c) => new(
        c.Id, EnumText<ChannelType>.ToDb(c.Type), c.Name, c.LocationId, c.IsActive, c.CredentialsLast4,
        !string.IsNullOrEmpty(ChannelSecrets.Read(secrets, c.CredentialsEncrypted).WebhookSecret), c.Settings);
}

/// <summary>Формат CredentialsEncrypted: Protect(JSON {"token","webhookSecret"}).</summary>
public static class ChannelSecrets
{
    private sealed record Payload(string? Token, string? WebhookSecret);

    public static (string? Token, string? WebhookSecret) Read(ISecretProtector protector, string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return (null, null);
        var p = JsonSerializer.Deserialize<Payload>(protector.Unprotect(encrypted));
        return (p?.Token, p?.WebhookSecret);
    }

    public static string Write(ISecretProtector protector, string? token, string? webhookSecret) =>
        protector.Protect(JsonSerializer.Serialize(new Payload(token, webhookSecret)));
}
