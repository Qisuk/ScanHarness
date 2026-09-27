// Compares agent brains on a fixed set of prompts: for each model (and decision mode) it runs
// every case in eval-cases.json and reports accuracy and latency, with the keyword brain as a
// baseline. Works against any OpenAI-compatible endpoint (Ollama, LM Studio, llama.cpp, ...).
//
//   dotnet run --project ScanHarness.Eval -- [options]
//
//   --base-url <url>     OpenAI-compatible base URL      (default: http://localhost:11434/v1)
//   --api-key <key>      Bearer token, if the endpoint needs one
//   --models a,b,c       Models to evaluate               (default: every model the endpoint lists)
//   --mode json|tools|both                                (default: json, the app default)
//   --repeat <n>         Runs per case, to expose non-determinism (default: 1)
//   --cases <path>       Case file                        (default: eval-cases.json next to the exe)
//   --out <dir>          Where to write the Markdown report (default: TestResults)

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Llm;

var options = EvalOptions.Parse(args);
if (options == null)
{
    return 1;
}

var cases = JsonSerializer.Deserialize<List<EvalCase>>(
    File.ReadAllText(options.CasesPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

var llmOptions = new LlmOptions { BaseUrl = options.BaseUrl, ApiKey = options.ApiKey, TimeoutSeconds = 600 };
var http = new HttpClient();
OpenAiCompatibleClient.Configure(http, llmOptions);
var client = new OpenAiCompatibleClient(http);

var models = options.Models;
if (models.Count == 0)
{
    try
    {
        models = (await client.ListModelsAsync())
            .Where(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not list models from {options.BaseUrl}: {ex.Message}");
        return 1;
    }
}

Console.WriteLine($"Endpoint: {options.BaseUrl}");
Console.WriteLine($"Cases:    {cases.Count} x {options.Repeat} run(s)");
Console.WriteLine($"Models:   {string.Join(", ", models)}");
Console.WriteLine();

// The keyword brain is the baseline every LLM should beat.
var runs = new List<(string Label, string Mode, IAgentBrain Brain, bool NeedsWarmup)>
{
    ("keyword (baseline)", "rules", new KeywordAgentBrain(), false)
};
foreach (var model in models)
{
    foreach (var mode in options.Modes)
    {
        runs.Add((model, mode == LlmDecisionMode.Tools ? "tools" : "json", new OpenAiCompatibleAgentBrain(client, model, mode, allowModeFallback: false), true));
    }
}

var results = new List<RunResult>();
foreach (var run in runs)
{
    Console.Write($"{run.Label} [{run.Mode}] ");

    if (run.NeedsWarmup)
    {
        // The first request loads the model into memory; don't count that against its latency.
        var warmup = await run.Brain.DecideAsync("scan example.com");
        if (warmup.Error != null)
        {
            Console.WriteLine($"skipped: {warmup.Error}");
            results.Add(new RunResult(run.Label, run.Mode, new(), Skipped: warmup.Error));
            continue;
        }
    }

    var outcomes = new List<CaseOutcome>();
    foreach (var evalCase in cases)
    {
        for (var i = 0; i < options.Repeat; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var decision = await run.Brain.DecideAsync(evalCase.Prompt);
            stopwatch.Stop();

            var outcome = CaseOutcome.Score(evalCase, decision, stopwatch.Elapsed);
            outcomes.Add(outcome);
            Console.Write(outcome.Correct ? "." : "x");
        }
    }

    var result = new RunResult(run.Label, run.Mode, outcomes);
    results.Add(result);
    Console.WriteLine($" {result.Correct}/{outcomes.Count}");
}

var report = Report.Build(options, cases.Count, results);
Console.WriteLine();
Console.WriteLine(report);

Directory.CreateDirectory(options.OutDir);
var reportPath = Path.Combine(options.OutDir, $"llm-eval-{DateTime.Now:yyyyMMdd-HHmmss}.md");
File.WriteAllText(reportPath, report);
Console.WriteLine($"Report written to {Path.GetFullPath(reportPath)}");
return 0;

/// <summary>
/// One prompt with its expected decision. <c>Expect</c> is "scanner", "attack", "weather" or "none".
/// <c>Url</c> and <c>Location</c> are checked when present; an empty <c>Location</c> means "no location".
/// </summary>
record EvalCase(string Prompt, string Expect, string? Url, string? Location)
{
    public string Describe() =>
        Expect + (Url != null ? $" {Url}" : "") + (Location != null ? $" location={(Location == "" ? "(none)" : Location)}" : "");
}

/// <summary>How one brain handled one case. <c>ActualArgs</c> summarises the chosen parameters.</summary>
record CaseOutcome(EvalCase Case, string Actual, string ActualArgs, bool Correct, bool Errored, TimeSpan Latency)
{
    public static CaseOutcome Score(EvalCase evalCase, ToolDecision decision, TimeSpan latency)
    {
        var actual = decision.Error != null ? "error" : decision.ToolFound ? decision.Tool ?? "?" : "none";
        decision.Parameters.TryGetValue("url", out var url);
        decision.Parameters.TryGetValue("location", out var location);

        var toolCorrect = string.Equals(actual, evalCase.Expect, StringComparison.OrdinalIgnoreCase);
        var urlCorrect = evalCase.Url == null || string.Equals(url?.TrimEnd('/'), evalCase.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        var locationCorrect = evalCase.Location == null || string.Equals(location ?? "", evalCase.Location, StringComparison.OrdinalIgnoreCase);

        var args = string.Join(" ", decision.Parameters.Select(p => p.Key == "url" ? p.Value : $"{p.Key}={p.Value}"));
        return new CaseOutcome(evalCase, actual, args, toolCorrect && urlCorrect && locationCorrect, decision.Error != null, latency);
    }
}

/// <summary>All outcomes for one brain/mode.</summary>
record RunResult(string Label, string Mode, List<CaseOutcome> Outcomes, string? Skipped = null)
{
    public int Correct => Outcomes.Count(o => o.Correct);
    public int Errors => Outcomes.Count(o => o.Errored);
    public double Accuracy => Outcomes.Count == 0 ? 0 : (double)Correct / Outcomes.Count;

    public TimeSpan Percentile(double p)
    {
        if (Outcomes.Count == 0) return TimeSpan.Zero;
        var sorted = Outcomes.Select(o => o.Latency).OrderBy(t => t).ToList();
        return sorted[(int)Math.Ceiling(p * sorted.Count) - 1];
    }
}

static class Report
{
    public static string Build(EvalOptions options, int caseCount, List<RunResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# LLM brain evaluation — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"Endpoint `{options.BaseUrl}`, {caseCount} cases x {options.Repeat} run(s).");
        sb.AppendLine();
        sb.AppendLine("| Brain | Mode | Accuracy | Correct | Errors | Median | p95 |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|");

        foreach (var r in results.OrderByDescending(r => r.Accuracy).ThenBy(r => r.Percentile(0.5)))
        {
            if (r.Skipped != null)
            {
                sb.AppendLine($"| {r.Label} | {r.Mode} | skipped | | | | |");
                continue;
            }

            sb.AppendLine($"| {r.Label} | {r.Mode} | {r.Accuracy:P0} | {r.Correct}/{r.Outcomes.Count} | {r.Errors} | " +
                          $"{r.Percentile(0.5).TotalSeconds:0.00}s | {r.Percentile(0.95).TotalSeconds:0.00}s |");
        }

        foreach (var r in results.Where(r => r.Skipped != null))
        {
            sb.AppendLine();
            sb.AppendLine($"- **{r.Label} [{r.Mode}]** skipped: {r.Skipped}");
        }

        var failures = results.SelectMany(r => r.Outcomes.Where(o => !o.Correct).Select(o => (r, o))).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Misses");
            sb.AppendLine();
            sb.AppendLine("| Brain | Mode | Prompt | Expected | Got |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var (r, o) in failures)
            {
                var expected = o.Case.Describe();
                var got = o.ActualArgs == "" ? o.Actual : $"{o.Actual} {o.ActualArgs}";
                sb.AppendLine($"| {r.Label} | {r.Mode} | {o.Case.Prompt.Replace("|", "\\|")} | {expected} | {got} |");
            }
        }

        return sb.ToString();
    }
}

class EvalOptions
{
    public string BaseUrl { get; private set; } = Environment.GetEnvironmentVariable("LLM_BASE_URL") ?? "http://localhost:11434/v1";
    public string? ApiKey { get; private set; } = Environment.GetEnvironmentVariable("LLM_API_KEY");
    public List<string> Models { get; private set; } = new();
    public List<LlmDecisionMode> Modes { get; private set; } = new() { LlmDecisionMode.JsonSchema };
    public int Repeat { get; private set; } = 1;
    public string CasesPath { get; private set; } = Path.Combine(AppContext.BaseDirectory, "eval-cases.json");
    public string OutDir { get; private set; } = "TestResults";

    public static EvalOptions? Parse(string[] args)
    {
        var options = new EvalOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

            try
            {
                switch (args[i])
                {
                    case "--base-url": options.BaseUrl = Next(); break;
                    case "--api-key": options.ApiKey = Next(); break;
                    case "--models": options.Models = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                    case "--repeat": options.Repeat = Math.Max(1, int.Parse(Next())); break;
                    case "--cases": options.CasesPath = Next(); break;
                    case "--out": options.OutDir = Next(); break;
                    case "--mode":
                        options.Modes = Next().ToLowerInvariant() switch
                        {
                            "tools" => new() { LlmDecisionMode.Tools },
                            "json" => new() { LlmDecisionMode.JsonSchema },
                            "both" => new() { LlmDecisionMode.Tools, LlmDecisionMode.JsonSchema },
                            var other => throw new ArgumentException($"Unknown mode '{other}' (use tools, json or both)")
                        };
                        break;
                    default: throw new ArgumentException($"Unknown option {args[i]}");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine("See the comment at the top of ScanHarness.Eval/Program.cs for usage.");
                return null;
            }
        }

        return options;
    }
}
