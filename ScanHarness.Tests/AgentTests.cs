using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Agents;
using ScanHarness.Services.Boundaries;
using ScanHarness.Services.Controls;
using ScanHarness.Services.Engagements;
using ScanHarness.Services.Llm;
using Xunit;

namespace ScanHarness.Tests
{
    /// <summary>The agents, their contracts, and running them as tools.</summary>
    public class AgentTests
    {
        private static ToolCall Attack(string url, string severity, params string[] findings) => new()
        {
            Tool = "attack", Action = "execute", Status = ToolCallStatus.Completed, Parameters = new() { ["url"] = url },
            Result = new JsonObject
            {
                ["status"] = "success", ["severity"] = severity, ["vulnerabilitiesFound"] = findings.Length,
                ["details"] = new JsonObject { ["tests_run"] = 150, ["findings"] = new JsonArray(findings.Select(f => (JsonNode?)f).ToArray()) },
            }.ToJsonString(),
        };

        private static ToolCall Scan(string url, params string[] pages) => new()
        {
            Tool = "scanner", Action = "simple-scanner", Status = ToolCallStatus.Completed, Parameters = new() { ["url"] = url },
            Result = new JsonObject
            {
                ["status"] = "success",
                ["links"] = new JsonArray(pages.Select(p => (JsonNode?)new JsonObject { ["url"] = p }).ToArray()),
            }.ToJsonString(),
        };

        private static readonly ToolCall[] Session =
        {
            Scan("https://shop.acme.test", "https://shop.acme.test/", "https://shop.acme.test/login"),
            Attack("https://shop.acme.test", "high", "SQL injection risk", "Authentication bypass"),
            Attack("https://blog.acme.test", "low", "Missing security headers"),
            new() { Tool = "attack", Status = ToolCallStatus.Failed, Error = "unreachable" },
        };

        // --- Contracts ---

        [Fact]
        public void FindingSet_is_built_from_completed_scans_and_attacks_only()
        {
            var findings = FindingSet.From(Session);

            Assert.Equal(3, findings.Results.Count);
            Assert.Equal(new[] { "none", "high", "low" }, findings.Results.Select(r => r.Severity));
            Assert.Equal(new[] { "SQL injection risk", "Authentication bypass" }, findings.Results[1].Findings);
        }

        [Fact]
        public void EngagementSummary_counts_the_work_without_naming_any_host()
        {
            var summary = EngagementSummary.From(FindingSet.From(Session));

            Assert.Equal(new EngagementSummary(HostCount: 2, ScansRun: 1, AttacksRun: 2, HighFindings: 2, MediumFindings: 0, LowFindings: 1, TestsRun: 300), summary);
            Assert.DoesNotContain("acme", System.Text.Json.JsonSerializer.Serialize(summary));
        }

        // --- Agents ---

        [Fact]
        public void Findings_analyst_puts_high_severity_first_with_specific_advice()
        {
            var result = FindingsAnalyst.Analyse(FindingSet.From(Session));

            Assert.Equal(new[] { "P1", "P1", "P3", "P3" }, result.Items.Select(i => i.Priority));
            Assert.Equal("Use parameterised queries throughout and validate input server-side.",
                result.Items.Single(i => i.Finding == "SQL injection risk").Action);
            Assert.Contains(result.Items, i => i.Finding == "Sensitive page exposed" && i.Target == "https://shop.acme.test/login");
            Assert.StartsWith("Reviewed 3 results and made 4 recommendations (2 P1, 2 P3). Most urgent:", result.Summary);
        }

        [Fact]
        public void Findings_analyst_says_so_when_there_is_nothing_to_analyse()
        {
            Assert.Equal("There are no completed scan or attack results in this session to analyse yet.",
                FindingsAnalyst.Analyse(FindingSet.From(Array.Empty<ToolCall>())).Summary);
        }

        [Fact]
        public void A_models_recommendations_for_findings_or_targets_not_in_the_input_are_dropped()
        {
            var input = FindingSet.From(Session);
            var answer = JsonNode.Parse("""
                { "recommendations": [
                  { "priority": "P1", "finding": "SQL injection risk", "target": "https://shop.acme.test", "action": "Parameterise." },
                  { "priority": "P1", "finding": "Invented finding", "target": "https://shop.acme.test", "action": "x" },
                  { "priority": "P1", "finding": "SQL injection risk", "target": "https://other.example", "action": "x" },
                  { "priority": "P0", "finding": "Authentication bypass", "target": "https://shop.acme.test", "action": "x" }
                ] }
                """)!.AsObject();

            var kept = Assert.Single(FindingsAnalyst.Validate(answer, input)!);
            Assert.Equal("Parameterise.", kept.Action);
        }

