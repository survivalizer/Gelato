using System.Net;
using System.Net.Sockets;
using Gelato.Config;

namespace Gelato.Services;

/// <summary>
/// Decides whether a stream path may be handed to a client for direct play instead of being
/// masked so the client proxies through Jellyfin. Only a remote http(s) URL whose host a
/// client elsewhere could actually reach qualifies: the in-process P2P proxy lives on
/// loopback and rejects non-loopback callers, addresses inside the private/LAN ranges and
/// container hostnames resolve only from the server's own network, and placeholder or local
/// paths are meaningless to a client.
/// </summary>
public static class DirectPlayPolicy
{
    /// <summary>
    /// Suffixes reserved for names that never resolve outside a local network.
    /// </summary>
    private static readonly string[] InternalSuffixes =
    [
        "local",
        "internal",
        "lan",
        "home",
        "docker",
        "test",
        "invalid",
        "localdomain",
        "localhost",
    ];

    /// <summary>
    /// Whether <paramref name="userId"/> may be handed <paramref name="path"/> for direct
    /// play: the effective (per-user, else global) DirectPlay setting must be on AND the
    /// path must be a remote http(s) URL the client can reach.
    /// </summary>
    public static bool IsAllowed(PluginConfiguration? cfg, Guid userId, string? path)
    {
        if (cfg is null)
            return false;

        return cfg.GetEffectiveConfig(userId).DirectPlay && IsDirectPlayable(path);
    }

    public static bool IsDirectPlayable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // DnsSafeHost strips the brackets IPv6 hosts carry in Uri.Host, so the literal
        // parses below.
        return !IsInternalHost(uri.DnsSafeHost);
    }

    /// <summary>
    /// Whether <paramref name="host"/> is reachable only from the server's own network.
    /// Addons running beside Jellyfin routinely return URLs they resolved from inside their
    /// container - a client elsewhere cannot open those, so they must proxy instead.
    /// Classified from the text alone: no DNS lookup, because the name resolving here says
    /// nothing about whether it resolves for the client.
    /// Fails closed - a host that cannot be classified is not confirmed reachable either.
    /// </summary>
    public static bool IsInternalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        // A trailing dot is the fully-qualified form of the same name.
        host = host.Trim().TrimEnd('.');

        if (host.Length == 0)
            return true;

        if (IPAddress.TryParse(host, out var ip))
            return IsInternalAddress(ip);

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // A publicly reachable host always carries a TLD. A single label is a container
        // service name or a LAN hostname - "mycelium", "aiostreams", "jellyfin".
        if (!host.Contains('.', StringComparison.Ordinal))
            return true;

        var tld = host[(host.LastIndexOf('.') + 1)..];

        return InternalSuffixes.Contains(tld, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsInternalAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::ffff:10.0.0.5 carries a v4 address that still needs range checking.
            if (ip.IsIPv4MappedToIPv6)
                return IsInternalAddress(ip.MapToIPv4());

            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6Any);
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return true;

        var octets = ip.GetAddressBytes();

        return octets[0] switch
        {
            0 => true, // 0.0.0.0/8 unspecified
            10 => true, // 10.0.0.0/8
            127 => true, // loopback, already covered above
            255 => true, // broadcast
            100 => (octets[1] & 0b1100_0000) == 0b0100_0000, // 100.64.0.0/10 CGNAT
            169 => octets[1] == 254, // 169.254.0.0/16 link-local
            172 => octets[1] >= 16 && octets[1] <= 31, // 172.16.0.0/12
            192 => octets[1] == 168, // 192.168.0.0/16
            _ => false,
        };
    }
}
