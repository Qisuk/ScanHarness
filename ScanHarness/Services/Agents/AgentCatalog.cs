using System.Text.Json.Serialization;
using ScanHarness.Services.Boundaries;

namespace ScanHarness.Services.Agents
{
    /// <summary>Which agent implementation runs, and so which input and output contract it has.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AgentKind
    {
        /// <summary>Input: <see cref="FindingSet"/> (the session's results). Output: <see cref="Recommendations"/>.</summary>
        FindingsAnalyst,

        /// <summary>Input: <see cref="EngagementSummary"/> (no hostnames). Output: <see cref="CostEstimate"/>.</summary>
        CostEstimator,

        /// <summary>Input: the customer conversation. Output: <see cref="ScopeProposal"/>. Quarantined on the Scoping page.</summary>
        Scoping
    }

    /// <summary>An agent: a brain with one job, a fixed contract and its own trust settings.</summary>
    /// <param name="Id">Stable identifier; also the tool call's action and the policy key ("agent:&lt;id&gt;").</param>
    /// <param name="FunctionName">Name the orchestrator's brain uses to call it.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Kind">Implementation and contract.</param>
    /// <param name="Description">Shown to the orchestrator's brain so it can decide when to call the agent.</param>
    /// <param name="Brain"><see cref="RuleBasedBrain"/> or an LLM model id; can be changed on the Permissions tab.</param>
    /// <param name="Clearance">Most sensitive data it may receive; null uses the default for its brain's classification.</param>
    /// <param name="OutputTrust">Whether its output can drive actions directly.</param>
    /// <param name="RequiresApproval">Whether the user must approve the call.</param>
    /// <param name="OfferedToOrchestrator">Whether the harness's decision brain can call it as a tool.</param>
    /// <param name="Confirmation">Message shown when the agent is chosen.</param>
    /// <param name="Keywords">Words the keyword brain matches to choose it.</param>
    public sealed record AgentDefinition(
        string Id,
        string FunctionName,
        string Name,
        AgentKind Kind,
        string Description,
        string Brain,
        Sensitivity? Clearance,
        Trust OutputTrust,
        bool RequiresApproval,
        bool OfferedToOrchestrator,
        string Confirmation,
        IReadOnlyList<string> Keywords)
    {
        /// <summary>Brain id for the built-in rule-based implementation of each agent.</summary>
        public const string RuleBasedBrain = "rule-based";

        /// <summary>How sensitive the agent's output is: recommendations name hosts, prices and scope are confidential.</summary>
        public Sensitivity OutputLabel => Sensitivity.CustomerConfidential;

        /// <summary>What the agent receives, for the UI.</summary>
        public string InputContract => Kind switch
        {
            AgentKind.FindingsAnalyst => "FindingSet: each result's target, tool, severity and findings",
            AgentKind.CostEstimator => "EngagementSummary: host count, findings by severity, tests run (no hostnames)",
            AgentKind.Scoping => "The customer conversation",
            _ => Kind.ToString()
        };

        /// <summary>The label of the data the agent's contract carries, before any pseudonymisation.</summary>
        public Sensitivity InputLabel => Kind == AgentKind.CostEstimator ? Sensitivity.Engagement : Sensitivity.CustomerConfidential;

        /// <summary>The agent as an entry in the tool catalog.</summary>
        public ToolDefinition ToTool() => new(
            FunctionName, ToolDefinition.AgentTool, Id, Description, RequiresApproval, Array.Empty<ToolParameter>(), Confirmation,
            OutputLabel: OutputLabel, OutputTrust: OutputTrust, Keywords: Keywords);
    }

    /// <summary>
    /// Every agent, read from <c>agents.json</c>. Agents offered to the orchestrator appear in
    /// <see cref="ToolCatalog.All"/> as tools. The app loads the configured file at startup with <see cref="Use"/>;
    /// until then (e.g. in unit tests) the file next to the app is used.
    /// </summary>
    public static class AgentCatalog
    {
        public const string DefaultFileName = "agents.json";

        private static IReadOnlyList<AgentDefinition>? _defined;

        /// <summary>Every agent, including ones only used on their own page.</summary>
        public static IReadOnlyList<AgentDefinition> Defined =>
            _defined ??= Load(Path.Combine(AppContext.BaseDirectory, DefaultFileName));

        /// <summary>Agents the orchestrator's brain may call as tools.</summary>
        public static IReadOnlyList<AgentDefinition> All => Defined.Where(a => a.OfferedToOrchestrator).ToList();

