using Microsoft.Data.Sqlite;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Persistence;
using Xunit;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Tests
{
    /// <summary>
    /// Sessions, users and persistence working together: SessionState, LogService and ToolCallService
    /// over a real SQLite store, including recreating a session as a restarted app would.
    /// </summary>
    public sealed class SessionPersistenceTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "scanharness-tests", Guid.NewGuid().ToString("N"));
        private readonly string _databasePath;
        private readonly SqliteActivityStore _store;

        public SessionPersistenceTests()
        {
            _databasePath = Path.Combine(_directory, "activity.db");
            _store = new SqliteActivityStore(_databasePath);
            _store.Initialize();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        // One browser circuit: its own session state and services over the shared store.
        private (SessionState Session, LogService Log, ToolCallService Tools) Circuit(string user = "chris", string toolResponse = "{\"status\":\"success\"}")
        {
            var session = new SessionState(_store);
            session.SwitchUser(user);
            var log = new LogService(session, _store);
            var tools = new ToolCallService(new HttpClient(FakeHttpMessageHandler.Returning(toolResponse)), log, session, _store);
            return (session, log, tools);
        }

        // --- SessionState ---

        [Fact]
        public void Starts_as_the_default_user_with_an_unsaved_session()
        {
            var session = new SessionState(_store);

            Assert.Equal("chris", session.CurrentUser.Name);
            Assert.False(session.IsSaved);
            Assert.Empty(_store.ListSessions(null));
        }

        [Fact]
        public void Switching_user_starts_a_new_session_and_ignores_unknown_users()
        {
            var session = new SessionState(_store);
            var first = session.Current.Id;

            Assert.True(session.SwitchUser("raccoon"));
            Assert.Equal("raccoon", session.CurrentUser.Name);
            Assert.NotEqual(first, session.Current.Id);
            Assert.Equal("raccoon", session.Current.UserName);

            Assert.False(session.SwitchUser("mallory"));
            Assert.Equal("raccoon", session.CurrentUser.Name);
        }

        [Fact]
        public void A_session_is_only_saved_once_something_happens_in_it()
        {
            var (session, log, _) = Circuit();
            Assert.Empty(_store.ListSessions(null));

            log.LogUser("Request: scan example.com");

            Assert.True(session.IsSaved);
            Assert.Single(_store.ListSessions("chris"));
        }

        [Fact]
        public void Title_is_the_first_request()
        {
            var (session, _, _) = Circuit();

            session.RecordTitle("scan example.com for links");
            session.RecordTitle("attack example.com");

            Assert.Equal("scan example.com for links", _store.GetSession(session.Current.Id)!.Title);
        }

        [Fact]
        public void Users_see_only_their_own_sessions_and_admin_sees_everyones()
        {
            foreach (var user in new[] { "chris", "raccoon", "blob" })
            {
                Circuit(user).Log.LogUser($"Request from {user}");
            }

            Assert.Equal(new[] { "chris" }, Circuit("chris").Session.VisibleSessions().Select(s => s.UserName));
            Assert.Equal(new[] { "blob" }, Circuit("blob").Session.VisibleSessions().Select(s => s.UserName));
            Assert.Equal(new[] { "blob", "chris", "raccoon" },
                Circuit("admin").Session.VisibleSessions().Select(s => s.UserName).OrderBy(n => n));
        }

        [Fact]
        public void A_user_cannot_open_another_users_session_but_admin_can()
        {
            var chris = Circuit("chris");
            chris.Log.LogUser("Request: private");
            var chrisSessionId = chris.Session.Current.Id;

            var blob = Circuit("blob");
            var blobSessionBefore = blob.Session.Current.Id;
            Assert.Null(blob.Session.OpenSession(chrisSessionId));
            Assert.Equal(blobSessionBefore, blob.Session.Current.Id);

            var admin = Circuit("admin");
            Assert.NotNull(admin.Session.OpenSession(chrisSessionId));
            Assert.Equal(chrisSessionId, admin.Session.Current.Id);
        }

        [Fact]
        public void Opening_an_unknown_session_does_nothing()
        {
            var (session, _, _) = Circuit();
            var before = session.Current.Id;

            Assert.Null(session.OpenSession("no-such-session"));
            Assert.Equal(before, session.Current.Id);
        }

        // --- Persistence through the services ---

        [Fact]
        public void Every_log_entry_is_saved_with_its_session_and_user()
        {
            var (session, log, _) = Circuit("raccoon");

            log.LogUser("Request: weather", data: new() { ["prompt"] = "weather" }, brain: "Keyword brain (rule-based)");
            log.LogInfo("Agent selected tool: weather/forecast");

            var saved = _store.LoadLogs(session.Current.Id);
            Assert.Equal(2, saved.Count);
            Assert.All(saved, e => Assert.Equal("raccoon", e.UserName));
            Assert.Equal("weather", saved[0].Data!["prompt"].ToString());
        }

        [Fact]
        public async Task A_tool_call_is_saved_with_its_response_status_and_timestamps()
        {
            var (session, _, tools) = Circuit("chris", toolResponse: "[{\"date\":\"2026-09-29\",\"temperatureC\":12}]");

            var call = await tools.ExecuteToolAsync("weather", "forecast", new() { ["location"] = "London" }, brain: "Keyword brain (rule-based)");
            tools.RecordSummary(call, "The user asked me to get the weather forecast for London.", "Keyword brain (rule-based)");

            var saved = Assert.Single(_store.LoadToolCalls(session.Current.Id));
            Assert.Equal(ToolCallStatus.Completed, saved.Status);
            Assert.Equal("chris", saved.UserName);
            Assert.Contains("temperatureC", saved.Result);
            Assert.Equal("London", saved.Parameters["location"]);
            Assert.NotNull(saved.CompletedAt);
            Assert.Equal("The user asked me to get the weather forecast for London.", saved.Summary);
        }

        [Fact]
        public async Task Approval_decisions_are_saved_with_who_decided_and_when()
        {
            var (session, _, tools) = Circuit("blob");

            var pending = tools.ExecuteToolAsync("attack", "execute", new() { ["url"] = "https://example.com" }, requiresApproval: true);
            Assert.Equal(ToolCallStatus.WaitingForApproval, Assert.Single(_store.LoadToolCalls(session.Current.Id)).Status);

            tools.RejectToolCall(Assert.Single(tools.GetToolCalls()).Id);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));

            var saved = Assert.Single(_store.LoadToolCalls(session.Current.Id));
            Assert.Equal(ToolCallStatus.Cancelled, saved.Status);
            Assert.Equal("rejected", saved.ApprovalDecision);
            Assert.Equal("blob", saved.ApprovalDecidedBy);
            Assert.NotNull(saved.ApprovalDecidedAt);
            Assert.Contains(_store.LoadLogs(session.Current.Id), e => e.Level == LogLevel.User && e.Message.StartsWith("Rejected tool call"));
        }

        [Fact]
        public async Task A_session_can_be_recreated_from_the_store_after_a_restart()
        {
            var (session, log, tools) = Circuit("chris");
            log.LogUser("Request: scan example.com", data: new() { ["prompt"] = "scan example.com" });
            var call = await tools.ExecuteToolAsync("scanner", "simple-scanner", new() { ["url"] = "https://example.com" });
            tools.RecordSummary(call, "The user asked me to scan https://example.com for links.", "Keyword brain (rule-based)");
            log.LogSummary("The user asked me to scan https://example.com for links.", call.Id);
            var sessionId = session.Current.Id;

            // A restarted app: new store over the same file, and a fresh circuit.
            var restartedStore = new SqliteActivityStore(_databasePath);
            restartedStore.Initialize();
            var fresh = new SessionState(restartedStore);
            var freshLog = new LogService(fresh, restartedStore);
            var freshTools = new ToolCallService(new HttpClient(FakeHttpMessageHandler.Returning("{}")), freshLog, fresh, restartedStore);

            Assert.NotNull(fresh.OpenSession(sessionId));
            freshLog.LoadSession(restartedStore.LoadLogs(sessionId));
            freshTools.LoadSession(restartedStore.LoadToolCalls(sessionId));

            Assert.Equal(log.GetLogs().Select(e => (e.Id, e.Message, e.Timestamp)), freshLog.GetLogs().Select(e => (e.Id, e.Message, e.Timestamp)));
            var restored = Assert.Single(freshTools.GetToolCalls());
            Assert.Equal(call.Id, restored.Id);
            Assert.Equal(call.Result, restored.Result);
            Assert.Equal(call.Summary, restored.Summary);

            // New activity continues in the reopened session.
            freshLog.LogUser("Request: attack example.com");
            Assert.Equal(log.GetLogs().Count + 1, restartedStore.LoadLogs(sessionId).Count);
        }

        [Fact]
        public void Admin_activity_in_another_users_session_is_recorded_as_admin()
        {
            var chris = Circuit("chris");
            chris.Log.LogUser("Request: scan example.com");

            var admin = Circuit("admin");
            admin.Session.OpenSession(chris.Session.Current.Id);
            admin.Log.LogUser("Request: weather in London");

            var users = _store.LoadLogs(chris.Session.Current.Id).Select(e => e.UserName);
            Assert.Equal(new[] { "chris", "admin" }, users);
            Assert.Equal("chris", _store.GetSession(chris.Session.Current.Id)!.UserName); // still chris's session
        }

        [Fact]
        public void Services_without_a_store_keep_working_in_memory()
        {
            var log = new LogService();
            log.LogInfo("in memory only");

            Assert.Single(log.GetLogs());
            Assert.Null(log.GetLogs()[0].SessionId);
        }
    }
}
