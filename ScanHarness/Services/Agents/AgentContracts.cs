using System.Text.Json;
using System.Text.Json.Nodes;
using ScanHarness.Models;

namespace ScanHarness.Services.Agents
{
    /// <summary>One scan or attack result from the session, as the findings analyst sees it.</summary>
    /// <param name="Target">The URL the tool ran against.</param>
    /// <param name="Tool">"scanner" or "attack".</param>
    /// <param name="Status">The tool's reported status, e.g. "success".</param>
    /// <param name="Severity">Overall severity reported by an attack ("none", "low", "medium", "high"); "none" for scans.</param>
    /// <param name="Findings">Named findings (attacks) or discovered pages (scans).</param>
    /// <param name="TestsRun">Tests an attack ran; 0 for scans.</param>
    public sealed record TargetResult(string Target, string Tool, string Status, string Severity, IReadOnlyList<string> Findings, int TestsRun);

    /// <summary>
    /// Input to the findings analyst: the session's completed scan and attack results. Built by code from
    /// stored tool calls, never from model output. Label: customer confidential (it names hosts).
    /// </summary>
    public sealed record FindingSet(IReadOnlyList<TargetResult> Results)
    {
        public static FindingSet From(IEnumerable<ToolCall> calls)
        {
            var results = new List<TargetResult>();
            foreach (var call in calls.Where(c => c.Status == ToolCallStatus.Completed && c.Tool is "scanner" or "attack"))
            {
                JsonObject? result;
                try
                {
                    result = string.IsNullOrWhiteSpace(call.Result) ? null : JsonNode.Parse(call.Result) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (result == null)
                {
                    continue;
                }

                call.Parameters.TryGetValue("url", out var target);
                var status = Text(result["status"]) ?? "unknown";
                if (call.Tool == "attack")
                {
                    var details = result["details"] as JsonObject;
                    var findings = details?["findings"]?.AsArray().Select(Text).OfType<string>().ToList() ?? new List<string>();
                    var testsRun = details?["tests_run"] is JsonValue tests && tests.TryGetValue<int>(out var n) ? n : 0;
                    results.Add(new TargetResult(target ?? "", "attack", status, Text(result["severity"]) ?? "none", findings, testsRun));
                }
                else
                {
                    var pages = result["links"]?.AsArray().Select(l => Text(l?["url"])).OfType<string>().ToList() ?? new List<string>();
                    results.Add(new TargetResult(target ?? "", "scanner", status, "none", pages, 0));
                }
            }

            return new FindingSet(results);
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>
    /// Input to the cost estimator: the size of the work with nothing that identifies the customer.
    /// Built by code from a <see cref="FindingSet"/>. Label: engagement.
    /// </summary>
    public sealed record EngagementSummary(int HostCount, int ScansRun, int AttacksRun, int HighFindings, int MediumFindings, int LowFindings, int TestsRun)
    {
        public int TotalFindings => HighFindings + MediumFindings + LowFindings;

        public static EngagementSummary From(FindingSet findings)
        {
            var hosts = findings.Results.Select(r => Uri.TryCreate(r.Target, UriKind.Absolute, out var uri) ? uri.Host : r.Target)
                .Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var attacks = findings.Results.Where(r => r.Tool == "attack").ToList();
            int Count(string severity) => attacks.Where(r => r.Severity == severity).Sum(r => r.Findings.Count);
            return new EngagementSummary(hosts, findings.Results.Count(r => r.Tool == "scanner"), attacks.Count,
                Count("high"), Count("medium"), Count("low"), attacks.Sum(r => r.TestsRun));
        }
    }

    /// <summary>One recommendation from the findings analyst.</summary>
    /// <param name="Priority">P1 (most urgent) to P3.</param>
    /// <param name="Finding">What was found.</param>
    /// <param name="Target">Where it was found.</param>
    /// <param name="Action">What to do about it.</param>
    public sealed record Recommendation(string Priority, string Finding, string Target, string Action);

    /// <summary>Output of the findings analyst.</summary>
    public sealed record Recommendations(IReadOnlyList<Recommendation> Items, string Summary);

    /// <summary>One line of a cost estimate.</summary>
    public sealed record CostLine(string Item, decimal Days);

    /// <summary>Output of the cost estimator. The numbers always come from code; a brain may only add the narrative.</summary>
    public sealed record CostEstimate(IReadOnlyList<CostLine> Lines, decimal Days, decimal DayRate, string Currency, decimal Total, string Narrative);

    /// <summary>Output of the scoping agent: a proposal that code validates and a person approves before it becomes an engagement.</summary>
    /// <param name="Customer">Who the engagement is for.</param>
    /// <param name="Domains">Domains the customer asked to include.</param>
    /// <param name="WindowStart">First day of testing, if agreed.</param>
    /// <param name="WindowEnd">Last day of testing, if agreed.</param>
    /// <param name="Rules">Rules of engagement the customer stated.</param>
    public sealed record ScopeProposal(string Customer, IReadOnlyList<string> Domains, DateOnly? WindowStart, DateOnly? WindowEnd, IReadOnlyList<string> Rules)
    {
        public static ScopeProposal Empty { get; } = new("", Array.Empty<string>(), null, null, Array.Empty<string>());
    }
}
