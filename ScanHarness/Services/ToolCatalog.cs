using System.Text.Json.Serialization;
using ScanHarness.Services.Agents;
using ScanHarness.Services.Boundaries;
using ScanHarness.Services.Egress;

namespace ScanHarness.Services
{
    /// <summary>How a tool parameter's value is validated.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ToolParameterType
    {
        /// <summary>A web address; normalised to an absolute http(s) URL.</summary>
        Url,

        /// <summary>Free text, such as a place name.</summary>
        Text
    }

    /// <summary>A parameter a tool accepts.</summary>
    /// <param name="Name">Parameter name, used in the LLM schema and passed to the tool API.</param>
    /// <param name="Type">How the value is validated.</param>
    /// <param name="Description">Description shown to the LLM.</param>
    /// <param name="Required">Whether the tool can't run without it.</param>
    public sealed record ToolParameter(string Name, ToolParameterType Type, string Description, bool Required);

    /// <summary>How a tool is called over HTTP.</summary>
    /// <param name="Method">GET or POST.</param>
    /// <param name="Path">Path relative to <c>Tools:BaseUrl</c>, with <c>{name}</c> replaced by the URL-encoded parameter.</param>
    public sealed record ToolHttpBinding(string Method, string Path)
    {
        /// <summary>
        /// The path with parameters filled in. A query pair whose parameter is missing is dropped, so
        /// "forecast?location={location}" becomes "forecast" when no location was given.
        /// </summary>
        public string Resolve(IReadOnlyDictionary<string, string> parameters)
        {
            var split = Path.Split('?', 2);
            var path = Fill(split[0], parameters);
            if (split.Length == 1)
            {
                return path;
            }

            var pairs = split[1].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(pair => Placeholders(pair).All(parameters.ContainsKey))
                .Select(pair => Fill(pair, parameters))
                .ToList();
            return pairs.Count == 0 ? path : $"{path}?{string.Join("&", pairs)}";
        }

        private static string Fill(string template, IReadOnlyDictionary<string, string> parameters) =>
            parameters.Aggregate(template, (text, p) => text.Replace("{" + p.Key + "}", Uri.EscapeDataString(p.Value)));

        internal static IEnumerable<string> Placeholders(string template) =>
            System.Text.RegularExpressions.Regex.Matches(template, @"\{(\w+)\}").Select(m => m.Groups[1].Value);
    }

    /// <summary>What a tool may reach on the network through the egress proxy (see docs/design/egress-proxy.md).</summary>
    /// <param name="Domains">Domains (and their subdomains) the tool may always reach, e.g. a weather API.</param>
    /// <param name="Ports">Ports it may use; null uses egress.json's allowedPorts.</param>
    /// <param name="EngagementScope">Whether it may also reach the domains of the engagement its call runs under.</param>
    public sealed record ToolEgress(IReadOnlyList<string> Domains, IReadOnlyList<int>? Ports, bool EngagementScope)
    {
        /// <summary>No network access at all: what a tool gets when it has no egress block and no scope.</summary>
        public static ToolEgress None { get; } = new(Array.Empty<string>(), null, false);
    }

