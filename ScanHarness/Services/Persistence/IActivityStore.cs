using ScanHarness.Models;

namespace ScanHarness.Services.Persistence
{
    /// <summary>
    /// Durable storage for sessions, tool calls and log entries: enough to recreate a session in full
    /// (requests, decisions, tool calls and responses, approvals, summaries and every timestamp).
    /// The harness only talks to this interface, so the SQLite implementation can be swapped for another
    /// database without touching the services or UI.
    /// </summary>
    public interface IActivityStore
    {
        /// <summary>
        /// Creates the storage if needed and closes out calls left unfinished by a previous run
        /// (e.g. waiting for an approval that can no longer arrive).
        /// </summary>
        /// <returns>Number of unfinished calls that were marked as interrupted.</returns>
        int Initialize();

        /// <summary>Inserts or updates a session.</summary>
        void SaveSession(HarnessSession session);

        /// <summary>Inserts or updates a tool call; called at every change of status.</summary>
        void SaveToolCall(ToolCall call);

        /// <summary>Appends a log entry and records it as the session's latest activity.</summary>
        void AppendLog(LogEntry entry);

        /// <summary>Sessions newest first, with their tool-call counts. <paramref name="userName"/> null lists every user's.</summary>
        IReadOnlyList<HarnessSession> ListSessions(string? userName);

        /// <summary>Finds a session by id, or null.</summary>
        HarnessSession? GetSession(string sessionId);

        /// <summary>A session's tool calls, oldest first.</summary>
        IReadOnlyList<ToolCall> LoadToolCalls(string sessionId);

        /// <summary>A session's log entries, oldest first.</summary>
        IReadOnlyList<LogEntry> LoadLogs(string sessionId);
    }

    /// <summary>Settings for the activity store (config section "Persistence").</summary>
    public class PersistenceOptions
    {
        public const string SectionName = "Persistence";

        /// <summary>SQLite database file; relative paths are resolved against the app's content root.</summary>
        public string DatabasePath { get; set; } = "Data/scanharness.db";
    }
}
