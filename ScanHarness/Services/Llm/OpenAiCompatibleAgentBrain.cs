using System.Text.Json;
using System.Text.Json.Nodes;
using ScanHarness.Models;

namespace ScanHarness.Services.Llm
{
    /// <summary>
    /// Agent brain backed by any OpenAI-compatible chat completions endpoint. The model only
    /// chooses a tool and its arguments; the harness validates the arguments and decides approval
    /// and the user-facing confirmation text from <see cref="ToolCatalog"/>, so model output never
    /// bypasses the attack confirmation.
    /// </summary>
    public class OpenAiCompatibleAgentBrain : IAgentBrain
    {
        private const string NoToolMessage =
            "I don't have a suitable tool for that request. I can scan a URL for links, run a simulated attack " +
            "against a URL, or get a weather forecast.";

        private const string SharedInstructions =
            "You are the decision-making brain of ScanHarness, a demo harness whose tools are simulations " +
            "that return canned responses and never touch the network.\n" +
            "Decide whether one of the available tools fits the user's request.\n" +
            "- If a tool fits, choose exactly one tool.\n" +
            "- Only use argument values the user actually gave. Never invent a URL or a place.\n" +
            "- If the tool needs a URL or domain and the user didn't give one, choose no tool and ask for it.\n" +
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
        /// <param name="classification">
        /// Internal or external (see <see cref="LlmOptions.ClassificationFor"/>). Defaults to external, so
        /// controls scoped to external brains still apply when nothing better is known.
        /// </param>
        public OpenAiCompatibleAgentBrain(
            OpenAiCompatibleClient client,
            string model,
            LlmDecisionMode mode = LlmDecisionMode.Tools,
            double temperature = 0,
            bool allowModeFallback = true,
            Classification classification = Classification.External)
        {
            _client = client;
            Model = model;
            Mode = mode;
            _temperature = temperature;
            _allowModeFallback = allowModeFallback;
            Classification = classification;
        }

        /// <summary>The model id sent to the endpoint.</summary>
        public string Model { get; }

        /// <summary>The decision mode in use; may change from Tools to JsonSchema after a fallback.</summary>
        public LlmDecisionMode Mode { get; private set; }

        public string Name => $"{Model} (LLM)";

        public Classification Classification { get; }

        public async Task<ToolDecision> DecideAsync(string prompt)
        {
            var text = (prompt ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return ToolDecision.NoTool(
                    "Tell me what you'd like to do — for example, \"scan example.com for links\", \"attack example.com\" " +
                    "or \"what's the weather in London?\".");
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
                return Mode == LlmDecisionMode.Tools ? ParseToolCallResponse(response, text) : ParseJsonSchemaResponse(response, text);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return ToolDecision.Failed($"The {Model} brain returned a response I couldn't understand.", ex.Message);
            }
        }

        /// <summary>
        /// Asks the model to summarise the finished call from the tool's actual response. Falls back to
        /// the rule-based <see cref="ToolResultSummarizer"/> when the model fails or replies with nothing.
        /// </summary>
        public async Task<ActionSummary> SummarizeAsync(string prompt, ToolCall toolCall)
        {
            try
            {
                var response = await _client.CreateChatCompletionAsync(BuildSummaryRequest(prompt, toolCall));
                var text = CleanSummary(FirstChoiceMessage(response)?["content"]?.GetValue<string>());
                if (text.Length > 0)
                {
                    return new ActionSummary(text);
                }

                return new ActionSummary(ToolResultSummarizer.Summarize(toolCall), "the model returned an empty summary");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmException or JsonException or InvalidOperationException)
            {
                var reason = ex is TaskCanceledException ? "the request timed out" : ex.Message;
                return new ActionSummary(ToolResultSummarizer.Summarize(toolCall), reason);
            }
        }