        [Fact]
        public void Cost_estimate_numbers_come_from_code()
        {
            var estimate = CostEstimator.Calculate(new EngagementSummary(2, 1, 2, 2, 1, 1, 300), 1000m, "GBP");

            // 1 planning + 2 × 0.5 hosts + 2 × 0.5 high + 0.25 medium + 0.1 low = 3.35, rounded up to 3.5.
            Assert.Equal(3.5m, estimate.Days);
            Assert.Equal(3500m, estimate.Total);
            Assert.Equal(5, estimate.Lines.Count);
            Assert.Equal("Estimated 3.5 days (GBP 3,500) for 2 hosts with 4 findings to retest (2 high, 1 medium, 1 low).", estimate.Narrative);
        }

        [Fact]
        public void Scoping_agent_extracts_the_scope_from_what_the_customer_said()
        {
            var proposal = ScopingAgent.Extract(new[]
            {
                new ScopingMessage(true, "Hi, we are Acme Ltd. Please test acme.test and https://shop.acme.test/."),
                new ScopingMessage(false, "What dates? Try example.org."),
                new ScopingMessage(true, "From 2026-10-01 to 14 Oct 2026. No testing at weekends. Only the web tier."),
            });

            Assert.Equal("Acme Ltd", proposal.Customer);
            Assert.Equal(new[] { "acme.test", "shop.acme.test" }, proposal.Domains);   // the agent's own words don't count
            Assert.Equal(new DateOnly(2026, 10, 1), proposal.WindowStart);
            Assert.Equal(new DateOnly(2026, 10, 14), proposal.WindowEnd);
            Assert.Equal(new[] { "No testing at weekends.", "Only the web tier." }, proposal.Rules);
            Assert.StartsWith("Thanks, I have what I need.", ScopingAgent.Reply(proposal));
        }

        [Fact]
        public void Scoping_agent_asks_for_whatever_is_missing()
        {
            Assert.StartsWith("Thanks. Which organisation", ScopingAgent.Reply(ScopeProposal.Empty));
            Assert.StartsWith("What dates", ScopingAgent.Reply(new ScopeProposal("Acme", new[] { "acme.test" }, null, null, Array.Empty<string>())));
        }

        // --- Running agents ---

        private sealed class Setup
        {
            public Setup(FakeHttpMessageHandler? llm = null)
            {
                Session = new SessionState();
                Log = new LogService(Session);
                Policies = new ComponentPolicyService(llm: Options.Create(new LlmOptions()));
                Broker = new BoundaryBroker(Policies, new ControlService(Session), Log);
                Runner = new AgentRunner(Broker, llm == null ? null : new OpenAiCompatibleClient(llm.CreateClient()));
            }

            public SessionState Session { get; }
            public LogService Log { get; }
            public ComponentPolicyService Policies { get; }
            public BoundaryBroker Broker { get; }
            public AgentRunner Runner { get; }
        }

        [Fact]
        public async Task Rule_based_analyst_runs_over_the_session_and_logs_the_handoff()
        {
            var setup = new Setup();

            var result = JsonNode.Parse(await setup.Runner.RunAsync("findings-analyst", Session, "call-1"))!;

            Assert.Equal("success", result["status"]!.GetValue<string>());
            Assert.Equal("rule-based", result["brain"]!.GetValue<string>());
            Assert.Equal("Allowed", result["input"]!["shared"]!.GetValue<string>());
            Assert.Equal(4, result["output"]!["items"]!.AsArray().Count);
            var handoff = Handoff.FromJson(ChatTranscript.ReadString(setup.Log.GetLogs().Single(), "handoff"))!;
            Assert.Equal("FindingSet", handoff.What);
            Assert.Equal("Findings analyst", handoff.ToName);
        }

