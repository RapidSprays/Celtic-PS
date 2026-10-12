using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CH.Server;

/// <summary>Which addresses may connect, and which count as the host's own machine.</summary>
public static class NetworkAccess
{
    /// <summary>Tailscale's address ranges: 100.64.0.0/10 (IPv4, CGNAT) and fd7a:115c:a1e0::/48 (IPv6).</summary>
    public static bool IsTailscale(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 100 && (b[1] & 0xC0) == 64;
        }
        var v6 = address.GetAddressBytes();
        return v6.Length == 16 && v6[0] == 0xfd && v6[1] == 0x7a && v6[2] == 0x11 && v6[3] == 0x5c && v6[4] == 0xa1 && v6[5] == 0xe0;
    }

    /// <summary>Loopback or one of this machine's own interface addresses (e.g. its own Tailscale IP).</summary>
    public static bool IsLocalMachine(IPAddress address)
    {
        address = Normalize(address);
        return IPAddress.IsLoopback(address) || LocalAddresses().Contains(address);
    }

    /// <summary>This machine's Tailscale IPv4 address, if Tailscale is connected.</summary>
    public static IPAddress? TailscaleAddress() =>
        LocalAddresses().FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsTailscale(a));

    private static HashSet<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .ToHashSet();

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
