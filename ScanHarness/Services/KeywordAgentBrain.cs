using System.Text.RegularExpressions;
using ScanHarness.Models;

namespace ScanHarness.Services
{
    /// <summary>
    /// Rule-based brain. Chooses a tool by matching keywords in the user's prompt and
    /// extracting a target URL. Always available, so it's the fallback when no LLM endpoint
    /// is reachable and the baseline LLM brains are compared against in ScanHarness.Eval.
    /// </summary>
    public class KeywordAgentBrain : IAgentBrain
    {
        public string Name => "Keyword brain (rule-based)";

        /// <summary>Runs inside the harness, so always internal.</summary>
        public Classification Classification => Classification.Internal;

        private static readonly string[] AttackKeywords =
            { "attack", "exploit", "hack", "breach", "penetrat", "pwn" };

        private static readonly string[] ScanKeywords =
            { "scan", "link", "crawl", "spider", "enumerate", "map", "discover" };

        private static readonly string[] WeatherKeywords =
            { "weather", "forecast", "temperature" };

        // A capitalised place name after "in", "for" or "at", e.g. "weather in New York".
        private static readonly Regex LocationRegex = new(
            @"\b(?:in|for|at)\s+(\p{Lu}[\p{L}'-]*(?:\s+\p{Lu}[\p{L}'-]*)*)",
            RegexOptions.Compiled);

        // Matches a full URL, or a bare host like "example.com" / "sub.example.co.uk[/path]".
        private static readonly Regex UrlRegex = new(
            @"\b((https?:\/\/)?([a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}(:\d+)?(\/\S*)?)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Task<ToolDecision> DecideAsync(string prompt)
        {
            var text = (prompt ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return Task.FromResult(ToolDecision.NoTool(
                    "Tell me what you'd like to do — for example, \"scan example.com for links\", \"attack example.com\" " +
                    "or \"what's the weather in London?\"."));
            }

            var lower = text.ToLowerInvariant();
            var url = ExtractUrl(text);

            // Agents and any tool added in tools.json with keywords. Checked first: agents only read
            // stored results, so choosing one can't cause anything to happen to a target.
            var keyworded = ToolCatalog.All.FirstOrDefault(t => t.Keywords?.Any(k => lower.Contains(k.ToLowerInvariant())) == true);
            if (keyworded != null)
            {
                return Task.FromResult(DecisionFor(keyworded, url));
            }

            var wantsAttack = AttackKeywords.Any(k => lower.Contains(k));
            var wantsScan = ScanKeywords.Any(k => lower.Contains(k));

            // Attack is the more consequential intent, so it wins if explicitly requested.
            if (wantsAttack)
            {
                if (url == null)
                {
                    return Task.FromResult(ToolDecision.NoTool(
                        "I can run a simulated attack, but I need a target URL. Which URL should I attack?"));
                }

                return Task.FromResult(new ToolDecision
                {
                    ToolFound = true,
                    Tool = "attack",
                    Action = "execute",
                    Parameters = new Dictionary<string, string> { ["url"] = url },
                    RequiresApproval = true, // Attack always requires confirmation.
                    Message = $"This looks like an attack request. I'll run a simulated attack against {url} once you approve."
                });
            }

            if (wantsScan)
            {
                if (url == null)
                {
                    return Task.FromResult(ToolDecision.NoTool(
                        "I can scan a site for links, but I need a target URL. Which URL should I scan?"));
                }

                return Task.FromResult(new ToolDecision
                {
                    ToolFound = true,
                    Tool = "scanner",
                    Action = "simple-scanner",
                    Parameters = new Dictionary<string, string> { ["url"] = url },
                    RequiresApproval = false,
                    Message = $"I'll scan {url} for links using the SimpleScanner tool."
                });
            }

            if (WeatherKeywords.Any(k => lower.Contains(k)))
            {
                // Location is optional: without one the tool still returns a forecast.
                var parameters = new Dictionary<string, string>();
                var location = LocationRegex.Match(text);
                if (location.Success)
                {
                    parameters["location"] = location.Groups[1].Value;
                }

                var weather = ToolCatalog.FindByTool("weather")!;
                return Task.FromResult(new ToolDecision
                {
                    ToolFound = true,
                    Tool = weather.Tool,
                    Action = weather.Action,
                    Parameters = parameters,
                    RequiresApproval = weather.RequiresApproval,
                    Message = weather.FormatConfirmation(parameters)
                });
            }

            return Task.FromResult(ToolDecision.NoTool(
                "I don't have a suitable tool for that request. I can scan a URL for links, run a simulated attack " +
                "against a URL, get a weather forecast, or ask an agent to analyse this session's findings or estimate the cost."));
        }

        /// <summary>Summarises the call from the tool's actual response using fixed rules.</summary>
        public Task<ActionSummary> SummarizeAsync(string prompt, ToolCall toolCall) =>
            Task.FromResult(new ActionSummary(ToolResultSummarizer.Summarize(toolCall)));

        /// <summary>Combines the per-action summaries using fixed rules.</summary>
        public Task<ActionSummary> SummarizeSessionAsync(IReadOnlyList<ToolCall> toolCalls) =>
            Task.FromResult(new ActionSummary(SessionReportBuilder.CombineSummaries(toolCalls)));

        /// <summary>Summarises activity across sessions using fixed rules.</summary>
        public Task<ActionSummary> SummarizeActivityAsync(IReadOnlyList<ActivityRow> rows, string window) =>
            Task.FromResult(new ActionSummary(ActivitySummaryBuilder.Combine(rows, window)));

        // A decision for a catalog entry matched by keyword: a URL parameter takes the URL in the prompt,
        // and a required parameter that can't be filled means asking for it instead.
        private static ToolDecision DecisionFor(ToolDefinition tool, string? url)
        {
            var parameters = new Dictionary<string, string>();
            foreach (var parameter in tool.Parameters)
            {
                if (parameter.Type == ToolParameterType.Url && url != null)
                {
                    parameters[parameter.Name] = url;
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

        private static string? ExtractUrl(string text)
        {
            var match = UrlRegex.Match(text);
            if (!match.Success)
            {
                return null;
            }

            var value = match.Value.TrimEnd('.', ',', ')', ']', '!', '?');
            return value.Contains("://") ? value : "https://" + value;
        }
    }
}
