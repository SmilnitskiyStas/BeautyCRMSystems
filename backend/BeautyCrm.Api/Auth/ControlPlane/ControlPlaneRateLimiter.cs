namespace BeautyCrm.Api.Auth.ControlPlane;

/// <summary>
/// Ліміт групи control plane: 60 запитів/хв на kid (§27.1), фіксоване вікно. У пам'яті процесу (один інстанс API, ADR-001);
/// ключі — лише відомі kid (множина обмежена конфігом), тому пам'ять не росте від зовнішнього вводу.
/// </summary>
public sealed class ControlPlaneRateLimiter(TimeProvider time, int permitPerWindow = 60, int windowSeconds = 60)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Window, int Count)> _state = new(StringComparer.Ordinal);

    /// <summary>true — дозволено; інакше <paramref name="retryAfterSeconds"/> — скільки чекати до наступного вікна (≥ 1).</summary>
    public bool TryAcquire(string kid, out int retryAfterSeconds)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var window = now / windowSeconds;
        lock (_gate)
        {
            var (w, count) = _state.TryGetValue(kid, out var s) ? s : (window, 0);
            if (w != window) { w = window; count = 0; }
            if (count >= permitPerWindow)
            {
                _state[kid] = (w, count);
                retryAfterSeconds = (int)Math.Max(1, (window + 1) * windowSeconds - now);
                return false;
            }
            _state[kid] = (w, count + 1);
            retryAfterSeconds = 0;
            return true;
        }
    }
}