        /// <summary>
        /// Asks the model to summarise the session from its per-action summaries. The word limit is
        /// enforced on the reply whatever the model returns; on failure the rule-based summary is used.
        /// </summary>
        public async Task<ActionSummary> SummarizeSessionAsync(IReadOnlyList<ToolCall> toolCalls)
        {
            if (toolCalls.Count == 0)
            {
                return new ActionSummary(SessionReportBuilder.EmptySessionSummary);
            }

            try
            {
                var response = await _client.CreateChatCompletionAsync(BuildSessionSummaryRequest(toolCalls));
                var text = CleanSessionSummary(FirstChoiceMessage(response)?["content"]?.GetValue<string>());
                if (text.Length > 0)
                {
                    return new ActionSummary(SessionReportBuilder.LimitWords(text, SessionReportBuilder.MaxSummaryWords));
                }

                return new ActionSummary(SessionReportBuilder.CombineSummaries(toolCalls), "the model returned an empty summary");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmException or JsonException or InvalidOperationException)
            {
                var reason = ex is TaskCanceledException ? "the request timed out" : ex.Message;
                return new ActionSummary(SessionReportBuilder.CombineSummaries(toolCalls), reason);
            }
        }

        /// <summary>
        /// Asks the model to summarise activity across users and sessions. It only sees what the controls
        /// allowed (see <see cref="ActivitySummaryBuilder"/>); the word limit is enforced on the reply and
        /// failures fall back to the rule-based summary.
        /// </summary>
        public async Task<ActionSummary> SummarizeActivityAsync(IReadOnlyList<ActivityRow> rows, string window)
        {
            if (rows.Count == 0)
            {
                return new ActionSummary(ActivitySummaryBuilder.NoActivity);
            }

            try
            {
                var response = await _client.CreateChatCompletionAsync(BuildActivitySummaryRequest(rows, window));
                var text = CleanSessionSummary(FirstChoiceMessage(response)?["content"]?.GetValue<string>());
                if (text.Length > 0)
                {
                    return new ActionSummary(SessionReportBuilder.LimitWords(text, SessionReportBuilder.MaxSummaryWords));
                }

                return new ActionSummary(ActivitySummaryBuilder.Combine(rows, window), "the model returned an empty summary");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmException or JsonException or InvalidOperationException)
            {
                var reason = ex is TaskCanceledException ? "the request timed out" : ex.Message;
                return new ActionSummary(ActivitySummaryBuilder.Combine(rows, window), reason);
            }
        }

        /// <summary>Most requests sent to the model in one activity summary (the most recent ones).</summary>
        internal const int MaxActivityRequestsForModel = 200;

        /// <summary>Builds the activity summary request: the window, the overview, and each request's safe outcome, oldest first.</summary>
        internal JsonObject BuildActivitySummaryRequest(IReadOnlyList<ActivityRow> rows, string window)
        {
            var included = rows
                .OrderByDescending(r => r.Turn.RequestedAt)
                .Take(MaxActivityRequestsForModel)
                .OrderBy(r => r.Turn.RequestedAt)
                .ToList();

            var requests = new JsonArray();
            foreach (var row in included)
            {
                requests.Add(new JsonObject
                {
                    ["time"] = row.Turn.RequestedAt.ToString("O"),
                    ["user"] = row.Turn.UserName,
                    ["request"] = ActivitySummaryBuilder.SafePrompt(row),
                    ["tool"] = row.Turn.ToolCall == null ? null : $"{row.Turn.ToolCall.Tool}/{row.Turn.ToolCall.Action}",
                    ["status"] = row.Turn.ToolCall?.Status.ToString(),
                    ["outcome"] = ActivitySummaryBuilder.Outcome(row),
                    ["controls"] = new JsonArray(row.Controls
                        .Select(v => (JsonNode?)$"{(v.Mode == Controls.ControlMode.Block ? "Block" : "Warning")}: {v.StageName} '{v.Name}' on the {v.TargetDescription}")
                        .ToArray()),
                });
            }

            var context = new JsonObject
            {
                ["window"] = window,
                ["overview"] = ActivitySummaryBuilder.Overview(rows).Describe(),
                ["requests_not_included"] = rows.Count - included.Count,
                ["requests"] = requests,
            };

            return new JsonObject
            {
                ["model"] = Model,
                ["temperature"] = _temperature,
                ["stream"] = false,
                ["max_tokens"] = 800,
                ["messages"] = Messages(ActivitySummaryInstructions, context.ToJsonString()),
            };
        }

