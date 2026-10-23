using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScanHarness.Services.Egress
{
    /// <summary>Callers that aren't tools.</summary>
    public static class EgressCallers
    {
        /// <summary>The harness's own calls to an LLM endpoint.</summary>
        public const string HarnessLlm = "harness:llm";

        /// <summary>A connection that presented no egress token.</summary>
        public const string Anonymous = "anonymous";
    }

    /// <summary>
    /// What one tool call (or the harness itself) may reach through the egress proxy. The harness builds it from
    /// stored records when a call is about to run, signs it into an egress token, and the proxy's policy check
    /// reads it back, so the proxy enforces the same engagement scope as <c>ScopeGuard</c> without trusting the tool.
    /// </summary>
    /// <param name="Caller">The tool's function name, or one of <see cref="EgressCallers"/>; egress.json rules apply by caller.</param>
    /// <param name="ExpiresAt">After this (UTC) the token is refused.</param>
    /// <param name="ToolCallId">The tool call the connections belong to, for the log.</param>
    /// <param name="SessionId">Session of the tool call.</param>
    /// <param name="UserName">User who made the request.</param>
    /// <param name="EngagementId">Engagement the call runs under, when the tool is limited to its scope.</param>
    /// <param name="EngagementLabel">The engagement's label, e.g. "Acme Ltd v2".</param>
    /// <param name="EngagementDomains">The engagement's domains (each covers its subdomains).</param>
    /// <param name="WindowStart">First day of the engagement's testing window.</param>
    /// <param name="WindowEnd">Last day of the engagement's testing window.</param>
    /// <param name="Domains">Domains the tool may always reach (its egress block in tools.json).</param>
    /// <param name="Ports">Ports the tool may use; null uses egress.json's allowedPorts.</param>
    public sealed record EgressGrant(
        string Caller,
        DateTime ExpiresAt,
        string? ToolCallId = null,
        string? SessionId = null,
        string? UserName = null,
        string? EngagementId = null,
        string? EngagementLabel = null,
        IReadOnlyList<string>? EngagementDomains = null,
        DateOnly? WindowStart = null,
        DateOnly? WindowEnd = null,
        IReadOnlyList<string>? Domains = null,
        IReadOnlyList<int>? Ports = null);

    /// <summary>
    /// Signs <see cref="EgressGrant"/>s into tokens and reads them back. A token is the grant as base64url JSON, a
    /// dot, and an HMAC-SHA256 of it, so a tool can carry it but not change it. Tools send it to the proxy as their
    /// proxy credential (Basic, any user name, the token as the password) or as a Bearer token.
    /// </summary>
    public sealed class EgressTokens
    {
        /// <summary>Header carrying a tool call's egress token from the harness to the tool API.</summary>
        public const string ToolHeader = "X-Egress-Token";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly byte[] _key;

        /// <param name="key">Signing key; at least 32 bytes. Null generates a random key (tokens then end with the process).</param>
        public EgressTokens(byte[]? key = null)
        {
            if (key != null && key.Length < 32)
            {
                throw new ArgumentException("The egress token signing key must be at least 32 bytes.", nameof(key));
            }

            _key = key ?? RandomNumberGenerator.GetBytes(32);
        }

        /// <summary>Signs a grant into a token.</summary>
        public string Mint(EgressGrant grant)
        {
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(grant, JsonOptions));
            return $"{payload}.{Base64Url(Sign(payload))}";
        }

        /// <summary>Reads a token minted by this instance.</summary>
        /// <param name="now">The current time (UTC), to check expiry.</param>
        /// <param name="problem">Why the token was refused, when it was.</param>
        /// <returns>The grant, or null when the token is malformed, tampered with, signed by another key or expired.</returns>
        public EgressGrant? Read(string? token, DateTime now, out string? problem)
        {
            problem = null;
            var parts = token?.Trim().Split('.');
            if (parts == null || parts.Length != 2 || parts[0].Length == 0)
            {
                problem = "the egress token is malformed";
                return null;
            }

            byte[] signature;
            byte[] json;
            try
            {
                signature = FromBase64Url(parts[1]);
                json = FromBase64Url(parts[0]);
            }
            catch (FormatException)
            {
                problem = "the egress token is malformed";
                return null;
            }

            if (!CryptographicOperations.FixedTimeEquals(signature, Sign(parts[0])))
            {
                problem = "the egress token's signature is wrong (it was changed, or issued by another harness run)";
                return null;
            }

            EgressGrant? grant;
            try
            {
                grant = JsonSerializer.Deserialize<EgressGrant>(json, JsonOptions);
            }
            catch (JsonException)
            {
                grant = null;
            }

            if (grant == null || string.IsNullOrWhiteSpace(grant.Caller))
            {
                problem = "the egress token is malformed";
                return null;
            }

            if (now > grant.ExpiresAt)
            {
                problem = $"the egress token expired at {grant.ExpiresAt:HH:mm:ss} UTC";
                return null;
            }

            return grant;
        }

        /// <summary>
        /// The token in a Proxy-Authorization header: "Basic base64(user:token)" or "Bearer token". Null when the
        /// header is missing or carries no token.
        /// </summary>
        public static string? FromProxyAuthorization(string? header)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                return null;
            }

            var space = header.IndexOf(' ');
            if (space <= 0)
            {
                return null;
            }

            var scheme = header[..space];
            var value = header[(space + 1)..].Trim();
            if (scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return value.Length > 0 ? value : null;
            }

            if (!scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                var colon = credentials.IndexOf(':');
                var password = colon < 0 ? "" : credentials[(colon + 1)..];
                return password.Length > 0 ? password : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private byte[] Sign(string payload) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload));

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string text)
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
            return Convert.FromBase64String(padded);
        }
    }
}