    /// <summary>
    /// Describes a tool the agent brain may choose. The catalog is the single source of truth for what a
    /// brain is offered and for whether a tool needs approval or an engagement scope, so a model can never
    /// talk its way out of the attack confirmation or the scope check.
    /// </summary>
    /// <param name="FunctionName">Name exposed to the LLM (OpenAI function name).</param>
    /// <param name="Tool">Tool identifier used in logs, e.g. "scanner"; "agent" for agents.</param>
    /// <param name="Action">Action on the tool, e.g. "simple-scanner"; the agent id for agents.</param>
    /// <param name="Description">Description shown to the LLM so it can decide when the tool fits.</param>
    /// <param name="RequiresApproval">Whether the user must approve the call before it runs.</param>
    /// <param name="Parameters">Parameters the tool accepts.</param>
    /// <param name="Confirmation">Message shown when the tool is chosen; <c>{name}</c> is replaced by the parameter.</param>
    /// <param name="ConfirmationWithoutOptional">Used instead when an optional parameter is missing.</param>
    /// <param name="Classification">Where the tool runs, before any policy override (see <see cref="ComponentPolicyService"/>).</param>
    /// <param name="OutputLabel">How sensitive the tool's results are.</param>
    /// <param name="OutputTrust">Whether its results can be believed (anything a target can influence is untrusted).</param>
    /// <param name="RequiresScope">The url parameter must be inside the session's approved engagement scope.</param>
    /// <param name="Http">How the tool is called; null for agents, which run in the harness.</param>
    /// <param name="Keywords">Words the keyword brain matches for tools it has no special handling for.</param>
    /// <param name="Egress">What the tool may reach through the egress proxy; null means only its engagement scope (if any).</param>
    public sealed record ToolDefinition(
        string FunctionName,
        string Tool,
        string Action,
        string Description,
        bool RequiresApproval,
        IReadOnlyList<ToolParameter> Parameters,
        string Confirmation,
        string? ConfirmationWithoutOptional = null,
        Classification Classification = Classification.Internal,
        Sensitivity OutputLabel = Sensitivity.CustomerConfidential,
        Trust OutputTrust = Trust.Untrusted,
        bool RequiresScope = false,
        ToolHttpBinding? Http = null,
        IReadOnlyList<string>? Keywords = null,
        ToolEgress? Egress = null)
    {
        /// <summary>Tool identifier used for agents.</summary>
        public const string AgentTool = "agent";

        /// <summary>True when this entry calls an agent rather than a tool API.</summary>
        public bool IsAgent => Tool == AgentTool;

        /// <summary>Key used for this tool's policy, e.g. "tool:simple_scanner" or "agent:findings-analyst".</summary>
        public string PolicyId => IsAgent ? $"agent:{Action}" : $"tool:{FunctionName}";

        /// <summary>
        /// What the tool may reach through the egress proxy. Without an egress block, a tool limited to the engagement
        /// scope may reach the engagement's domains and nothing else, and any other tool may reach nothing.
        /// </summary>
        public ToolEgress EffectiveEgress => Egress ?? (RequiresScope ? ToolEgress.None with { EngagementScope = true } : ToolEgress.None);

        /// <summary>Formats the confirmation message for the chosen parameters.</summary>
        public string FormatConfirmation(IReadOnlyDictionary<string, string> parameters)
        {
            var template = ConfirmationWithoutOptional != null && ToolHttpBinding.Placeholders(Confirmation).Any(p => !parameters.ContainsKey(p))
                ? ConfirmationWithoutOptional
                : Confirmation;
            return parameters.Aggregate(template, (text, p) => text.Replace("{" + p.Key + "}", p.Value));
        }
    }

    /// <summary>
    /// The tools available to the agent: the tool APIs in <c>tools.json</c>, then one entry per agent in
    /// <c>agents.json</c> (see <see cref="AgentCatalog"/>). The app loads the configured files at startup with
    /// <see cref="Use"/>; until then (e.g. in unit tests) the files next to the app are used.
    /// </summary>
    public static class ToolCatalog
    {
        public const string DefaultFileName = "tools.json";

        private static IReadOnlyList<ToolDefinition>? _tools;
        private static IReadOnlyList<ToolDefinition>? _all;

        /// <summary>Tool APIs followed by agents.</summary>
        public static IReadOnlyList<ToolDefinition> All =>
            _all ??= Tools.Concat(AgentCatalog.All.Select(a => a.ToTool())).ToList();

        /// <summary>The tool APIs only (no agents).</summary>
        public static IReadOnlyList<ToolDefinition> Tools =>
            _tools ??= Load(Path.Combine(AppContext.BaseDirectory, DefaultFileName));

        /// <summary>Replaces the tool APIs, e.g. with the file configured for the app. Agents are added from <see cref="AgentCatalog"/>.</summary>
        public static void Use(IReadOnlyList<ToolDefinition> tools)
        {
            _tools = tools;
            _all = null;
        }

        /// <summary>Rebuilds <see cref="All"/>, e.g. after <see cref="AgentCatalog.Use"/>.</summary>
        public static void Refresh() => _all = null;

