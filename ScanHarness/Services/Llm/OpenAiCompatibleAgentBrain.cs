using System.Text.Json;
using System.Text.Json.Nodes;
using ScanHarness.Models;

namespace ScanHarness.Services.Llm
{
    /// <summary>
    /// Agent brain backed by any OpenAI-compatible chat completions endpoint. The model only
    /// chooses a tool and its target URL; the harness decides approval and the user-facing
    /// confirmation text from <see cref="ToolCatalog"/>, so model output never bypasses the
    /// attack confirmation.
    /// </summary>
    public class OpenAiCompatibleAgentBrain : IAgentBrain
    {
        private const string NoToolMessage =
            "I don't have a suitable tool for that request. I can scan a URL for links, or run a simulated attack against a URL.";

        private const string SharedInstructions =
            "You are the decision-making brain of ScanHarness, a demo harness whose tools are simulations " +
            "that return canned responses and never touch the network.\n" +
            "Decide whether one of the available tools fits the user's request.\n" +
            "- If a tool fits and the user named a target URL or domain, choose exactly one tool with that URL.\n" +
            "- If a tool fits but no URL or domain was given, choose no tool and ask the user for the URL.\n" +
            "- If no tool fits, choose no tool and briefly say which tools you have.\n" +
            "- Use the attack tool only when the user explicitly asks to attack, exploit, hack or breach a target. " +
            "Requests to scan, crawl, map or list links use the scanner, even if they mention not attacking.\n" +
            "Keep any reply to one or two short sentences.";

        private readonly OpenAiCompatibleClient _client;
        private readonly double _temperature;
        private readonly bool _allowModeFallback;

        /// <param name="allowModeFallback">
        /// When true, a model the server says can't use tools is switched to <see cref="LlmDecisionMode.JsonSchema"/>
        /// and the request retried. The eval turns this off so each mode is measured as configured.
        /// </param>
        public OpenAiCompatibleAgentBrain(
            OpenAiCompatibleClient client,
            string model,
            LlmDecisionMode mode = LlmDecisionMode.Tools,
            double temperature = 0,
            bool allowModeFallback = true)
        {
            _client = client;
            Model = model;
            Mode = mode;
            _temperature = temperature;
            _allowModeFallback = allowModeFallback;
        }

        /// <summary>The model id sent to the endpoint.</summary>
        public string Model { get; }

        /// <summary>The decision mode in use; may change from Tools to JsonSchema after a fallback.</summary>
        public LlmDecisionMode Mode { get; private set; }

        public string Name => $"{Model} (LLM)";

        public async Task<ToolDecision> DecideAsync(string prompt)
        {
            var text = (prompt ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return ToolDecision.NoTool(
                    "Tell me what you'd like to do — for example, \"scan example.com for links\" or \"attack example.com\".");
            }

            JsonObject response;
            try
            {
                try
                {
                    response = await _client.CreateChatCompletionAsync(BuildRequest(text));
                }
                catch (LlmException ex) when (_allowModeFallback && Mode == LlmDecisionMode.Tools && IsToolsUnsupported(ex))
                {
                    // e.g. Ollama: "<model> does not support tools". Structured output works on any chat model.
                    Mode = LlmDecisionMode.JsonSchema;
                    response = await _client.CreateChatCompletionAsync(BuildRequest(text));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmException or JsonException)
            {
                var reason = ex is TaskCanceledException ? "the request timed out" : ex.Message;
                return ToolDecision.Failed($"The {Model} brain couldn't make a decision: {reason}", reason);
            }

            try
            {
                var decision = Mode == LlmDecisionMode.Tools ? ParseToolCallResponse(response) : ParseJsonSchemaResponse(response);
                return RequireTargetNamedByUser(decision, text);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return ToolDecision.Failed($"The {Model} brain returned a response I couldn't understand.", ex.Message);
            }
        }

        /// <summary>Builds the chat completion request for the configured decision mode.</summary>
        internal JsonObject BuildRequest(string prompt)
        {
            var request = new JsonObject
            {
                ["model"] = Model,
                ["temperature"] = _temperature,
                ["stream"] = false,
            };

            if (Mode == LlmDecisionMode.Tools)
            {
                request["messages"] = Messages(SharedInstructions, prompt);
                request["tools"] = new JsonArray(ToolCatalog.All.Select(ToToolSchema).ToArray<JsonNode?>());
                request["tool_choice"] = "auto";
            }
            else
            {
                var toolNames = string.Join(", ", ToolCatalog.All.Select(t => $"\"{t.FunctionName}\" ({t.Description})"));
                var instructions = SharedInstructions +
                    $"\nAvailable tools: {toolNames}.\n" +
                    "Respond only with a JSON object: {\"tool\": one of the tool names or \"none\", " +
                    "\"url\": the target URL or null, \"message\": a short reply to the user}.";

                request["messages"] = Messages(instructions, prompt);
                request["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "tool_decision",
                        ["strict"] = true,
                        ["schema"] = DecisionSchema(),
                    },
                };
            }

            return request;
        }

