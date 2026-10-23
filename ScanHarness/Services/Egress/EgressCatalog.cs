using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ScanHarness.Services.Engagements;

namespace ScanHarness.Services.Egress
{
    /// <summary>What an egress rule does when it matches.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EgressAction
    {
        /// <summary>Lets the connection go ahead (if no Deny rule matches and its address is allowed).</summary>
        Allow,

        /// <summary>Refuses the connection. Deny rules always win over Allow rules.</summary>
        Deny
    }

    /// <summary>How an egress rule matches a connection.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EgressMatchType
    {
        /// <summary>The host is the domain or one of its subdomains (the engagement scope's rule).</summary>
        Domain,

        /// <summary>The host is exactly this name.</summary>
        Exact,

        /// <summary>The whole host matches a .NET regular expression, which must be anchored with ^ and $.</summary>
        Regex,

        /// <summary>The address the host resolves to (or the IP literal asked for) is in this range.</summary>
        Cidr
    }

    /// <summary>One rule in <c>egress.json</c>.</summary>
    public sealed class EgressRule
    {
        /// <summary>How long a regex may run on one host; a timeout counts as a miss for Allow and a match for Deny (fail closed).</summary>
        public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        private readonly Regex? _regex;
        private readonly IPNetwork? _network;

        /// <param name="id">Stable identifier, shown in decisions and the log.</param>
        /// <param name="action">Allow or Deny.</param>
        /// <param name="type">How the rule matches.</param>
        /// <param name="value">A domain, a host, a regex or a CIDR range (already validated).</param>
        /// <param name="appliesTo">Callers the rule applies to (tool function names, or "harness:llm"); empty means everyone.</param>
        /// <param name="description">Why the rule exists, shown on the Controls page.</param>
        public EgressRule(string id, EgressAction action, EgressMatchType type, string value, IReadOnlyList<string>? appliesTo = null, string description = "")
        {
            Id = id;
            Action = action;
            Type = type;
            AppliesTo = appliesTo ?? Array.Empty<string>();
            Description = description;
            Value = type is EgressMatchType.Domain or EgressMatchType.Exact ? EgressHost.Normalise(value) ?? value : value;

            if (type == EgressMatchType.Regex)
            {
                _regex = new Regex(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            else if (type == EgressMatchType.Cidr)
            {
                _network = IPNetwork.Parse(value);
            }
        }

        public string Id { get; }

        public EgressAction Action { get; }

        public EgressMatchType Type { get; }

        public string Value { get; }

        public IReadOnlyList<string> AppliesTo { get; }

        public string Description { get; }

        /// <summary>Whether the rule applies to <paramref name="caller"/> (rules with no appliesTo apply to every caller).</summary>
        public bool AppliesToCaller(string caller) =>
            AppliesTo.Count == 0 || AppliesTo.Contains(caller, StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether a name rule (domain, exact or regex) matches the normalised host. CIDR rules never match names.</summary>
        public bool MatchesHost(string host)
        {
            switch (Type)
            {
                case EgressMatchType.Domain:
                    return ScopeGuard.IsWithin(host, Value);
                case EgressMatchType.Exact:
                    return string.Equals(host, Value, StringComparison.OrdinalIgnoreCase);
                case EgressMatchType.Regex:
                    try
                    {
                        return _regex!.IsMatch(host);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return Action == EgressAction.Deny;
                    }
                default:
                    return false;
            }
        }

        /// <summary>Whether a CIDR rule contains the address. Name rules never match addresses.</summary>
        public bool MatchesAddress(IPAddress address) =>
            _network is IPNetwork network && network.Contains(EgressHost.Canonical(address));

        /// <summary>A short description of what the rule matches, e.g. "acme.test and subdomains".</summary>
        public string MatchDescription => Type switch
        {
            EgressMatchType.Domain => $"{Value} and its subdomains",
            EgressMatchType.Exact => Value,
            EgressMatchType.Regex => $"hosts matching {Value}",
            _ => $"addresses in {Value}",
        };
    }

    /// <summary>The egress policy file: the ports and rules every connection through the proxy is checked against.</summary>
    /// <param name="AllowedPorts">Ports a connection may use unless a tool's egress block names its own.</param>
    /// <param name="BlockNonPublicAddresses">Refuse hosts that resolve to private, loopback, link-local or other non-public addresses.</param>
    /// <param name="Rules">Allow and Deny rules, in file order.</param>
    public sealed record EgressPolicyDefinition(IReadOnlyList<int> AllowedPorts, bool BlockNonPublicAddresses, IReadOnlyList<EgressRule> Rules)
    {
        /// <summary>Ports allowed when nothing else is configured.</summary>
        public static readonly IReadOnlyList<int> DefaultPorts = new[] { 80, 443 };

        /// <summary>No rules: only engagement scope and tools' own egress blocks can allow anything.</summary>
        public static EgressPolicyDefinition Empty { get; } = new(DefaultPorts, true, Array.Empty<EgressRule>());
    }

    /// <summary>
    /// Reads and validates <c>egress.json</c>. Like <c>controls.json</c>, a broken file stops the app at startup
    /// with every problem listed, rather than running with a weaker policy than intended.
    /// </summary>
    public static class EgressCatalog
    {
        public const string DefaultFileName = "egress.json";

        /// <summary>A host no sensible allow regex should match; one that does is almost certainly ".*" in disguise.</summary>
        internal const string Canary = "canary-host.invalid";

        private static EgressPolicyDefinition? _current;

        /// <summary>The policy in use; until <see cref="Use"/> is called (e.g. in unit tests) the file next to the app.</summary>
        public static EgressPolicyDefinition Current =>
            _current ??= Load(Path.Combine(AppContext.BaseDirectory, DefaultFileName));

        /// <summary>Replaces the policy, e.g. with the file configured for the app.</summary>
        public static void Use(EgressPolicyDefinition policy) => _current = policy;

        /// <exception cref="CatalogException">The file is missing or invalid; the message says what to fix.</exception>
        public static EgressPolicyDefinition Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new CatalogException($"Egress policy file not found: {path}");
            }

            return Parse(File.ReadAllText(path), path);
        }

        /// <summary>
        /// Parses an egress policy:
        /// <code>
        /// { "allowedPorts": [80, 443], "blockNonPublicAddresses": true,
        ///   "rules": [ { "id": "...", "action": "Allow|Deny", "appliesTo": ["weather_forecast"], "description": "...",
        ///                "match": { "type": "domain|exact|regex|cidr", "value": "..." } } ] }
        /// </code>
        /// Every problem found is reported together.
        /// </summary>
        public static EgressPolicyDefinition Parse(string json, string source = "egress")
        {
            var file = CatalogJson.Deserialize<EgressFile>(json, source);
            var errors = new List<string>();
            var rules = new List<EgressRule>();

            var ports = file?.AllowedPorts ?? EgressPolicyDefinition.DefaultPorts.ToList();
            foreach (var port in ports.Where(p => p is < 1 or > 65535))
                errors.Add($"allowedPorts: {port} isn't a valid port.");

            var entries = file?.Rules ?? new List<RuleEntry>();
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var label = string.IsNullOrWhiteSpace(e.Id) ? $"rule #{i + 1}" : $"rule \"{e.Id}\"";
                var before = errors.Count;

                if (string.IsNullOrWhiteSpace(e.Id)) errors.Add($"{label}: \"id\" is required.");
                else if (entries.Take(i).Any(o => o.Id == e.Id)) errors.Add($"{label}: the id is used more than once.");
                if (e.Action == null) errors.Add($"{label}: \"action\" must be Allow or Deny.");
                if (e.Match?.Type == null) errors.Add($"{label}: \"match.type\" must be one of {string.Join(", ", Enum.GetNames<EgressMatchType>())}.");
                else if (string.IsNullOrWhiteSpace(e.Match.Value)) errors.Add($"{label}: \"match.value\" is required.");
                else if (ValueProblem(e.Match.Type.Value, e.Match.Value, e.Action) is string problem) errors.Add($"{label}: {problem}");
                if (e.AppliesTo != null && e.AppliesTo.Any(string.IsNullOrWhiteSpace)) errors.Add($"{label}: \"appliesTo\" can't contain empty names.");

                if (errors.Count == before)
                {
                    rules.Add(new EgressRule(e.Id!, e.Action!.Value, e.Match!.Type!.Value, e.Match.Value!.Trim(), e.AppliesTo, e.Description ?? ""));
                }
            }

            CatalogJson.ThrowIfAny(errors, source);
            return new EgressPolicyDefinition(ports.Distinct().ToList(), file?.BlockNonPublicAddresses ?? true, rules);
        }

