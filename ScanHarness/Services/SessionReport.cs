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
    /// <param name="UserName">User who made the request that led to the call, if recorded.</param>
    public sealed record ToolCallListItem(int Number, DateTime Time, string Tool, string Action, string Parameters, ToolCallStatus Status, string? Brain, string? UserName = null);

    /// <summary>One control that tripped in a session, with the message that tripped it.</summary>
    /// <param name="Number">Position in the session, starting at 1.</param>
    /// <param name="Time">When the control tripped (UTC).</param>
    /// <param name="UserName">User whose action was checked.</param>
    /// <param name="Violation">Which control, at which stage, in which mode, and what it found.</param>
    /// <param name="Message">The message that tripped it (the question, reply, summary or tool call).</param>
    public sealed record ControlTrip(int Number, DateTime Time, string? UserName, Controls.ControlViolation Violation, string Message);

    /// <summary>Data offered to an external brain, tool or agent, from the handoff audit trail.</summary>
    public sealed record ExternalShare(int Number, DateTime Time, string? UserName, Boundaries.Handoff Handoff);

    /// <summary>The result of summarising a whole session.</summary>
    /// <param name="ToolCalls">Every tool call in the session, built deterministically.</param>
    /// <param name="Summary">Summary of the per-action summaries, at most <see cref="SessionReportBuilder.MaxSummaryWords"/> words.</param>
    /// <param name="SummaryBrain">Name of the brain that wrote <paramref name="Summary"/>.</param>
    /// <param name="FallbackReason">Set when the brain couldn't write the summary and the rule-based one was used.</param>
    /// <param name="CreatedAt">When the report was made (UTC).</param>
    /// <param name="ControlTrips">Every control that tripped in the session, built deterministically from the log.</param>
    /// <param name="ExternalShares">Every handoff to an external component in the session, from the log.</param>
    /// <param name="NetworkConnections">Every connection the session's tools asked the egress proxy for, from the log.</param>
    public sealed record SessionReport(
        IReadOnlyList<ToolCallListItem> ToolCalls,
        string Summary,
        string SummaryBrain,
        string? FallbackReason,
        DateTime CreatedAt,
        IReadOnlyList<ControlTrip>? ControlTrips = null,
        IReadOnlyList<ExternalShare>? ExternalShares = null,
        IReadOnlyList<NetworkConnection>? NetworkConnections = null)
    {
        public int SummaryWordCount => SessionReportBuilder.CountWords(Summary);

        public IReadOnlyList<ControlTrip> Trips => ControlTrips ?? Array.Empty<ControlTrip>();

        public IReadOnlyList<ExternalShare> Shares => ExternalShares ?? Array.Empty<ExternalShare>();

        public IReadOnlyList<NetworkConnection> Connections => NetworkConnections ?? Array.Empty<NetworkConnection>();
    }

    /// <summary>A connection a tool asked the egress proxy for, and whether it was allowed.</summary>
    public sealed record NetworkConnection(int Number, DateTime Time, string? UserName, string? Tool, EgressEvent Connection);

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

        /// <summary>
        /// Lists every handoff to an external brain, tool or agent in the session, in order: what was offered,
        /// how sensitive it was, and whether it was shared as it was, with placeholders, or blocked.
        /// </summary>
        public static IReadOnlyList<ExternalShare> ListExternalShares(IEnumerable<LogEntry> logs)
        {
            var shares = new List<ExternalShare>();
            foreach (var entry in logs.OrderBy(e => e.Timestamp))
            {
                if (Boundaries.Handoff.FromJson(ChatTranscript.ReadString(entry, "handoff")) is { ToClassification: Classification.External } handoff)
                {
                    shares.Add(new ExternalShare(shares.Count + 1, entry.Timestamp, entry.UserName, handoff));
                }
            }

            return shares;
        }

        /// <summary>
        /// Lists every connection the session's tools asked the egress proxy for, in order, allowed or refused.
        /// Built from the log (each connection is logged with its tool call), so reopened sessions give the same list.
        /// </summary>
        public static IReadOnlyList<NetworkConnection> ListNetworkConnections(IEnumerable<LogEntry> logs)
        {
            var connections = new List<NetworkConnection>();
            foreach (var entry in logs.OrderBy(e => e.Timestamp))
            {
                if (ToolCallService.ReadEgress(entry) is EgressEvent connection)
                {
                    connections.Add(new NetworkConnection(connections.Count + 1, connection.Timestamp, entry.UserName, connection.Caller, connection));
                }
            }

            return connections;
        }

        /// <summary>
        /// Lists every control that tripped in the session, in order, with the message that tripped it.
        /// Built from the log alone, so the same session always gives the same list.
        /// </summary>
        public static IReadOnlyList<ControlTrip> ListControlTrips(IEnumerable<LogEntry> logs)
        {
            var trips = new List<ControlTrip>();

            // The most recent text of each kind, for hits logged before violations stored the checked text.
            var lastSeen = new Dictionary<string, string>();

            foreach (var entry in logs.OrderBy(e => e.Timestamp))
            {
                Remember(lastSeen, ChatTranscript.Targets.Request, ChatTranscript.ReadString(entry, "prompt"));
                Remember(lastSeen, ChatTranscript.Targets.Reply, ChatTranscript.ReadString(entry, "agentMessage"));
                Remember(lastSeen, ChatTranscript.Targets.Summary, ChatTranscript.ReadString(entry, "summary"));
                Remember(lastSeen, ChatTranscript.Targets.SessionSummary, ChatTranscript.ReadString(entry, "sessionSummary"));
                var tool = ChatTranscript.ReadString(entry, "tool");
                if (tool != null)
                {
                    Remember(lastSeen, ChatTranscript.Targets.ToolCall, $"{tool}/{ChatTranscript.ReadString(entry, "action")}");
                }

                foreach (var violation in Controls.ControlService.FromJson(ChatTranscript.ReadString(entry, "controls")))
                {
                    var message = violation.Text ?? (lastSeen.TryGetValue(violation.Target, out var seen) ? seen : "");
                    trips.Add(new ControlTrip(trips.Count + 1, entry.Timestamp, entry.UserName, violation, message));
                }
            }

            return trips;
        }

        private static void Remember(Dictionary<string, string> lastSeen, string target, string? text)
        {
            if (text != null)
            {
                lastSeen[target] = text;
            }
        }

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
                    call.Brain,
                    call.UserName))
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
