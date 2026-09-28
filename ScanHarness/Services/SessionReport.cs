using ScanHarness.Models;

namespace ScanHarness.Services
{
    /// <summary>One line of the session's tool-call list.</summary>
    /// <param name="Number">Position in the session, starting at 1.</param>
    /// <param name="Time">When the call was made (UTC).</param>
    /// <param name="Tool">Tool identifier, e.g. "scanner".</param>
    /// <param name="Action">Action on the tool, e.g. "simple-scanner".</param>
    /// <param name="Parameters">Parameters as "key=value" pairs in key order, or empty.</param>
    /// <param name="Status">Final (or current) status of the call.</param>
    /// <param name="Brain">Brain that chose the call, if recorded.</param>
    public sealed record ToolCallListItem(int Number, DateTime Time, string Tool, string Action, string Parameters, ToolCallStatus Status, string? Brain);

    /// <summary>The result of summarising a whole session.</summary>
    /// <param name="ToolCalls">Every tool call in the session, built deterministically.</param>
    /// <param name="Summary">Summary of the per-action summaries, at most <see cref="SessionReportBuilder.MaxSummaryWords"/> words.</param>
    /// <param name="SummaryBrain">Name of the brain that wrote <paramref name="Summary"/>.</param>
    /// <param name="FallbackReason">Set when the brain couldn't write the summary and the rule-based one was used.</param>
    /// <param name="CreatedAt">When the report was made (UTC).</param>
    public sealed record SessionReport(
        IReadOnlyList<ToolCallListItem> ToolCalls,
        string Summary,
        string SummaryBrain,
        string? FallbackReason,
        DateTime CreatedAt)
    {
        public int SummaryWordCount => SessionReportBuilder.CountWords(Summary);
    }

    /// <summary>
    /// Deterministic parts of a session report: the tool-call list, the rule-based summary of
    /// summaries used by the keyword brain (and as the LLM fallback), and the word limit.
    /// </summary>
    public static class SessionReportBuilder
    {
        /// <summary>Upper bound on the session summary, whichever brain writes it.</summary>
        public const int MaxSummaryWords = 500;

        /// <summary>Message used when there is nothing to summarise.</summary>
        public const string EmptySessionSummary = "No tools have been called in this session yet.";

        /// <summary>Lists the calls in the order they were made; the same calls always give the same list.</summary>
        public static IReadOnlyList<ToolCallListItem> ListToolCalls(IEnumerable<ToolCall> calls) =>
            Ordered(calls)
                .Select((call, index) => new ToolCallListItem(
                    index + 1,
                    call.CreatedAt,
                    call.Tool,
                    call.Action,
                    FormatParameters(call.Parameters),
                    call.Status,
                    call.Brain))
                .ToList();

        /// <summary>
        /// Rule-based summary of summaries: a count of outcomes followed by each call's summary
        /// (or its rule-based summary if the brain hasn't written one), limited to <see cref="MaxSummaryWords"/>.
        /// </summary>
        public static string CombineSummaries(IEnumerable<ToolCall> calls)
        {
            var ordered = Ordered(calls).ToList();
            if (ordered.Count == 0)
            {
                return EmptySessionSummary;
            }

            var outcomes = ordered
                .GroupBy(c => c.Status)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {Describe(g.Key)}");
            var opening = $"{Plural(ordered.Count, "tool call")} this session: {string.Join(", ", outcomes)}.";

            var details = ordered.Select((call, index) =>
                $"({index + 1}) {(string.IsNullOrWhiteSpace(call.Summary) ? ToolResultSummarizer.Summarize(call) : call.Summary.Trim())}");

            return LimitWords(opening + " " + string.Join(" ", details), MaxSummaryWords);
        }

        /// <summary>Counts whitespace-separated words.</summary>
        public static int CountWords(string? text) =>
            string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        /// <summary>
        /// Returns <paramref name="text"/> unchanged if it has at most <paramref name="maxWords"/> words;
        /// otherwise the first <paramref name="maxWords"/> words followed by "…". Line breaks are kept.
        /// </summary>
        public static string LimitWords(string text, int maxWords)
        {
            var trimmed = text.Trim();
            if (CountWords(trimmed) <= maxWords)
            {
                return trimmed;
            }

            // Walk the text so paragraph breaks inside the kept words survive.
            var words = 0;
            var inWord = false;
            for (var i = 0; i < trimmed.Length; i++)
            {
                if (char.IsWhiteSpace(trimmed[i]))
                {
                    if (inWord && words == maxWords)
                    {
                        return trimmed[..i].TrimEnd('.', ',', ';', ':') + "…";
                    }

                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    words++;
                }
            }

            return trimmed;
        }

        // Oldest first; the id breaks ties so equal timestamps still give a stable order.
        private static IEnumerable<ToolCall> Ordered(IEnumerable<ToolCall> calls) =>
            calls.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal);

        private static string FormatParameters(IReadOnlyDictionary<string, string> parameters) =>
            string.Join(", ", parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

        private static string Describe(ToolCallStatus status) => status switch
        {
            ToolCallStatus.Completed => "completed",
            ToolCallStatus.Failed => "failed",
            ToolCallStatus.Cancelled => "rejected",
            ToolCallStatus.WaitingForApproval => "awaiting approval",
            var other => other.ToString().ToLowerInvariant()
        };

        private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
    }
}