        /// <summary>Finds a tool by the function name the LLM used (case-insensitive), or null.</summary>
        public static ToolDefinition? FindByFunctionName(string? functionName) =>
            All.FirstOrDefault(t => string.Equals(t.FunctionName, functionName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Finds a tool API by its identifier (e.g. "scanner"), or null. Agents share one identifier, so use <see cref="Find"/> for them.</summary>
        public static ToolDefinition? FindByTool(string? tool) =>
            All.FirstOrDefault(t => !t.IsAgent && string.Equals(t.Tool, tool, StringComparison.OrdinalIgnoreCase));

        /// <summary>Finds the entry for a tool call's tool and action (agents are "agent" plus the agent id), or null.</summary>
        public static ToolDefinition? Find(string? tool, string? action) =>
            All.FirstOrDefault(t => string.Equals(t.Tool, tool, StringComparison.OrdinalIgnoreCase)
                                    && (!t.IsAgent || string.Equals(t.Action, action, StringComparison.OrdinalIgnoreCase)));

        /// <summary>Every distinct parameter across all tools, in first-seen order (used for the JSON-schema decision shape).</summary>
        public static IReadOnlyList<ToolParameter> AllParameters =>
            All.SelectMany(t => t.Parameters).GroupBy(p => p.Name).Select(g => g.First()).ToList();

        /// <summary>Reads tool definitions from a JSON file.</summary>
        /// <exception cref="CatalogException">The file is missing or invalid; the message says what to fix.</exception>
        public static IReadOnlyList<ToolDefinition> Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new CatalogException($"Tool definitions file not found: {path}");
            }

            return Parse(File.ReadAllText(path), path);
        }

