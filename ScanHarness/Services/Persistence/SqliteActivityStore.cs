using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScanHarness.Models;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Services.Persistence
{
    /// <summary>
    /// <see cref="IActivityStore"/> backed by a local SQLite file. Timestamps are stored as ISO-8601 UTC
    /// text, enums by name, and parameter/data dictionaries as JSON. Each operation opens its own pooled
    /// connection, and a lock serialises access so concurrent circuits don't contend for the file.
    /// </summary>
    public sealed class SqliteActivityStore : IActivityStore
    {
        private const string InterruptedError = "Interrupted: the app stopped before this call finished.";

        private readonly string _connectionString;
        private readonly object _gate = new();

        /// <param name="databasePath">Path of the SQLite file; its folder is created if missing.</param>
        public SqliteActivityStore(string databasePath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        }

        public int Initialize()
        {
            lock (_gate)
            {
                using var connection = Open();
                Execute(connection, """
                    CREATE TABLE IF NOT EXISTS sessions (
                        id                TEXT PRIMARY KEY,
                        user_name         TEXT NOT NULL,
                        started_at        TEXT NOT NULL,
                        last_activity_at  TEXT NOT NULL,
                        title             TEXT
                    );
                    CREATE TABLE IF NOT EXISTS tool_calls (
                        id                  TEXT PRIMARY KEY,
                        session_id          TEXT NOT NULL REFERENCES sessions(id),
                        user_name           TEXT NOT NULL,
                        tool                TEXT NOT NULL,
                        action              TEXT NOT NULL,
                        parameters_json     TEXT NOT NULL,
                        created_at          TEXT NOT NULL,
                        status              TEXT NOT NULL,
                        result              TEXT,
                        requires_approval   INTEGER NOT NULL,
                        is_approved         INTEGER NOT NULL,
                        approval_decision   TEXT,
                        approval_decided_at TEXT,
                        approval_decided_by TEXT,
                        brain               TEXT,
                        error               TEXT,
                        summary             TEXT,
                        summary_brain       TEXT,
                        completed_at        TEXT
                    );
                    CREATE TABLE IF NOT EXISTS log_entries (
                        id            TEXT PRIMARY KEY,
                        session_id    TEXT NOT NULL REFERENCES sessions(id),
                        user_name     TEXT NOT NULL,
                        timestamp     TEXT NOT NULL,
                        level         TEXT NOT NULL,
                        message       TEXT NOT NULL,
                        tool_call_id  TEXT,
                        brain         TEXT,
                        data_json     TEXT
                    );
                    CREATE TABLE IF NOT EXISTS control_settings (
                        user_name   TEXT NOT NULL,
                        control_id  TEXT NOT NULL,
                        mode        TEXT NOT NULL,
                        changed_at  TEXT NOT NULL,
                        PRIMARY KEY (user_name, control_id)
                    );
                    CREATE INDEX IF NOT EXISTS ix_sessions_user ON sessions(user_name, last_activity_at);
                    CREATE INDEX IF NOT EXISTS ix_tool_calls_session ON tool_calls(session_id, created_at);
                    CREATE INDEX IF NOT EXISTS ix_log_entries_session ON log_entries(session_id, timestamp);
                    """);

                // Calls that were still waiting or running when the app stopped can never finish now. Close
                // them out, and add an entry to each session's timeline so the recreated session shows why.
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO log_entries (id, session_id, user_name, timestamp, level, message, tool_call_id, brain, data_json)
                    SELECT lower(hex(randomblob(16))), session_id, user_name, $now, 'Error',
                           'Tool call interrupted: ' || tool || '/' || action || ' (the app stopped before it finished)',
                           id, brain, NULL
                    FROM tool_calls
                    WHERE status IN ('Pending', 'WaitingForApproval', 'Approved', 'Running');

                    UPDATE tool_calls
                    SET status = 'Cancelled',
                        error = COALESCE(error, $error),
                        completed_at = COALESCE(completed_at, $now)
                    WHERE status IN ('Pending', 'WaitingForApproval', 'Approved', 'Running');

                    SELECT changes();
                    """;
                command.Parameters.AddWithValue("$error", InterruptedError);
                command.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
                var interrupted = Convert.ToInt32(command.ExecuteScalar());
                transaction.Commit();
                return interrupted;
            }
        }

        public void SaveSession(HarnessSession session)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO sessions (id, user_name, started_at, last_activity_at, title)
                    VALUES ($id, $user, $started, $last, $title)
                    ON CONFLICT(id) DO UPDATE SET
                        last_activity_at = excluded.last_activity_at,
                        title = COALESCE(sessions.title, excluded.title);
                    """;
                command.Parameters.AddWithValue("$id", session.Id);
                command.Parameters.AddWithValue("$user", session.UserName);
                command.Parameters.AddWithValue("$started", Format(session.StartedAt));
                command.Parameters.AddWithValue("$last", Format(session.LastActivityAt));
                command.Parameters.AddWithValue("$title", (object?)session.Title ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        public void SaveToolCall(ToolCall call)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO tool_calls (id, session_id, user_name, tool, action, parameters_json, created_at, status,
                        result, requires_approval, is_approved, approval_decision, approval_decided_at, approval_decided_by,
                        brain, error, summary, summary_brain, completed_at)
                    VALUES ($id, $session, $user, $tool, $action, $parameters, $created, $status,
                        $result, $requiresApproval, $isApproved, $decision, $decidedAt, $decidedBy,
                        $brain, $error, $summary, $summaryBrain, $completed)
                    ON CONFLICT(id) DO UPDATE SET
                        status = excluded.status,
                        result = excluded.result,
                        is_approved = excluded.is_approved,
                        approval_decision = excluded.approval_decision,
                        approval_decided_at = excluded.approval_decided_at,
                        approval_decided_by = excluded.approval_decided_by,
                        error = excluded.error,
                        summary = excluded.summary,
                        summary_brain = excluded.summary_brain,
                        completed_at = excluded.completed_at;
                    """;
                command.Parameters.AddWithValue("$id", call.Id);
                command.Parameters.AddWithValue("$session", Required(call.SessionId, nameof(call.SessionId)));
                command.Parameters.AddWithValue("$user", Required(call.UserName, nameof(call.UserName)));
                command.Parameters.AddWithValue("$tool", call.Tool);
                command.Parameters.AddWithValue("$action", call.Action);
                command.Parameters.AddWithValue("$parameters", JsonSerializer.Serialize(call.Parameters));
                command.Parameters.AddWithValue("$created", Format(call.CreatedAt));
                command.Parameters.AddWithValue("$status", call.Status.ToString());
                command.Parameters.AddWithValue("$result", DbValue(call.Result));
                command.Parameters.AddWithValue("$requiresApproval", call.RequiresApproval ? 1 : 0);
                command.Parameters.AddWithValue("$isApproved", call.IsApproved ? 1 : 0);
                command.Parameters.AddWithValue("$decision", DbValue(call.ApprovalDecision));
                command.Parameters.AddWithValue("$decidedAt", DbValue(call.ApprovalDecidedAt));
                command.Parameters.AddWithValue("$decidedBy", DbValue(call.ApprovalDecidedBy));
                command.Parameters.AddWithValue("$brain", DbValue(call.Brain));
                command.Parameters.AddWithValue("$error", DbValue(call.Error));
                command.Parameters.AddWithValue("$summary", DbValue(call.Summary));
                command.Parameters.AddWithValue("$summaryBrain", DbValue(call.SummaryBrain));
                command.Parameters.AddWithValue("$completed", DbValue(call.CompletedAt));
                command.ExecuteNonQuery();
            }
        }

        public void AppendLog(LogEntry entry)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO log_entries (id, session_id, user_name, timestamp, level, message, tool_call_id, brain, data_json)
                        VALUES ($id, $session, $user, $timestamp, $level, $message, $toolCall, $brain, $data);
                        """;
                    insert.Parameters.AddWithValue("$id", entry.Id);
                    insert.Parameters.AddWithValue("$session", Required(entry.SessionId, nameof(entry.SessionId)));
                    insert.Parameters.AddWithValue("$user", Required(entry.UserName, nameof(entry.UserName)));
                    insert.Parameters.AddWithValue("$timestamp", Format(entry.Timestamp));
                    insert.Parameters.AddWithValue("$level", entry.Level.ToString());
                    insert.Parameters.AddWithValue("$message", entry.Message);
                    insert.Parameters.AddWithValue("$toolCall", DbValue(entry.ToolCallId));
                    insert.Parameters.AddWithValue("$brain", DbValue(entry.Brain));
                    insert.Parameters.AddWithValue("$data", entry.Data == null ? DBNull.Value : JsonSerializer.Serialize(entry.Data));
                    insert.ExecuteNonQuery();
                }

                using (var touch = connection.CreateCommand())
                {
                    touch.Transaction = transaction;
                    touch.CommandText = "UPDATE sessions SET last_activity_at = MAX(last_activity_at, $timestamp) WHERE id = $session;";
                    touch.Parameters.AddWithValue("$timestamp", Format(entry.Timestamp));
                    touch.Parameters.AddWithValue("$session", entry.SessionId);
                    touch.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        public IReadOnlyList<HarnessSession> ListSessions(string? userName)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT s.id, s.user_name, s.started_at, s.last_activity_at, s.title,
                           (SELECT COUNT(*) FROM tool_calls t WHERE t.session_id = s.id),
                           (SELECT COUNT(*) FROM log_entries l WHERE l.session_id = s.id AND l.level = 'Warning' AND l.data_json LIKE '%"controls"%'),
                           (SELECT COUNT(*) FROM log_entries l WHERE l.session_id = s.id AND l.level = 'Block')
                    FROM sessions s
                    WHERE $user IS NULL OR s.user_name = $user
                    ORDER BY s.last_activity_at DESC, s.id;
                    """;
                command.Parameters.AddWithValue("$user", DbValue(userName));
                return ReadSessions(command);
            }
        }

        public HarnessSession? GetSession(string sessionId)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT s.id, s.user_name, s.started_at, s.last_activity_at, s.title,
                           (SELECT COUNT(*) FROM tool_calls t WHERE t.session_id = s.id),
                           (SELECT COUNT(*) FROM log_entries l WHERE l.session_id = s.id AND l.level = 'Warning' AND l.data_json LIKE '%"controls"%'),
                           (SELECT COUNT(*) FROM log_entries l WHERE l.session_id = s.id AND l.level = 'Block')
                    FROM sessions s
                    WHERE s.id = $id;
                    """;
                command.Parameters.AddWithValue("$id", sessionId);
                return ReadSessions(command).FirstOrDefault();
            }
        }

        public IReadOnlyList<ToolCall> LoadToolCalls(string sessionId)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, session_id, user_name, tool, action, parameters_json, created_at, status, result,
                           requires_approval, is_approved, approval_decision, approval_decided_at, approval_decided_by,
                           brain, error, summary, summary_brain, completed_at
                    FROM tool_calls
                    WHERE session_id = $session
                    ORDER BY created_at, id;
                    """;
                command.Parameters.AddWithValue("$session", sessionId);

                var calls = new List<ToolCall>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    calls.Add(new ToolCall
                    {
                        Id = reader.GetString(0),
                        SessionId = reader.GetString(1),
                        UserName = reader.GetString(2),
                        Tool = reader.GetString(3),
                        Action = reader.GetString(4),
                        Parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5)) ?? new(),
                        CreatedAt = Parse(reader.GetString(6)),
                        Status = Enum.Parse<ToolCallStatus>(reader.GetString(7)),
                        Result = GetNullableString(reader, 8),
                        RequiresApproval = reader.GetInt64(9) != 0,
                        IsApproved = reader.GetInt64(10) != 0,
                        ApprovalDecision = GetNullableString(reader, 11),
                        ApprovalDecidedAt = GetNullableDate(reader, 12),
                        ApprovalDecidedBy = GetNullableString(reader, 13),
                        Brain = GetNullableString(reader, 14),
                        Error = GetNullableString(reader, 15),
                        Summary = GetNullableString(reader, 16),
                        SummaryBrain = GetNullableString(reader, 17),
                        CompletedAt = GetNullableDate(reader, 18),
                    });
                }

                return calls;
            }
        }

        public IReadOnlyList<LogEntry> LoadLogs(string sessionId)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, session_id, user_name, timestamp, level, message, tool_call_id, brain, data_json
                    FROM log_entries
                    WHERE session_id = $session
                    ORDER BY timestamp, rowid;
                    """;
                command.Parameters.AddWithValue("$session", sessionId);

                var entries = new List<LogEntry>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var data = GetNullableString(reader, 8);
                    entries.Add(new LogEntry
                    {
                        Id = reader.GetString(0),
                        SessionId = reader.GetString(1),
                        UserName = reader.GetString(2),
                        Timestamp = Parse(reader.GetString(3)),
                        Level = Enum.Parse<LogLevel>(reader.GetString(4)),
                        Message = reader.GetString(5),
                        ToolCallId = GetNullableString(reader, 6),
                        Brain = GetNullableString(reader, 7),
                        Data = data == null ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(data),
                    });
                }

                return entries;
            }
        }

        public IReadOnlyList<string> SessionIdsWithActivity(DateTime? fromUtc, DateTime? toUtc)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT DISTINCT session_id FROM log_entries
                    WHERE ($from IS NULL OR timestamp >= $from) AND ($to IS NULL OR timestamp <= $to);
                    """;
                command.Parameters.AddWithValue("$from", DbValue(fromUtc));
                command.Parameters.AddWithValue("$to", DbValue(toUtc));

                var ids = new List<string>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ids.Add(reader.GetString(0));
                }

                return ids;
            }
        }

        public IReadOnlyDictionary<string, Controls.ControlMode> LoadControlSettings(string userName)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT control_id, mode FROM control_settings WHERE user_name = $user;";
                command.Parameters.AddWithValue("$user", userName);

                var settings = new Dictionary<string, Controls.ControlMode>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    // Ignore modes this version doesn't know rather than failing to load.
                    if (Enum.TryParse<Controls.ControlMode>(reader.GetString(1), out var mode))
                    {
                        settings[reader.GetString(0)] = mode;
                    }
                }

                return settings;
            }
        }

        public void SaveControlSetting(string userName, string controlId, Controls.ControlMode mode, DateTime changedAt)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO control_settings (user_name, control_id, mode, changed_at)
                    VALUES ($user, $control, $mode, $changed)
                    ON CONFLICT(user_name, control_id) DO UPDATE SET mode = excluded.mode, changed_at = excluded.changed_at;
                    """;
                command.Parameters.AddWithValue("$user", userName);
                command.Parameters.AddWithValue("$control", controlId);
                command.Parameters.AddWithValue("$mode", mode.ToString());
                command.Parameters.AddWithValue("$changed", Format(changedAt));
                command.ExecuteNonQuery();
            }
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            // Only ever called with the fixed schema DDL in Initialize, never user input.
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
            command.ExecuteNonQuery();
        }

        private static List<HarnessSession> ReadSessions(SqliteCommand command)
        {
            var sessions = new List<HarnessSession>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sessions.Add(new HarnessSession
                {
                    Id = reader.GetString(0),
                    UserName = reader.GetString(1),
                    StartedAt = Parse(reader.GetString(2)),
                    LastActivityAt = Parse(reader.GetString(3)),
                    Title = GetNullableString(reader, 4),
                    ToolCallCount = reader.GetInt32(5),
                    ControlWarningCount = reader.GetInt32(6),
                    ControlBlockCount = reader.GetInt32(7),
                });
            }

            return sessions;
        }

        // Round-trip ("O") format keeps full precision and sorts correctly as text.
        private static string Format(DateTime value) =>
            DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

        private static DateTime Parse(string value) =>
            DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        private static object DbValue(string? value) => (object?)value ?? DBNull.Value;

        private static object DbValue(DateTime? value) => value.HasValue ? Format(value.Value) : DBNull.Value;

        private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        private static DateTime? GetNullableDate(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));

        private static string Required(string? value, string name) =>
            string.IsNullOrEmpty(value) ? throw new InvalidOperationException($"{name} must be set before saving.") : value;
    }
}