        /// <summary>Why a rule's value can't be used, or null if it can.</summary>
        internal static string? ValueProblem(EgressMatchType type, string value, EgressAction? action)
        {
            value = value.Trim();
            switch (type)
            {
                case EgressMatchType.Domain:
                case EgressMatchType.Exact:
                    if (value.Contains('*'))
                        return "wildcards aren't allowed in domain rules; use a domain rule (which covers subdomains) or an anchored regex.";
                    var host = EgressHost.Normalise(value);
                    if (host == null || IPAddress.TryParse(host, out _))
                        return $"\"{value}\" isn't a valid host name (use a cidr rule for addresses).";
                    if (!host.Contains('.') && action == EgressAction.Allow)
                        return $"\"{value}\" would allow a whole top-level domain.";
                    return null;

                case EgressMatchType.Cidr:
                    return IPNetwork.TryParse(value, out _) ? null : $"\"{value}\" isn't a CIDR range such as 10.0.0.0/8.";

                case EgressMatchType.Regex:
                    Regex regex;
                    try
                    {
                        regex = new Regex(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, EgressRule.RegexTimeout);
                    }
                    catch (ArgumentException ex)
                    {
                        return $"\"{value}\" is not a valid regular expression: {ex.Message}";
                    }

                    if (!value.StartsWith('^') || !value.EndsWith('$') || value.EndsWith("\\$", StringComparison.Ordinal))
                        return $"the regex \"{value}\" must start with ^ and end with $, so it matches the whole host (otherwise \"example\\.com\" also matches \"example.com.evil.net\").";
                    if (UnescapedDotBetweenNameCharacters.IsMatch(value))
                        return $"the regex \"{value}\" has an unescaped \".\" between name characters, which matches any character; write \"\\.\".";
                    if (action == EgressAction.Allow && regex.IsMatch(Canary))
                        return $"the regex \"{value}\" matches any host (it matched \"{Canary}\").";
                    return null;

                default:
                    return "unknown match type.";
            }
        }

        // "cdn.acme" in a pattern almost always means "cdn\.acme": a dot (not after a backslash) with a name
        // character on both sides.
        private static readonly Regex UnescapedDotBetweenNameCharacters = new(@"(?<![\\])(?<=[A-Za-z0-9])\.(?=[A-Za-z0-9])", RegexOptions.CultureInvariant);

        // The file's shape. Everything is nullable so missing values are reported, not defaulted.
        private sealed class EgressFile
        {
            public List<int>? AllowedPorts { get; set; }
            public bool? BlockNonPublicAddresses { get; set; }
            public List<RuleEntry>? Rules { get; set; }
        }

        private sealed class RuleEntry
        {
            public string? Id { get; set; }
            public EgressAction? Action { get; set; }
            public List<string>? AppliesTo { get; set; }
            public string? Description { get; set; }
            public MatchEntry? Match { get; set; }
        }

        private sealed class MatchEntry
        {
            public EgressMatchType? Type { get; set; }
            public string? Value { get; set; }
        }
    }
}
