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

                // Engagements (approved scope and rules of engagement) and edits made on the Permissions tab.
                using (var boundaries = connection.CreateCommand())
                {
                    boundaries.CommandText = """
                        CREATE TABLE IF NOT EXISTS engagements (
                            id            TEXT PRIMARY KEY,
                            customer      TEXT NOT NULL,
                            domains_json  TEXT NOT NULL,
                            window_start  TEXT,
                            window_end    TEXT,
                            rules_json    TEXT NOT NULL,
                            excluded_json TEXT NOT NULL,
                            status        TEXT NOT NULL,
                            proposed_by   TEXT NOT NULL,
                            proposed_at   TEXT NOT NULL,
                            decided_by    TEXT,
                            decided_at    TEXT,
                            version       INTEGER NOT NULL
                        );
                        CREATE TABLE IF NOT EXISTS verified_domains (
                            domain       TEXT PRIMARY KEY,
                            verified_by  TEXT NOT NULL,
                            verified_at  TEXT NOT NULL
                        );
                        CREATE TABLE IF NOT EXISTS component_policies (
                            id              TEXT PRIMARY KEY,
                            classification  TEXT,
                            clearance       TEXT,
                            output_label    TEXT,
                            output_trust    TEXT,
                            enabled         INTEGER,
                            brain           TEXT,
                            changed_by      TEXT NOT NULL,
                            changed_at      TEXT NOT NULL
                        );
                        """;
                    boundaries.ExecuteNonQuery();
                }

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

        public IReadOnlyDictionary<string, Boundaries.PolicyOverride> LoadComponentPolicies()
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, classification, clearance, output_label, output_trust, enabled, brain FROM component_policies;";

                var policies = new Dictionary<string, Boundaries.PolicyOverride>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    // Values this version doesn't know are ignored (the default applies) rather than failing to load.
                    policies[reader.GetString(0)] = new Boundaries.PolicyOverride(
                        ParseEnum<Classification>(GetNullableString(reader, 1)),
                        ParseEnum<Boundaries.Sensitivity>(GetNullableString(reader, 2)),
                        ParseEnum<Boundaries.Sensitivity>(GetNullableString(reader, 3)),
                        ParseEnum<Boundaries.Trust>(GetNullableString(reader, 4)),
                        reader.IsDBNull(5) ? null : reader.GetInt32(5) != 0,
                        GetNullableString(reader, 6));
                }

                return policies;
            }
        }

        public void SaveComponentPolicy(string id, Boundaries.PolicyOverride policy, string changedBy, DateTime changedAt)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO component_policies (id, classification, clearance, output_label, output_trust, enabled, brain, changed_by, changed_at)
                    VALUES ($id, $classification, $clearance, $label, $trust, $enabled, $brain, $by, $at)
                    ON CONFLICT(id) DO UPDATE SET
                        classification = excluded.classification, clearance = excluded.clearance,
                        output_label = excluded.output_label, output_trust = excluded.output_trust,
                        enabled = excluded.enabled, brain = excluded.brain,
                        changed_by = excluded.changed_by, changed_at = excluded.changed_at;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$classification", DbValue(policy.Classification?.ToString()));
                command.Parameters.AddWithValue("$clearance", DbValue(policy.Clearance?.ToString()));
                command.Parameters.AddWithValue("$label", DbValue(policy.OutputLabel?.ToString()));
                command.Parameters.AddWithValue("$trust", DbValue(policy.OutputTrust?.ToString()));
                command.Parameters.AddWithValue("$enabled", policy.Enabled is bool enabled ? (enabled ? 1 : 0) : DBNull.Value);
                command.Parameters.AddWithValue("$brain", DbValue(policy.Brain));
                command.Parameters.AddWithValue("$by", changedBy);
                command.Parameters.AddWithValue("$at", Format(changedAt));
                command.ExecuteNonQuery();
            }
        }

        public void DeleteComponentPolicies(string? id)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM component_policies WHERE $id IS NULL OR id = $id;";
                command.Parameters.AddWithValue("$id", DbValue(id));
                command.ExecuteNonQuery();
            }
        }

        public void SaveEngagement(Engagement engagement)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO engagements (id, customer, domains_json, window_start, window_end, rules_json, excluded_json,
                                             status, proposed_by, proposed_at, decided_by, decided_at, version)
                    VALUES ($id, $customer, $domains, $start, $end, $rules, $excluded, $status, $proposedBy, $proposedAt,
                            $decidedBy, $decidedAt, $version)
                    ON CONFLICT(id) DO UPDATE SET
                        status = excluded.status, decided_by = excluded.decided_by, decided_at = excluded.decided_at;
                    """;
                command.Parameters.AddWithValue("$id", engagement.Id);
                command.Parameters.AddWithValue("$customer", engagement.Customer);
                command.Parameters.AddWithValue("$domains", JsonSerializer.Serialize(engagement.Domains));
                command.Parameters.AddWithValue("$start", DbValue(engagement.WindowStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                command.Parameters.AddWithValue("$end", DbValue(engagement.WindowEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                command.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(engagement.Rules));
                command.Parameters.AddWithValue("$excluded", JsonSerializer.Serialize(engagement.Excluded));
                command.Parameters.AddWithValue("$status", engagement.Status.ToString());
                command.Parameters.AddWithValue("$proposedBy", engagement.ProposedBy);
                command.Parameters.AddWithValue("$proposedAt", Format(engagement.ProposedAt));
                command.Parameters.AddWithValue("$decidedBy", DbValue(engagement.DecidedBy));
                command.Parameters.AddWithValue("$decidedAt", DbValue(engagement.DecidedAt));
                command.Parameters.AddWithValue("$version", engagement.Version);
                command.ExecuteNonQuery();
            }
        }

        public IReadOnlyList<string> ListVerifiedDomains()
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT domain FROM verified_domains ORDER BY domain;";

                var domains = new List<string>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    domains.Add(reader.GetString(0));
                }

                return domains;
            }
        }

        public void SaveVerifiedDomain(string domain, string verifiedBy, DateTime verifiedAt)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO verified_domains (domain, verified_by, verified_at) VALUES ($domain, $by, $at)
                    ON CONFLICT(domain) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$domain", domain);
                command.Parameters.AddWithValue("$by", verifiedBy);
                command.Parameters.AddWithValue("$at", Format(verifiedAt));
                command.ExecuteNonQuery();
            }
        }

        public IReadOnlyList<Engagement> ListEngagements() => QueryEngagements(null);

        public Engagement? GetEngagement(string id) => QueryEngagements(id).FirstOrDefault();

        private IReadOnlyList<Engagement> QueryEngagements(string? id)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, customer, domains_json, window_start, window_end, rules_json, excluded_json, status,
                           proposed_by, proposed_at, decided_by, decided_at, version
                    FROM engagements
                    WHERE $id IS NULL OR id = $id
                    ORDER BY proposed_at DESC;
                    """;
                command.Parameters.AddWithValue("$id", DbValue(id));

                var engagements = new List<Engagement>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    engagements.Add(new Engagement
                    {
                        Id = reader.GetString(0),
                        Customer = reader.GetString(1),
                        Domains = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? new(),
                        WindowStart = ParseDate(GetNullableString(reader, 3)),
                        WindowEnd = ParseDate(GetNullableString(reader, 4)),
                        Rules = JsonSerializer.Deserialize<List<string>>(reader.GetString(5)) ?? new(),
                        Excluded = JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? new(),
                        Status = Enum.TryParse<EngagementStatus>(reader.GetString(7), out var status) ? status : EngagementStatus.Rejected,
                        ProposedBy = reader.GetString(8),
                        ProposedAt = Parse(reader.GetString(9)),
                        DecidedBy = GetNullableString(reader, 10),
                        DecidedAt = GetNullableDate(reader, 11),
                        Version = reader.GetInt32(12),
                    });
                }

                return engagements;
            }
        }

        private static T? ParseEnum<T>(string? value) where T : struct, Enum =>
            Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

        private static DateOnly? ParseDate(string? value) =>
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

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