        /// <summary>Parses tool definitions (see the comment at the top of tools.json for the format). Every problem is reported together.</summary>
        public static IReadOnlyList<ToolDefinition> Parse(string json, string source = "tools")
        {
            var file = CatalogJson.Deserialize<ToolFile>(json, source);
            var errors = new List<string>();
            var tools = new List<ToolDefinition>();
            var entries = file?.Tools ?? new List<ToolEntry>();

            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var label = string.IsNullOrWhiteSpace(e.FunctionName) ? $"tool #{i + 1}" : $"tool \"{e.FunctionName}\"";
                var before = errors.Count;

                if (string.IsNullOrWhiteSpace(e.FunctionName) || !System.Text.RegularExpressions.Regex.IsMatch(e.FunctionName, "^[A-Za-z0-9_]+$"))
                    errors.Add($"{label}: \"functionName\" is required and may only contain letters, digits and underscores.");
                else if (entries.Take(i).Any(o => string.Equals(o.FunctionName, e.FunctionName, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"{label}: the functionName is used more than once.");
                if (string.IsNullOrWhiteSpace(e.Tool)) errors.Add($"{label}: \"tool\" is required.");
                else if (e.Tool == ToolDefinition.AgentTool) errors.Add($"{label}: \"tool\" can't be \"agent\"; define agents in agents.json.");
                if (string.IsNullOrWhiteSpace(e.Action)) errors.Add($"{label}: \"action\" is required.");
                if (string.IsNullOrWhiteSpace(e.Description)) errors.Add($"{label}: \"description\" is required.");
                if (string.IsNullOrWhiteSpace(e.Confirmation)) errors.Add($"{label}: \"confirmation\" is required.");
                if (e.Classification == null) errors.Add($"{label}: \"classification\" must be Internal or External.");
                if (e.OutputLabel == null) errors.Add($"{label}: \"outputLabel\" must be one of {string.Join(", ", Enum.GetNames<Sensitivity>())}.");
                if (e.OutputTrust == null) errors.Add($"{label}: \"outputTrust\" must be Trusted or Untrusted.");
                if (e.Http == null || string.IsNullOrWhiteSpace(e.Http.Path)) errors.Add($"{label}: \"http.path\" is required.");
                else if (!string.Equals(e.Http.Method, "GET", StringComparison.OrdinalIgnoreCase) && !string.Equals(e.Http.Method, "POST", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{label}: \"http.method\" must be GET or POST.");

                var parameters = new List<ToolParameter>();
                foreach (var p in e.Parameters ?? new List<ParameterEntry>())
                {
                    if (string.IsNullOrWhiteSpace(p.Name) || p.Type == null)
                        errors.Add($"{label}: every parameter needs a \"name\" and a \"type\" (url or text).");
                    else
                        parameters.Add(new ToolParameter(p.Name, p.Type.Value, p.Description ?? "", p.Required ?? false));
                }

                if (e.RequiresScope == true && !parameters.Any(p => p.Type == ToolParameterType.Url))
                    errors.Add($"{label}: \"requiresScope\" needs a url parameter to check.");
                ToolEgress? egress = null;
                if (e.Egress != null)
                {
                    var domains = new List<string>();
                    foreach (var raw in e.Egress.Domains ?? new List<string>())
                    {
                        var problem = EgressCatalog.ValueProblem(EgressMatchType.Domain, raw ?? "", EgressAction.Allow);
                        if (problem != null)
                            errors.Add($"{label}: egress domain {problem}");
                        else
                            domains.Add(EgressHost.Normalise(raw)!);
                    }

                    foreach (var port in (e.Egress.Ports ?? new List<int>()).Where(p => p is < 1 or > 65535))
                        errors.Add($"{label}: egress port {port} isn't a valid port.");
                    egress = new ToolEgress(domains, e.Egress.Ports, e.Egress.EngagementScope ?? (e.RequiresScope ?? false));
                }

                foreach (var name in ToolHttpBinding.Placeholders(e.Http?.Path ?? "").Concat(ToolHttpBinding.Placeholders(e.Confirmation ?? ""))
                             .Where(name => parameters.All(p => p.Name != name)).Distinct())
                    errors.Add($"{label}: {{{name}}} doesn't match a parameter.");

                if (errors.Count == before)
                {
                    tools.Add(new ToolDefinition(e.FunctionName!, e.Tool!, e.Action!, e.Description!, e.RequiresApproval ?? false, parameters,
                        e.Confirmation!, e.ConfirmationWithoutOptional, e.Classification!.Value, e.OutputLabel!.Value, e.OutputTrust!.Value,
                        e.RequiresScope ?? false, new ToolHttpBinding(e.Http!.Method!.ToUpperInvariant(), e.Http.Path!), e.Keywords ?? new List<string>(),
                        egress));
                }
            }

            CatalogJson.ThrowIfAny(errors, source);
            return tools;
        }

        // The file's shape. Everything is nullable so missing values are reported, not defaulted.
        private sealed class ToolFile
        {
            public List<ToolEntry>? Tools { get; set; }
        }

        private sealed class ToolEntry
        {
            public string? FunctionName { get; set; }
            public string? Tool { get; set; }
            public string? Action { get; set; }
            public string? Description { get; set; }
            public bool? RequiresApproval { get; set; }
            public bool? RequiresScope { get; set; }
            public List<ParameterEntry>? Parameters { get; set; }
            public string? Confirmation { get; set; }
            public string? ConfirmationWithoutOptional { get; set; }
            public HttpEntry? Http { get; set; }
            public Classification? Classification { get; set; }
            public Sensitivity? OutputLabel { get; set; }
            public Trust? OutputTrust { get; set; }
            public List<string>? Keywords { get; set; }
            public EgressEntry? Egress { get; set; }
        }

        private sealed class EgressEntry
        {
            public List<string>? Domains { get; set; }
            public List<int>? Ports { get; set; }
            public bool? EngagementScope { get; set; }
        }

        private sealed class ParameterEntry
        {
            public string? Name { get; set; }
            public ToolParameterType? Type { get; set; }
            public string? Description { get; set; }
            public bool? Required { get; set; }
        }

        private sealed class HttpEntry
        {
            public string? Method { get; set; }
            public string? Path { get; set; }
        }
    }

    /// <summary>A definitions file (tools, agents) is missing or invalid.</summary>
    public sealed class CatalogException : Exception
    {
        public CatalogException(string message) : base(message) { }
    }

    /// <summary>Shared JSON reading and error reporting for the definition files.</summary>
    internal static class CatalogJson
    {
        internal static readonly System.Text.Json.JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        };

        internal static T? Deserialize<T>(string json, string source)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new CatalogException($"{source} is not valid JSON for this file: {ex.Message}");
            }
        }

        internal static void ThrowIfAny(List<string> errors, string source)
        {
            if (errors.Count > 0)
            {
                throw new CatalogException($"{source} has {errors.Count} problem{(errors.Count == 1 ? "" : "s")}:\n- {string.Join("\n- ", errors)}");
            }
        }
    }
}