        public static void Use(IReadOnlyList<AgentDefinition> agents)
        {
            _defined = agents;
            ToolCatalog.Refresh();
        }

        public static AgentDefinition? Find(string? id) => Defined.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The first agent of a kind (e.g. the scoping agent for the Scoping page), or null.</summary>
        public static AgentDefinition? FirstOfKind(AgentKind kind) => Defined.FirstOrDefault(a => a.Kind == kind);

        public static IReadOnlyList<AgentDefinition> Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new CatalogException($"Agent definitions file not found: {path}");
            }

            return Parse(File.ReadAllText(path), path);
        }

        /// <summary>Parses agent definitions (see the comment at the top of agents.json). Every problem is reported together.</summary>
        public static IReadOnlyList<AgentDefinition> Parse(string json, string source = "agents")
        {
            var file = CatalogJson.Deserialize<AgentFile>(json, source);
            var errors = new List<string>();
            var agents = new List<AgentDefinition>();
            var entries = file?.Agents ?? new List<AgentEntry>();

            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var label = string.IsNullOrWhiteSpace(e.Id) ? $"agent #{i + 1}" : $"agent \"{e.Id}\"";
                var before = errors.Count;

                if (string.IsNullOrWhiteSpace(e.Id)) errors.Add($"{label}: \"id\" is required.");
                else if (entries.Take(i).Any(o => string.Equals(o.Id, e.Id, StringComparison.OrdinalIgnoreCase))) errors.Add($"{label}: the id is used more than once.");
                if (string.IsNullOrWhiteSpace(e.FunctionName) || !System.Text.RegularExpressions.Regex.IsMatch(e.FunctionName, "^[A-Za-z0-9_]+$"))
                    errors.Add($"{label}: \"functionName\" is required and may only contain letters, digits and underscores.");
                else if (entries.Take(i).Any(o => string.Equals(o.FunctionName, e.FunctionName, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"{label}: the functionName is used more than once.");
                if (string.IsNullOrWhiteSpace(e.Name)) errors.Add($"{label}: \"name\" is required.");
                if (e.Kind == null) errors.Add($"{label}: \"kind\" must be one of {string.Join(", ", Enum.GetNames<AgentKind>())}.");
                if (string.IsNullOrWhiteSpace(e.Description)) errors.Add($"{label}: \"description\" is required.");
                if (string.IsNullOrWhiteSpace(e.Confirmation)) errors.Add($"{label}: \"confirmation\" is required.");
                if (e.OutputTrust == null) errors.Add($"{label}: \"outputTrust\" must be Trusted or Untrusted.");
                if (e.Kind == AgentKind.Scoping && e.OfferedToOrchestrator == true)
                    errors.Add($"{label}: a Scoping agent talks to customers, so it can't be offered to the orchestrator.");

                if (errors.Count == before)
                {
                    agents.Add(new AgentDefinition(e.Id!, e.FunctionName!, e.Name!, e.Kind!.Value, e.Description!,
                        string.IsNullOrWhiteSpace(e.Brain) ? AgentDefinition.RuleBasedBrain : e.Brain.Trim(),
                        e.Clearance, e.OutputTrust!.Value, e.RequiresApproval ?? false, e.OfferedToOrchestrator ?? true,
                        e.Confirmation!, e.Keywords ?? new List<string>()));
                }
            }

            // Agents become tools, so their function names must not clash with the tool APIs'.
            foreach (var clash in agents.Where(a => ToolCatalog.Tools.Any(t => string.Equals(t.FunctionName, a.FunctionName, StringComparison.OrdinalIgnoreCase))))
            {
                errors.Add($"agent \"{clash.Id}\": functionName \"{clash.FunctionName}\" is already used by a tool in tools.json.");
            }

            CatalogJson.ThrowIfAny(errors, source);
            return agents;
        }

        private sealed class AgentFile
        {
            public List<AgentEntry>? Agents { get; set; }
        }

        private sealed class AgentEntry
        {
            public string? Id { get; set; }
            public string? FunctionName { get; set; }
            public string? Name { get; set; }
            public AgentKind? Kind { get; set; }
            public string? Description { get; set; }
            public string? Brain { get; set; }
            public Sensitivity? Clearance { get; set; }
            public Trust? OutputTrust { get; set; }
            public bool? RequiresApproval { get; set; }
            public bool? OfferedToOrchestrator { get; set; }
            public string? Confirmation { get; set; }
            public List<string>? Keywords { get; set; }
        }
    }
}
