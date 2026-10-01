using Microsoft.Extensions.Options;
using ScanHarness.Services.Agents;
using ScanHarness.Services.Llm;
using ScanHarness.Services.Persistence;

namespace ScanHarness.Services.Boundaries
{
    /// <summary>What kind of component a policy is for.</summary>
    public enum ComponentKind { Brain, Tool, Agent }

    /// <summary>
    /// The effective trust settings of one brain, tool or agent: where it runs, what it may receive, and how
    /// its output is treated.
    /// </summary>
    /// <param name="Id">Policy key: "brain:&lt;id&gt;", "tool:&lt;function name&gt;" or "agent:&lt;id&gt;".</param>
    /// <param name="Kind">Brain, tool or agent.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Classification">Internal or external hosting.</param>
    /// <param name="Clearance">Most sensitive data it may receive.</param>
    /// <param name="OutputLabel">How sensitive its output is.</param>
    /// <param name="OutputTrust">Whether its output can drive actions directly.</param>
    /// <param name="Enabled">Disabled tools and agents are blocked before they run.</param>
    /// <param name="Brain">For agents, the brain that runs it.</param>
    /// <param name="IsEdited">True when an edit from the Permissions tab is in effect.</param>
    public sealed record ComponentPolicy(
        string Id,
        ComponentKind Kind,
        string Name,
        Classification Classification,
        Sensitivity Clearance,
        Sensitivity OutputLabel,
        Trust OutputTrust,
        bool Enabled,
        string? Brain,
        bool IsEdited)
    {
        /// <summary>Whether data with <paramref name="label"/> may be given to this component as it is.</summary>
        public bool IsClearedFor(Sensitivity label) => label <= Clearance;
    }

    /// <summary>An edit to a component's policy; null fields keep the default.</summary>
    public sealed record PolicyOverride(
        Classification? Classification = null,
        Sensitivity? Clearance = null,
        Sensitivity? OutputLabel = null,
        Trust? OutputTrust = null,
        bool? Enabled = null,
        string? Brain = null);

    /// <summary>
    /// Works out every component's effective policy from three layers, later ones winning: the definition
    /// (tools.json, agents.json, the brain's endpoint), config (<c>Tools:Classifications</c>, <c>Llm:*Classification*</c>),
    /// and edits made on the Permissions tab, which are saved for everyone. Singleton, so an edit applies to
    /// every user at once.
    /// </summary>
    public sealed class ComponentPolicyService
    {
        private readonly IActivityStore? _store;
        private readonly LlmOptions _llm;
        private readonly object _gate = new();
        private Dictionary<string, PolicyOverride>? _edits;
        private IReadOnlyDictionary<string, Classification> _toolClassifications = new Dictionary<string, Classification>();

        /// <param name="store">Where edits are saved; null keeps them in memory (e.g. in tests).</param>
        public ComponentPolicyService(IActivityStore? store = null, IOptions<LlmOptions>? llm = null)
        {
            _store = store;
            _llm = llm?.Value ?? new LlmOptions();
        }

        /// <summary>Raised when an edit changes any policy.</summary>
        public event Action? OnChanged;

        /// <summary>Config overrides of tool classifications, by function name (<c>Tools:Classifications</c>).</summary>
        public void UseToolClassifications(IReadOnlyDictionary<string, Classification> overrides) =>
            _toolClassifications = new Dictionary<string, Classification>(overrides, StringComparer.OrdinalIgnoreCase);

        public static string BrainKey(string brainId) => $"brain:{brainId}";

        /// <summary>The policy of a tool or agent entry in the tool catalog.</summary>
        public ComponentPolicy ForTool(ToolDefinition tool)
        {
            if (tool.IsAgent && AgentCatalog.Find(tool.Action) is AgentDefinition agent)
            {
                return ForAgent(agent);
            }

            var edit = Edit(tool.PolicyId);
            var classification = edit?.Classification
                ?? (_toolClassifications.TryGetValue(tool.FunctionName, out var configured) ? configured : tool.Classification);
            return new ComponentPolicy(tool.PolicyId, ComponentKind.Tool, tool.FunctionName, classification,
                edit?.Clearance ?? classification.DefaultClearance(), edit?.OutputLabel ?? tool.OutputLabel,
                edit?.OutputTrust ?? tool.OutputTrust, edit?.Enabled ?? true, null, edit != null);
        }

