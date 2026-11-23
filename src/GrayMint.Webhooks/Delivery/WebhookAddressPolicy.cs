using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GrayMint.Webhooks.Delivery;

// Where a message may go: by https, to a public address that is not the sending machine's own, so a subscriber can't
// aim it at the host's machine or network. The sender judges every address again as it connects, which is the check
// that holds: a name can resolve to another address by then.
internal static class WebhookAddressPolicy
{
    // an IPv4 address inside these leads to it, so it is judged as that address
    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");

    // IANA's special-purpose IPv4 addresses: this machine, private and shared networks, link-local (a cloud's
    // metadata service among them), documentation, benchmarks, multicast and what is reserved
    private static readonly IPNetwork[] NonPublicIpv4Networks = [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4")
    ];

    // Every public IPv6 address is in here. The rest of IPv6 is this machine, local networks, multicast and space
    // given to nobody yet, so IPv6 is judged by what is public, not by what is not: a special range that is named
    // later is closed without a change here.
    private static readonly IPNetwork GlobalUnicastIpv6 = IPNetwork.Parse("2000::/3");

    // IANA's special-purpose addresses inside it: protocol assignments, a tunnel into IPv4 among them, and
    // documentation
    private static readonly IPNetwork[] NonPublicIpv6Networks = [
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("3fff::/20")
    ];

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsPublic(address.MapToIPv4());

        if (Nat64.Contains(address))
            return IsPublic(new IPAddress(address.GetAddressBytes().AsSpan(12, 4)));

        if (SixToFour.Contains(address))
            return IsPublic(new IPAddress(address.GetAddressBytes().AsSpan(2, 4)));

        return address.AddressFamily == AddressFamily.InterNetwork
            ? !NonPublicIpv4Networks.Any(x => x.Contains(address))
            : GlobalUnicastIpv6.Contains(address) && !NonPublicIpv6Networks.Any(x => x.Contains(address));
    }

    // Why a message can never go to the address, whatever a later attempt finds; or null. A name's addresses are not
    // looked up here: they change, so they are the connect's to judge, and a refusal there is retried.
    public static string? FindRefusal(Uri url, bool allowInsecureTargets)
    {
        if (allowInsecureTargets)
            return null;

        if (url.Scheme != Uri.UriSchemeHttps)
            return "The address is not https.";

        return IPAddress.TryParse(url.DnsSafeHost, out var address) && !IsPublic(address)
            ? "The address is not a public one."
            : null;
    }

    // this machine's addresses, as its network interfaces have them now
    public static IReadOnlySet<IPAddress> GetOwnAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(x => x.GetIPProperties().UnicastAddresses)
            .Select(x => x.Address)
            .ToHashSet();
    }

    // The addresses of a host that a message may go to: its public ones, less this machine's own. A public address
    // of this machine is the machine itself, reached past its firewall and with its own address as the caller's.
    public static IReadOnlyList<IPAddress> GetTargets(string host, IReadOnlyList<IPAddress> addresses,
        IReadOnlySet<IPAddress> ownAddresses)
    {
        var publicAddresses = addresses.Where(IsPublic).ToArray();
        var targets = publicAddresses
            .Where(x => !ownAddresses.Contains(x.IsIPv4MappedToIPv6 ? x.MapToIPv4() : x))
            .ToArray();

        if (targets.Length != 0)
            return targets;

        throw new HttpRequestException(publicAddresses.Length == 0
            ? $"The host {host} has no public address."
            : $"The host {host} is the sender's own machine.");
    }
}
