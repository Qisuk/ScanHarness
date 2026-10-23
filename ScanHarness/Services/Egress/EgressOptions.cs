using System.Text.Json.Serialization;

namespace ScanHarness.Services.Egress
{
    /// <summary>How far the harness relies on the egress proxy.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EgressMode
    {
        /// <summary>No proxy: tool calls carry no egress token and the policy endpoint answers 404.</summary>
        Off,

        /// <summary>
        /// Tools are pointed at the proxy and every connection through it is checked and logged, but nothing stops a
        /// tool going around it (e.g. tools run with dotnet run on the same machine).
        /// </summary>
        Advisory,

        /// <summary>Tools run on a network whose only way out is the proxy (docker-compose's egress profile).</summary>
        Enforced
    }

    /// <summary>Settings for network egress control (config section "Egress"; see docs/design/egress-proxy.md).</summary>
    public class EgressOptions
    {
        public const string SectionName = "Egress";

        /// <summary>The shortest policy key accepted, so the proxy's shared secret can't be guessed.</summary>
        public const int MinimumPolicyKeyLength = 16;

        public EgressMode Mode { get; set; } = EgressMode.Off;

        /// <summary>Egress policy file, relative to the content root.</summary>
        public string DefinitionsPath { get; set; } = EgressCatalog.DefaultFileName;

        /// <summary>
        /// Shared secret the proxy sends with every policy check, so nothing else can ask the policy endpoint (and
        /// learn what's allowed) or fill the log. Required unless <see cref="Mode"/> is Off; keep it in user secrets
        /// or an environment variable.
        /// </summary>
        public string PolicyKey { get; set; } = string.Empty;

        /// <summary>How long a tool call's egress token lasts.</summary>
        public int TokenLifetimeSeconds { get; set; } = 300;

        /// <summary>
        /// Base64 signing key for egress tokens (at least 32 bytes). Empty generates one per run, which is fine while
        /// the harness both issues and checks tokens.
        /// </summary>
        public string TokenSigningKey { get; set; } = string.Empty;

        /// <summary>
        /// The proxy's forward-proxy address as the harness reaches it (e.g. http://localhost:3128). Used to send the
        /// harness's own LLM traffic through the proxy when <see cref="RouteLlmTraffic"/> is on.
        /// </summary>
        public string ProxyUrl { get; set; } = string.Empty;

        /// <summary>
        /// Send the harness's calls to a non-loopback LLM endpoint through the proxy (caller "harness:llm"), so data
        /// leaving for a hosted model is checked and logged like tool traffic. Needs an Allow rule for the provider.
        /// </summary>
        public bool RouteLlmTraffic { get; set; }

        /// <summary>
        /// The proxy's JSON access log (mounted from the container). When set, traffic figures (bytes and duration)
        /// are read from it and added to the recorded connections.
        /// </summary>
        public string AccessLogPath { get; set; } = string.Empty;

        /// <summary>Problems that stop the app starting with these settings.</summary>
        public IReadOnlyList<string> Problems()
        {
            var problems = new List<string>();
            if (Mode != EgressMode.Off && PolicyKey.Length < MinimumPolicyKeyLength)
                problems.Add($"Egress:PolicyKey must be at least {MinimumPolicyKeyLength} characters when Egress:Mode is {Mode} (it must match the proxy's EGRESS_POLICY_KEY).");
            if (TokenLifetimeSeconds is < 10 or > 86400)
                problems.Add("Egress:TokenLifetimeSeconds must be between 10 and 86400.");
            if (TokenSigningKey.Length > 0 && SigningKeyBytes() is not { Length: >= 32 })
                problems.Add("Egress:TokenSigningKey must be base64 for at least 32 bytes, or empty.");
            if (RouteLlmTraffic && !Uri.TryCreate(ProxyUrl, UriKind.Absolute, out _))
                problems.Add("Egress:ProxyUrl must be an absolute URL when Egress:RouteLlmTraffic is on.");
            return problems;
        }

        /// <summary>The configured signing key, or null for a generated one (or when it isn't valid base64).</summary>
        public byte[]? SigningKeyBytes()
        {
            if (TokenSigningKey.Length == 0)
                return null;
            try
            {
                return Convert.FromBase64String(TokenSigningKey);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