        private const string ActivitySummaryInstructions =
            "You summarise activity in ScanHarness (a demo harness whose tools are simulations) for an administrator.\n" +
            "You are given a time window, an overview, and the requests users made in it, each with its outcome and any " +
            "guardrail controls (warnings or blocks) it tripped.\n" +
            "Write one summary in plain prose of no more than 450 words: what happened overall, what each user did, which " +
            "tools were used and how they went, and every warning and block (who, and what the control was). Mention if " +
            "some requests were not included.\n" +
            "Use only facts from the given data. Never invent findings, numbers, names or URLs, and never guess at content " +
            "marked withheld or blocked.\n" +
            "Plain text only: no markdown, headings or bullet lists. Short paragraphs are fine.";

        /// <summary>Builds the session summary request: each call's outcome and summary, oldest first.</summary>
        internal JsonObject BuildSessionSummaryRequest(IReadOnlyList<ToolCall> toolCalls)
        {
            var actions = new JsonArray();
            foreach (var (call, index) in toolCalls.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal).Select((c, i) => (c, i)))
            {
                actions.Add(new JsonObject
                {
                    ["number"] = index + 1,
                    ["action"] = ToolResultSummarizer.DescribeRequest(call),
                    ["status"] = call.Status.ToString(),
                    ["summary"] = string.IsNullOrWhiteSpace(call.Summary) ? ToolResultSummarizer.Summarize(call) : call.Summary,
                });
            }

            return new JsonObject
            {
                ["model"] = Model,
                ["temperature"] = _temperature,
                ["stream"] = false,
                // A little over 450 words of English; the hard 500-word limit is applied afterwards.
                ["max_tokens"] = 800,
                ["messages"] = Messages(SessionSummaryInstructions, new JsonObject { ["actions"] = actions }.ToJsonString()),
            };
        }

        private const string SessionSummaryInstructions =
            "You summarise a ScanHarness session (a demo harness whose tools are simulations) for the user.\n" +
            "You are given every action taken in the session, in order, each with its status and an existing summary.\n" +
            "Write one overall summary in plain prose of no more than 450 words: what the user asked for across the " +
            "session, what the tools reported (group similar actions together), which actions failed or were rejected, " +
            "and any notable results.\n" +
            "Use only facts from the given summaries. Never invent findings, numbers, names or URLs.\n" +
            "Plain text only: no markdown, headings or bullet lists. Short paragraphs are fine.";

