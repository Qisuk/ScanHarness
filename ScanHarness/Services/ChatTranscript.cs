using System.Text.Json;
using ScanHarness.Models;
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
