using System.Text.Json;
using ScanHarness.Models;
using ScanHarness.Services.Controls;
using ScanHarness.Services.Engagements;

namespace ScanHarness.Services.Boundaries
{
    /// <summary>What happened to data at a boundary.</summary>
    public enum ShareOutcome
    {
        /// <summary>The receiver is cleared for the data; it was passed as it is.</summary>
        Allowed,

        /// <summary>The receiver isn't cleared, so identifying values were swapped for placeholders first.</summary>
        Pseudonymised,

        /// <summary>The receiver isn't cleared and placeholders wouldn't be enough, so nothing was passed.</summary>
        Blocked
    }

    /// <summary>A record of data crossing from one component to another, logged for the audit trail.</summary>
    /// <param name="From">Who sent it, e.g. "harness" or "tool:simple_scanner".</param>
    /// <param name="To">Who received it (a policy id).</param>
    /// <param name="ToName">The receiver's display name.</param>
    /// <param name="ToClassification">Where the receiver runs.</param>
    /// <param name="Label">How sensitive the data was.</param>
    /// <param name="Clearance">The receiver's clearance.</param>
    /// <param name="Outcome">Allowed, pseudonymised or blocked.</param>
    /// <param name="Placeholders">Distinct values replaced, when pseudonymised.</param>
    /// <param name="What">What the data was, e.g. "tool result" or "FindingSet".</param>
    public sealed record Handoff(string From, string To, string ToName, Classification ToClassification, Sensitivity Label,
        Sensitivity Clearance, ShareOutcome Outcome, int Placeholders, string What)
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        /// <summary>One-line description for the activity feed.</summary>
        public string Describe() =>
            $"{What} ({Label.Label()}) → {ToName} ({ToClassification.Label()}, cleared for {Clearance.Label()}): " + Outcome switch
            {
                ShareOutcome.Allowed => "shared",
                ShareOutcome.Pseudonymised => $"shared with {Placeholders} value{(Placeholders == 1 ? "" : "s")} replaced by placeholders",
                _ => "blocked, not shared"
            };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        /// <summary>Reads a handoff logged with <see cref="ToJson"/>, or null if the data isn't one.</summary>
        public static Handoff? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                return JsonSerializer.Deserialize<Handoff>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>The result of offering data to a component.</summary>
    /// <param name="Outcome">What happened.</param>
    /// <param name="Text">The data as the receiver may see it (placeholders in place), or null when blocked.</param>
    /// <param name="Handoff">The audit record, already logged.</param>
    public sealed record Shared(ShareOutcome Outcome, string? Text, Handoff Handoff);

    /// <summary>
    /// The single point every boundary crossing goes through (see docs/design/multi-agent-boundaries.md):
    /// clearance checks with pseudonymisation, the ToolRule and ToolOut checks (controls, disabled components
    /// and the engagement scope), and the handoff audit trail. One per Blazor circuit, so each user's
    /// placeholders stay stable across their conversation.
    /// </summary>
    public class BoundaryBroker
    {
        /// <summary>Control id used for the built-in engagement scope check.</summary>
        public const string ScopeControlId = "builtin:engagement-scope";

        /// <summary>Control id used when a disabled tool or agent is chosen.</summary>
        public const string DisabledControlId = "builtin:component-disabled";

        private readonly ComponentPolicyService _policies;
        private readonly ControlService _controls;
        private readonly LogService _log;
        private readonly EngagementService? _engagements;

        public BoundaryBroker(ComponentPolicyService policies, ControlService controls, LogService log, EngagementService? engagements = null)
        {
            _policies = policies;
            _controls = controls;
            _log = log;
            _engagements = engagements;
        }

        /// <summary>This circuit's placeholders.</summary>
        public PseudonymMap Pseudonyms { get; } = new();

        public ComponentPolicyService Policies => _policies;

