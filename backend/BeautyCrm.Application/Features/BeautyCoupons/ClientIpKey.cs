using System.Net;
using System.Net.Sockets;

namespace BeautyCrm.Application.Features.BeautyCoupons;

/// <summary>TASK-790 (M5): ключ ліміту за адресою клієнта. IPv4 як є; IPv4-mapped IPv6 -> IPv4; IPv6 -> префікс /64.</summary>
public static class ClientIpKey
{
    public static string Normalize(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var addr)) return ip?.Trim() ?? "unknown";
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (addr.AddressFamily != AddressFamily.InterNetworkV6) return addr.ToString();
        var b = addr.GetAddressBytes();
        return $"v6:{Convert.ToHexString(b, 0, 8).ToLowerInvariant()}/64";
    }
}
