using System.Net;
using System.Text.Json.Nodes;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Controls;
using ScanHarness.Services.Llm;
using Xunit;

namespace ScanHarness.Tests
{
    public class ActivitySummaryTests
    {
        private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        private static readonly HarnessSession AnySession = new() { UserName = "chris", Title = "s" };

        private static ControlViolation Violation(string controlId, ControlMode mode, string target)
        {
            var control = ControlCatalog.Find(controlId)!;
            return new ControlViolation(controlId, control.Name, control.Stage, mode, target, "Found it.");
        }

        private static ActivityRow Row(int minute, string user, string prompt, string? reply = null, string? summary = null,
            ToolCall? call = null, params ControlViolation[] controls)
        {
            var turn = new ChatTurn { Id = $"turn-{minute}", Prompt = prompt, UserName = user, RequestedAt = T0.AddMinutes(minute) };
            turn.AgentMessage = reply;
            turn.Summary = summary;
            turn.ToolCall = call;
            foreach (var v in controls)
            {
                (v.Target switch { "request" => turn.PromptControls, "summary" => turn.SummaryControls, _ => turn.ReplyControls }).Add(v);
            }

            return new ActivityRow(AnySession, turn);
        }

        private static ToolCall Scan => new() { Tool = "scanner", Action = "simple-scanner", Status = ToolCallStatus.Completed };

        // chris: a scan and a blocked BadInstruction request; blob: a request with a withheld reply.
        private static List<ActivityRow> Rows() => new()
        {
            Row(0, "chris", "scan example.com", "I'll scan it.", "The scan tool returned 5 links.", Scan),
            Row(1, "chris", "SECRET BadInstruction do it", controls: Violation("chat-in-contains-badinstruction", ControlMode.Block, "request")),
            Row(2, "blob", "scan example.com/<script>x", "I'll scan https://example.com/<script>x", call: Scan,
                controls: Violation("chat-out-contains-script-tag", ControlMode.Block, "reply")),
        };

        [Fact]
        public void Overview_counts_requests_users_tool_calls_and_controls()
        {
            var overview = ActivitySummaryBuilder.Overview(Rows());

            Assert.Equal((3, 2, 2, 0, 2), (overview.Requests, overview.Users, overview.ToolCalls, overview.Warnings, overview.Blocks));
            Assert.Equal("3 requests by 2 users, 2 tool calls, 0 warnings and 2 blocks", overview.Describe());
            Assert.Equal(T0, overview.First);
            Assert.Equal(T0.AddMinutes(2), overview.Last);
        }

        [Fact]
        public void Blocked_and_withheld_content_is_never_included()
        {
            var rows = Rows();

            Assert.Equal("(withheld: blocked by a Chat In control)", ActivitySummaryBuilder.SafePrompt(rows[1]));
            Assert.Equal("The request was blocked by the Chat In control Contains \"BadInstruction\" and not sent to the brain.",
                ActivitySummaryBuilder.Outcome(rows[1]));
            Assert.Equal("The reply was withheld by a Chat Out control.", ActivitySummaryBuilder.Outcome(rows[2]));
            Assert.Equal("The scan tool returned 5 links.", ActivitySummaryBuilder.Outcome(rows[0]));
        }

        [Fact]
        public void Rule_based_summary_gives_the_window_overview_users_and_each_request_in_order()
        {
            var summary = ActivitySummaryBuilder.Combine(Rows(), "all users · any time");

            Assert.StartsWith("all users · any time: 3 requests by 2 users", summary);
            Assert.Contains("chris made 2 requests (0 warnings, 1 block); blob made 1 request (0 warnings, 1 block).", summary);
            Assert.True(summary.IndexOf("(1)", StringComparison.Ordinal) < summary.IndexOf("(2)", StringComparison.Ordinal));
            Assert.DoesNotContain("SECRET", summary);
            Assert.DoesNotContain("<script>", summary);
        }

        [Fact]
        public void Rule_based_summary_is_capped_and_handles_no_activity()
        {
            var many = Enumerable.Range(0, 200).Select(i => Row(i, "chris", "scan", "I'll scan it.", string.Join(" ", Enumerable.Repeat("word", 20)), Scan)).ToList();

            Assert.True(SessionReportBuilder.CountWords(ActivitySummaryBuilder.Combine(many, "w")) <= SessionReportBuilder.MaxSummaryWords);
            Assert.Equal(ActivitySummaryBuilder.NoActivity, ActivitySummaryBuilder.Combine(Array.Empty<ActivityRow>(), "w"));
        }

