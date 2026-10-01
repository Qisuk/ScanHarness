using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ScanHarness.Models;
using ScanHarness.Services.Boundaries;
using ScanHarness.Services.Llm;

namespace ScanHarness.Services.Agents
{
    /// <summary>Settings for agents (config section "Agents").</summary>
    public class AgentOptions
    {
        public const string SectionName = "Agents";

        /// <summary>Agent definitions file, relative to the content root.</summary>
        public string DefinitionsPath { get; set; } = AgentCatalog.DefaultFileName;

        /// <summary>How deep calls may chain (the orchestrator calling an agent is depth 1).</summary>
        public int MaxDepth { get; set; } = 2;

        /// <summary>Longest an agent's brain may take before the rule-based answer is used.</summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>Day rate used by the cost estimator.</summary>
        public decimal DayRate { get; set; } = 1200m;

        public string Currency { get; set; } = "GBP";
    }

    /// <summary>The scoping agent's answer to one customer message.</summary>
    /// <param name="Reply">What the agent says back.</param>
    /// <param name="Proposal">The scope as understood so far (untrusted until validated and approved).</param>
    /// <param name="Brain">The brain that answered.</param>
    /// <param name="FallbackReason">Set when an LLM brain failed and the rule-based answer was used.</param>
    public sealed record ScopingTurn(string Reply, ScopeProposal Proposal, string Brain, string? FallbackReason);

    /// <summary>Raised when the boundary rules stop an agent running (not cleared, disabled, too deep).</summary>
    public sealed class AgentBlockedException : Exception
    {
        public AgentBlockedException(string message) : base(message) { }
    }

    /// <summary>
    /// Runs agents. Each agent's input is built by code from stored records (never from model output), offered
    /// to it through the <see cref="BoundaryBroker"/> (so an agent not cleared for the data gets placeholders or
    /// nothing), and its output is checked against the input and has placeholders mapped back.
    /// </summary>
    public class AgentRunner
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

        private readonly BoundaryBroker _broker;
        private readonly OpenAiCompatibleClient? _llm;
        private readonly AgentOptions _options;

        public AgentRunner(BoundaryBroker broker, OpenAiCompatibleClient? llm = null, IOptions<AgentOptions>? options = null)
        {
            _broker = broker;
            _llm = llm;
            _options = options?.Value ?? new AgentOptions();
        }

        /// <summary>
        /// Runs an agent the orchestrator chose, over the session's tool calls. Returns the result as JSON for
        /// the tool call: status, agent, brain, the input handoff, a code-built summary and the structured output.
        /// </summary>
        /// <exception cref="AgentBlockedException">The agent is unknown, disabled, too deep, or not cleared for its input.</exception>
        public async Task<string> RunAsync(string agentId, IReadOnlyList<ToolCall> sessionCalls, string toolCallId, int depth = 1, CancellationToken cancellationToken = default)
        {
            var agent = AgentCatalog.Find(agentId) ?? throw new AgentBlockedException($"There is no agent '{agentId}'.");
            if (depth > _options.MaxDepth)
                throw new AgentBlockedException($"Call depth {depth} is over the limit of {_options.MaxDepth}.");

            var policy = _broker.Policies.ForAgent(agent);
            if (!policy.Enabled)
                throw new AgentBlockedException($"{agent.Name} is disabled on the Permissions tab.");

            var findings = FindingSet.From(sessionCalls.Where(c => c.Id != toolCallId));
            object input = agent.Kind == AgentKind.CostEstimator ? EngagementSummary.From(findings) : findings;
            var shared = _broker.Share("harness", policy, agent.InputLabel, JsonSerializer.Serialize(input, input.GetType(), Json),
                agent.Kind == AgentKind.CostEstimator ? nameof(EngagementSummary) : nameof(FindingSet), toolCallId);
            if (shared.Outcome == ShareOutcome.Blocked)
                throw new AgentBlockedException($"{agent.Name} isn't cleared for {agent.InputLabel.Label()} data, so it wasn't run.");

            var brain = policy.Brain ?? AgentDefinition.RuleBasedBrain;
            string? fallback = null;
            object output;
            string summary;

            switch (agent.Kind)
            {
                case AgentKind.FindingsAnalyst:
                {
                    var analysis = FindingsAnalyst.Analyse(findings);
                    if (!ComponentPolicyService.IsRuleBased(brain))
                    {
                        (var items, fallback) = await TryLlmAsync(brain, FindingsAnalyst.LlmInstructions, shared.Text!, "recommendations",
                            FindingsAnalyst.Schema(), answer =>
                                FindingsAnalyst.Validate(answer, JsonSerializer.Deserialize<FindingSet>(shared.Text!, Json)!)
                                    ?.Select(r => r with { Finding = _broker.Restore(r.Finding), Target = _broker.Restore(r.Target), Action = _broker.Restore(r.Action) })
                                    .ToList(),
                            new ReasoningContext(ReasoningStages.Agent(agent.Name), agent.Name, policy.Classification, toolCallId), cancellationToken);
                        if (items != null)
                            analysis = new Recommendations(items, FindingsAnalyst.Summarise(items, findings));
                    }

                    output = analysis;
                    summary = analysis.Summary;
                    break;
                }

                case AgentKind.CostEstimator:
                {
                    var estimate = CostEstimator.Calculate((EngagementSummary)input, _options.DayRate, _options.Currency);
                    if (!ComponentPolicyService.IsRuleBased(brain))
                    {
                        var context = JsonSerializer.Serialize(new { summary = input, estimate = estimate with { Narrative = "" } }, Json);
                        (var narrative, fallback) = await TryLlmAsync(brain, CostEstimator.LlmInstructions, context, "cost_narrative",
                            CostEstimator.Schema(), answer => AgentLlm.Text(answer["narrative"]) is { Length: > 0 } text ? text : null,
                            new ReasoningContext(ReasoningStages.Agent(agent.Name), agent.Name, policy.Classification, toolCallId), cancellationToken);
                        if (narrative != null)
                            estimate = estimate with { Narrative = narrative };
                    }

                    output = estimate;
                    summary = $"{estimate.Narrative} Total: {CostEstimator.Days(estimate.Days)} at {CostEstimator.Money(estimate.DayRate, estimate.Currency)} a day = {CostEstimator.Money(estimate.Total, estimate.Currency)}.";
                    break;
                }

                default:
                    throw new AgentBlockedException($"{agent.Name} works on its own page and can't be called as a tool.");
            }

            var result = new JsonObject
            {
                ["status"] = "success",
                ["agent"] = agent.Id,
                ["agentName"] = agent.Name,
                ["brain"] = brain,
                ["outputTrust"] = policy.OutputTrust.ToString(),
                ["input"] = new JsonObject
                {
                    ["contract"] = shared.Handoff.What,
                    ["label"] = agent.InputLabel.ToString(),
                    ["shared"] = shared.Outcome.ToString(),
                    ["placeholders"] = shared.Handoff.Placeholders,
                },
                ["summary"] = summary,
                ["fallbackReason"] = fallback,
                ["output"] = JsonSerializer.SerializeToNode(output, output.GetType(), Json),
            };
            return result.ToJsonString();
        }

