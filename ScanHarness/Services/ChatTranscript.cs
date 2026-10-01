using System.Text.Json;
using ScanHarness.Models;
using ScanHarness.Services.Controls;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Services
{
    /// <summary>What a chat turn represents.</summary>
    public enum ChatTurnKind
    {
        /// <summary>A user's request and the agent's reply to it.</summary>
        Request,

        /// <summary>A summary of the whole session, made with the Summarise session button.</summary>
        SessionSummary
    }

    /// <summary>What a model was doing when it reasoned; the "stage" of a logged reasoning entry.</summary>
    public static class ReasoningStages
    {
        public const string Decision = "decision";
        public const string Summary = "summary";
        public const string SessionSummary = "session summary";
        public const string ActivitySummary = "activity summary";
        public const string Scoping = "scoping agent";

        /// <summary>The stage for an agent, e.g. "agent: Findings analyst".</summary>
        public static string Agent(string name) => $"agent: {name}";
    }

    /// <summary>A model's reasoning at one step of a turn.</summary>
    /// <param name="Stage">What it was doing (see <see cref="ReasoningStages"/>).</param>
    /// <param name="Brain">The brain or agent that reasoned.</param>
    /// <param name="Text">The reasoning, shown unless <paramref name="Withheld"/>.</param>
    /// <param name="Withheld">A Chat Out control blocked it from being shown.</param>
    public sealed record ReasoningStep(string Stage, string? Brain, string Text, bool Withheld, DateTime Time, string? ToolCallId);

    /// <summary>
    /// One exchange in the chat window: the user's question and the agent's reply, with the tool call
    /// (if any) the request led to, so the reply can be selected to show that call's output.
    /// </summary>
    public sealed class ChatTurn
    {
        /// <summary>Id of the log entry that started the turn; stable across reloads.</summary>
        public required string Id { get; init; }

        public ChatTurnKind Kind { get; init; } = ChatTurnKind.Request;

        /// <summary>The user's question (empty for a session summary).</summary>
        public string Prompt { get; init; } = "";

        /// <summary>User who asked (or requested the session summary).</summary>
        public string? UserName { get; init; }

        public DateTime RequestedAt { get; init; }

        /// <summary>Brain that handled the request.</summary>
        public string? Brain { get; set; }

        /// <summary>The agent's reply to the request (its decision), or the session summary text.</summary>
        public string? AgentMessage { get; set; }

        public DateTime? RepliedAt { get; set; }

        /// <summary>Set when the brain itself failed to decide.</summary>
        public string? Error { get; set; }

        /// <summary>The tool call the request led to, if any.</summary>
        public ToolCall? ToolCall { get; set; }

        /// <summary>Summary of the finished tool call.</summary>
        public string? Summary { get; set; }

        /// <summary>Brain that wrote <see cref="Summary"/>.</summary>
        public string? SummaryBrain { get; set; }

        /// <summary>Chat In controls that matched the question.</summary>
        public List<ControlViolation> PromptControls { get; } = new();

        /// <summary>Chat Out controls that matched the reply (or session summary), and ToolRule controls that matched its tool call.</summary>
        public List<ControlViolation> ReplyControls { get; } = new();

        /// <summary>ToolOut controls that matched the tool call's result, and Chat Out controls that matched its summary.</summary>
        public List<ControlViolation> SummaryControls { get; } = new();

        /// <summary>The models' reasoning behind this turn: the decision, any agent, and the summary, in order.</summary>
        public List<ReasoningStep> Reasoning { get; } = new();

        /// <summary>Chat Out controls that matched the reasoning.</summary>
        public List<ControlViolation> ReasoningControls { get; } = new();

        /// <summary>
        /// Everything the reply's pip reports: warnings (Advise) raised on the question it answers, then
        /// hits on the reply and its tool call. A blocked question has no reply, so only warnings carry over.
        /// </summary>
        public IReadOnlyList<ControlViolation> ReplyPipControls =>
            PromptControls.Where(v => v.Mode == ControlMode.Advise).Concat(ReplyControls).ToList();

        /// <summary>A Chat In control blocked the question, so it was never sent to the brain.</summary>
        public bool PromptBlocked => PromptControls.Any(IsBlock);

        /// <summary>A Chat Out control blocked the reply (or session summary); its text is withheld.</summary>
        public bool ReplyWithheld => ReplyControls.Any(v => IsBlock(v) && v.Stage == ControlStage.ChatOut);

        /// <summary>A ToolRule control blocked the tool call, so the tool never ran.</summary>
        public bool ToolBlocked => ReplyControls.Any(v => IsBlock(v) && v.Stage == ControlStage.ToolRule);

        /// <summary>A Chat Out control blocked the summary; its text is withheld.</summary>
        public bool SummaryWithheld => SummaryControls.Any(v => IsBlock(v) && v.Stage == ControlStage.ChatOut);

        private static bool IsBlock(ControlViolation v) => v.Mode == ControlMode.Block;
    }

    /// <summary>
    /// Builds the chat transcript from a session's log entries and tool calls. Because it only reads what
    /// is logged, the same transcript appears live and when a saved session is reopened.
    /// </summary>
    public static class ChatTranscript
    {
        private const string RequestPrefix = "Request: ";

        /// <summary>Turns in the order they happened.</summary>
        public static IReadOnlyList<ChatTurn> Build(IEnumerable<LogEntry> logs, IEnumerable<ToolCall> toolCalls)
        {
            var callsById = toolCalls.ToDictionary(c => c.Id);
            var turns = new List<ChatTurn>();
            ChatTurn? current = null;

            foreach (var entry in logs.OrderBy(e => e.Timestamp))
            {
                var prompt = PromptOf(entry);
                if (prompt != null)
                {
                    current = new ChatTurn
                    {
                        Id = entry.Id,
                        Prompt = prompt,
                        UserName = entry.UserName,
                        RequestedAt = entry.Timestamp,
                        Brain = entry.Brain,
                    };
                    turns.Add(current);
                    continue;
                }

                var sessionSummary = ReadString(entry, "sessionSummary");
                if (sessionSummary != null)
                {
                    turns.Add(new ChatTurn
                    {
                        Id = entry.Id,
                        Kind = ChatTurnKind.SessionSummary,
                        UserName = entry.UserName,
                        RequestedAt = entry.Timestamp,
                        Brain = entry.Brain,
                        AgentMessage = sessionSummary,
                        RepliedAt = entry.Timestamp,
                    });
                    current = null;
                    continue;
                }

                // Reasoning belongs to the turn it was logged in (or the session summary just made). The Scoping
                // page's and the admin's activity summary aren't part of the chat.
                var reasoning = ReadString(entry, "reasoning");
                if (reasoning != null && entry.Level == LogLevel.Reasoning)
                {
                    var stage = ReadString(entry, "stage") ?? "";
                    var owner = stage == ReasoningStages.SessionSummary ? turns.LastOrDefault(t => t.Kind == ChatTurnKind.SessionSummary) : current;
                    if (stage is not (ReasoningStages.Scoping or ReasoningStages.ActivitySummary))
                    {
                        // "True" when logged live, "true" when read back from the store.
                        var withheld = string.Equals(ReadString(entry, "withheld"), "true", StringComparison.OrdinalIgnoreCase);
                        owner?.Reasoning.Add(new ReasoningStep(stage, entry.Brain, reasoning, withheld, entry.Timestamp, entry.ToolCallId));
                    }

                    continue;
                }

                // Control hits are logged right after what they checked; attach them to that part of the turn.
                var controls = ReadString(entry, "controls");
                if (controls != null)
                {
                    AttachControls(turns, current, ReadString(entry, "target"), ControlService.FromJson(controls));
                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                var agentMessage = ReadString(entry, "agentMessage");
                if (agentMessage != null && current.AgentMessage == null)
                {
                    current.AgentMessage = agentMessage;
                    current.RepliedAt = entry.Timestamp;
                    current.Brain ??= entry.Brain;
                    current.Error = ReadString(entry, "error");
                }

                if (entry.ToolCallId != null && current.ToolCall == null && callsById.TryGetValue(entry.ToolCallId, out var call))
                {
                    current.ToolCall = call;
                }

                if (entry.Level == LogLevel.Summary && current.ToolCall != null && entry.ToolCallId == current.ToolCall.Id)
                {
                    current.Summary = ReadString(entry, "summary") ?? current.Summary;
                    current.SummaryBrain = entry.Brain;
                }
            }

            // The tool call itself holds the latest summary (e.g. one recorded before its log entry arrived).
            foreach (var turn in turns.Where(t => t.ToolCall != null))
            {
                turn.Summary ??= turn.ToolCall!.Summary;
                turn.SummaryBrain ??= turn.ToolCall!.SummaryBrain;
            }

            return turns;
        }

        /// <summary>Log data "target" values: what a control checked.</summary>
        public static class Targets
        {
            public const string Request = "request";
            public const string Reply = "reply";
            public const string ToolCall = "tool call";

            /// <summary>A tool or agent result checked by ToolOut controls before a brain reads it.</summary>
            public const string ToolOutput = "tool output";

            /// <summary>A model's reasoning, checked by Chat Out controls before it is shown.</summary>
            public const string Reasoning = "reasoning";
            public const string Summary = "summary";
            public const string SessionSummary = "session summary";

            /// <summary>The admin's summary of all activity; not part of the chat, so the transcript ignores it.</summary>
            public const string ActivitySummary = "activity summary";

            /// <summary>A message in the Scoping page's customer conversation; not part of the harness chat either.</summary>
            public const string ScopingMessage = "scoping message";
        }

        private static void AttachControls(List<ChatTurn> turns, ChatTurn? current, string? target, IReadOnlyList<ControlViolation> violations)
        {
            if (target is Targets.ActivitySummary or Targets.ScopingMessage)
            {
                return;
            }

            if (target == Targets.Reasoning)
            {
                (current ?? turns.LastOrDefault())?.ReasoningControls.AddRange(violations);
                return;
            }

            if (target == Targets.SessionSummary)
            {
                turns.LastOrDefault(t => t.Kind == ChatTurnKind.SessionSummary)?.ReplyControls.AddRange(violations);
                return;
            }

            if (current == null)
            {
                return;
            }

            var list = target switch
            {
                Targets.Request => current.PromptControls,
                Targets.Summary or Targets.ToolOutput => current.SummaryControls,
                _ => current.ReplyControls, // reply and tool call
            };
            list.AddRange(violations);
        }

        // A request is a user entry carrying the prompt; older entries without data fall back to the message.
        private static string? PromptOf(LogEntry entry)
        {
            if (entry.Level != LogLevel.User)
            {
                return null;
            }

            return ReadString(entry, "prompt")
                ?? (entry.Message.StartsWith(RequestPrefix, StringComparison.Ordinal) ? entry.Message[RequestPrefix.Length..] : null);
        }

        /// <summary>
        /// Reads a string from a log entry's data. Values are plain objects when logged live and
        /// <see cref="JsonElement"/>s when loaded from the store.
        /// </summary>
        public static string? ReadString(LogEntry entry, string key)
        {
            if (entry.Data == null || !entry.Data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            var text = value is JsonElement json
                ? (json.ValueKind == JsonValueKind.String ? json.GetString() : json.ValueKind == JsonValueKind.Null ? null : json.ToString())
                : value.ToString();

            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
