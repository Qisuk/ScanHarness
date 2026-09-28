using Microsoft.Data.Sqlite;
using ScanHarness.Models;
using ScanHarness.Services.Persistence;
using Xunit;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Tests
{
    /// <summary>Runs against a real SQLite file in a temp folder, deleted after each test.</summary>
    public sealed class SqliteActivityStoreTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "scanharness-tests", Guid.NewGuid().ToString("N"));
        private readonly SqliteActivityStore _store;

        public SqliteActivityStoreTests()
        {
            _store = new SqliteActivityStore(Path.Combine(_directory, "nested", "activity.db"));
            _store.Initialize();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

        private HarnessSession SaveSession(string user, int minutes = 0, string? title = null)
        {
            var session = new HarnessSession { UserName = user, StartedAt = T0.AddMinutes(minutes), LastActivityAt = T0.AddMinutes(minutes), Title = title };
            _store.SaveSession(session);
            return session;
        }

        private static ToolCall Call(HarnessSession session, ToolCallStatus status = ToolCallStatus.Completed) => new()
        {
            SessionId = session.Id,
            UserName = session.UserName,
            Tool = "attack",
            Action = "execute",
            Parameters = new() { ["url"] = "https://example.com" },
            CreatedAt = T0.AddSeconds(5),
            Status = status,
            RequiresApproval = true,
            Brain = "Keyword brain (rule-based)",
        };

        [Fact]
        public void Initialize_creates_the_database_folder_and_is_repeatable()
        {
            Assert.True(File.Exists(Path.Combine(_directory, "nested", "activity.db")));
            Assert.Equal(0, _store.Initialize());
        }

        [Fact]
        public void Tool_call_round_trips_every_field_including_approval_and_timestamps()
        {
            var session = SaveSession("chris");
            var call = Call(session);
            call.Result = "{\"status\":\"success\"}";
            call.IsApproved = true;
            call.ApprovalDecision = "approved";
            call.ApprovalDecidedAt = T0.AddSeconds(8);
            call.ApprovalDecidedBy = "chris";
            call.Error = null;
            call.Summary = "The user asked me to run a simulated attack…";
            call.SummaryBrain = "llama3.1:8b (LLM)";
            call.CompletedAt = T0.AddSeconds(12).AddTicks(1234);
            _store.SaveToolCall(call);

            var loaded = Assert.Single(_store.LoadToolCalls(session.Id));

            Assert.Equal(call.Id, loaded.Id);
            Assert.Equal("chris", loaded.UserName);
            Assert.Equal(new Dictionary<string, string> { ["url"] = "https://example.com" }, loaded.Parameters);
            Assert.Equal(call.CreatedAt, loaded.CreatedAt);
            Assert.Equal(ToolCallStatus.Completed, loaded.Status);
            Assert.Equal(call.Result, loaded.Result);
            Assert.True(loaded.RequiresApproval);
            Assert.True(loaded.IsApproved);
            Assert.Equal("approved", loaded.ApprovalDecision);
            Assert.Equal(call.ApprovalDecidedAt, loaded.ApprovalDecidedAt);
            Assert.Equal("chris", loaded.ApprovalDecidedBy);
            Assert.Equal(call.Summary, loaded.Summary);
            Assert.Equal(call.SummaryBrain, loaded.SummaryBrain);
            Assert.Equal(call.CompletedAt, loaded.CompletedAt); // full tick precision
            Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        }

        [Fact]
        public void Saving_a_call_again_updates_it_rather_than_duplicating_it()
        {
            var session = SaveSession("chris");
            var call = Call(session, ToolCallStatus.WaitingForApproval);
            _store.SaveToolCall(call);

            call.Status = ToolCallStatus.Cancelled;
            call.ApprovalDecision = "rejected";
            _store.SaveToolCall(call);

            var loaded = Assert.Single(_store.LoadToolCalls(session.Id));
            Assert.Equal(ToolCallStatus.Cancelled, loaded.Status);
            Assert.Equal("rejected", loaded.ApprovalDecision);
        }

        [Fact]
        public void Log_entries_round_trip_in_order_with_user_brain_and_data()
        {
            var session = SaveSession("raccoon");
            var first = new LogEntry
            {
                SessionId = session.Id, UserName = "raccoon", Timestamp = T0.AddSeconds(1), Level = LogLevel.User,
                Message = "Request: scan example.com", Brain = "Keyword brain (rule-based)",
                Data = new() { ["prompt"] = "scan example.com", ["elapsedMs"] = 12L },
            };
            var second = new LogEntry
            {
                SessionId = session.Id, UserName = "raccoon", Timestamp = T0.AddSeconds(2), Level = LogLevel.Summary,
                Message = "The user asked me to scan…", ToolCallId = "call-1",
            };
            _store.AppendLog(second);
            _store.AppendLog(first);

            var loaded = _store.LoadLogs(session.Id);

            Assert.Equal(new[] { first.Id, second.Id }, loaded.Select(e => e.Id));
            Assert.Equal(LogLevel.User, loaded[0].Level);
            Assert.Equal("raccoon", loaded[0].UserName);
            Assert.Equal("Keyword brain (rule-based)", loaded[0].Brain);
            Assert.Equal("scan example.com", loaded[0].Data!["prompt"].ToString());
            Assert.Equal("call-1", loaded[1].ToolCallId);
            Assert.Null(loaded[1].Data);
        }

        [Fact]
        public void Appending_a_log_entry_moves_the_sessions_last_activity_forward()
        {
            var session = SaveSession("chris");
            _store.AppendLog(new LogEntry { SessionId = session.Id, UserName = "chris", Timestamp = T0.AddHours(1), Message = "x" });

            Assert.Equal(T0.AddHours(1), _store.GetSession(session.Id)!.LastActivityAt);
        }

        [Fact]
        public void Sessions_are_listed_per_user_newest_first_with_call_counts()
        {
            var older = SaveSession("chris", minutes: 0, title: "scan example.com");
            var newer = SaveSession("chris", minutes: 10, title: "weather in London");
            var other = SaveSession("blob", minutes: 5);
            _store.SaveToolCall(Call(older));

            var chris = _store.ListSessions("chris");
            Assert.Equal(new[] { newer.Id, older.Id }, chris.Select(s => s.Id));
            Assert.Equal(1, chris[1].ToolCallCount);
            Assert.Equal("scan example.com", chris[1].Title);

            Assert.Equal(new[] { other.Id }, _store.ListSessions("blob").Select(s => s.Id));
            Assert.Empty(_store.ListSessions("raccoon"));
            Assert.Equal(3, _store.ListSessions(null).Count);
        }

        [Fact]
        public void Session_title_is_kept_once_set()
        {
            var session = SaveSession("chris", title: "first request");
            session.Title = "second request";
            _store.SaveSession(session);

            Assert.Equal("first request", _store.GetSession(session.Id)!.Title);
        }

        [Fact]
        public void Initialize_marks_unfinished_calls_from_a_previous_run_as_interrupted()
        {
            var session = SaveSession("chris");
            var waiting = Call(session, ToolCallStatus.WaitingForApproval);
            var done = Call(session, ToolCallStatus.Completed);
            _store.SaveToolCall(waiting);
            _store.SaveToolCall(done);

            // A new store over the same file, as after an app restart.
            var restarted = new SqliteActivityStore(Path.Combine(_directory, "nested", "activity.db"));
            Assert.Equal(1, restarted.Initialize());

            var calls = restarted.LoadToolCalls(session.Id).ToDictionary(c => c.Id);
            Assert.Equal(ToolCallStatus.Cancelled, calls[waiting.Id].Status);
            Assert.StartsWith("Interrupted", calls[waiting.Id].Error);
            Assert.NotNull(calls[waiting.Id].CompletedAt);
            Assert.Equal(ToolCallStatus.Completed, calls[done.Id].Status);

            // The session's timeline records the interruption, against the call and its user.
            var entry = Assert.Single(restarted.LoadLogs(session.Id));
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Equal(waiting.Id, entry.ToolCallId);
            Assert.Equal("chris", entry.UserName);
            Assert.StartsWith("Tool call interrupted: attack/execute", entry.Message);

            // Running it again finds nothing left to close.
            Assert.Equal(0, restarted.Initialize());
        }

        [Fact]
        public void Saving_without_a_session_or_user_is_refused()
        {
            Assert.Throws<InvalidOperationException>(() => _store.SaveToolCall(new ToolCall { Tool = "x", Action = "y" }));
            Assert.Throws<InvalidOperationException>(() => _store.AppendLog(new LogEntry { Message = "orphan" }));
        }
    }
}
