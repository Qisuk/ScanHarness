using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ScanHarness.Services.Egress
{
    /// <summary>A connection target the proxy was asked for: a normalised host and a port.</summary>
    /// <param name="Host">Lower-case host with no trailing dot; IDNs in punycode; IPv6 addresses without brackets.</param>
    /// <param name="Port">The port asked for.</param>
    /// <param name="Address">Set when <paramref name="Host"/> is an IP address rather than a name.</param>
    public sealed record EgressTarget(string Host, int Port, IPAddress? Address)
    {
        /// <summary>True when the target is an IP address (an "IP literal") rather than a name.</summary>
        public bool IsAddress => Address != null;

        /// <summary>"host:port", with IPv6 addresses in brackets.</summary>
        public override string ToString() => EgressHost.Join(Host, Port);
    }

    /// <summary>
    /// Parsing and normalising the hosts the egress proxy is asked to reach, and the address ranges it never
    /// connects to. Every rule is matched against the same normalised form, so "Example.COM.", "example.com"
    /// and an IDN's Unicode and punycode spellings can't be used to slip past a rule.
    /// </summary>
    public static class EgressHost
    {
        private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

        /// <summary>
        /// Addresses that aren't on the public internet: private, loopback, link-local (including cloud metadata
        /// services at 169.254.169.254), shared, benchmarking, multicast and reserved ranges.
        /// </summary>
        public static readonly IReadOnlyList<IPNetwork> NonPublicRanges = new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
            "224.0.0.0/4", "240.0.0.0/4",
            "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8", "2001:db8::/32",
        }.Select(IPNetwork.Parse).ToList();

        /// <summary>
        /// Normalises a host name: trims it, lower-cases it, drops a trailing dot and converts an IDN to punycode.
        /// IP addresses come back in their canonical form. Returns null when it isn't a valid host.
        /// </summary>
        public static string? Normalise(string? raw)
        {
            var host = raw?.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(host))
            {
                return null;
            }

            if (host.StartsWith('[') && host.EndsWith(']'))
            {
                host = host[1..^1];
            }

            if (IPAddress.TryParse(host, out var address) && (host.Contains(':') || host.Count(c => c == '.') == 3))
            {
                return Canonical(address).ToString();
            }

            try
            {
                host = Idn.GetAscii(host).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return null;
            }

            // A numeric last label isn't a real top-level domain, but resolvers read names like "127.1" or
            // "2130706433" as addresses; refuse them so an address can't be dressed up as a name.
            var last = host[(host.LastIndexOf('.') + 1)..];
            if (last.All(char.IsAsciiDigit) || last.StartsWith("0x", StringComparison.Ordinal))
            {
                return null;
            }

            return Uri.CheckHostName(host) == UriHostNameType.Dns ? host : null;
        }

        /// <summary>
        /// Parses what a client asked the proxy for: "host:port" for CONNECT, or the Host of a plain HTTP request
        /// ("host" or "host:port"). <paramref name="defaultPort"/> is used when no port is given.
        /// </summary>
        /// <returns>The target, or null when it isn't a valid host and port.</returns>
        public static EgressTarget? ParseTarget(string? authority, int? defaultPort)
        {
            if (string.IsNullOrWhiteSpace(authority))
            {
                return null;
            }

            var text = authority.Trim();
            string hostPart;
            string? portPart = null;
            if (text.StartsWith('['))
            {
                var close = text.IndexOf(']');
                if (close < 0)
                {
                    return null;
                }

                hostPart = text[1..close];
                var rest = text[(close + 1)..];
                if (rest.Length > 0)
                {
                    if (!rest.StartsWith(':'))
                        return null;
                    portPart = rest[1..];
                }
            }
            else if (text.Count(c => c == ':') == 1)
            {
                var colon = text.IndexOf(':');
                hostPart = text[..colon];
                portPart = text[(colon + 1)..];
            }
            else
            {
                // No colon, or a bare IPv6 address (which can't carry a port without brackets).
                hostPart = text;
            }

            int port;
            if (portPart == null)
            {
                if (defaultPort == null)
                    return null;
                port = defaultPort.Value;
            }
            else if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                return null;
            }

            var host = Normalise(hostPart);
            if (host == null)
            {
                return null;
            }

            return new EgressTarget(host, port, IPAddress.TryParse(host, out var address) ? address : null);
        }

        /// <summary>True when <paramref name="address"/> isn't a public internet address (see <see cref="NonPublicRanges"/>).</summary>
        public static bool IsNonPublic(IPAddress address)
        {
            var canonical = Canonical(address);
            return NonPublicRanges.Any(range => range.Contains(canonical));
        }

        /// <summary>An IPv4-mapped IPv6 address (::ffff:10.0.0.1) as plain IPv4, so it meets the IPv4 ranges.</summary>
        public static IPAddress Canonical(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        /// <summary>"host:port", with IPv6 addresses in brackets (the form Envoy's upstream header expects).</summary>
        public static string Join(string host, int port) =>
            IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}"
                : $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
    }
}
