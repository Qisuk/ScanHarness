using System.Text.Json.Nodes;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Agents;
using ScanHarness.Services.Boundaries;
using ScanHarness.Services.Controls;
using ScanHarness.Services.Llm;
using Xunit;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Tests
{
    /// <summary>Capturing models' reasoning, logging it against the action, and drilling into it from the chat.</summary>
    public class ReasoningTests
    {
        private static JsonObject Response(string? content, string? reasoningContent = null, string? reasoning = null)
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
            if (reasoningContent != null) message["reasoning_content"] = reasoningContent;
            if (reasoning != null) message["reasoning"] = reasoning;
            return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) };
        }

        private static string? Content(JsonObject response) => response["choices"]![0]!["message"]!["content"]?.GetValue<string>();

        // --- Normalising what providers return ---

        [Fact]
        public void Reasoning_fields_from_different_providers_end_up_in_one_place()
        {
            var deepSeek = Response("answer", reasoningContent: "first I check");
            var ollama = Response("answer", reasoning: "thinking about it");
            LlmReasoning.Normalise(deepSeek);
            LlmReasoning.Normalise(ollama);

            Assert.Equal("first I check", LlmReasoning.Of(deepSeek));
            Assert.Equal("thinking about it", LlmReasoning.Of(ollama));
            Assert.Equal("answer", Content(ollama));
        }

        [Fact]
        public void Think_blocks_are_moved_out_of_the_answer()
        {
            var response = Response("<think>The user wants {weather}.\nParis is named.</think>\n{\"tool\":\"weather_forecast\"}");

            LlmReasoning.Normalise(response);

            Assert.Equal("The user wants {weather}.\nParis is named.", LlmReasoning.Of(response));
            Assert.Equal("{\"tool\":\"weather_forecast\"}", Content(response));
        }

        [Fact]
        public void An_unfinished_think_block_is_all_reasoning_and_models_without_reasoning_give_none()
        {
            var cutOff = Response("<think>still thinking when the tokens ran out");
            var plain = Response("just an answer");
            LlmReasoning.Normalise(cutOff);
            LlmReasoning.Normalise(plain);

            Assert.Equal("still thinking when the tokens ran out", LlmReasoning.Of(cutOff));
            Assert.Equal("", Content(cutOff));
            Assert.Null(LlmReasoning.Of(plain));
        }

        [Fact]
        public void Very_long_reasoning_is_cut()
        {
            var response = Response("a", reasoning: new string('x', LlmReasoning.MaxLength + 50));
            LlmReasoning.Normalise(response);

            Assert.EndsWith("… (truncated)", LlmReasoning.Of(response));
        }

        // --- Brains return it ---

        private static string Completion(string content, string? reasoning = null)
        {
            var response = Response(content, reasoning: reasoning);
            return response.ToJsonString();
        }

        [Fact]
        public async Task A_reasoning_models_decision_and_summary_carry_their_reasoning()
        {
            var decide = FakeHttpMessageHandler.Returning(Completion(
                "<think>They asked about weather {in Paris}.</think>{\"tool\":\"weather_forecast\",\"url\":null,\"location\":\"Paris\",\"message\":\"Getting it.\"}"));
            var brain = new OpenAiCompatibleAgentBrain(new OpenAiCompatibleClient(decide.CreateClient()), "reasoner", LlmDecisionMode.JsonSchema);

            var decision = await brain.DecideAsync("what's the weather in Paris?");

            Assert.True(decision.ToolFound);   // the braces inside the thinking didn't break parsing
            Assert.Equal("Paris", decision.Parameters["location"]);
            Assert.Equal("They asked about weather {in Paris}.", decision.Reasoning);

            var summarise = FakeHttpMessageHandler.Returning(Completion("The forecast is mild.", reasoning: "Temperatures 12 to 18."));
            var summary = await new OpenAiCompatibleAgentBrain(new OpenAiCompatibleClient(summarise.CreateClient()), "reasoner")
                .SummarizeAsync("weather", new ToolCall { Tool = "weather", Status = ToolCallStatus.Completed, Result = "[]" });

            Assert.Equal("The forecast is mild.", summary.Text);
            Assert.Equal("Temperatures 12 to 18.", summary.Reasoning);
        }

        // --- Logging and the transcript ---

        private sealed class Setup
        {
            public Setup()
            {
                Session = new SessionState();
                Log = new LogService(Session);
                Controls = new ControlService(Session);
                Broker = new BoundaryBroker(new ComponentPolicyService(), Controls, Log);
            }

            public SessionState Session { get; }
            public LogService Log { get; }
            public ControlService Controls { get; }
            public BoundaryBroker Broker { get; }

            public IReadOnlyList<LogEntry> Reasoning => Log.GetLogs().Where(l => l.Level == LogLevel.Reasoning).ToList();
        }

        [Fact]
        public void Reasoning_is_logged_against_the_tool_call_with_a_short_preview()
        {
            var setup = new Setup();

            setup.Broker.RecordReasoning(ReasoningStages.Summary, "reasoner (LLM)", Classification.Internal, "Line one.\nLine two.", "call-1");
            setup.Broker.RecordReasoning(ReasoningStages.Decision, "reasoner (LLM)", Classification.Internal, null);
            setup.Broker.RecordReasoning(ReasoningStages.Decision, "reasoner (LLM)", Classification.Internal, "  ");

            var entry = Assert.Single(setup.Reasoning);
            Assert.Equal("Reasoning (summary): Line one. Line two.", entry.Message);
            Assert.Equal("call-1", entry.ToolCallId);
            Assert.Equal("reasoner (LLM)", entry.Brain);
            Assert.Equal("Line one.\nLine two.", ChatTranscript.ReadString(entry, "reasoning"));
        }

        [Fact]
        public void Reasoning_a_Chat_Out_control_blocks_is_withheld_from_display_but_kept_in_the_log()
        {
            var setup = new Setup();

            setup.Broker.RecordReasoning(ReasoningStages.Decision, "reasoner (LLM)", Classification.Internal, "I could reply with <script>alert(1)</script>");

            var entry = Assert.Single(setup.Reasoning);
            Assert.Equal("Reasoning (decision): withheld: blocked by a Chat Out control", entry.Message);
            Assert.Contains("<script>", ChatTranscript.ReadString(entry, "reasoning"));
            Assert.Contains(setup.Log.GetLogs(), l => l.Level == LogLevel.Block && ChatTranscript.ReadString(l, "target") == ChatTranscript.Targets.Reasoning);
        }

        [Fact]
        public void The_transcript_puts_each_step_of_reasoning_on_its_turn()
        {
            var setup = new Setup();
            var log = setup.Log;
            var call = new ToolCall { Id = "call-1", Tool = "weather", Action = "forecast", Status = ToolCallStatus.Completed };

            log.LogUser("Request: weather in Paris", data: new() { ["prompt"] = "weather in Paris" });
            setup.Broker.RecordReasoning(ReasoningStages.Decision, "reasoner (LLM)", Classification.Internal, "They named Paris.");
            log.LogInfo("Agent selected tool", data: new() { ["agentMessage"] = "Getting it." });
            log.LogInfo("Tool call initiated", call.Id);
            setup.Broker.RecordReasoning(ReasoningStages.Summary, "reasoner (LLM)", Classification.Internal, "Mild temperatures.", call.Id);
            setup.Broker.RecordReasoning(ReasoningStages.Scoping, "Scoping agent", Classification.Internal, "Not part of the chat.",
                target: ChatTranscript.Targets.ScopingMessage);
            log.LogSummary("Session summary", data: new() { ["sessionSummary"] = "One forecast." });
            setup.Broker.RecordReasoning(ReasoningStages.SessionSummary, "reasoner (LLM)", Classification.Internal, "Only one action.");

            var turns = ChatTranscript.Build(log.GetLogs(), new[] { call });

            Assert.Equal(new[] { "decision", "summary" }, turns[0].Reasoning.Select(r => r.Stage));
            Assert.Equal(new[] { null, "call-1" }, turns[0].Reasoning.Select(r => r.ToolCallId));
            Assert.Equal("Only one action.", Assert.Single(turns[1].Reasoning).Text);
        }

        [Fact]
        public void A_withheld_step_and_its_control_appear_on_the_turn()
        {
            var setup = new Setup();
            setup.Log.LogUser("Request: hi", data: new() { ["prompt"] = "hi" });
            setup.Broker.RecordReasoning(ReasoningStages.Decision, "reasoner (LLM)", Classification.Internal, "<script>");

            var turn = Assert.Single(ChatTranscript.Build(setup.Log.GetLogs(), Array.Empty<ToolCall>()));

            Assert.True(Assert.Single(turn.Reasoning).Withheld);
            Assert.Equal("chat-out-contains-script-tag", Assert.Single(turn.ReasoningControls).ControlId);
            Assert.False(turn.ReplyWithheld);
        }

        // --- Across the boundary and in agents ---

        private sealed class ReasoningBrain : IAgentBrain
        {
            public string Name => "reasoner (LLM)";
            public Classification Classification => Classification.External;
            public Task<ToolDecision> DecideAsync(string prompt) => throw new NotSupportedException();

            public Task<ActionSummary> SummarizeAsync(string prompt, ToolCall toolCall) =>
                Task.FromResult(new ActionSummary("Scanned HOST_1.", Reasoning: $"HOST_1 came from {toolCall.Parameters["url"]}."));

            public Task<ActionSummary> SummarizeSessionAsync(IReadOnlyList<ToolCall> toolCalls) => throw new NotSupportedException();
            public Task<ActionSummary> SummarizeActivityAsync(IReadOnlyList<ActivityRow> rows, string window) => throw new NotSupportedException();
        }

        [Fact]
        public async Task An_external_brains_reasoning_has_its_placeholders_mapped_back()
        {
            var setup = new Setup();
            var policies = setup.Broker.Policies;
            policies.Update("brain:hosted", new PolicyOverride(Classification: Classification.External), "admin");
            var call = new ToolCall
            {
                Tool = "scanner", Action = "simple-scanner", Status = ToolCallStatus.Completed,
                Parameters = new() { ["url"] = "https://shop.acme.test" }, Result = "{}",
            };

            var summary = await setup.Broker.SummarizeAsync(new ReasoningBrain(), "hosted", "scan shop.acme.test", call, ControlCheck.Passed);

            Assert.Equal("shop.acme.test came from https://shop.acme.test.", summary.Reasoning);
        }

        [Fact]
        public async Task An_llm_agents_reasoning_is_logged_against_its_tool_call()
        {
            var setup = new Setup();
            setup.Broker.Policies.Update("agent:cost-estimator", new PolicyOverride(Brain: "reasoner"), "admin");
            var llm = FakeHttpMessageHandler.Returning(Completion("{\"narrative\":\"Two days.\"}", reasoning: "Small job."));
            var runner = new AgentRunner(setup.Broker, new OpenAiCompatibleClient(llm.CreateClient()));

            await runner.RunAsync("cost-estimator", Array.Empty<ToolCall>(), "call-7");

            var entry = Assert.Single(setup.Reasoning);
            Assert.Equal("call-7", entry.ToolCallId);
            Assert.Equal("agent: Cost estimator", ChatTranscript.ReadString(entry, "stage"));
            Assert.Equal("Cost estimator (reasoner)", entry.Brain);
        }
    }
}