        /// <summary>
        /// What may happen to data with <paramref name="label"/> offered to <paramref name="to"/>: allowed if it's
        /// cleared, pseudonymised if placeholders bring customer-confidential data down to its clearance, otherwise blocked.
        /// </summary>
        public static ShareOutcome Decide(Sensitivity label, ComponentPolicy to)
        {
            if (to.IsClearedFor(label))
                return ShareOutcome.Allowed;
            if (label == Sensitivity.CustomerConfidential && to.IsClearedFor(Sensitivity.Engagement))
                return ShareOutcome.Pseudonymised;
            return ShareOutcome.Blocked;
        }

        /// <summary>Offers <paramref name="text"/> to a component, applying its clearance, and logs the handoff.</summary>
        /// <param name="from">The sender, for the audit trail.</param>
        /// <param name="what">What the data is, e.g. "tool result".</param>
        public Shared Share(string from, ComponentPolicy to, Sensitivity label, string text, string what, string? toolCallId = null)
        {
            var outcome = Decide(label, to);
            var before = Pseudonyms.Count;
            string? shared = outcome switch
            {
                ShareOutcome.Allowed => text,
                ShareOutcome.Pseudonymised => Pseudonyms.Pseudonymise(text),
                _ => null
            };
            var placeholders = outcome == ShareOutcome.Pseudonymised ? CountPlaceholders(shared!) : 0;

            var handoff = new Handoff(from, to.Id, to.Name, to.Classification, label, to.Clearance, outcome, placeholders, what);
            LogHandoff(handoff, toolCallId);
            return new Shared(outcome, shared, handoff);
        }

        /// <summary>Puts original values back in place of this circuit's placeholders.</summary>
        public string Restore(string text) => Pseudonyms.Restore(text);

        /// <summary>Logs a handoff: Block level when blocked (it counts as a block in the session), otherwise Info.</summary>
        public void LogHandoff(Handoff handoff, string? toolCallId = null)
        {
            var data = new Dictionary<string, object> { ["handoff"] = handoff.ToJson() };
            var message = $"Handoff: {handoff.Describe()}";
            if (handoff.Outcome == ShareOutcome.Blocked)
                _log.LogBlock(message, toolCallId, data, brain: handoff.ToName);
            else
                _log.LogInfo(message, toolCallId, data, brain: handoff.ToName);
        }

        /// <summary>
        /// Everything that runs before a chosen tool or agent executes: ToolRule controls (scoped by its
        /// classification), whether it is enabled, and the engagement scope for tools that require one.
        /// </summary>
        public ControlCheck CheckToolIn(ToolDecision decision, string description)
        {
            var definition = ToolCatalog.Find(decision.Tool, decision.Action);
            var policy = definition == null ? null : _policies.ForTool(definition);
            var check = _controls.Check(ControlStage.ToolRule, ChatTranscript.Targets.ToolCall, description, policy?.Classification);
            var violations = check.Violations.ToList();

            if (policy is { Enabled: false })
            {
                violations.Add(Builtin(DisabledControlId, "Component disabled", $"{policy.Name} is disabled on the Permissions tab.", description, policy.Classification));
            }

            if (definition is { RequiresScope: true } && _engagements != null)
            {
                decision.Parameters.TryGetValue("url", out var url);
                var reason = _engagements.ScopeViolation(url);
                if (reason != null)
                {
                    violations.Add(Builtin(ScopeControlId, "Engagement scope", reason, description, policy?.Classification));
                }
            }

            return violations.Count == 0 ? ControlCheck.Passed : new ControlCheck(violations);
        }

        /// <summary>
        /// ToolOut controls on a finished call's result, before any brain reads it. Scoped by the brain that
        /// would receive it (null checks every control).
        /// </summary>
        public ControlCheck CheckToolOut(ToolCall call, Classification? receiver) =>
            call.Status == ToolCallStatus.Completed
                ? _controls.Check(ControlStage.ToolOut, ChatTranscript.Targets.ToolOutput, call.Result, receiver)
                : ControlCheck.Passed;