        // Keeps paragraph breaks (unlike CleanSummary) but drops markdown emphasis, wrapping quotes and extra blank lines.
        private static string CleanSessionSummary(string? text)
        {
            var cleaned = (text ?? "").Replace("**", "").Replace("\r\n", "\n").Trim().Trim('"', '“', '”').Trim();
            var paragraphs = cleaned.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(p => string.Join(" ", p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                .Where(p => p.Length > 0);
            return string.Join("\n\n", paragraphs);
        }

        /// <summary>Builds the summary request: the user's request, what was done, and the tool's raw response.</summary>
        internal JsonObject BuildSummaryRequest(string prompt, ToolCall toolCall)
        {
            var outcome = toolCall.Status switch
            {
                ToolCallStatus.Completed => "completed",
                ToolCallStatus.Failed => "failed",
                ToolCallStatus.Cancelled => "rejected by the user, so the tool was not run",
                var other => other.ToString()
            };

            var context = new JsonObject
            {
                ["user_request"] = prompt,
                ["action"] = ToolResultSummarizer.DescribeRequest(toolCall),
                ["tool"] = ToolResultSummarizer.ToolName(toolCall),
                ["outcome"] = outcome,
                ["error"] = toolCall.Error,
                ["tool_response"] = ToolResponseNode(toolCall.Result),
            };

            return new JsonObject
            {
                ["model"] = Model,
                ["temperature"] = _temperature,
                ["stream"] = false,
                ["messages"] = Messages(SummaryInstructions, context.ToJsonString()),
            };
        }

        private const string SummaryInstructions =
            "You report the outcome of a tool call made by ScanHarness, a demo harness whose tools are simulations.\n" +
            "Write two or three plain sentences. Start with \"The user asked me to <action>.\" using the given action, " +
            "then say what the tool returned: the status and the key facts, such as counts, severities, notable " +
            "findings, links or temperatures.\n" +
            "Use only facts that appear in tool_response. Never invent findings, numbers, names or URLs.\n" +
            "If the outcome is failed or rejected, say so and don't describe any result.\n" +
            "Plain text only: no markdown, lists or quotes around the answer.";

        private const int MaxToolResponseChars = 4000;

        // Embeds the tool's JSON response as JSON (so the model sees structure), truncated as text if very large.
        private static JsonNode? ToolResponseNode(string? result)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                return null;
            }

            if (result.Length > MaxToolResponseChars)
            {
                return JsonValue.Create(result[..MaxToolResponseChars] + "… (truncated)");
            }

            try
            {
                return JsonNode.Parse(result);
            }
            catch (JsonException)
            {
                return JsonValue.Create(result);
            }
        }

