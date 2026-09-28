using ScanHarness.Services.Controls;

namespace ScanHarness.Services
{
    /// <summary>Counts for the requests being summarised.</summary>
    public sealed record ActivityOverview(int Requests, int Users, int ToolCalls, int Warnings, int Blocks, DateTime? First, DateTime? Last)
    {
        /// <summary>e.g. "12 requests by 3 users, 8 tool calls, 2 warnings and 1 block".</summary>
        public string Describe() =>
            $"{ActivitySummaryBuilder.Plural(Requests, "request")} by {ActivitySummaryBuilder.Plural(Users, "user")}, " +
            $"{ActivitySummaryBuilder.Plural(ToolCalls, "tool call")}, {ActivitySummaryBuilder.Plural(Warnings, "warning")} " +
            $"and {ActivitySummaryBuilder.Plural(Blocks, "block")}";
    }

    /// <summary>The result of summarising the requests in the All activity view.</summary>
    /// <param name="Window">What was summarised, e.g. "all users · 28 Sep 09:00 to 12:00".</param>
    /// <param name="Overview">Counts, built directly from the requests.</param>
    /// <param name="Summary">The summary, at most <see cref="SessionReportBuilder.MaxSummaryWords"/> words.</param>
    /// <param name="SummaryBrain">Brain that wrote the summary.</param>
    /// <param name="FallbackReason">Set when the brain couldn't write it and the rule-based summary was used.</param>
    /// <param name="CreatedAt">When it was made (UTC).</param>
    /// <param name="Controls">Controls that tripped on the summary itself (Chat Out).</param>
    public sealed record ActivityReport(
        string Window,
        ActivityOverview Overview,
        string Summary,
        string SummaryBrain,
        string? FallbackReason,
        DateTime CreatedAt,
        IReadOnlyList<ControlViolation> Controls)
    {
        public int SummaryWordCount => SessionReportBuilder.CountWords(Summary);
    }

    /// <summary>
    /// Deterministic parts of an activity summary: the overview, what each request may safely say, and the
    /// rule-based summary used by the keyword brain and as the LLM fallback. Content a control blocked or
    /// withheld is never included, so summarising can't leak what the control stopped.
    /// </summary>
    public static class ActivitySummaryBuilder
    {
        public const string NoActivity = "No requests match these filters.";

        public static ActivityOverview Overview(IReadOnlyList<ActivityRow> rows) => new(
            Requests: rows.Count,
            Users: rows.Select(r => r.Turn.UserName).Distinct().Count(),
            ToolCalls: rows.Count(r => r.Turn.ToolCall != null),
            Warnings: rows.Sum(r => r.Controls.Count(v => v.Mode == ControlMode.Advise)),
            Blocks: rows.Sum(r => r.Controls.Count(v => v.Mode == ControlMode.Block)),
            First: rows.Count == 0 ? null : rows.Min(r => r.Turn.RequestedAt),
            Last: rows.Count == 0 ? null : rows.Max(r => r.Turn.RequestedAt));

        /// <summary>The request text, unless a Chat In control blocked it (then only that fact).</summary>
        public static string SafePrompt(ActivityRow row) =>
            row.Turn.PromptBlocked ? "(withheld: blocked by a Chat In control)" : row.Turn.Prompt;

        /// <summary>What happened, using only text the controls allowed to be shown.</summary>
        public static string Outcome(ActivityRow row)
        {
            var turn = row.Turn;
            if (turn.PromptBlocked)
            {
                var names = string.Join(", ", turn.PromptControls.Where(v => v.Mode == ControlMode.Block).Select(v => v.Name).Distinct());
                return $"The request was blocked by the Chat In control {names} and not sent to the brain.";
            }

            string text;
            if (turn.Summary != null)
            {
                text = turn.SummaryWithheld ? "The summary was withheld by a Chat Out control." : turn.Summary;
            }
            else if (turn.ReplyWithheld)
            {
                text = "The reply was withheld by a Chat Out control.";
            }
            else
            {
                text = turn.AgentMessage ?? "There was no reply.";
            }

            return turn.ToolBlocked ? $"{text} The tool call was blocked by a ToolRule control." : text;
        }

        /// <summary>
        /// Rule-based summary: the overview, requests per user, then each request's outcome oldest first,
        /// limited to <see cref="SessionReportBuilder.MaxSummaryWords"/> words.
        /// </summary>
        public static string Combine(IReadOnlyList<ActivityRow> rows, string window)
        {
            if (rows.Count == 0)
            {
                return NoActivity;
            }

            var overview = Overview(rows);
            var byUser = rows
                .GroupBy(r => r.Turn.UserName ?? "unknown")
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var warnings = g.Sum(r => r.Controls.Count(v => v.Mode == ControlMode.Advise));
                    var blocks = g.Sum(r => r.Controls.Count(v => v.Mode == ControlMode.Block));
                    var controls = warnings + blocks == 0 ? "" : $" ({Plural(warnings, "warning")}, {Plural(blocks, "block")})";
                    return $"{g.Key} made {Plural(g.Count(), "request")}{controls}";
                });

            var items = rows
                .OrderBy(r => r.Turn.RequestedAt).ThenBy(r => r.Turn.Id, StringComparer.Ordinal)
                .Select((r, i) => $"({i + 1}) {r.Turn.RequestedAt.ToLocalTime():dd MMM HH:mm} {r.Turn.UserName}: {Outcome(r)}");

            var text = $"{window}: {overview.Describe()}. {string.Join("; ", byUser)}. {string.Join(" ", items)}";
            return SessionReportBuilder.LimitWords(text, SessionReportBuilder.MaxSummaryWords);
        }

        internal static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
    }
}