        /// <summary>The policy of a tool call's tool, or null for a tool that isn't in the catalog.</summary>
        public ComponentPolicy? ForTool(string? tool, string? action) =>
            ToolCatalog.Find(tool, action) is ToolDefinition definition ? ForTool(definition) : null;

        /// <summary>An agent's policy. Its classification follows the brain that runs it unless edited.</summary>
        public ComponentPolicy ForAgent(AgentDefinition agent)
        {
            var key = $"agent:{agent.Id}";
            var edit = Edit(key);
            var brain = edit?.Brain ?? agent.Brain;
            var classification = edit?.Classification ?? ClassificationOfBrain(brain);
            return new ComponentPolicy(key, ComponentKind.Agent, agent.Name, classification,
                edit?.Clearance ?? agent.Clearance ?? classification.DefaultClearance(), edit?.OutputLabel ?? agent.OutputLabel,
                edit?.OutputTrust ?? agent.OutputTrust, edit?.Enabled ?? true, brain, edit != null);
        }

        /// <summary>
        /// A brain's policy. Rule-based brains run in the harness, so they're internal and trusted; LLM brains
        /// take their classification from the endpoint config and their output is untrusted.
        /// </summary>
        public ComponentPolicy ForBrain(string brainId, string? name = null)
        {
            var key = BrainKey(brainId);
            var edit = Edit(key);
            var ruleBased = IsRuleBased(brainId);
            var classification = edit?.Classification ?? DefaultBrainClassification(brainId);
            return new ComponentPolicy(key, ComponentKind.Brain, name ?? brainId, classification,
                edit?.Clearance ?? classification.DefaultClearance(), edit?.OutputLabel ?? Sensitivity.CustomerConfidential,
                edit?.OutputTrust ?? (ruleBased ? Trust.Trusted : Trust.Untrusted), true, null, edit != null);
        }

        /// <summary>Where a brain runs, after edits.</summary>
        public Classification ClassificationOfBrain(string brainId) =>
            Edit(BrainKey(brainId))?.Classification ?? DefaultBrainClassification(brainId);

        public static bool IsRuleBased(string brainId) =>
            brainId is AgentBrainSelector.KeywordBrainId or AgentDefinition.RuleBasedBrain;

        private Classification DefaultBrainClassification(string brainId) =>
            IsRuleBased(brainId) ? Classification.Internal : _llm.ClassificationFor(brainId);

        /// <summary>Applies an edit on top of any earlier one for the same component, and saves it.</summary>
        public void Update(string id, PolicyOverride change, string changedBy)
        {
            lock (_gate)
            {
                var current = Edits().TryGetValue(id, out var existing) ? existing : new PolicyOverride();
                var merged = new PolicyOverride(
                    change.Classification ?? current.Classification,
                    change.Clearance ?? current.Clearance,
                    change.OutputLabel ?? current.OutputLabel,
                    change.OutputTrust ?? current.OutputTrust,
                    change.Enabled ?? current.Enabled,
                    change.Brain ?? current.Brain);
                Edits()[id] = merged;
                _store?.SaveComponentPolicy(id, merged, changedBy, DateTime.UtcNow);
            }

            OnChanged?.Invoke();
        }

        /// <summary>Removes the edits for one component (null: every component), restoring the defaults.</summary>
        public void Reset(string? id)
        {
            lock (_gate)
            {
                if (id == null)
                    Edits().Clear();
                else
                    Edits().Remove(id);
                _store?.DeleteComponentPolicies(id);
            }

            OnChanged?.Invoke();
        }

        private PolicyOverride? Edit(string id)
        {
            lock (_gate)
            {
                return Edits().TryGetValue(id, out var edit) ? edit : null;
            }
        }

        // Loaded from the store the first time a policy is needed.
        private Dictionary<string, PolicyOverride> Edits() =>
            _edits ??= _store?.LoadComponentPolicies().ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, PolicyOverride>();
    }
}