        // Models sometimes wrap the answer in quotes or markdown emphasis; strip that and collapse whitespace.
        private static string CleanSummary(string? text)
        {
            var cleaned = (text ?? "").Trim().Trim('"', '“', '”').Replace("**", "").Trim();
            return string.Join(" ", cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
                var tools = string.Join("\n", ToolCatalog.All.Select(t =>
                    $"- \"{t.FunctionName}\": {t.Description} Arguments: " +
                    string.Join(", ", t.Parameters.Select(p => $"{p.Name} ({(p.Required ? "required" : "optional")}): {p.Description}"))));
                var fields = string.Join(", ", ToolCatalog.AllParameters.Select(p => $"\"{p.Name}\""));
                var instructions = SharedInstructions +
                    $"\nAvailable tools:\n{tools}\n" +
                    "Respond only with a JSON object with the fields \"tool\" (one of the tool names, or \"none\"), " +
                    $"{fields} (the chosen tool's arguments; null when not given or not used), " +
                    "and \"message\" (a short reply to the user).";

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

        private static ToolDecision ParseToolCallResponse(JsonObject response, string prompt)
        {
            var message = FirstChoiceMessage(response);
            var content = message?["content"]?.GetValue<string>()?.Trim();

            var toolCall = message?["tool_calls"]?.AsArray().FirstOrDefault();
            if (toolCall != null)
            {
                var function = toolCall["function"];
                var name = function?["name"]?.GetValue<string>();
                return DecisionFor(name, ParseArguments(function?["arguments"]), prompt);
            }

            // Some small models write the tool call as JSON in the message text instead of tool_calls,
            // e.g. {"name": "simple_scanner", "arguments": {"url": "example.com"}}.
            if (TryParseJsonObject(content, out var inline) && inline["name"] is JsonValue)
            {
                var name = inline["name"]!.GetValue<string>();
                if (ToolCatalog.FindByFunctionName(name) != null)
                {
                    return DecisionFor(name, ParseArguments(inline["arguments"] ?? inline["parameters"]), prompt);
                }
            }

            return ToolDecision.NoTool(string.IsNullOrWhiteSpace(content) ? NoToolMessage : content);
        }

        private static ToolDecision ParseJsonSchemaResponse(JsonObject response, string prompt)
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

            // Arguments are top-level fields next to "tool" in this mode.
            return DecisionFor(tool, decision, prompt);
        }

        /// <summary>
        /// Turns the model's chosen function and arguments into a decision: validates each argument
        /// against the catalog, drops values the user never typed, and applies the catalog's approval rules.
        /// </summary>
        private static ToolDecision DecisionFor(string? functionName, JsonObject? arguments, string prompt)
        {
            var tool = ToolCatalog.FindByFunctionName(functionName);
            if (tool == null)
            {
                return ToolDecision.NoTool($"The model asked for a tool I don't have ('{functionName}'). {NoToolMessage}");
            }

            var parameters = new Dictionary<string, string>();
            foreach (var parameter in tool.Parameters)
            {
                var value = ValidateArgument(parameter, ReadString(arguments?[parameter.Name]), prompt);
                if (value != null)
                {
                    parameters[parameter.Name] = value;
                }
                else if (parameter.Required)
                {
                    return ToolDecision.NoTool(parameter.Type == ToolParameterType.Url
                        ? $"I can use the {tool.FunctionName} tool, but I need a target URL. Which URL should I use?"
                        : $"I can use the {tool.FunctionName} tool, but I need the {parameter.Name}.");
                }
            }

            return new ToolDecision
            {
                ToolFound = true,
                Tool = tool.Tool,
                Action = tool.Action,
                Parameters = parameters,
                RequiresApproval = tool.RequiresApproval,
                Message = tool.FormatConfirmation(parameters),
            };
        }

        /// <summary>
        /// Returns the cleaned argument value, or null when it's missing, malformed, or not something the
        /// user said. Models asked to fill an argument will otherwise invent one (e.g. "launch an attack"
        /// → example.com, "what's the weather?" → London), and a tool must never act on a value the user
        /// didn't give.
        /// </summary>
        internal static string? ValidateArgument(ToolParameter parameter, string? rawValue, string prompt)
        {
            switch (parameter.Type)
            {
                case ToolParameterType.Url:
                    var url = NormalizeUrl(rawValue);
                    if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        return null;
                    }

                    var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
                    return prompt.Contains(host, StringComparison.OrdinalIgnoreCase) ? url : null;

                default:
                    var text = rawValue?.Trim().TrimEnd('.', ',', '!', '?');
                    return !string.IsNullOrWhiteSpace(text) && prompt.Contains(text, StringComparison.OrdinalIgnoreCase)
                        ? text
                        : null;
            }
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
        private static JsonObject? ParseArguments(JsonNode? arguments) =>
            arguments is JsonValue value && value.TryGetValue<string>(out var json)
                ? (TryParseJsonObject(json, out var parsed) ? parsed : null)
                : arguments as JsonObject;

        // Strings are used as-is; numbers and booleans are converted; null and objects are ignored.
        private static string? ReadString(JsonNode? node) => node is JsonValue value ? value.ToString() : null;

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

        private static JsonNode ToToolSchema(ToolDefinition tool)
        {
            var properties = new JsonObject();
            foreach (var parameter in tool.Parameters)
            {
                properties[parameter.Name] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = parameter.Description,
                };
            }

            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.FunctionName,
                    ["description"] = tool.Description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = new JsonArray(tool.Parameters.Where(p => p.Required).Select(p => (JsonNode?)p.Name).ToArray()),
                    },
                },
            };
        }

        // Flat decision shape: {"tool", <every parameter across all tools, nullable>, "message"}.
        // Strict JSON schema needs every property required, so unused arguments are null.
        private static JsonObject DecisionSchema()
        {
            var properties = new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(ToolCatalog.All.Select(t => (JsonNode?)t.FunctionName).Append("none").ToArray()),
                },
            };

            foreach (var parameter in ToolCatalog.AllParameters)
            {
                properties[parameter.Name] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] = parameter.Description,
                };
            }

            properties["message"] = new JsonObject { ["type"] = "string" };

            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(properties.Select(p => (JsonNode?)p.Key).ToArray()),
                ["additionalProperties"] = false,
            };
        }
    }
}