        /// <summary>
        /// Summarises a finished call with <paramref name="brain"/>, keeping to the boundary rules: a blocked
        /// ToolOut check or a brain not cleared for the result gets the rule-based summary instead, and a brain
        /// cleared only for placeholders gets a pseudonymised copy whose summary is mapped back.
        /// </summary>
        public async Task<ActionSummary> SummarizeAsync(IAgentBrain brain, string brainId, string prompt, ToolCall call, ControlCheck toolOut)
        {
            var policy = _policies.ForBrain(brainId, brain.Name);
            if (ComponentPolicyService.IsRuleBased(brainId))
            {
                return await brain.SummarizeAsync(prompt, call);
            }

            if (toolOut.IsBlocked)
            {
                return new ActionSummary(ToolResultSummarizer.Summarize(call), "a ToolOut control blocked the result from reaching the brain");
            }

            var label = _policies.ForTool(call.Tool, call.Action)?.OutputLabel ?? Sensitivity.CustomerConfidential;
            var payload = JsonSerializer.Serialize(new { prompt, call.Parameters, call.Result, call.Error });
            var shared = Share($"tool:{call.Tool}/{call.Action}", policy, label, payload, "tool result", call.Id);
            switch (shared.Outcome)
            {
                case ShareOutcome.Blocked:
                    return new ActionSummary(ToolResultSummarizer.Summarize(call), $"{brain.Name} isn't cleared for {label.Label()} data");
                case ShareOutcome.Allowed:
                    return await brain.SummarizeAsync(prompt, call);
                default:
                    var copy = new ToolCall
                    {
                        Id = call.Id,
                        Tool = call.Tool,
                        Action = call.Action,
                        Status = call.Status,
                        Parameters = call.Parameters.ToDictionary(p => p.Key, p => Pseudonyms.Pseudonymise(p.Value)),
                        Result = call.Result == null ? null : Pseudonyms.Pseudonymise(call.Result),
                        Error = call.Error == null ? null : Pseudonyms.Pseudonymise(call.Error),
                    };
                    var summary = await brain.SummarizeAsync(Pseudonyms.Pseudonymise(prompt), copy);
                    return summary with { Text = Restore(summary.Text), Reasoning = summary.Reasoning == null ? null : Restore(summary.Reasoning) };
            }
        }

        /// <summary>
        /// Logs a model's reasoning against the request (and tool call, when there is one) so the action can be drilled
        /// into later. It is model output shown to users, so Chat Out controls check it first: a blocked one is withheld
        /// from display (the original stays in the log data). Nothing is logged when there is no reasoning.
        /// </summary>
        /// <param name="stage">What the model was doing, e.g. <see cref="ReasoningStages.Decision"/>.</param>
        /// <param name="target">What controls report it as; the Scoping page passes its own so it stays out of the harness chat.</param>
        public void RecordReasoning(string stage, string brainName, Classification? brainClassification, string? reasoning, string? toolCallId = null,
            string target = ChatTranscript.Targets.Reasoning)
        {
            if (string.IsNullOrWhiteSpace(reasoning))
                return;

            var text = Llm.LlmReasoning.Limit(reasoning.Trim());
            var check = _controls.Check(ControlStage.ChatOut, target, text, brainClassification);
            var preview = check.IsBlocked ? "withheld: blocked by a Chat Out control" : Preview(text);
            _log.LogReasoning($"Reasoning ({stage}): {preview}", toolCallId,
                new() { ["reasoning"] = text, ["stage"] = stage, ["withheld"] = check.IsBlocked }, brainName);
            ControlReporter.Report(_log, check, brainName, toolCallId);
        }

        // First line-ish of the reasoning for the activity feed; the full text is in the entry's data.
        private static string Preview(string text)
        {
            var oneLine = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "…";
        }

        private static ControlViolation Builtin(string id, string name, string detail, string text, Classification? subject) =>
            new(id, name, ControlStage.ToolRule, ControlMode.Block, ChatTranscript.Targets.ToolCall, detail, text, subject);

        private static int CountPlaceholders(string text) =>
            System.Text.RegularExpressions.Regex.Matches(text, @"\b(?:HOST|EMAIL|IP)_\d+\b").Select(m => m.Value).Distinct().Count();
    }
}
