using ScanHarness.Models;
using ScanHarness.Services.Persistence;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Services
{
    /// <summary>
    /// Tracks activity for the harness. Keeps the current session's log entries in memory for the
    /// UI activity feed, saves every entry to the activity store (stamped with the session and the
    /// user who performed the action), and appends it to a text log file for auditing and debugging.
    /// </summary>
    public class LogService
    {
        private readonly List<LogEntry> _logs = new();
        private readonly object _lockObject = new();
        private readonly string _logFilePath;
        private readonly SessionState? _session;
        private readonly IActivityStore? _store;

        /// <summary>Raised whenever the set of log entries changes so the UI can refresh.</summary>
        public event Action? OnLogsChanged;

        /// <param name="session">Current user and session; entries are stamped with them. Optional for tests.</param>
        /// <param name="store">Where entries are saved; null keeps them in memory only.</param>
        public LogService(SessionState? session = null, IActivityStore? store = null)
        {
            _session = session;
            _store = store;

            var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            _logFilePath = Path.Combine(logDirectory, $"toolcalls-{DateTime.UtcNow:yyyy-MM-dd}.log");
        }

        /// <summary>Replaces the in-memory entries with a stored session's, e.g. when a session is opened.</summary>
        public void LoadSession(IEnumerable<LogEntry> entries)
        {
            lock (_lockObject)
            {
                _logs.Clear();
                _logs.AddRange(entries.OrderBy(e => e.Timestamp));
            }

            OnLogsChanged?.Invoke();
        }

        public void LogInfo(string message, string? toolCallId = null, Dictionary<string, object>? data = null, string? brain = null)
            => Add(LogLevel.Info, message, toolCallId, data, brain);

        public void LogWarning(string message, string? toolCallId = null, Dictionary<string, object>? data = null, string? brain = null)
            => Add(LogLevel.Warning, message, toolCallId, data, brain);

        public void LogError(string message, string? toolCallId = null, Dictionary<string, object>? data = null, string? brain = null)
            => Add(LogLevel.Error, message, toolCallId, data, brain);

        public void LogSuccess(string message, string? toolCallId = null, Dictionary<string, object>? data = null, string? brain = null)
            => Add(LogLevel.Success, message, toolCallId, data, brain);

        /// <summary>Logs an action taken by the user (a request or an approval decision).</summary>
        public void LogUser(string message, string? toolCallId = null, Dictionary<string, object>? data = null, string? brain = null)
            => Add(LogLevel.User, message, toolCallId, data, brain);

        /// <summary>Logs something a control blocked (Advise-mode hits use <see cref="LogWarning"/>).</summary>
        public void LogBlock(string message, string? toolCallId = null, Dictionary<string, object>? data = null, string? brain = null)
            => Add(LogLevel.Block, message, toolCallId, data, brain);

        /// <summary>Logs a model's reasoning ("thinking"); the full text goes in the data, linked to the tool call when there is one.</summary>
        public void LogReasoning(string message, string? toolCallId = null, Dictionary<string, object>? data = null, string? brain = null)
            => Add(LogLevel.Reasoning, message, toolCallId, data, brain);

        /// <summary>Logs the brain's summary of a finished action.</summary>
        public void LogSummary(string message, string? toolCallId = null, Dictionary<string, object>? data = null, string? brain = null)
            => Add(LogLevel.Summary, message, toolCallId, data, brain);

        private void Add(LogLevel level, string message, string? toolCallId, Dictionary<string, object>? data, string? brain)
        {
            var entry = new LogEntry
            {
                Level = level,
                Message = message,
                ToolCallId = toolCallId,
                Data = data,
                Brain = brain,
                SessionId = _session?.Current.Id,
                UserName = _session?.CurrentUser.Name,
            };

            lock (_lockObject)
            {
                _logs.Add(entry);
            }

            Persist(entry);
            WriteToFile(entry);
            OnLogsChanged?.Invoke();
        }

        private void Persist(LogEntry entry)
        {
            if (_store == null || _session == null)
            {
                return;
            }

            try
            {
                _session.EnsureSaved();
                _store.AppendLog(entry);
            }
            catch (Exception ex)
            {
                // Saving is best-effort for the UI: record the failure in the text log instead of breaking the page.
                WriteLine($"{DateTime.UtcNow:O}\tError\t{entry.SessionId}\t-\t-\t-\tCould not save log entry {entry.Id}: {ex.Message}");
            }
        }

        private void WriteToFile(LogEntry entry)
        {
            // Tab-separated: timestamp, level, session id, user, tool call id, brain, message ("-" for empty columns).
            // Line breaks and tabs (e.g. from multi-line messages) are escaped so each entry stays on one line.
            static string Column(string? value) => string.IsNullOrEmpty(value) ? "-" : value;
            WriteLine($"{entry.Timestamp:O}\t{entry.Level}\t{Column(entry.SessionId)}\t{Column(entry.UserName)}\t" +
                      $"{Column(entry.ToolCallId)}\t{Column(entry.Brain)}\t{OneLine(entry.Message)}");
        }

        /// <summary>Escapes line breaks and tabs so a message fits one tab-separated log line.</summary>
        internal static string OneLine(string message) =>
            message.Replace("\\", "\\\\").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n").Replace("\t", "\\t");

        private void WriteLine(string line)
        {
            try
            {
                lock (_lockObject)
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // File logging is best-effort; never let an auditing failure break a tool call.
            }
        }

        /// <summary>Returns all log entries in chronological order.</summary>
        public List<LogEntry> GetLogs()
        {
            lock (_lockObject)
            {
                return _logs.OrderBy(l => l.Timestamp).ToList();
            }
        }

        /// <summary>Returns the most recent <paramref name="count"/> entries in chronological order.</summary>
        public List<LogEntry> GetRecentLogs(int count = 50)
        {
            lock (_lockObject)
            {
                return _logs
                    .OrderByDescending(l => l.Timestamp)
                    .Take(count)
                    .OrderBy(l => l.Timestamp)
                    .ToList();
            }
        }

        /// <summary>Clears the activity feed view only; saved entries stay in the store and the log file.</summary>
        public void Clear()
        {
            lock (_lockObject)
            {
                _logs.Clear();
            }
            OnLogsChanged?.Invoke();
        }
    }
}
