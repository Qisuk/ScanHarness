using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScanHarness.Models;

namespace ScanHarness.Services
{
    /// <summary>A brain's summary of a finished action.</summary>
    /// <param name="Text">The summary shown to the user and logged.</param>
    /// <param name="FallbackReason">
    /// Set when the brain couldn't write the summary itself (e.g. the LLM was unreachable) and the
    /// rule-based summary from <see cref="ToolResultSummarizer"/> was used instead.
    /// </param>
    /// <param name="Reasoning">The model's reasoning while writing the summary, when it is a reasoning model.</param>
    public sealed record ActionSummary(string Text, string? FallbackReason = null, string? Reasoning = null);

    /// <summary>
    /// Builds a plain-language summary of a finished tool call from the call itself and the tool's
    /// actual response: what the user asked for, then what the tool returned. Every fact comes from
    /// the result, so the summary can't overstate what the tool found.
    /// </summary>
    public static class ToolResultSummarizer
    {
        private const int MaxListedItems = 5;

        /// <summary>Summarises <paramref name="call"/>, e.g. "The user asked me to scan … The scan tool …".</summary>
        public static string Summarize(ToolCall call)
        {
            var request = $"The user asked me to {DescribeRequest(call)}.";
            var tool = ToolName(call);

            return call.Status switch
            {
                ToolCallStatus.Cancelled => $"{request} The user rejected the request, so the {tool} was not run.",
                ToolCallStatus.Failed => $"{request} The {tool} failed" +
                                         (string.IsNullOrWhiteSpace(call.Error) ? "" : $" ({call.Error.TrimEnd('.')})") +
                                         ", so there is no result.",
                ToolCallStatus.Completed => $"{request} {DescribeResult(call, tool)}",
                _ => $"{request} The {tool} hasn't finished yet."
            };
        }

        /// <summary>What the user asked for, phrased to follow "The user asked me to …".</summary>
        public static string DescribeRequest(ToolCall call)
        {
            call.Parameters.TryGetValue("url", out var url);
            call.Parameters.TryGetValue("location", out var location);

            return call.Tool switch
            {
                "scanner" => $"scan {url} for links",
                "attack" => $"run a simulated attack against {url}",
                "weather" => location != null ? $"get the weather forecast for {location}" : "get the weather forecast",
                ToolDefinition.AgentTool => $"ask the {AgentName(call)}",
                _ => $"run the {call.Tool}/{call.Action} tool"
            };
        }

        /// <summary>Short name for the tool used in sentences, e.g. "scan tool".</summary>
        public static string ToolName(ToolCall call) => call.Tool switch
        {
            "scanner" => "scan tool",
            "attack" => "attack tool",
            "weather" => "weather tool",
            ToolDefinition.AgentTool => AgentName(call),
            _ => $"{call.Tool} tool"
        };

