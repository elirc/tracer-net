using System.Net;
using System.Net.Sockets;

namespace Tracer.Domain;

/// <summary>
/// Whether a URL is one this server is willing to POST to.
///
/// <para>
/// <b>This is an SSRF control, not a formatting check.</b> A webhook URL is
/// attacker-supplied input that the *server* then fetches, from inside the
/// network, with whatever the server can reach. Point one at
/// <c>http://169.254.169.254/latest/meta-data/iam/</c> and a cloud host will hand
/// over its instance credentials; point it at an internal admin service and every
/// signed payload becomes a request nobody at the edge ever saw. The delivery log
/// then obligingly reports the response back to whoever asked.
/// </para>
/// <para>
/// <b>What this cannot do, stated plainly.</b> A hostname is only checked when it
/// is a literal IP. <c>evil.example.com</c> that resolves to 127.0.0.1 passes here
/// and always will, because the answer depends on DNS at the moment of the
/// request, not at the moment of the check — and it can differ between the two
/// (DNS rebinding). Closing that properly means resolving at send time and
/// pinning the connection to the address that was checked, via a custom
/// <c>SocketsHttpHandler.ConnectCallback</c>. That belongs in the sender, and it
/// is the honest next step rather than something this class quietly implies it
/// has done.
/// </para>
/// </summary>
public static class WebhookUrlPolicy
{
    public static bool IsAllowed(string url, out string reason)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = "Must be an absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            // file:, ftp:, gopher: and friends are not transports for this, and
            // each is its own way to make the server read something local.
            reason = "Must be an http or https URL.";
            return false;
        }

        if (IPAddress.TryParse(uri.Host, out var literal) && !IsPublic(literal))
        {
            reason = "Must not point at a private, loopback, or link-local address.";
            return false;
        }

        if (uri.IsLoopback)
        {
            reason = "Must not point at this machine.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// True for addresses that live out on the internet. Everything else — the
    /// private ranges, loopback, link-local (which is where cloud metadata
    /// services sit), and the unique-local v6 range — is somewhere this server
    /// can reach but the person configuring the webhook should not be able to
    /// aim it at.
    /// </summary>
    private static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IPAddress.IPv6Loopback.Equals(address))
            {
                return false;
            }

            // ::ffff:127.0.0.1 is loopback wearing a v6 hat; unwrap before judging.
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPublic(address.MapToIPv4());
            }

            // fc00::/7 — unique local, the v6 equivalent of 10.0.0.0/8.
            return (address.GetAddressBytes()[0] & 0xFE) != 0xFC;
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => false,                                  // 10.0.0.0/8
            127 => false,                                 // loopback
            0 => false,                                   // "this network"
            172 when octets[1] is >= 16 and <= 31 => false, // 172.16.0.0/12
            192 when octets[1] == 168 => false,           // 192.168.0.0/16
            169 when octets[1] == 254 => false,           // link-local, incl. cloud metadata
            100 when octets[1] is >= 64 and <= 127 => false, // 100.64.0.0/10 carrier-grade NAT
            >= 224 => false,                              // multicast and reserved
            _ => true,
        };
    }
}
