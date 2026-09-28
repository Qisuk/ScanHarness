using ScanHarness.Models;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Services
{
    /// <summary>
    /// Tracks activity for the harness. Keeps an in-memory list of log entries for the
    /// UI activity feed and appends every entry to a log file for auditing and debugging.
    /// </summary>
    public class LogService
    {
        private readonly List<LogEntry> _logs = new();
        private readonly object _lockObject = new();
        private readonly string _logFilePath;

        /// <summary>Raised whenever the set of log entries changes so the UI can refresh.</summary>
        public event Action? OnLogsChanged;

        public LogService()
        {
            var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            _logFilePath = Path.Combine(logDirectory, $"toolcalls-{DateTime.UtcNow:yyyy-MM-dd}.log");
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
                Brain = brain
            };

            lock (_lockObject)
            {
                _logs.Add(entry);
            }

            WriteToFile(entry);
            OnLogsChanged?.Invoke();
        }

        private void WriteToFile(LogEntry entry)
        {
            // Tab-separated: timestamp, level, tool call id, brain, message ("-" for empty columns).
            var toolCallPart = string.IsNullOrEmpty(entry.ToolCallId) ? "-" : entry.ToolCallId;
            var brainPart = string.IsNullOrEmpty(entry.Brain) ? "-" : entry.Brain;
            var line = $"{entry.Timestamp:O}\t{entry.Level}\t{toolCallPart}\t{brainPart}\t{entry.Message}";

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