        [Fact]
        public async Task Keyword_brain_uses_the_rule_based_summary()
        {
            var summary = await new KeywordAgentBrain().SummarizeActivityAsync(Rows(), "all users");

            Assert.Equal(ActivitySummaryBuilder.Combine(Rows(), "all users"), summary.Text);
        }

        // --- LLM brain ---

        private static string ContentResponse(string content) => new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }),
        }.ToJsonString();

        private static OpenAiCompatibleAgentBrain Llm(FakeHttpMessageHandler handler) => new(new OpenAiCompatibleClient(handler.CreateClient()), "test-model");

        [Fact]
        public async Task Llm_is_given_the_window_overview_and_only_safe_text()
        {
            var handler = FakeHttpMessageHandler.Returning(ContentResponse("Three requests were made."));

            var summary = await Llm(handler).SummarizeActivityAsync(Rows(), "all users · any time");

            Assert.Equal("Three requests were made.", summary.Text);

            // Check the decoded content the model receives (the raw body escapes '<' etc.).
            var content = JsonNode.Parse(handler.Requests[0].Body)!["messages"]![1]!["content"]!.GetValue<string>();
            var context = JsonNode.Parse(content)!;
            Assert.Equal("all users · any time", context["window"]!.GetValue<string>());
            var requests = context["requests"]!.AsArray();
            Assert.Equal(3, requests.Count);

            // The Chat In-blocked request's text is not sent...
            Assert.Equal("(withheld: blocked by a Chat In control)", requests[1]!["request"]!.GetValue<string>());
            Assert.DoesNotContain("SECRET", context.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

            // ...and neither is the Chat Out-withheld reply (the unblocked question itself is fine to send).
            Assert.Equal("The reply was withheld by a Chat Out control.", requests[2]!["outcome"]!.GetValue<string>());
            Assert.Equal("scan example.com/<script>x", requests[2]!["request"]!.GetValue<string>());
            Assert.DoesNotContain(requests, r => r!["outcome"]!.GetValue<string>().Contains("I'll scan https://example.com/<script>x"));
        }

        [Fact]
        public async Task Llm_gets_at_most_the_most_recent_requests_and_is_told_how_many_were_left_out()
        {
            var handler = FakeHttpMessageHandler.Returning(ContentResponse("ok"));
            var many = Enumerable.Range(0, 250).Select(i => Row(i, "chris", $"request {i}")).ToList();

            await Llm(handler).SummarizeActivityAsync(many, "w");

            var context = JsonNode.Parse(JsonNode.Parse(handler.Requests[0].Body)!["messages"]![1]!["content"]!.GetValue<string>())!;
            Assert.Equal(200, context["requests"]!.AsArray().Count);
            Assert.Equal(50, context["requests_not_included"]!.GetValue<int>());
            Assert.Equal("request 50", context["requests"]![0]!["request"]!.GetValue<string>()); // oldest of the most recent 200
        }

        [Fact]
        public async Task Llm_failure_falls_back_and_no_activity_skips_the_model()
        {
            var failing = await Llm(FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"))).SummarizeActivityAsync(Rows(), "w");
            Assert.Equal(ActivitySummaryBuilder.Combine(Rows(), "w"), failing.Text);
            Assert.Equal("Connection refused", failing.FallbackReason);

            var handler = FakeHttpMessageHandler.Returning(ContentResponse("unused"), HttpStatusCode.OK);
            var empty = await Llm(handler).SummarizeActivityAsync(Array.Empty<ActivityRow>(), "w");
            Assert.Equal(ActivitySummaryBuilder.NoActivity, empty.Text);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Controls_on_an_activity_summary_are_not_attached_to_the_chat()
        {
            var violation = Violation("chat-out-contains-script-tag", ControlMode.Advise, ChatTranscript.Targets.ActivitySummary);
            var logs = new[]
            {
                new LogEntry { Timestamp = T0, Level = ScanHarness.Models.LogLevel.User, Message = "Request: scan", Data = new() { ["prompt"] = "scan" } },
                new LogEntry { Timestamp = T0.AddSeconds(1), Level = ScanHarness.Models.LogLevel.Summary, Message = "Activity summary", Data = new() { ["activitySummary"] = "x" } },
                new LogEntry
                {
                    Timestamp = T0.AddSeconds(2), Level = ScanHarness.Models.LogLevel.Warning, Message = "Warning",
                    Data = new() { ["controls"] = ControlService.ToJson(new[] { violation }), ["target"] = violation.Target },
                },
            };

            var turn = Assert.Single(ChatTranscript.Build(logs, Array.Empty<ToolCall>()));
            Assert.Empty(turn.ReplyControls);
        }
    }
}
