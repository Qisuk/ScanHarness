using Microsoft.Data.Sqlite;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Controls;
using ScanHarness.Services.Persistence;
using Xunit;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Tests
{
    /// <summary>Controls tripped in session reports, session severity, and the admin activity explorer.</summary>
    public sealed class ControlReportingTests : IDisposable
    {
        private const string BadInstruction = "chat-in-contains-badinstruction";
        private const string ScriptTag = "chat-out-contains-script-tag";
        private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "scanharness-tests", Guid.NewGuid().ToString("N"));
        private readonly SqliteActivityStore _store;
        private int _second;

        public ControlReportingTests()
        {
            _store = new SqliteActivityStore(Path.Combine(_directory, "activity.db"));
            _store.Initialize();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        private LogEntry Entry(LogLevel level, string message, Dictionary<string, object>? data = null, string user = "chris", string? session = null, DateTime? at = null) =>
            new() { Timestamp = at ?? T0.AddSeconds(_second++), Level = level, Message = message, Data = data, UserName = user, SessionId = session };

        private static ControlViolation Violation(string controlId, ControlMode mode, string target, string? text = null)
        {
            var control = ControlCatalog.Find(controlId)!;
            return new ControlViolation(controlId, control.Name, control.Stage, mode, target, "Found it.", text);
        }

        private LogEntry ControlEntry(ControlViolation v, string user = "chris", string? session = null, DateTime? at = null) =>
            Entry(v.Mode == ControlMode.Block ? LogLevel.Block : LogLevel.Warning, $"{v.Mode}: …",
                new() { ["controls"] = ControlService.ToJson(new[] { v }), ["target"] = v.Target }, user, session, at);

        // --- Controls tripped ---

        [Fact]
        public void Control_trips_are_listed_in_order_with_the_message_that_tripped_them()
        {
            var logs = new[]
            {
                Entry(LogLevel.User, "Request: x", new() { ["prompt"] = "BadInstruction please" }),
                ControlEntry(Violation(BadInstruction, ControlMode.Advise, "request", "BadInstruction please")),
                Entry(LogLevel.Info, "decision", new() { ["agentMessage"] = "I'll scan https://example.com/<script>x" }),
                ControlEntry(Violation(ScriptTag, ControlMode.Block, "reply", "I'll scan https://example.com/<script>x")),
            };

            var trips = SessionReportBuilder.ListControlTrips(logs);

            Assert.Equal(new[] { 1, 2 }, trips.Select(t => t.Number));
            Assert.Equal("BadInstruction please", trips[0].Message);
            Assert.Equal(ControlMode.Advise, trips[0].Violation.Mode);
            Assert.Equal("I'll scan https://example.com/<script>x", trips[1].Message);
            Assert.Equal(ControlMode.Block, trips[1].Violation.Mode);
            Assert.Equal("chris", trips[1].UserName);
        }

        [Fact]
        public void Hits_logged_without_their_text_fall_back_to_the_message_logged_before_them()
        {
            var logs = new[]
            {
                Entry(LogLevel.User, "Request: x", new() { ["prompt"] = "older BadInstruction" }),
                ControlEntry(Violation(BadInstruction, ControlMode.Block, "request")),
                Entry(LogLevel.Summary, "summary", new() { ["summary"] = "a <script> summary" }),
                ControlEntry(Violation(ScriptTag, ControlMode.Advise, "summary")),
            };

            var trips = SessionReportBuilder.ListControlTrips(logs);

            Assert.Equal(new[] { "older BadInstruction", "a <script> summary" }, trips.Select(t => t.Message));
        }

        [Fact]
        public void No_trips_when_no_controls_fired()
        {
            Assert.Empty(SessionReportBuilder.ListControlTrips(new[] { Entry(LogLevel.Warning, "No tool selected") }));
        }

        [Fact]
        public void Checked_text_is_saved_with_each_violation_and_truncated()
        {
            var service = new ControlService(new SessionState());
            var longText = "BadInstruction " + new string('x', 800);

            var violation = Assert.Single(service.Check(ControlStage.ChatIn, "request", longText).Violations);

            Assert.StartsWith("BadInstruction x", violation.Text);
            Assert.Equal(ControlViolation.MaxTextLength + 1, violation.Text!.Length); // + the ellipsis
        }

        // --- Session severity ---

        private HarnessSession Session(string user, string title)
        {
            var session = new HarnessSession { UserName = user, Title = title, StartedAt = T0, LastActivityAt = T0 };
            _store.SaveSession(session);
            return session;
        }

        [Fact]
        public void Sessions_report_control_warnings_and_blocks_but_not_other_warnings()
        {
            var clean = Session("chris", "clean");
            var warned = Session("chris", "warned");
            var blocked = Session("chris", "blocked");
            _store.AppendLog(Entry(LogLevel.Warning, "No tool selected", session: clean.Id));
            _store.AppendLog(ControlEntry(Violation(BadInstruction, ControlMode.Advise, "request"), session: warned.Id));
            _store.AppendLog(ControlEntry(Violation(BadInstruction, ControlMode.Advise, "request"), session: blocked.Id));
            _store.AppendLog(ControlEntry(Violation(ScriptTag, ControlMode.Block, "reply"), session: blocked.Id));

            var sessions = _store.ListSessions("chris").ToDictionary(s => s.Title!);

            Assert.Equal("none", sessions["clean"].ControlSeverity);
            Assert.Equal((1, 0, "warning"), (sessions["warned"].ControlWarningCount, sessions["warned"].ControlBlockCount, sessions["warned"].ControlSeverity));
            Assert.Equal((1, 1, "block"), (sessions["blocked"].ControlWarningCount, sessions["blocked"].ControlBlockCount, sessions["blocked"].ControlSeverity));
        }

        [Fact]
        public void Sessions_with_activity_in_a_time_range_are_found()
        {
            var early = Session("chris", "early");
            var late = Session("blob", "late");
            _store.AppendLog(Entry(LogLevel.Info, "x", session: early.Id, at: T0));
            _store.AppendLog(Entry(LogLevel.Info, "x", user: "blob", session: late.Id, at: T0.AddHours(2)));

            Assert.Equal(new[] { late.Id }, _store.SessionIdsWithActivity(T0.AddHours(1), null));
            Assert.Equal(new[] { early.Id }, _store.SessionIdsWithActivity(null, T0.AddHours(1)));
            Assert.Equal(2, _store.SessionIdsWithActivity(null, null).Count);
        }

        // --- Activity explorer ---

        // chris: a clean scan at T0 and a warned request at T0+1h; blob: a blocked request at T0+2h.
        private ActivityExplorer SeedActivity(out HarnessSession chrisSession, out HarnessSession blobSession)
        {
            chrisSession = Session("chris", "chris session");
            blobSession = Session("blob", "blob session");

            void Request(HarnessSession s, string user, DateTime at, string prompt, ControlViolation? violation = null)
            {
                _store.AppendLog(Entry(LogLevel.User, $"Request: {prompt}", new() { ["prompt"] = prompt }, user, s.Id, at));
                if (violation != null)
                {
                    _store.AppendLog(ControlEntry(violation, user, s.Id, at.AddSeconds(1)));
                }
            }

            Request(chrisSession, "chris", T0, "scan example.com");
            Request(chrisSession, "chris", T0.AddHours(1), "BadInstruction scan", Violation(BadInstruction, ControlMode.Advise, "request"));
            Request(blobSession, "blob", T0.AddHours(2), "BadInstruction attack", Violation(BadInstruction, ControlMode.Block, "request"));
            return new ActivityExplorer(_store);
        }

        [Fact]
        public void Every_users_requests_are_listed_newest_first()
        {
            var explorer = SeedActivity(out _, out _);

            var result = explorer.Find(new ActivityFilter());

            Assert.Equal(new[] { "BadInstruction attack", "BadInstruction scan", "scan example.com" }, result.Rows.Select(r => r.Turn.Prompt));
            Assert.Equal(new[] { "block", "warning", "none" }, result.Rows.Select(r => r.Severity));
            Assert.Equal(3, result.TotalMatches);
        }

        [Fact]
        public void Requests_can_be_filtered_by_time_range_and_user()
        {
            var explorer = SeedActivity(out _, out _);

            var inRange = explorer.Find(new ActivityFilter(FromUtc: T0.AddMinutes(30), ToUtc: T0.AddHours(3)));
            Assert.Equal(2, inRange.Rows.Count);

            var chrisInRange = explorer.Find(new ActivityFilter(UserName: "chris", FromUtc: T0.AddMinutes(30)));
            Assert.Equal(new[] { "BadInstruction scan" }, chrisInRange.Rows.Select(r => r.Turn.Prompt));
        }

        [Theory]
        [InlineData(ControlFilter.Warning, "BadInstruction scan")]
        [InlineData(ControlFilter.Block, "BadInstruction attack")]
        [InlineData(ControlFilter.None, "scan example.com")]
        public void Requests_can_be_filtered_by_control_outcome(ControlFilter filter, string expected)
        {
            var explorer = SeedActivity(out _, out _);

            Assert.Equal(new[] { expected }, explorer.Find(new ActivityFilter(Controls: filter)).Rows.Select(r => r.Turn.Prompt));
        }

        [Fact]
        public void Warnings_or_blocks_by_any_user_in_a_time_range()
        {
            var explorer = SeedActivity(out _, out _);

            var result = explorer.Find(new ActivityFilter(FromUtc: T0.AddMinutes(30), Controls: ControlFilter.Flagged));

            Assert.Equal(new[] { "blob", "chris" }, result.Rows.Select(r => r.Turn.UserName));
        }

        [Fact]
        public void Requests_can_be_filtered_by_session_and_limited()
        {
            var explorer = SeedActivity(out var chrisSession, out _);

            var result = explorer.Find(new ActivityFilter(SessionId: chrisSession.Id), limit: 1);

            Assert.Single(result.Rows);
            Assert.Equal(2, result.TotalMatches);
            Assert.Equal(chrisSession.Id, result.Rows[0].Session.Id);
        }
    }
}
