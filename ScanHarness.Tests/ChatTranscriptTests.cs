using System.Text.Json;
using ScanHarness.Models;
using ScanHarness.Services;
using Xunit;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Tests
{
    public class ChatTranscriptTests
    {
        private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        private int _second;

        private LogEntry Entry(LogLevel level, string message, Dictionary<string, object>? data = null, string? toolCallId = null,
            string user = "chris", string? brain = "Keyword brain (rule-based)") => new()
        {
            Timestamp = T0.AddSeconds(_second++),
            Level = level,
            Message = message,
            Data = data,
            ToolCallId = toolCallId,
            UserName = user,
            Brain = brain,
        };

        private static ToolCall Call(string id, string tool, ToolCallStatus status, string? summary = null) => new()
        {
            Id = id,
            Tool = tool,
            Action = "run",
            Status = status,
            Summary = summary,
        };

        // One full request as the harness logs it: request, decision, tool call entries and summary.
        private List<LogEntry> Request(string prompt, string reply, string? toolCallId, string? summary = null, string user = "chris")
        {
            var entries = new List<LogEntry>
            {
                Entry(LogLevel.User, $"Request: {prompt}", new() { ["prompt"] = prompt }, user: user),
                Entry(toolCallId == null ? LogLevel.Warning : LogLevel.Info, "decision",
                    new() { ["agentMessage"] = reply, ["toolFound"] = toolCallId != null }, user: user),
            };

            if (toolCallId != null)
            {
                entries.Add(Entry(LogLevel.Info, "Tool call initiated", toolCallId: toolCallId, user: user));
                entries.Add(Entry(LogLevel.Success, "Tool call completed", toolCallId: toolCallId, user: user));
            }

            if (summary != null)
            {
                entries.Add(Entry(LogLevel.Summary, summary + " · 0.0s", new() { ["summary"] = summary }, toolCallId, user: user));
            }

            return entries;
        }

        [Fact]
        public void Each_request_becomes_a_turn_with_the_reply_tool_call_and_summary()
        {
            var logs = Request("scan example.com", "I'll scan https://example.com for links.", "call-1", "The scan tool returned 3 links.");
            var calls = new[] { Call("call-1", "scanner", ToolCallStatus.Completed) };

            var turn = Assert.Single(ChatTranscript.Build(logs, calls));

            Assert.Equal(ChatTurnKind.Request, turn.Kind);
            Assert.Equal("scan example.com", turn.Prompt);
            Assert.Equal("chris", turn.UserName);
            Assert.Equal("I'll scan https://example.com for links.", turn.AgentMessage);
            Assert.Equal("call-1", turn.ToolCall!.Id);
            Assert.Equal("The scan tool returned 3 links.", turn.Summary);
            Assert.Equal(T0, turn.RequestedAt);
            Assert.Equal(T0.AddSeconds(1), turn.RepliedAt);
        }

        [Fact]
        public void Turns_are_in_order_and_each_keeps_its_own_tool_call()
        {
            var logs = Request("scan example.com", "Scanning.", "call-1", "Scan summary.");
            logs.AddRange(Request("what's the weather?", "Getting the forecast.", "call-2", "Weather summary."));
            var calls = new[] { Call("call-1", "scanner", ToolCallStatus.Completed), Call("call-2", "weather", ToolCallStatus.Completed) };

            var turns = ChatTranscript.Build(logs.OrderByDescending(e => e.Timestamp), calls);

            Assert.Equal(new[] { "scan example.com", "what's the weather?" }, turns.Select(t => t.Prompt));
            Assert.Equal(new[] { "call-1", "call-2" }, turns.Select(t => t.ToolCall!.Id));
            Assert.Equal(new[] { "Scan summary.", "Weather summary." }, turns.Select(t => t.Summary));
        }

        [Fact]
        public void A_request_with_no_tool_has_a_reply_but_no_tool_call()
        {
            var turn = Assert.Single(ChatTranscript.Build(Request("tell me a joke", "I don't have a suitable tool.", null), Array.Empty<ToolCall>()));

            Assert.Equal("I don't have a suitable tool.", turn.AgentMessage);
            Assert.Null(turn.ToolCall);
            Assert.Null(turn.Summary);
        }

        [Fact]
        public void A_request_still_being_decided_has_no_reply_yet()
        {
            var logs = new List<LogEntry> { Entry(LogLevel.User, "Request: attack example.com", new() { ["prompt"] = "attack example.com" }) };

            var turn = Assert.Single(ChatTranscript.Build(logs, Array.Empty<ToolCall>()));

            Assert.Null(turn.AgentMessage);
            Assert.Null(turn.RepliedAt);
        }

        [Fact]
        public void Brain_failure_is_shown_as_the_turns_error()
        {
            var logs = new List<LogEntry>
            {
                Entry(LogLevel.User, "Request: scan example.com", new() { ["prompt"] = "scan example.com" }),
                Entry(LogLevel.Error, "Brain failed", new() { ["agentMessage"] = "The model couldn't decide.", ["error"] = "Connection refused" }),
            };

            var turn = Assert.Single(ChatTranscript.Build(logs, Array.Empty<ToolCall>()));

            Assert.Equal("The model couldn't decide.", turn.AgentMessage);
            Assert.Equal("Connection refused", turn.Error);
        }

        [Fact]
        public void Summary_recorded_on_the_tool_call_is_used_when_not_yet_logged()
        {
            var logs = Request("attack example.com", "Attacking once approved.", "call-1");
            var calls = new[] { Call("call-1", "attack", ToolCallStatus.Cancelled, summary: "The user rejected the request.") };

            Assert.Equal("The user rejected the request.", Assert.Single(ChatTranscript.Build(logs, calls)).Summary);
        }

        [Fact]
        public void Session_summary_becomes_its_own_agent_turn()
        {
            var logs = Request("scan example.com", "Scanning.", "call-1", "Scan summary.");
            logs.Add(Entry(LogLevel.User, "Requested a summary of the session (1 tool calls)"));
            logs.Add(Entry(LogLevel.Summary, "Session summary (4 words): …", new() { ["sessionSummary"] = "One scan was run." }));

            var turns = ChatTranscript.Build(logs, new[] { Call("call-1", "scanner", ToolCallStatus.Completed) });

            Assert.Equal(2, turns.Count);
            Assert.Equal(ChatTurnKind.SessionSummary, turns[1].Kind);
            Assert.Equal("One scan was run.", turns[1].AgentMessage);
            Assert.Equal("scan example.com", turns[0].Prompt); // the summary request isn't a chat question
        }

        [Fact]
        public void Other_user_actions_are_not_chat_questions()
        {
            var logs = new List<LogEntry>
            {
                Entry(LogLevel.User, "Switched decision brain to llama3.1:8b (LLM)"),
                Entry(LogLevel.User, "Approved tool call: attack/execute"),
            };

            Assert.Empty(ChatTranscript.Build(logs, Array.Empty<ToolCall>()));
        }

        [Fact]
        public void Data_loaded_from_the_store_as_json_is_read_the_same()
        {
            // The store deserialises data values as JsonElements.
            Dictionary<string, object> AsStored(Dictionary<string, object> data) =>
                JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(data))!;

            var logs = new List<LogEntry>
            {
                Entry(LogLevel.User, "Request: weather in Oslo", AsStored(new() { ["prompt"] = "weather in Oslo" }), user: "raccoon"),
                Entry(LogLevel.Info, "decision", AsStored(new() { ["agentMessage"] = "Getting the forecast for Oslo.", ["toolFound"] = true }), user: "raccoon"),
            };

            var turn = Assert.Single(ChatTranscript.Build(logs, Array.Empty<ToolCall>()));

            Assert.Equal("weather in Oslo", turn.Prompt);
            Assert.Equal("Getting the forecast for Oslo.", turn.AgentMessage);
            Assert.Equal("raccoon", turn.UserName);
        }

        [Fact]
        public void Requests_logged_without_data_fall_back_to_the_message()
        {
            var turn = Assert.Single(ChatTranscript.Build(new[] { Entry(LogLevel.User, "Request: scan example.com") }, Array.Empty<ToolCall>()));

            Assert.Equal("scan example.com", turn.Prompt);
        }
    }
}
