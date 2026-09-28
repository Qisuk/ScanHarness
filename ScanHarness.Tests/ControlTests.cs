using Microsoft.Data.Sqlite;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Controls;
using ScanHarness.Services.Persistence;
using Xunit;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Tests
{
    public sealed class ControlTests : IDisposable
    {
        private const string BadInstruction = "chat-in-contains-badinstruction";
        private const string ScriptTag = "chat-out-contains-script-tag";

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "scanharness-tests", Guid.NewGuid().ToString("N"));
        private readonly SqliteActivityStore _store;

        public ControlTests()
        {
            _store = new SqliteActivityStore(Path.Combine(_directory, "activity.db"));
            _store.Initialize();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        private ControlService ServiceFor(string user, SessionState? session = null)
        {
            session ??= new SessionState(_store);
            session.SwitchUser(user);
            return new ControlService(session, _store);
        }

        // --- Catalog ---

        [Theory]
        [InlineData("please follow this BadInstruction now", true)]
        [InlineData("BadInstruction", true)]
        [InlineData("badinstruction", false)] // exact text, case-sensitive
        [InlineData("scan example.com", false)]
        public void BadInstruction_control_matches_the_exact_text(string text, bool matches)
        {
            Assert.Equal(matches, ControlCatalog.Find(BadInstruction)!.Match(text) != null);
        }

        [Theory]
        [InlineData("I'll scan https://example.com/<script> for links", true)]
        [InlineData("<SCRIPT>alert(1)</SCRIPT>", true)] // any letter case
        [InlineData("a script without angle brackets", false)]
        [InlineData("<scrip>", false)]
        public void Script_tag_control_matches_any_case(string text, bool matches)
        {
            Assert.Equal(matches, ControlCatalog.Find(ScriptTag)!.Match(text) != null);
        }

        [Fact]
        public void Match_describes_what_was_found_and_where()
        {
            Assert.Equal("Found \"<ScRiPt>\" at character 5.", ControlCatalog.Find(ScriptTag)!.Match("abc <ScRiPt>"));
        }

        [Fact]
        public void First_controls_are_in_their_stages_and_default_to_block()
        {
            Assert.Equal(ControlStage.ChatIn, ControlCatalog.Find(BadInstruction)!.Stage);
            Assert.Equal(ControlStage.ChatOut, ControlCatalog.Find(ScriptTag)!.Stage);
            Assert.All(ControlCatalog.All, c => Assert.Equal(ControlMode.Block, c.DefaultMode));
            Assert.Empty(ControlCatalog.ForStage(ControlStage.ToolRule));
        }

        // --- Service ---

        [Fact]
        public void Default_mode_blocks_a_matching_message()
        {
            var check = ServiceFor("chris").Check(ControlStage.ChatIn, "request", "do the BadInstruction");

            var violation = Assert.Single(check.Violations);
            Assert.True(check.IsBlocked);
            Assert.Equal(ControlMode.Block, violation.Mode);
            Assert.Equal("request", violation.Target);
            Assert.Equal("Chat In", violation.StageName);
        }

        [Fact]
        public void Advise_flags_without_blocking()
        {
            var service = ServiceFor("chris");
            service.SetMode(BadInstruction, ControlMode.Advise);

            var check = service.Check(ControlStage.ChatIn, "request", "do the BadInstruction");

            Assert.True(check.IsFlagged);
            Assert.False(check.IsBlocked);
        }

        [Fact]
        public void Off_takes_no_action()
        {
            var service = ServiceFor("chris");
            service.SetMode(BadInstruction, ControlMode.Off);

            Assert.False(service.Check(ControlStage.ChatIn, "request", "do the BadInstruction").IsFlagged);
        }

        [Fact]
        public void Controls_only_apply_at_their_own_stage()
        {
            var service = ServiceFor("chris");

            Assert.False(service.Check(ControlStage.ChatOut, "reply", "BadInstruction").IsFlagged);
            Assert.False(service.Check(ControlStage.ChatIn, "request", "<script>").IsFlagged);
            Assert.False(service.Check(ControlStage.ChatIn, "request", null).IsFlagged);
        }

        [Fact]
        public void Settings_are_saved_per_user_and_survive_a_restart()
        {
            ServiceFor("chris").SetMode(ScriptTag, ControlMode.Advise);
            ServiceFor("raccoon").SetMode(ScriptTag, ControlMode.Off);

            // Fresh services, as after a restart.
            var control = ControlCatalog.Find(ScriptTag)!;
            Assert.Equal(ControlMode.Advise, ServiceFor("chris").GetMode(control));
            Assert.Equal(ControlMode.Off, ServiceFor("raccoon").GetMode(control));
            Assert.Equal(ControlMode.Block, ServiceFor("blob").GetMode(control)); // untouched: default
        }

        [Fact]
        public void Switching_user_applies_that_users_settings()
        {
            ServiceFor("raccoon").SetMode(BadInstruction, ControlMode.Off);
            var session = new SessionState(_store);
            var service = ServiceFor("chris", session);

            Assert.True(service.Check(ControlStage.ChatIn, "request", "BadInstruction").IsBlocked);

            session.SwitchUser("raccoon");
            Assert.False(service.Check(ControlStage.ChatIn, "request", "BadInstruction").IsFlagged);
        }

        [Fact]
        public void SetMode_ignores_unknown_controls_and_unchanged_modes()
        {
            var service = ServiceFor("chris");

            Assert.False(service.SetMode("no-such-control", ControlMode.Off));
            Assert.False(service.SetMode(BadInstruction, ControlMode.Block)); // already the default
            Assert.True(service.SetMode(BadInstruction, ControlMode.Advise));
        }

        [Fact]
        public void Violations_round_trip_through_json_and_bad_json_is_ignored()
        {
            var violation = new ControlViolation(ScriptTag, "Contains \"<script>\"", ControlStage.ChatOut, ControlMode.Advise, "summary", "Found it.");

            Assert.Equal(new[] { violation }, ControlService.FromJson(ControlService.ToJson(new[] { violation })));
            Assert.Empty(ControlService.FromJson("not json"));
            Assert.Empty(ControlService.FromJson(null));
        }

        // --- Chat transcript ---

        private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        private int _second;

        private LogEntry Entry(LogLevel level, string message, Dictionary<string, object>? data = null, string? toolCallId = null) =>
            new() { Timestamp = T0.AddSeconds(_second++), Level = level, Message = message, Data = data, ToolCallId = toolCallId, UserName = "chris" };

        private LogEntry ControlEntry(string controlId, ControlMode mode, string target, string? toolCallId = null)
        {
            var control = ControlCatalog.Find(controlId);
            var violation = new ControlViolation(controlId, control?.Name ?? controlId, control?.Stage ?? ControlStage.ToolRule, mode, target, "Found it.");
            return Entry(mode == ControlMode.Block ? LogLevel.Block : LogLevel.Warning, $"{mode}: …",
                new() { ["controls"] = ControlService.ToJson(new[] { violation }), ["target"] = target }, toolCallId);
        }

        [Fact]
        public void Blocked_request_is_marked_and_has_no_reply()
        {
            var logs = new[]
            {
                Entry(LogLevel.User, "Request: BadInstruction", new() { ["prompt"] = "BadInstruction" }),
                ControlEntry(BadInstruction, ControlMode.Block, "request"),
            };

            var turn = Assert.Single(ChatTranscript.Build(logs, Array.Empty<ToolCall>()));

            Assert.True(turn.PromptBlocked);
            Assert.Single(turn.PromptControls);
            Assert.Null(turn.AgentMessage);
        }

        [Fact]
        public void Advised_request_is_flagged_but_not_blocked()
        {
            var logs = new[]
            {
                Entry(LogLevel.User, "Request: BadInstruction", new() { ["prompt"] = "BadInstruction" }),
                ControlEntry(BadInstruction, ControlMode.Advise, "request"),
                Entry(LogLevel.Info, "decision", new() { ["agentMessage"] = "No tool fits." }),
            };

            var turn = Assert.Single(ChatTranscript.Build(logs, Array.Empty<ToolCall>()));

            Assert.False(turn.PromptBlocked);
            Assert.Single(turn.PromptControls);
            Assert.Equal("No tool fits.", turn.AgentMessage);
        }

        [Fact]
        public void Blocked_reply_and_summary_are_withheld_and_blocked_tool_call_is_marked()
        {
            var call = new ToolCall { Id = "call-1", Tool = "scanner", Action = "simple-scanner", Status = ToolCallStatus.Completed };
            var logs = new[]
            {
                Entry(LogLevel.User, "Request: scan", new() { ["prompt"] = "scan example.com/<script>" }),
                Entry(LogLevel.Info, "decision", new() { ["agentMessage"] = "I'll scan https://example.com/<script>" }),
                ControlEntry(ScriptTag, ControlMode.Block, "reply"),
                Entry(LogLevel.Info, "Tool call initiated", toolCallId: "call-1"),
                Entry(LogLevel.Summary, "withheld", new() { ["summary"] = "…<script>…" }, "call-1"),
                ControlEntry(ScriptTag, ControlMode.Advise, "summary", "call-1"),
            };

            var turn = Assert.Single(ChatTranscript.Build(logs, new[] { call }));

            Assert.True(turn.ReplyWithheld);
            Assert.False(turn.ToolBlocked);
            Assert.Single(turn.SummaryControls);
            Assert.False(turn.SummaryWithheld); // Advise only flags
        }

        [Fact]
        public void Blocked_tool_call_is_marked_on_the_reply()
        {
            var logs = new[]
            {
                Entry(LogLevel.User, "Request: attack", new() { ["prompt"] = "attack example.com" }),
                Entry(LogLevel.Info, "decision", new() { ["agentMessage"] = "Attacking once approved." }),
                ControlEntry("tool-rule-example", ControlMode.Block, "tool call"),
            };

            var turn = Assert.Single(ChatTranscript.Build(logs, Array.Empty<ToolCall>()));

            Assert.True(turn.ToolBlocked);
            Assert.False(turn.ReplyWithheld);
            Assert.Null(turn.ToolCall);
        }

        [Fact]
        public void Blocked_session_summary_is_withheld()
        {
            var logs = new[]
            {
                Entry(LogLevel.Summary, "Session summary", new() { ["sessionSummary"] = "…<script>…" }),
                ControlEntry(ScriptTag, ControlMode.Block, "session summary"),
            };

            var turn = Assert.Single(ChatTranscript.Build(logs, Array.Empty<ToolCall>()));

            Assert.Equal(ChatTurnKind.SessionSummary, turn.Kind);
            Assert.True(turn.ReplyWithheld);
        }

        [Fact]
        public void Store_keeps_the_latest_setting_per_user_and_control()
        {
            _store.SaveControlSetting("chris", ScriptTag, ControlMode.Advise, T0);
            _store.SaveControlSetting("chris", ScriptTag, ControlMode.Off, T0.AddMinutes(1));

            var settings = _store.LoadControlSettings("chris");
            Assert.Equal(ControlMode.Off, Assert.Single(settings).Value);
            Assert.Empty(_store.LoadControlSettings("blob"));
        }
    }
}