        private static string DescribeResult(ToolCall call, string tool)
        {
            JsonNode? result;
            try
            {
                result = string.IsNullOrWhiteSpace(call.Result) ? null : JsonNode.Parse(call.Result);
            }
            catch (JsonException)
            {
                return $"The {tool} returned a response I couldn't read.";
            }

            if (result == null)
            {
                return $"The {tool} returned an empty response.";
            }

            try
            {
                return call.Tool switch
                {
                    "scanner" when result is JsonObject scan => DescribeScan(scan),
                    "attack" when result is JsonObject attack => DescribeAttack(attack),
                    "weather" when result is JsonArray forecast => DescribeForecast(forecast),
                    ToolDefinition.AgentTool when result is JsonObject agent => DescribeAgent(agent, tool),
                    _ => $"The {tool} returned: {Truncate(result.ToJsonString(), 300)}"
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                // Unexpected shape (e.g. a number where text was expected); show the raw result instead.
                return $"The {tool} returned: {Truncate(result.ToJsonString(), 300)}";
            }
        }

        /// <summary>An agent's display name in lower case, e.g. "findings analyst".</summary>
        public static string AgentName(ToolCall call) =>
            (Agents.AgentCatalog.Find(call.Action)?.Name ?? call.Action).ToLowerInvariant();

        // Agents return a summary built by code from their structured output.
        private static string DescribeAgent(JsonObject agent, string name)
        {
            var summary = agent["summary"]?.GetValue<string>();
            var brain = agent["brain"]?.GetValue<string>();
            var by = string.IsNullOrWhiteSpace(brain) ? "" : $" ({brain})";
            return string.IsNullOrWhiteSpace(summary)
                ? $"The {name}{by} returned no summary."
                : $"The {name}{by} reported: {EnsurePeriod(summary.Trim())}";
        }

        private static string DescribeScan(JsonObject scan)
        {
            var sentence = StatusSentence("scan tool", scan);
            var links = scan["links"]?.AsArray()
                .Select(l => l?["linkText"]?.GetValue<string>() ?? l?["url"]?.GetValue<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .ToList() ?? new List<string>();

            return links.Count == 0
                ? $"{sentence} No links were returned."
                : $"{sentence} It returned {Count(links.Count, "link")}: {JoinList(links)}.";
        }

        private static string DescribeAttack(JsonObject attack)
        {
            var sentence = StatusSentence("attack tool", attack);
            var count = attack["vulnerabilitiesFound"]?.GetValue<int>() ?? 0;
            var severity = attack["severity"]?.GetValue<string>();
            var details = attack["details"] as JsonObject;
            var testsRun = details?["tests_run"]?.GetValue<int>();
            var findings = details?["findings"]?.AsArray()
                .Select(f => f?.GetValue<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f!)
                .ToList() ?? new List<string>();

            var tests = testsRun is > 0 ? $" across {testsRun} tests" : "";
            if (count == 0)
            {
                return $"{sentence} It found no vulnerabilities{tests}.";
            }

            var severityText = string.IsNullOrWhiteSpace(severity) || severity == "none" ? "" : $"{severity}-severity ";
            var including = findings.Count == 0 ? "" : $", including {JoinList(findings)}";
            return $"{sentence} It found {Count(count, severityText + "vulnerability", severityText + "vulnerabilities")}{tests}{including}.";
        }

        private static string DescribeForecast(JsonArray forecast)
        {
            var days = forecast.OfType<JsonObject>().ToList();
            if (days.Count == 0)
            {
                return "The weather tool returned no forecast.";
            }

            var location = days[0]["location"]?.GetValue<string>();
            var temperatures = days.Select(d => d["temperatureC"]?.GetValue<int>()).OfType<int>().ToList();
            var first = days[0];
            var firstDate = DateOnly.TryParse(first["date"]?.GetValue<string>(), CultureInfo.InvariantCulture, out var date)
                ? date.ToString("ddd d MMM", CultureInfo.InvariantCulture)
                : "the first day";

            var where = location != null ? $" for {location}" : "";
            var range = temperatures.Count > 0 ? $" with temperatures from {temperatures.Min()}°C to {temperatures.Max()}°C" : "";
            var firstDay = $"{firstDate} is {first["temperatureC"]?.GetValue<int>()}°C and {first["summary"]?.GetValue<string>() ?? "unknown"}";

            return $"The weather tool returned a {days.Count}-day forecast{where}{range}; {firstDay}.";
        }

        // e.g. The scan tool reported "success": Scan completed successfully. Found 5 links.
        private static string StatusSentence(string tool, JsonObject result)
        {
            var status = result["status"]?.GetValue<string>() ?? "unknown";
            var message = result["message"]?.GetValue<string>()?.Trim();
            return string.IsNullOrWhiteSpace(message)
                ? $"The {tool} reported \"{status}\"."
                : $"The {tool} reported \"{status}\": {EnsurePeriod(message)}";
        }

        private static string Count(int n, string singular, string? plural = null) =>
            $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";

        // "a", "a and b", "a, b and c", or the first few followed by "and N more".
        private static string JoinList(IReadOnlyList<string> items)
        {
            var shown = items.Take(MaxListedItems).ToList();
            var more = items.Count - shown.Count;
            if (more > 0)
            {
                return string.Join(", ", shown) + $" and {more} more";
            }

            return shown.Count == 1 ? shown[0] : string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
        }

        private static string EnsurePeriod(string text) => text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

        private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
    }
}
