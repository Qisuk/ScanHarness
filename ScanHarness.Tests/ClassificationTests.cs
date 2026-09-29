using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScanHarness.Services;
using ScanHarness.Services.Controls;
using ScanHarness.Services.Llm;
using Xunit;

namespace ScanHarness.Tests
{
    /// <summary>Internal/external classification of brains and tools, and controls scoped by it.</summary>
    public class ClassificationTests
    {
        // --- Brains ---

        [Theory]
        [InlineData("http://localhost:11434/v1", Classification.Internal)]
        [InlineData("http://127.0.0.1:1234/v1", Classification.Internal)]
        [InlineData("http://[::1]:8080/v1", Classification.Internal)]
        [InlineData("https://api.openai.com/v1", Classification.External)]
        [InlineData("http://gpu-box.lan:11434/v1", Classification.External)]
        [InlineData("not a url", Classification.External)]
        public void Llm_brains_are_internal_only_on_a_loopback_endpoint(string baseUrl, Classification expected)
        {
            Assert.Equal(expected, new LlmOptions { BaseUrl = baseUrl }.ClassificationFor("any-model"));
        }

        [Fact]
        public void Configured_classification_overrides_the_endpoint_and_models_override_both()
        {
            var options = new LlmOptions
            {
                BaseUrl = "https://llm.example.com/v1",
                Classification = Classification.Internal,
                ModelClassifications = { ["hosted-model"] = Classification.External },
            };

            Assert.Equal(Classification.Internal, options.ClassificationFor("private-model"));
            Assert.Equal(Classification.External, options.ClassificationFor("hosted-model"));
        }

        [Fact]
        public void Keyword_brain_is_internal()
        {
            Assert.Equal(Classification.Internal, new KeywordAgentBrain().Classification);
        }

        [Fact]
        public async Task Brain_picker_labels_and_brains_carry_their_classification()
        {
            var options = new LlmOptions { ModelClassifications = { ["cloud:1b"] = Classification.External } };
            var selector = new AgentBrainSelector(
                new KeywordAgentBrain(),
                new OpenAiCompatibleClient(FakeHttpMessageHandler.Returning(
                    "{\"data\":[{\"id\":\"local:3b\"},{\"id\":\"cloud:1b\"}]}").CreateClient()),
                Options.Create(options),
                NullLogger<AgentBrainSelector>.Instance);

            await selector.InitializeAsync();

            Assert.Equal(
                new[] { "Keyword brain (rule-based) · internal", "cloud:1b · external", "local:3b · internal" },
                selector.Options.Select(o => o.DisplayName));
            Assert.Equal(
                new Classification?[] { Classification.Internal, Classification.External, Classification.Internal },
                selector.Options.Select(o => o.Classification));

            selector.Select("cloud:1b");
            selector.SelectSummary("local:3b");
            Assert.Equal(Classification.External, selector.Current.Classification);
            Assert.Equal(Classification.Internal, selector.SummaryBrain.Classification);
        }

        // --- Tools ---

        [Fact]
        public void Tools_have_default_classifications_that_config_can_override()
        {
            // The only test that changes the overrides; it restores them so other tests see the defaults.
            try
            {
                Assert.Equal(Classification.Internal, ToolCatalog.ClassificationOf("scanner"));
                Assert.Equal(Classification.Internal, ToolCatalog.ClassificationOf("attack"));
                Assert.Equal(Classification.External, ToolCatalog.ClassificationOf("weather"));
                Assert.Null(ToolCatalog.ClassificationOf("no-such-tool"));

                ToolCatalog.UseClassifications(new Dictionary<string, Classification> { ["SIMPLE_SCANNER"] = Classification.External });

                Assert.Equal(Classification.External, ToolCatalog.ClassificationOf("scanner"));
                Assert.Equal(Classification.External, ToolCatalog.ClassificationOf("weather"));
            }
            finally
            {
                ToolCatalog.UseClassifications(new Dictionary<string, Classification>());
            }
        }

        // --- Control scopes ---

        private const string ScopedControls = """
            { "controls": [
              { "id": "in-any", "stage": "ChatIn", "name": "Any", "defaultMode": "Advise",
                "rule": { "type": "contains", "value": "secret" } },
              { "id": "in-external", "stage": "ChatIn", "name": "External only", "defaultMode": "Block", "appliesTo": "External",
                "rule": { "type": "contains", "value": "secret" } },
              { "id": "out-internal", "stage": "ChatOut", "name": "Internal only", "defaultMode": "Advise", "appliesTo": "internal",
                "rule": { "type": "contains", "value": "secret" } },
              { "id": "tool-external", "stage": "ToolRule", "name": "External tools", "defaultMode": "Block", "appliesTo": "External",
                "rule": { "type": "regex", "value": "url=" } }
            ] }
            """;

        private static ControlService ServiceWith(string json) => new(new SessionState(), controls: ControlCatalog.Parse(json));

        [Fact]
        public void AppliesTo_is_read_from_json_and_defaults_to_any()
        {
            var controls = ControlCatalog.Parse(ScopedControls);

            Assert.Equal(
                new[] { ControlScope.Any, ControlScope.External, ControlScope.Internal, ControlScope.External },
                controls.Select(c => c.AppliesTo));
            Assert.Equal(
                new[] { "all brains", "external brains", "internal brains", "external tools" },
                controls.Select(ControlCatalog.ScopeLabel));
        }