        /// <summary>
        /// The scoping agent's reply to the conversation so far. The customer's words are offered to the agent
        /// through the broker like any other data; its answer is untrusted and only becomes an engagement after
        /// validation and approval.
        /// </summary>
        public async Task<ScopingTurn> ContinueScopingAsync(IReadOnlyList<ScopingMessage> conversation, CancellationToken cancellationToken = default)
        {
            var agent = AgentCatalog.FirstOfKind(AgentKind.Scoping) ?? throw new AgentBlockedException("No scoping agent is defined in agents.json.");
            var policy = _broker.Policies.ForAgent(agent);
            if (!policy.Enabled)
                throw new AgentBlockedException($"{agent.Name} is disabled on the Permissions tab.");

            var proposal = ScopingAgent.Extract(conversation);
            var brain = policy.Brain ?? AgentDefinition.RuleBasedBrain;
            if (ComponentPolicyService.IsRuleBased(brain))
            {
                return new ScopingTurn(ScopingAgent.Reply(proposal), proposal, brain, null);
            }

            var transcript = string.Join("\n", conversation.Select(m => $"{(m.FromCustomer ? "Customer" : "Agent")}: {m.Text}"));
            var shared = _broker.Share("customer", policy, Sensitivity.CustomerConfidential, transcript, "customer conversation");
            if (shared.Outcome == ShareOutcome.Blocked)
            {
                return new ScopingTurn(ScopingAgent.Reply(proposal), proposal, AgentDefinition.RuleBasedBrain, $"{agent.Name} isn't cleared for the conversation");
            }

            var (answer, fallback) = await TryLlmAsync(brain, ScopingAgent.LlmInstructions, shared.Text!, "scope_proposal", ScopingAgent.Schema(),
                a => (object?)ScopingAgent.Read(a),
                new ReasoningContext(ReasoningStages.Scoping, agent.Name, policy.Classification, null, ChatTranscript.Targets.ScopingMessage), cancellationToken);
            if (answer is not ValueTuple<ScopeProposal, string> read)
            {
                return new ScopingTurn(ScopingAgent.Reply(proposal), proposal, AgentDefinition.RuleBasedBrain, fallback);
            }

            var (llmProposal, reply) = read;
            var restored = new ScopeProposal(
                _broker.Restore(llmProposal.Customer),
                llmProposal.Domains.Select(_broker.Restore).ToList(),
                llmProposal.WindowStart,
                llmProposal.WindowEnd,
                llmProposal.Rules.Select(_broker.Restore).ToList());
            return new ScopingTurn(reply.Length > 0 ? _broker.Restore(reply) : ScopingAgent.Reply(restored), restored, brain, null);
        }

        // Asks the agent's LLM and parses the answer; any failure gives null and the reason, and the caller keeps the rule-based answer.
        /// <summary>Where an agent's reasoning is logged: its stage, the agent's name and hosting, and the tool call it belongs to.</summary>
        private sealed record ReasoningContext(string Stage, string AgentName, Classification Classification, string? ToolCallId,
            string Target = ChatTranscript.Targets.Reasoning);

        private async Task<(T? Value, string? FallbackReason)> TryLlmAsync<T>(string model, string system, string user, string schemaName,
            JsonObject schema, Func<JsonObject, T?> read, ReasoningContext reasoning, CancellationToken cancellationToken)
        {
            if (_llm == null)
                return (default, "no LLM endpoint is configured");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            try
            {
                var (answer, thinking) = await AgentLlm.AskJsonAsync(_llm, model, system, user, schemaName, schema, timeout.Token);

                // Logged whatever happens next, with placeholders mapped back, so the agent's step can be drilled into.
                _broker.RecordReasoning(reasoning.Stage, $"{reasoning.AgentName} ({model})", reasoning.Classification,
                    thinking == null ? null : _broker.Restore(thinking), reasoning.ToolCallId, reasoning.Target);

                if (answer == null)
                    return (default, "the model didn't return a JSON object");
                var value = read(answer);
                return value == null ? (default, "the model's answer didn't match the input") : (value, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmException or JsonException or InvalidOperationException)
            {
                return (default, ex is TaskCanceledException ? "the request timed out" : ex.Message);
            }
        }
    }
}