        private static string ModelAnswer(string content) =>
            new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) }.ToJsonString();

        [Fact]
        public async Task An_external_llm_analyst_sees_placeholders_and_its_answer_is_mapped_back()
        {
            var llm = FakeHttpMessageHandler.Returning(ModelAnswer("""
                {"recommendations":[{"priority":"P1","finding":"SQL injection risk","target":"https://HOST_1","action":"Fix HOST_1 first."}]}
                """));
            var setup = new Setup(llm);
            setup.Policies.Update("agent:findings-analyst", new PolicyOverride(Brain: "hosted-model", Classification: Classification.External), "admin");

            var result = JsonNode.Parse(await setup.Runner.RunAsync("findings-analyst", Session, "call-1"))!;

            var sent = Assert.Single(llm.Requests).Body;
            Assert.DoesNotContain("acme", sent);
            Assert.Contains("HOST_1", sent);
            Assert.Equal("Pseudonymised", result["input"]!["shared"]!.GetValue<string>());
            var item = result["output"]!["items"]!.AsArray().Single()!;
            Assert.Equal("https://shop.acme.test", item["target"]!.GetValue<string>());
            Assert.Equal("Fix shop.acme.test first.", item["action"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_failing_llm_falls_back_to_the_rule_based_answer()
        {
            var setup = new Setup(FakeHttpMessageHandler.Throwing(new HttpRequestException("connection refused")));
            setup.Policies.Update("agent:cost-estimator", new PolicyOverride(Brain: "local-model"), "admin");

            var result = JsonNode.Parse(await setup.Runner.RunAsync("cost-estimator", Session, "call-1"))!;

            Assert.Equal("connection refused", result["fallbackReason"]!.GetValue<string>());
            Assert.StartsWith("Estimated", result["output"]!["narrative"]!.GetValue<string>());
        }

        [Fact]
        public async Task Agents_that_are_disabled_or_not_cleared_do_not_run()
        {
            var setup = new Setup();
            setup.Policies.Update("agent:findings-analyst", new PolicyOverride(Enabled: false), "admin");
            setup.Policies.Update("agent:cost-estimator", new PolicyOverride(Clearance: Sensitivity.Public), "admin");

            Assert.Contains("disabled", (await Assert.ThrowsAsync<AgentBlockedException>(() => setup.Runner.RunAsync("findings-analyst", Session, "c"))).Message);
            Assert.Contains("isn't cleared for engagement data", (await Assert.ThrowsAsync<AgentBlockedException>(() => setup.Runner.RunAsync("cost-estimator", Session, "c"))).Message);
            Assert.Contains("own page", (await Assert.ThrowsAsync<AgentBlockedException>(() => setup.Runner.RunAsync("scoping-agent", Session, "c"))).Message);
            Assert.Contains("over the limit", (await Assert.ThrowsAsync<AgentBlockedException>(() => new Setup().Runner.RunAsync("cost-estimator", Session, "c", depth: 3))).Message);
        }

        // --- Choosing and calling agents ---

        [Theory]
        [InlineData("please analyse the findings", "findings-analyst")]
        [InlineData("what would this engagement cost?", "cost-estimator")]
        [InlineData("estimate the cost of attacking example.com", "cost-estimator")]
        public async Task Keyword_brain_chooses_agents_by_their_keywords(string prompt, string agent)
        {
            var decision = await new KeywordAgentBrain().DecideAsync(prompt);

            Assert.True(decision.ToolFound);
            Assert.Equal(ToolDefinition.AgentTool, decision.Tool);
            Assert.Equal(agent, decision.Action);
            Assert.False(decision.RequiresApproval);
        }

        [Fact]
        public async Task An_agent_tool_call_runs_in_the_harness_without_calling_the_tool_api()
        {
            var api = FakeHttpMessageHandler.Returning("{}");
            var setup = new Setup();
            var tools = new ToolCallService(new HttpClient(api), setup.Log, setup.Session, agents: setup.Runner);

            var call = await tools.ExecuteToolAsync("agent", "cost-estimator", new Dictionary<string, string>());

            Assert.Equal(ToolCallStatus.Completed, call.Status);
            Assert.Empty(api.Requests);
            Assert.StartsWith("The user asked me to ask the cost estimator. The cost estimator (rule-based) reported: No hosts have been tested",
                ToolResultSummarizer.Summarize(call));
        }

        [Fact]
        public async Task Http_tools_are_called_as_tools_json_describes()
        {
            var api = FakeHttpMessageHandler.Returning("[]");
            var tools = new ToolCallService(new HttpClient(api), new LogService(), options: Options.Create(new ToolOptions { BaseUrl = "https://tools.test/" }));

            await tools.ExecuteToolAsync("weather", "forecast", new Dictionary<string, string>());
            await tools.ExecuteToolAsync("attack", "execute", new Dictionary<string, string> { ["url"] = "https://acme.test" });

            Assert.Equal("GET https://tools.test/WeatherForecast", $"{api.Requests[0].Request.Method} {api.Requests[0].Request.RequestUri}");
            Assert.Equal("POST https://tools.test/api/tools/attack?url=https%3A%2F%2Facme.test", $"{api.Requests[1].Request.Method} {api.Requests[1].Request.RequestUri}");
        }
    }
}