        private ToolDecision ParseToolCallResponse(JsonObject response)
        {
            var message = FirstChoiceMessage(response);
            var content = message?["content"]?.GetValue<string>()?.Trim();

            var toolCall = message?["tool_calls"]?.AsArray().FirstOrDefault();
            if (toolCall != null)
            {
                var function = toolCall["function"];
                var name = function?["name"]?.GetValue<string>();
                return DecisionFor(name, ReadUrlArgument(function?["arguments"]));
            }

            // Some small models write the tool call as JSON in the message text instead of tool_calls,
            // e.g. {"name": "simple_scanner", "arguments": {"url": "example.com"}}.
            if (TryParseJsonObject(content, out var inline) && inline["name"] is JsonValue)
            {
                var name = inline["name"]!.GetValue<string>();
                if (ToolCatalog.FindByFunctionName(name) != null)
                {
                    return DecisionFor(name, ReadUrlArgument(inline["arguments"] ?? inline["parameters"]));
                }
            }

            return ToolDecision.NoTool(string.IsNullOrWhiteSpace(content) ? NoToolMessage : content);
        }

        private ToolDecision ParseJsonSchemaResponse(JsonObject response)
        {
            var content = FirstChoiceMessage(response)?["content"]?.GetValue<string>();
            if (!TryParseJsonObject(content, out var decision))
            {
                throw new JsonException("Expected a JSON object in the message content.");
            }

            var tool = decision["tool"]?.GetValue<string>();
            var reply = decision["message"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrWhiteSpace(tool) || tool.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return ToolDecision.NoTool(string.IsNullOrWhiteSpace(reply) ? NoToolMessage : reply);
            }

            return DecisionFor(tool, decision["url"] is JsonValue url ? url.ToString() : null);
        }

        /// <summary>
        /// Rejects a tool call whose target host the user never typed. Models asked to fill a required
        /// <c>url</c> argument will otherwise invent one (e.g. "launch an attack" → example.com), and a
        /// tool must never be pointed at a target the user didn't name.
        /// </summary>
        internal static ToolDecision RequireTargetNamedByUser(ToolDecision decision, string prompt)
        {
            if (!decision.ToolFound || !decision.Parameters.TryGetValue("url", out var url)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return decision;
            }

            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            if (prompt.Contains(host, StringComparison.OrdinalIgnoreCase))
            {
                return decision;
            }

            var tool = ToolCatalog.All.FirstOrDefault(t => t.Tool == decision.Tool)?.FunctionName ?? decision.Tool;
            return ToolDecision.NoTool($"I can use the {tool} tool, but I need a target URL. Which URL should I use?");
        }

        /// <summary>Turns the model's chosen function and URL into a decision, applying the catalog's approval rules.</summary>
        private static ToolDecision DecisionFor(string? functionName, string? rawUrl)
        {
            var tool = ToolCatalog.FindByFunctionName(functionName);
            if (tool == null)
            {
                return ToolDecision.NoTool($"The model asked for a tool I don't have ('{functionName}'). {NoToolMessage}");
            }

            var url = NormalizeUrl(rawUrl);
            if (url == null)
            {
                return ToolDecision.NoTool($"I can use the {tool.FunctionName} tool, but I need a target URL. Which URL should I use?");
            }

            return new ToolDecision
            {
                ToolFound = true,
                Tool = tool.Tool,
                Action = tool.Action,
                Parameters = new Dictionary<string, string> { ["url"] = url },
                RequiresApproval = tool.RequiresApproval,
                Message = tool.FormatConfirmation(url),
            };
        }

        /// <summary>
        /// Accepts "example.com", "https://example.com/path" etc. and returns an absolute http(s) URL,
        /// or null when the value doesn't look like a web address.
        /// </summary>
        internal static string? NormalizeUrl(string? value)
        {
            var text = value?.Trim().TrimEnd('.', ',', ')', ']', '!', '?');
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (!text.Contains("://"))
            {
                text = "https://" + text;
            }

            var isWebAddress = Uri.TryCreate(text, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && (uri.Host.Contains('.') || uri.IsLoopback);

            return isWebAddress ? text : null;
        }

        // Arguments are normally a JSON-encoded string, but some servers send an object.
        private static string? ReadUrlArgument(JsonNode? arguments)
        {
            var args = arguments is JsonValue value && value.TryGetValue<string>(out var json)
                ? (TryParseJsonObject(json, out var parsed) ? parsed : null)
                : arguments as JsonObject;

            return args?["url"] is JsonValue url ? url.ToString() : null;
        }

        /// <summary>Parses a JSON object from model text, tolerating code fences and surrounding prose.</summary>
        internal static bool TryParseJsonObject(string? text, out JsonObject result)
        {
            result = null!;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return false;
            }

            try
            {
                if (JsonNode.Parse(text[start..(end + 1)]) is JsonObject obj)
                {
                    result = obj;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Not valid JSON.
            }

            return false;
        }

        // Servers word this differently ("does not support tools", "tools are not supported", ...).
        private static bool IsToolsUnsupported(LlmException ex) =>
            ex.Message.Contains("support tools", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("tools are not supported", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("tool use is not supported", StringComparison.OrdinalIgnoreCase);

        private static JsonNode? FirstChoiceMessage(JsonObject response) =>
            response["choices"]?.AsArray().FirstOrDefault()?["message"];

        private static JsonArray Messages(string system, string user) => new(
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = user });

        private static JsonNode ToToolSchema(ToolDefinition tool) => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = tool.FunctionName,
                ["description"] = tool.Description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["url"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The target URL or domain, e.g. https://example.com or example.com.",
                        },
                    },
                    ["required"] = new JsonArray("url"),
                },
            },
        };

        private static JsonObject DecisionSchema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(ToolCatalog.All.Select(t => (JsonNode?)t.FunctionName).Append("none").ToArray()),
                },
                ["url"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                ["message"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("tool", "url", "message"),
            ["additionalProperties"] = false,
        };
    }
}
