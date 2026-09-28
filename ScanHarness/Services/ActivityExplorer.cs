using ScanHarness.Models;
using ScanHarness.Services.Controls;
using ScanHarness.Services.Persistence;

namespace ScanHarness.Services
{
    /// <summary>Which requests to show by the controls they tripped.</summary>
    public enum ControlFilter
    {
        /// <summary>Every request.</summary>
        Any,

        /// <summary>Requests that tripped no control.</summary>
        None,

        /// <summary>Requests with at least one warning (Advise) and no block.</summary>
        Warning,

        /// <summary>Requests where a control blocked something.</summary>
        Block,

        /// <summary>Requests that tripped any control (warning or block).</summary>
        Flagged
    }

    /// <summary>Filters for <see cref="ActivityExplorer"/>; null values don't filter.</summary>
    /// <param name="UserName">Only requests made by this user.</param>
    /// <param name="SessionId">Only this session.</param>
    /// <param name="FromUtc">Only requests at or after this time.</param>
    /// <param name="ToUtc">Only requests at or before this time.</param>
    /// <param name="Controls">Only requests with this control outcome.</param>
    public sealed record ActivityFilter(
        string? UserName = null,
        string? SessionId = null,
        DateTime? FromUtc = null,
        DateTime? ToUtc = null,
        ControlFilter Controls = ControlFilter.Any);

    /// <summary>One request in the activity list, with the session it belongs to.</summary>
    public sealed record ActivityRow(HarnessSession Session, ChatTurn Turn)
    {
        /// <summary>Every control the request tripped: on the question, the reply, its tool call and its summary.</summary>
        public IReadOnlyList<ControlViolation> Controls { get; } =
            Turn.PromptControls.Concat(Turn.ReplyControls).Concat(Turn.SummaryControls).ToList();

        /// <summary>"none", "warning" or "block": the most severe control outcome.</summary>
        public string Severity =>
            Controls.Any(v => v.Mode == ControlMode.Block) ? "block" : Controls.Count > 0 ? "warning" : "none";
    }

    /// <summary>The requests matching a filter, newest first, and how many matched in total.</summary>
    public sealed record ActivityResult(IReadOnlyList<ActivityRow> Rows, int TotalMatches);

    /// <summary>
    /// Finds requests across every user's sessions, for the admin's All activity view: e.g. everything in
    /// a time range, everything by one user in a time range, or everything that raised a warning. Each
    /// request is rebuilt from the session's log with <see cref="ChatTranscript"/>, so it carries the
    /// same reply, tool call and control hits as the chat.
    /// </summary>
    public class ActivityExplorer
    {
        /// <summary>Most rows returned; <see cref="ActivityResult.TotalMatches"/> still counts them all.</summary>
        public const int DefaultLimit = 500;

        private readonly IActivityStore _store;

        public ActivityExplorer(IActivityStore store)
        {
            _store = store;
        }

        /// <summary>Sessions for the session filter, newest first; only <paramref name="ownerName"/>'s when given.</summary>
        public IReadOnlyList<HarnessSession> Sessions(string? ownerName) => _store.ListSessions(ownerName);

        public ActivityResult Find(ActivityFilter filter, int limit = DefaultLimit)
        {
            var sessions = _store.ListSessions(null).ToDictionary(s => s.Id);
            var sessionIds = filter.SessionId != null
                ? new[] { filter.SessionId }
                : _store.SessionIdsWithActivity(filter.FromUtc, filter.ToUtc);

            var rows = new List<ActivityRow>();
            foreach (var sessionId in sessionIds)
            {
                if (!sessions.TryGetValue(sessionId, out var session))
                {
                    continue;
                }

                var turns = ChatTranscript.Build(_store.LoadLogs(sessionId), _store.LoadToolCalls(sessionId));
                rows.AddRange(turns
                    .Where(t => t.Kind == ChatTurnKind.Request)
                    .Select(t => new ActivityRow(session, t))
                    .Where(r => Matches(r, filter)));
            }

            var ordered = rows.OrderByDescending(r => r.Turn.RequestedAt).ThenBy(r => r.Turn.Id, StringComparer.Ordinal).ToList();
            return new ActivityResult(ordered.Take(limit).ToList(), ordered.Count);
        }

        private static bool Matches(ActivityRow row, ActivityFilter filter)
        {
            var turn = row.Turn;
            if (filter.UserName != null && !string.Equals(turn.UserName, filter.UserName, StringComparison.Ordinal))
            {
                return false;
            }

            if ((filter.FromUtc is DateTime from && turn.RequestedAt < from) || (filter.ToUtc is DateTime to && turn.RequestedAt > to))
            {
                return false;
            }

            return filter.Controls switch
            {
                ControlFilter.None => row.Severity == "none",
                ControlFilter.Warning => row.Severity == "warning",
                ControlFilter.Block => row.Severity == "block",
                ControlFilter.Flagged => row.Severity != "none",
                _ => true,
            };
        }
    }
}
