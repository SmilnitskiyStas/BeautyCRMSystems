using System.Text.Json;
using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

public sealed partial class EfBeautyStore
{
    public async Task<IReadOnlyList<ChannelRecord>> ListAsync(CancellationToken ct) =>
        (await db.Channels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)).Select(ToRecord).ToList();

    async Task<ChannelRecord?> IChannelSettingsStore.GetAsync(Guid id, CancellationToken ct) =>
        await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is { } c ? ToRecord(c) : null;

    /// <summary>Null = колізія PK (23505): id вже існує в іншому tenant (RLS ховає його від цього tenant).</summary>
    public async Task<ChannelRecord?> UpsertAsync(Guid id, UpsertChannelRequest r, CancellationToken ct)
    {
        var ch = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (ch is null)
            db.Channels.Add(ch = new Channel { Id = id, Type = EnumText<ChannelType>.FromDb(r.Type!) });
        ch.Name = r.Name.Trim(); ch.LocationId = r.LocationId; ch.IsActive = r.IsActive;
        if (r.SettingsJson is not null) ch.Settings = r.SettingsJson;

        if (r.Token is not null || r.WebhookSecret is not null || r.AppSecret is not null || r.VerifyToken is not null)
        {
            var current = ChannelSecrets.Read(secrets, ch.CredentialsEncrypted);
            var token = r.Token ?? current.Token;
            ch.CredentialsEncrypted = ChannelSecrets.Write(secrets, current with
            {
                Token = token,
                WebhookSecret = r.WebhookSecret ?? current.WebhookSecret,
                AppSecret = r.AppSecret ?? current.AppSecret,
                VerifyToken = r.VerifyToken ?? current.VerifyToken,
            });
            ch.CredentialsLast4 = token is { Length: >= 4 } ? token[^4..] : null;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear();
            return null;
        }
        return ToRecord(ch);
    }

    private ChannelRecord ToRecord(Channel c)
    {
        var s = ChannelSecrets.Read(secrets, c.CredentialsEncrypted);
        return new ChannelRecord(
            c.Id, EnumText<ChannelType>.ToDb(c.Type), c.Name, c.LocationId, c.IsActive, c.CredentialsLast4,
            !string.IsNullOrEmpty(s.WebhookSecret), c.Settings, !string.IsNullOrEmpty(s.AppSecret), !string.IsNullOrEmpty(s.VerifyToken));
    }
}

/// <summary>Секрети каналу (розшифровані). Instagram/Messenger: AppSecret — HMAC підпису, VerifyToken — GET-handshake.</summary>
public sealed record ChannelSecretSet(string? Token, string? WebhookSecret, string? AppSecret = null, string? VerifyToken = null);

/// <summary>Формат CredentialsEncrypted: Protect(JSON {"token","webhookSecret","appSecret","verifyToken"}); старі записи без нових полів читаються.</summary>
public static class ChannelSecrets
{
    public static ChannelSecretSet Read(ISecretProtector protector, string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return new ChannelSecretSet(null, null);
        return JsonSerializer.Deserialize<ChannelSecretSet>(protector.Unprotect(encrypted), Options) ?? new ChannelSecretSet(null, null);
    }

    public static string Write(ISecretProtector protector, ChannelSecretSet secrets) =>
        protector.Protect(JsonSerializer.Serialize(secrets, Options));

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
