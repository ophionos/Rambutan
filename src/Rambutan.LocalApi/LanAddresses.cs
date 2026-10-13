using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Rambutan.LocalApi;

/// <summary>One IPv4 address of this machine, with what decides whether a phone
/// on the same network can likely reach it.</summary>
/// <param name="HasGateway">The interface has a default gateway: it is the
/// machine's way onto a real network, not a host-only or virtual switch.</param>
public sealed record LanCandidate(IPAddress Address, string InterfaceName, bool HasGateway);

/// <summary>
/// Which of this machine's addresses to put in the pairing link. The server
/// listens on all of them; this only picks the one a phone most likely shares a
/// network with, and orders the rest after it.
/// </summary>
public static class LanAddresses
{
    /// <summary>Adapter names of virtual switches and VPNs (WSL, Hyper-V,
    /// VirtualBox, VMware, Docker): reachable from this machine, rarely from a phone.</summary>
    private static readonly string[] VirtualNames =
        ["vethernet", "wsl", "hyper-v", "virtualbox", "vmware", "docker", "vbox", "loopback"];

    /// <summary>The addresses worth offering, best first: IPv4 only, no loopback
    /// or link-local; then interfaces with a gateway, private ranges, and
    /// physical adapters ahead of the rest.</summary>
    public static IReadOnlyList<IPAddress> Rank(IEnumerable<LanCandidate> candidates) => candidates
        .Where(c => c.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(c.Address)
                    && !IsLinkLocal(c.Address))
        .OrderByDescending(c => c.HasGateway)
        .ThenByDescending(c => IsPrivate(c.Address))
        .ThenBy(c => IsVirtual(c.InterfaceName))
        .Select(c => c.Address)
        .Distinct()
        .ToArray();

    /// <summary>This machine's addresses, ranked. Empty when no network is up.</summary>
    public static IReadOnlyList<IPAddress> Discover()
    {
        var candidates = new List<LanCandidate>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in props.UnicastAddresses)
                    candidates.Add(new LanCandidate(unicast.Address, $"{nic.Name} {nic.Description}", hasGateway));
            }
        }
        catch (NetworkInformationException)
        {
            // No interfaces readable: the same as no network.
        }

        return Rank(candidates);
    }

    internal static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168);
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private static bool IsVirtual(string name)
    {
        var lower = name.ToLowerInvariant();
        return VirtualNames.Any(lower.Contains);
    }
}
