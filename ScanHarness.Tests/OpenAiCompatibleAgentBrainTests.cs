using System.Net;
using System.Text.Json.Nodes;
using ScanHarness.Services.Llm;
using Xunit;

namespace ScanHarness.Tests
{
    public class OpenAiCompatibleAgentBrainTests
    {
        private const string Model = "test-model";

        private static OpenAiCompatibleAgentBrain BrainFor(FakeHttpMessageHandler handler, LlmDecisionMode mode = LlmDecisionMode.Tools) =>
            new(new OpenAiCompatibleClient(handler.CreateClient()), Model, mode);

        // A chat completion whose message calls a single function.
        private static string ToolCallResponse(string function, string argumentsJson) => new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "",
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "call_1",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = function, ["arguments"] = argumentsJson },
                    }),
                },
                ["finish_reason"] = "tool_calls",
            }),
        }.ToJsonString();

        // A chat completion whose message is plain text.
        private static string ContentResponse(string content) => new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                ["finish_reason"] = "stop",
            }),
        }.ToJsonString();

        [Fact]
        public async Task Scanner_tool_call_becomes_a_scanner_decision_without_approval()
        {
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ToolCallResponse("simple_scanner", "{\"url\":\"example.com\"}")));

            var decision = await brain.DecideAsync("scan example.com for links");

            Assert.True(decision.ToolFound);
            Assert.Equal("scanner", decision.Tool);
            Assert.Equal("simple-scanner", decision.Action);
            Assert.False(decision.RequiresApproval);
            Assert.Equal("https://example.com", decision.Parameters["url"]);
            Assert.Null(decision.Error);
        }

        [Fact]
        public async Task Attack_tool_call_always_requires_approval()
        {
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ToolCallResponse("attack", "{\"url\":\"https://example.com\"}")));

            var decision = await brain.DecideAsync("attack example.com");

            Assert.True(decision.ToolFound);
            Assert.Equal("attack", decision.Tool);
            Assert.Equal("execute", decision.Action);
            Assert.True(decision.RequiresApproval);
            Assert.Contains("approve", decision.Message);
        }

        [Fact]
        public async Task Arguments_sent_as_a_json_object_are_accepted()
        {
            var json = ToolCallResponse("simple_scanner", "x").Replace("\"arguments\":\"x\"", "\"arguments\":{\"url\":\"example.org\"}");
            var brain = BrainFor(FakeHttpMessageHandler.Returning(json));

            var decision = await brain.DecideAsync("scan example.org");

            Assert.True(decision.ToolFound);
            Assert.Equal("https://example.org", decision.Parameters["url"]);
        }

        [Fact]
        public async Task Plain_text_reply_means_no_tool_and_is_passed_to_the_user()
        {
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ContentResponse("I can only scan or attack URLs.")));

            var decision = await brain.DecideAsync("what's the weather?");

            Assert.False(decision.ToolFound);
            Assert.Null(decision.Error);
            Assert.Equal("I can only scan or attack URLs.", decision.Message);
        }

        [Fact]
        public async Task Tool_call_written_as_json_in_the_text_is_recognised()
        {
            var content = "```json\n{\"name\": \"simple_scanner\", \"arguments\": {\"url\": \"example.com\"}}\n```";
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ContentResponse(content)));

            var decision = await brain.DecideAsync("scan example.com");

            Assert.True(decision.ToolFound);
            Assert.Equal("scanner", decision.Tool);
        }

        [Fact]
        public async Task Unknown_function_name_is_rejected()
        {
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ToolCallResponse("delete_everything", "{\"url\":\"example.com\"}")));

            var decision = await brain.DecideAsync("delete example.com");

            Assert.False(decision.ToolFound);
            Assert.Contains("delete_everything", decision.Message);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"url\":\"\"}")]
        [InlineData("{\"url\":\"not a url\"}")]
        [InlineData("{\"url\":\"ftp://example.com\"}")]
        public async Task Tool_call_without_a_usable_url_asks_for_one(string arguments)
        {
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ToolCallResponse("simple_scanner", arguments)));

            var decision = await brain.DecideAsync("scan something");

            Assert.False(decision.ToolFound);
            Assert.Contains("need a target URL", decision.Message);
        }

        [Theory]
        [InlineData("attack", "launch an attack")]
        [InlineData("simple_scanner", "scan a website for me")]
        public async Task Target_the_user_never_named_is_rejected(string function, string prompt)
        {
            // The model invents example.com because the url argument is required.
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ToolCallResponse(function, "{\"url\":\"https://example.com\"}")));

            var decision = await brain.DecideAsync(prompt);

            Assert.False(decision.ToolFound);
            Assert.Contains("need a target URL", decision.Message);
        }

        [Theory]
        [InlineData("scan www.example.com please", "https://www.example.com")]
        [InlineData("scan example.com please", "https://www.example.com")]
        [InlineData("scan EXAMPLE.com", "https://example.com/")]
        public async Task Target_named_by_the_user_is_accepted(string prompt, string modelUrl)
        {
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ToolCallResponse("simple_scanner", $"{{\"url\":\"{modelUrl}\"}}")));

            var decision = await brain.DecideAsync(prompt);

            Assert.True(decision.ToolFound);
        }

        [Fact]
        public async Task Unreachable_endpoint_returns_a_failed_decision()
        {
            var brain = BrainFor(FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused")));

            var decision = await brain.DecideAsync("scan example.com");

            Assert.False(decision.ToolFound);
            Assert.Equal("Connection refused", decision.Error);
            Assert.Contains(Model, decision.Message);
        }

        [Fact]
        public async Task Timeout_returns_a_failed_decision()
        {
            var brain = BrainFor(FakeHttpMessageHandler.Throwing(new TaskCanceledException()));

            var decision = await brain.DecideAsync("scan example.com");

            Assert.Equal("the request timed out", decision.Error);
        }

        [Fact]
        public async Task Error_status_surfaces_the_providers_error_message()
        {
            var handler = FakeHttpMessageHandler.Returning("{\"error\":{\"message\":\"model 'test-model' not found\"}}", HttpStatusCode.NotFound);
            var brain = BrainFor(handler);

            var decision = await brain.DecideAsync("scan example.com");

            Assert.False(decision.ToolFound);
            Assert.Contains("model 'test-model' not found", decision.Error);
        }

        [Fact]
        public async Task Ollama_style_string_error_is_surfaced()
        {
            var brain = BrainFor(FakeHttpMessageHandler.Returning("{\"error\":\"model requires more system memory\"}", HttpStatusCode.InternalServerError));

            var decision = await brain.DecideAsync("scan example.com");

            Assert.Contains("model requires more system memory", decision.Error);
        }

        [Fact]
        public async Task Malformed_response_returns_a_failed_decision()
        {
            var brain = BrainFor(FakeHttpMessageHandler.Returning("this is not json"));

            var decision = await brain.DecideAsync("scan example.com");

            Assert.False(decision.ToolFound);
            Assert.NotNull(decision.Error);
        }

        [Fact]
        public async Task Empty_prompt_does_not_call_the_endpoint()
        {
            var handler = FakeHttpMessageHandler.Returning(ContentResponse("unused"));
            var brain = BrainFor(handler);

            var decision = await brain.DecideAsync("   ");

            Assert.False(decision.ToolFound);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Tools_mode_sends_model_tools_and_temperature()
        {
            var handler = FakeHttpMessageHandler.Returning(ContentResponse("ok"));
            await BrainFor(handler).DecideAsync("scan example.com");

            var (request, body) = Assert.Single(handler.Requests);
            var json = JsonNode.Parse(body)!;

            Assert.Equal("http://llm.test/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal(Model, json["model"]!.GetValue<string>());
            Assert.Equal(0, json["temperature"]!.GetValue<double>());
            var toolNames = json["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>());
            Assert.Equal(new[] { "simple_scanner", "attack" }, toolNames);
            Assert.Equal("scan example.com", json["messages"]![1]!["content"]!.GetValue<string>());
        }

        [Fact]
        public async Task Json_schema_mode_requests_structured_output()
        {
            var handler = FakeHttpMessageHandler.Returning(ContentResponse("{\"tool\":\"none\",\"url\":null,\"message\":\"hi\"}"));
            await BrainFor(handler, LlmDecisionMode.JsonSchema).DecideAsync("hello");

            var json = JsonNode.Parse(handler.Requests[0].Body)!;

            Assert.Null(json["tools"]);
            Assert.Equal("json_schema", json["response_format"]!["type"]!.GetValue<string>());
        }

        [Fact]
        public async Task Json_schema_mode_parses_a_tool_decision()
        {
            var content = "{\"tool\":\"attack\",\"url\":\"example.com\",\"message\":\"Attacking now\"}";
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ContentResponse(content)), LlmDecisionMode.JsonSchema);

            var decision = await brain.DecideAsync("attack example.com");

            Assert.True(decision.ToolFound);
            Assert.Equal("attack", decision.Tool);
            Assert.True(decision.RequiresApproval);
            Assert.Equal("https://example.com", decision.Parameters["url"]);
        }

        [Fact]
        public async Task Json_schema_mode_none_returns_the_models_message()
        {
            var content = "{\"tool\":\"none\",\"url\":null,\"message\":\"No tool fits that.\"}";
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ContentResponse(content)), LlmDecisionMode.JsonSchema);

            var decision = await brain.DecideAsync("write a poem");

            Assert.False(decision.ToolFound);
            Assert.Null(decision.Error);
            Assert.Equal("No tool fits that.", decision.Message);
        }

        [Fact]
        public async Task Json_schema_mode_with_non_json_content_fails_cleanly()
        {
            var brain = BrainFor(FakeHttpMessageHandler.Returning(ContentResponse("Sure! I'll scan it.")), LlmDecisionMode.JsonSchema);

            var decision = await brain.DecideAsync("scan example.com");

            Assert.False(decision.ToolFound);
            Assert.NotNull(decision.Error);
        }

        // First request (with tools) is rejected; later requests get a JSON-mode decision.
        private static FakeHttpMessageHandler ToolsUnsupportedThenJson(string jsonContent)
        {
            var calls = 0;
            return new FakeHttpMessageHandler(_ => calls++ == 0
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"registry.ollama.ai/library/test-model does not support tools\"}}")
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ContentResponse(jsonContent)) });
        }

        [Fact]
        public async Task Model_without_tool_support_falls_back_to_json_schema_mode()
        {
            var handler = ToolsUnsupportedThenJson("{\"tool\":\"simple_scanner\",\"url\":\"example.com\",\"message\":\"ok\"}");
            var brain = BrainFor(handler);

            var decision = await brain.DecideAsync("scan example.com");

            Assert.True(decision.ToolFound);
            Assert.Equal("scanner", decision.Tool);
            Assert.Equal(LlmDecisionMode.JsonSchema, brain.Mode);
            Assert.Equal(2, handler.Requests.Count);
            Assert.NotNull(JsonNode.Parse(handler.Requests[0].Body)!["tools"]);
            Assert.NotNull(JsonNode.Parse(handler.Requests[1].Body)!["response_format"]);

            // The fallback sticks, so the next decision goes straight to JSON mode.
            await brain.DecideAsync("scan example.org");
            Assert.Equal(3, handler.Requests.Count);
            Assert.Null(JsonNode.Parse(handler.Requests[2].Body)!["tools"]);
        }

        [Fact]
        public async Task Fallback_can_be_disabled()
        {
            var handler = ToolsUnsupportedThenJson("{\"tool\":\"none\",\"url\":null,\"message\":\"unused\"}");
            var brain = new OpenAiCompatibleAgentBrain(new OpenAiCompatibleClient(handler.CreateClient()), Model, allowModeFallback: false);

            var decision = await brain.DecideAsync("scan example.com");

            Assert.Contains("does not support tools", decision.Error);
            Assert.Single(handler.Requests);
            Assert.Equal(LlmDecisionMode.Tools, brain.Mode);
        }

        [Theory]
        [InlineData("example.com", "https://example.com")]
        [InlineData("http://example.com/path?q=1", "http://example.com/path?q=1")]
        [InlineData("  sub.example.co.uk.  ", "https://sub.example.co.uk")]
        [InlineData("http://localhost:8080", "http://localhost:8080")]
        [InlineData("example", null)]
        [InlineData("javascript:alert(1)", null)]
        [InlineData(null, null)]
        public void NormalizeUrl_accepts_web_addresses_only(string? input, string? expected)
        {
            Assert.Equal(expected, OpenAiCompatibleAgentBrain.NormalizeUrl(input));
        }
    }
}