        [Fact]
        public void Shipped_controls_apply_to_everything()
        {
            var controls = ControlCatalog.Load(Path.Combine(AppContext.BaseDirectory, ControlCatalog.DefaultFileName));

            Assert.All(controls, c => Assert.Equal(ControlScope.Any, c.AppliesTo));
        }

        [Theory]
        [InlineData("\"Everywhere\"")]
        [InlineData("\"5\"")]
        public void An_unknown_appliesTo_is_reported(string value)
        {
            var json = ScopedControls.Replace("\"appliesTo\": \"internal\"", $"\"appliesTo\": {value}");
            Assert.NotEqual(ScopedControls, json);

            var ex = Assert.Throws<ControlCatalogException>(() => ControlCatalog.Parse(json));

            Assert.Contains("control \"out-internal\": \"appliesTo\" must be one of Any, Internal, External.", ex.Message);
        }

        [Theory]
        [InlineData(ControlScope.Any, Classification.Internal, true)]
        [InlineData(ControlScope.Any, Classification.External, true)]
        [InlineData(ControlScope.Internal, Classification.Internal, true)]
        [InlineData(ControlScope.Internal, Classification.External, false)]
        [InlineData(ControlScope.External, Classification.Internal, false)]
        [InlineData(ControlScope.External, Classification.External, true)]
        [InlineData(ControlScope.Internal, null, true)]   // unknown subject: fail closed
        [InlineData(ControlScope.External, null, true)]
        public void Scope_decides_which_subjects_a_control_applies_to(ControlScope scope, Classification? subject, bool applies)
        {
            var control = new ControlDefinition("id", ControlStage.ChatIn, "n", "", ControlMode.Block,
                new ControlRule(ControlRuleType.Contains, "x"), scope);

            Assert.Equal(applies, control.AppliesToSubject(subject));
        }

        [Fact]
        public void Chat_in_to_an_internal_brain_skips_external_only_controls()
        {
            var check = ServiceWith(ScopedControls).Check(ControlStage.ChatIn, ChatTranscript.Targets.Request, "the secret", Classification.Internal);

            var violation = Assert.Single(check.Violations);
            Assert.Equal("in-any", violation.ControlId);
            Assert.False(check.IsBlocked);
        }

        [Fact]
        public void Chat_in_to_an_external_brain_applies_external_controls_and_records_the_subject()
        {
            var check = ServiceWith(ScopedControls).Check(ControlStage.ChatIn, ChatTranscript.Targets.Request, "the secret", Classification.External);

            Assert.Equal(new[] { "in-any", "in-external" }, check.Violations.Select(v => v.ControlId));
            Assert.True(check.IsBlocked);
            Assert.All(check.Violations, v => Assert.Equal(Classification.External, v.Subject));
            Assert.Equal("request to an external brain", check.Violations[1].TargetDescription);
        }

        [Fact]
        public void Chat_out_is_scoped_by_the_brain_that_wrote_the_text()
        {
            var service = ServiceWith(ScopedControls);

            var fromInternal = service.Check(ControlStage.ChatOut, ChatTranscript.Targets.Summary, "a secret", Classification.Internal);
            var fromExternal = service.Check(ControlStage.ChatOut, ChatTranscript.Targets.Summary, "a secret", Classification.External);

            Assert.Equal("summary from an internal brain", Assert.Single(fromInternal.Violations).TargetDescription);
            Assert.False(fromExternal.IsFlagged);
        }

        [Fact]
        public void Tool_rule_is_scoped_by_the_tool_classification()
        {
            var service = ServiceWith(ScopedControls);
            const string call = "weather/forecast url=x";

            Assert.True(service.Check(ControlStage.ToolRule, ChatTranscript.Targets.ToolCall, call, ToolCatalog.ClassificationOf("weather")).IsBlocked);
            Assert.False(service.Check(ControlStage.ToolRule, ChatTranscript.Targets.ToolCall, call, ToolCatalog.ClassificationOf("scanner")).IsFlagged);
            Assert.Equal("tool call for an external tool",
                service.Check(ControlStage.ToolRule, ChatTranscript.Targets.ToolCall, call, Classification.External).Violations[0].TargetDescription);
        }

        [Fact]
        public void An_unknown_subject_is_checked_by_every_control()
        {
            var check = ServiceWith(ScopedControls).Check(ControlStage.ChatIn, ChatTranscript.Targets.Request, "the secret");

            Assert.Equal(2, check.Violations.Count);
            Assert.All(check.Violations, v => Assert.Equal(v.Target, v.TargetDescription));
        }

        [Fact]
        public void The_subject_survives_the_log_round_trip()
        {
            var check = ServiceWith(ScopedControls).Check(ControlStage.ChatIn, ChatTranscript.Targets.Request, "the secret", Classification.External);

            var restored = ControlService.FromJson(ControlService.ToJson(check.Violations));

            Assert.Equal(check.Violations, restored);
        }
    }
}
