using System.Net;
using System.Text.Json.Nodes;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Llm;
using Xunit;

namespace ScanHarness.Tests
{
    public class SessionReportTests
    {
        private static readonly DateTime Start = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

        private static ToolCall Call(int minute, string tool, string action, Dictionary<string, string> parameters,
            ToolCallStatus status, string? summary = null, string? brain = "Keyword brain (rule-based)") => new()
        {
            Id = $"call-{minute:D2}",
            CreatedAt = Start.AddMinutes(minute),
            Tool = tool,
            Action = action,
            Parameters = parameters,
            Status = status,
            Summary = summary,
            Brain = brain,
        };

        private static List<ToolCall> Session() => new()
        {
            // Deliberately out of order: the list must sort by time.
            Call(2, "attack", "execute", new() { ["url"] = "https://example.com" }, ToolCallStatus.Cancelled,
                "The user asked me to run a simulated attack against https://example.com. The user rejected the request, so the attack tool was not run."),
            Call(0, "scanner", "simple-scanner", new() { ["url"] = "https://example.com" }, ToolCallStatus.Completed,
                "The user asked me to scan https://example.com for links. The scan tool returned 3 links."),
            Call(1, "weather", "forecast", new() { ["location"] = "London" }, ToolCallStatus.Completed, summary: null),
        };

        [Fact]
        public void Tool_list_is_in_call_order_with_numbers_parameters_status_and_brain()
        {
            var list = SessionReportBuilder.ListToolCalls(Session());

            Assert.Equal(new[] { 1, 2, 3 }, list.Select(i => i.Number));
            Assert.Equal(new[] { "scanner/simple-scanner", "weather/forecast", "attack/execute" }, list.Select(i => $"{i.Tool}/{i.Action}"));
            Assert.Equal("url=https://example.com", list[0].Parameters);
            Assert.Equal("location=London", list[1].Parameters);
            Assert.Equal(ToolCallStatus.Cancelled, list[2].Status);
            Assert.All(list, i => Assert.Equal("Keyword brain (rule-based)", i.Brain));
        }

        [Fact]
        public void Tool_list_is_deterministic()
        {
            var first = SessionReportBuilder.ListToolCalls(Session());
            var shuffled = Session().OrderByDescending(c => c.Id).ToList();

            Assert.Equal(first, SessionReportBuilder.ListToolCalls(shuffled));
        }

        [Fact]
        public void Parameters_are_listed_in_key_order()
        {
            var call = Call(0, "custom", "run", new() { ["zeta"] = "1", ["alpha"] = "2" }, ToolCallStatus.Completed);

            Assert.Equal("alpha=2, zeta=1", SessionReportBuilder.ListToolCalls(new[] { call })[0].Parameters);
        }

        [Fact]
        public void Rule_based_summary_counts_outcomes_and_includes_every_summary_in_order()
        {
            var summary = SessionReportBuilder.CombineSummaries(Session());

            Assert.StartsWith("3 tool calls this session: 2 completed, 1 rejected.", summary);
            var scan = summary.IndexOf("(1) The user asked me to scan", StringComparison.Ordinal);
            var weather = summary.IndexOf("(2) The user asked me to get the weather forecast for London.", StringComparison.Ordinal);
            var attack = summary.IndexOf("(3) The user asked me to run a simulated attack", StringComparison.Ordinal);
            Assert.True(scan > 0 && weather > scan && attack > weather);
        }

        [Fact]
        public void Empty_session_says_so()
        {
            Assert.Equal(SessionReportBuilder.EmptySessionSummary, SessionReportBuilder.CombineSummaries(Array.Empty<ToolCall>()));
        }

        [Fact]
        public void Rule_based_summary_never_exceeds_the_word_limit()
        {
            var longSummary = string.Join(" ", Enumerable.Repeat("word", 120));
            var calls = Enumerable.Range(0, 10)
                .Select(i => Call(i, "scanner", "simple-scanner", new() { ["url"] = "https://example.com" }, ToolCallStatus.Completed, longSummary))
                .ToList();

            var summary = SessionReportBuilder.CombineSummaries(calls);

            Assert.Equal(SessionReportBuilder.MaxSummaryWords, SessionReportBuilder.CountWords(summary));
            Assert.EndsWith("…", summary);
        }

