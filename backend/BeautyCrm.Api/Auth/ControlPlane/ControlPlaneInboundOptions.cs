using System.Security.Cryptography;

namespace BeautyCrm.Api.Auth.ControlPlane;

public enum ControlPlaneKeyStatus { Active, Retiring }

/// <summary>Вхідний ключ control plane. Секрет (сирі байти) живе лише в пам'яті; у логи/БД іде тільки kid і відбиток.</summary>
public sealed class ControlPlaneKey
{
    public ControlPlaneKey(string kid, byte[] secret, ControlPlaneKeyStatus status)
    {
        Kid = kid;
        Secret = secret;
        Status = status;
        Fingerprint = ControlPlaneSignature.Fingerprint(secret);
    }

    public string Kid { get; }
    public ControlPlaneKeyStatus Status { get; }
    public string Fingerprint { get; }
    internal byte[] Secret { get; }

    public override string ToString() => $"{Kid} ({Status}, fp {Fingerprint})";
}

/// <summary>
/// Конфіг <c>ControlPlane:Inbound:Keys:{kid}:Secret</c> (base64) і <c>:Status</c> (active | retiring), ADR-018 D1.
/// Fail closed: ключ із невалідним kid/статусом/base64 або коротший за 32 байти відкидається (у лог — лише kid і причина);
/// якщо після цього немає жодного active-ключа, група <c>/api/control</c> вимкнена повністю (retiring-ключі теж не діють).
/// Ключі читаються на старті: ротація = змінити env + рестарт (runbook, human gate).
/// </summary>
public sealed class ControlPlaneInboundOptions
{
    private readonly Dictionary<string, ControlPlaneKey> _keys;

    public ControlPlaneInboundOptions(IEnumerable<ControlPlaneKey> keys)
    {
        var all = keys.ToDictionary(k => k.Kid, StringComparer.Ordinal);
        Enabled = all.Values.Any(k => k.Status == ControlPlaneKeyStatus.Active);
        _keys = Enabled ? all : [];
    }

    /// <summary>true, лише якщо є хоча б один валідний active-ключ.</summary>
    public bool Enabled { get; }

    public bool TryGetKey(string kid, out ControlPlaneKey key) => _keys.TryGetValue(kid, out key!);

    public IReadOnlyCollection<ControlPlaneKey> Keys => _keys.Values;

    public static ControlPlaneInboundOptions From(IConfiguration config, ILogger? log = null)
    {
        var keys = new List<ControlPlaneKey>();
        foreach (var section in config.GetSection("ControlPlane:Inbound:Keys").GetChildren())
        {
            var kid = section.Key.ToLowerInvariant(); // env-провайдер на Windows може підняти регістр
            if (!ControlPlaneSignature.KidFormat.IsMatch(kid))
            {
                log?.LogWarning("Control plane key ignored: kid has invalid format (kid length {Length})", section.Key.Length);
                continue;
            }
            if (keys.Any(k => k.Kid == kid))
            {
                log?.LogWarning("Control plane key {Kid} ignored: duplicate kid", kid);
                continue;
            }

            ControlPlaneKeyStatus status;
            switch (section["Status"]?.Trim().ToLowerInvariant())
            {
                case "active": status = ControlPlaneKeyStatus.Active; break;
                case "retiring": status = ControlPlaneKeyStatus.Retiring; break;
                default:
                    log?.LogWarning("Control plane key {Kid} ignored: Status must be active or retiring", kid);
                    continue;
            }

            var raw = section["Secret"];
            if (string.IsNullOrEmpty(raw))
            {
                log?.LogWarning("Control plane key {Kid} ignored: Secret is empty", kid);
                continue;
            }
            var buffer = new byte[raw.Length]; // base64 завжди коротший за текст
            if (!Convert.TryFromBase64String(raw, buffer, out var written))
            {
                log?.LogWarning("Control plane key {Kid} ignored: Secret is not valid base64", kid);
                continue;
            }
            if (written < ControlPlaneSignature.MinKeyBytes)
            {
                log?.LogWarning("Control plane key {Kid} ignored: Secret is shorter than {Min} bytes", kid, ControlPlaneSignature.MinKeyBytes);
                CryptographicOperations.ZeroMemory(buffer);
                continue;
            }

            var secret = buffer[..written];
            CryptographicOperations.ZeroMemory(buffer);
            keys.Add(new ControlPlaneKey(kid, secret, status));
        }

        var options = new ControlPlaneInboundOptions(keys);
        if (!options.Enabled)
            log?.LogInformation("Control plane (/api/control) is disabled: no valid active ControlPlane:Inbound key configured.");
        else
            foreach (var k in options.Keys)
                log?.LogInformation("Control plane key loaded: kid {Kid}, status {Status}, fingerprint {Fingerprint}", k.Kid, k.Status, k.Fingerprint);
        return options;
    }
}
