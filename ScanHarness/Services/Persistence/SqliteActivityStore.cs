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
                // Fixed schema text; values are only ever passed as parameters (see below and the other queries).
                using (var schema = connection.CreateCommand())
                {
                    schema.CommandText = """
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
                        """;
                    schema.ExecuteNonQuery();
                }

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

                // Every connection the egress proxy was asked to make. Not tied to sessions by a foreign key: a
                // connection without a valid egress token belongs to no session, and it must be recorded too.
                using (var egress = connection.CreateCommand())
                {
                    egress.CommandText = """
                        CREATE TABLE IF NOT EXISTS egress_events (
                            id              TEXT PRIMARY KEY,
                            timestamp       TEXT NOT NULL,
                            request_id      TEXT,
                            method          TEXT NOT NULL,
                            target          TEXT NOT NULL,
                            host            TEXT,
                            port            INTEGER,
                            upstream        TEXT,
                            allowed         INTEGER NOT NULL,
                            status          INTEGER NOT NULL,
                            rule            TEXT NOT NULL,
                            reason          TEXT NOT NULL,
                            caller          TEXT NOT NULL,
                            tool_call_id    TEXT,
                            session_id      TEXT,
                            user_name       TEXT,
                            engagement_id   TEXT,
                            bytes_sent      INTEGER,
                            bytes_received  INTEGER,
                            duration_ms     INTEGER
                        );
                        CREATE INDEX IF NOT EXISTS ix_egress_events_time ON egress_events(timestamp);
                        CREATE INDEX IF NOT EXISTS ix_egress_events_tool_call ON egress_events(tool_call_id);
                        CREATE INDEX IF NOT EXISTS ix_egress_events_request ON egress_events(request_id);
                        """;
                    egress.ExecuteNonQuery();
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

        public void SaveEgressEvent(EgressEvent e)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO egress_events (id, timestamp, request_id, method, target, host, port, upstream, allowed, status, rule,
                                               reason, caller, tool_call_id, session_id, user_name, engagement_id,
                                               bytes_sent, bytes_received, duration_ms)
                    VALUES ($id, $timestamp, $requestId, $method, $target, $host, $port, $upstream, $allowed, $status, $rule,
                            $reason, $caller, $toolCallId, $sessionId, $userName, $engagementId, $sent, $received, $duration)
                    ON CONFLICT(id) DO UPDATE SET
                        bytes_sent = excluded.bytes_sent, bytes_received = excluded.bytes_received, duration_ms = excluded.duration_ms;
                    """;
                command.Parameters.AddWithValue("$id", e.Id);
                command.Parameters.AddWithValue("$timestamp", Format(e.Timestamp));
                command.Parameters.AddWithValue("$requestId", DbValue(e.RequestId));
                command.Parameters.AddWithValue("$method", e.Method);
                command.Parameters.AddWithValue("$target", e.Target);
                command.Parameters.AddWithValue("$host", DbValue(e.Host));
                command.Parameters.AddWithValue("$port", (object?)e.Port ?? DBNull.Value);
                command.Parameters.AddWithValue("$upstream", DbValue(e.Upstream));
                command.Parameters.AddWithValue("$allowed", e.Allowed ? 1 : 0);
                command.Parameters.AddWithValue("$status", e.Status);
                command.Parameters.AddWithValue("$rule", e.Rule);
                command.Parameters.AddWithValue("$reason", e.Reason);
                command.Parameters.AddWithValue("$caller", e.Caller);
                command.Parameters.AddWithValue("$toolCallId", DbValue(e.ToolCallId));
                command.Parameters.AddWithValue("$sessionId", DbValue(e.SessionId));
                command.Parameters.AddWithValue("$userName", DbValue(e.UserName));
                command.Parameters.AddWithValue("$engagementId", DbValue(e.EngagementId));
                command.Parameters.AddWithValue("$sent", (object?)e.BytesSent ?? DBNull.Value);
                command.Parameters.AddWithValue("$received", (object?)e.BytesReceived ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration", (object?)e.DurationMs ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        public IReadOnlyList<EgressEvent> LoadEgressEvents(string toolCallId)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {EgressColumns} FROM egress_events WHERE tool_call_id = $toolCallId ORDER BY timestamp, rowid;";
                command.Parameters.AddWithValue("$toolCallId", toolCallId);
                return ReadEgressEvents(command);
            }
        }

        public IReadOnlyList<EgressEvent> QueryEgressEvents(EgressEventQuery query)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT {EgressColumns} FROM egress_events
                    WHERE ($from IS NULL OR timestamp >= $from) AND ($to IS NULL OR timestamp <= $to)
                      AND ($user IS NULL OR user_name = $user) AND ($session IS NULL OR session_id = $session)
                      AND ($allowed IS NULL OR allowed = $allowed) AND ($caller IS NULL OR caller = $caller)
                      AND ($host IS NULL OR instr(lower(coalesce(host, target)), lower($host)) > 0)
                      AND ($challenges IS NULL OR (status = 407) = $challenges)
                    ORDER BY timestamp DESC, rowid DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$from", DbValue(query.FromUtc));
                command.Parameters.AddWithValue("$to", DbValue(query.ToUtc));
                command.Parameters.AddWithValue("$user", DbValue(query.UserName));
                command.Parameters.AddWithValue("$session", DbValue(query.SessionId));
                command.Parameters.AddWithValue("$allowed", query.Allowed is bool allowed ? (allowed ? 1 : 0) : DBNull.Value);
                command.Parameters.AddWithValue("$caller", DbValue(query.Caller));
                command.Parameters.AddWithValue("$host", DbValue(string.IsNullOrWhiteSpace(query.HostContains) ? null : query.HostContains.Trim()));
                command.Parameters.AddWithValue("$limit", Math.Max(1, query.Limit));
                command.Parameters.AddWithValue("$challenges", query.Challenges is bool challenges ? (challenges ? 1 : 0) : DBNull.Value);
                return ReadEgressEvents(command);
            }
        }

        public bool UpdateEgressTraffic(string requestId, long? bytesSent, long? bytesReceived, long? durationMs)
        {
            lock (_gate)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE egress_events SET bytes_sent = $sent, bytes_received = $received, duration_ms = $duration
                    WHERE request_id = $requestId;
                    """;
                command.Parameters.AddWithValue("$requestId", requestId);
                command.Parameters.AddWithValue("$sent", (object?)bytesSent ?? DBNull.Value);
                command.Parameters.AddWithValue("$received", (object?)bytesReceived ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration", (object?)durationMs ?? DBNull.Value);
                return command.ExecuteNonQuery() > 0;
            }
        }

        private const string EgressColumns = """
            id, timestamp, request_id, method, target, host, port, upstream, allowed, status, rule, reason, caller,
            tool_call_id, session_id, user_name, engagement_id, bytes_sent, bytes_received, duration_ms
            """;

        private static List<EgressEvent> ReadEgressEvents(SqliteCommand command)
        {
            static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

            var events = new List<EgressEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new EgressEvent
                {
                    Id = reader.GetString(0),
                    Timestamp = Parse(reader.GetString(1)),
                    RequestId = GetNullableString(reader, 2),
                    Method = reader.GetString(3),
                    Target = reader.GetString(4),
                    Host = GetNullableString(reader, 5),
                    Port = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    Upstream = GetNullableString(reader, 7),
                    Allowed = reader.GetInt32(8) != 0,
                    Status = reader.GetInt32(9),
                    Rule = reader.GetString(10),
                    Reason = reader.GetString(11),
                    Caller = reader.GetString(12),
                    ToolCallId = GetNullableString(reader, 13),
                    SessionId = GetNullableString(reader, 14),
                    UserName = GetNullableString(reader, 15),
                    EngagementId = GetNullableString(reader, 16),
                    BytesSent = Long(reader, 17),
                    BytesReceived = Long(reader, 18),
                    DurationMs = Long(reader, 19),
                });
            }

            return events;
        }

        private static T? ParseEnum<T>(string? value) where T : struct, Enum =>
            Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

        private static DateOnly? ParseDate(string? value) =>
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

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