        [Theory]
        [InlineData("one two three", 5, "one two three")]
        [InlineData("one two three four five six", 3, "one two three…")]
        [InlineData("one, two, three, four", 3, "one, two, three…")]
        [InlineData("First paragraph here.\n\nSecond paragraph goes on", 5, "First paragraph here.\n\nSecond paragraph…")]
        public void LimitWords_cuts_at_the_limit_and_keeps_paragraphs(string text, int max, string expected)
        {
            Assert.Equal(expected, SessionReportBuilder.LimitWords(text, max));
        }

        // --- Brains ---

        private static string ContentResponse(string content) => new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            }),
        }.ToJsonString();

        private static OpenAiCompatibleAgentBrain LlmBrain(FakeHttpMessageHandler handler) =>
            new(new OpenAiCompatibleClient(handler.CreateClient()), "test-model");

        [Fact]
        public async Task Keyword_brain_uses_the_rule_based_summary()
        {
            var summary = await new KeywordAgentBrain().SummarizeSessionAsync(Session());

            Assert.Equal(SessionReportBuilder.CombineSummaries(Session()), summary.Text);
            Assert.Null(summary.FallbackReason);
        }

        [Fact]
        public async Task Llm_brain_sends_every_action_in_order_and_asks_for_under_the_limit()
        {
            var handler = FakeHttpMessageHandler.Returning(ContentResponse("The user scanned, checked the weather and tried an attack."));

            var summary = await LlmBrain(handler).SummarizeSessionAsync(Session());

            Assert.Equal("The user scanned, checked the weather and tried an attack.", summary.Text);
            var request = JsonNode.Parse(handler.Requests[0].Body)!;
            Assert.Contains("450 words", request["messages"]![0]!["content"]!.GetValue<string>());
            Assert.NotNull(request["max_tokens"]);

            var actions = JsonNode.Parse(request["messages"]![1]!["content"]!.GetValue<string>())!["actions"]!.AsArray();
            Assert.Equal(new[] { "Completed", "Completed", "Cancelled" }, actions.Select(a => a!["status"]!.GetValue<string>()));
            // A call without a stored summary gets its rule-based one.
            Assert.StartsWith("The user asked me to get the weather forecast for London.", actions[1]!["summary"]!.GetValue<string>());
        }

        [Fact]
        public async Task Llm_summary_is_cut_to_the_word_limit()
        {
            var tooLong = string.Join(" ", Enumerable.Repeat("finding", 700));
            var summary = await LlmBrain(FakeHttpMessageHandler.Returning(ContentResponse(tooLong))).SummarizeSessionAsync(Session());

            Assert.Equal(SessionReportBuilder.MaxSummaryWords, SessionReportBuilder.CountWords(summary.Text));
        }

        [Fact]
        public async Task Llm_summary_keeps_paragraphs_and_drops_markdown()
        {
            var reply = "\"**Overview.** The user scanned a site.\n\n\nThen   they asked for the weather.\"";
            var summary = await LlmBrain(FakeHttpMessageHandler.Returning(ContentResponse(reply))).SummarizeSessionAsync(Session());

            Assert.Equal("Overview. The user scanned a site.\n\nThen they asked for the weather.", summary.Text);
        }

        [Fact]
        public async Task Llm_failure_falls_back_to_the_rule_based_summary()
        {
            var summary = await LlmBrain(FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused")))
                .SummarizeSessionAsync(Session());

            Assert.Equal(SessionReportBuilder.CombineSummaries(Session()), summary.Text);
            Assert.Equal("Connection refused", summary.FallbackReason);
        }

        [Fact]
        public async Task Llm_brain_does_not_call_the_model_for_an_empty_session()
        {
            var handler = FakeHttpMessageHandler.Returning(ContentResponse("unused"), HttpStatusCode.OK);

            var summary = await LlmBrain(handler).SummarizeSessionAsync(Array.Empty<ToolCall>());

            Assert.Equal(SessionReportBuilder.EmptySessionSummary, summary.Text);
            Assert.Empty(handler.Requests);
        }
    }
}
