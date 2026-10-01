using ScanHarness.Services;
using ScanHarness.Services.Agents;
using ScanHarness.Services.Boundaries;
using Xunit;

namespace ScanHarness.Tests
{
    /// <summary>Tools read from tools.json and agents from agents.json.</summary>
    public class ToolAndAgentCatalogTests
    {
        // --- The shipped files ---

        [Fact]
        public void Shipped_tools_keep_their_approval_scope_and_trust_settings()
        {
            var tools = ToolCatalog.Load(Path.Combine(AppContext.BaseDirectory, ToolCatalog.DefaultFileName));

            Assert.Equal(new[] { "simple_scanner", "attack", "weather_forecast" }, tools.Select(t => t.FunctionName));
            Assert.Equal(new[] { false, true, false }, tools.Select(t => t.RequiresApproval));
            Assert.Equal(new[] { true, true, false }, tools.Select(t => t.RequiresScope));
            Assert.Equal(new[] { Classification.Internal, Classification.Internal, Classification.External }, tools.Select(t => t.Classification));
            Assert.Equal(new[] { Sensitivity.CustomerConfidential, Sensitivity.CustomerConfidential, Sensitivity.Public }, tools.Select(t => t.OutputLabel));
            Assert.All(tools, t => Assert.Equal(Trust.Untrusted, t.OutputTrust));
        }

        [Fact]
        public void Shipped_agents_offer_the_analyst_and_estimator_but_keep_the_scoping_agent_on_its_own_page()
        {
            var agents = AgentCatalog.Load(Path.Combine(AppContext.BaseDirectory, AgentCatalog.DefaultFileName));

            Assert.Equal(new[] { "findings-analyst", "cost-estimator", "scoping-agent" }, agents.Select(a => a.Id));
            Assert.Equal(new[] { AgentKind.FindingsAnalyst, AgentKind.CostEstimator, AgentKind.Scoping }, agents.Select(a => a.Kind));
            Assert.All(agents, a => Assert.Equal(AgentDefinition.RuleBasedBrain, a.Brain));
            Assert.Equal(new[] { true, true, false }, agents.Select(a => a.OfferedToOrchestrator));
            Assert.Equal(new[] { "analyse_findings", "estimate_cost" }, AgentCatalog.All.Select(a => a.FunctionName));
        }

        [Fact]
        public void Agents_appear_in_the_tool_catalog_after_the_tool_apis()
        {
            Assert.Equal(new[] { "simple_scanner", "attack", "weather_forecast", "analyse_findings", "estimate_cost" },
                ToolCatalog.All.Select(t => t.FunctionName));

            var analyst = ToolCatalog.FindByFunctionName("analyse_findings")!;
            Assert.True(analyst.IsAgent);
            Assert.Equal("agent:findings-analyst", analyst.PolicyId);
            Assert.Empty(analyst.Parameters);
            Assert.Same(analyst, ToolCatalog.Find("agent", "findings-analyst"));
            Assert.Null(ToolCatalog.FindByTool("agent"));
        }

        // --- HTTP bindings and confirmations ---

        [Fact]
        public void Http_path_fills_parameters_url_encoded()
        {
            var binding = new ToolHttpBinding("GET", "api/tools/simple-scanner?url={url}");

            Assert.Equal("api/tools/simple-scanner?url=https%3A%2F%2Fexample.com%2Fa%20b",
                binding.Resolve(new Dictionary<string, string> { ["url"] = "https://example.com/a b" }));
        }

        [Fact]
        public void Http_path_drops_query_pairs_for_missing_optional_parameters()
        {
            var binding = new ToolHttpBinding("GET", "WeatherForecast?location={location}&days=5");

            Assert.Equal("WeatherForecast?days=5", binding.Resolve(new Dictionary<string, string>()));
            Assert.Equal("WeatherForecast?location=Paris&days=5", binding.Resolve(new Dictionary<string, string> { ["location"] = "Paris" }));
        }

        [Fact]
        public void Confirmation_uses_the_short_form_when_an_optional_parameter_is_missing()
        {
            var weather = ToolCatalog.FindByTool("weather")!;

            Assert.Equal("I'll get the weather forecast for Oslo.", weather.FormatConfirmation(new Dictionary<string, string> { ["location"] = "Oslo" }));
            Assert.Equal("I'll get the weather forecast.", weather.FormatConfirmation(new Dictionary<string, string>()));
        }

        // --- Validation ---

        private const string ValidTool = """
            { "functionName": "port_scan", "tool": "ports", "action": "scan", "description": "d", "confirmation": "Scanning {url}.",
              "parameters": [ { "name": "url", "type": "url", "required": true } ], "requiresScope": true,
              "http": { "method": "GET", "path": "api/ports?url={url}" },
              "classification": "Internal", "outputLabel": "CustomerConfidential", "outputTrust": "Untrusted" }
            """;

        [Fact]
        public void A_new_tool_needs_only_a_json_entry()
        {
            var tool = Assert.Single(ToolCatalog.Parse($$"""{ "tools": [ {{ValidTool}} ] }"""));

            Assert.Equal("port_scan", tool.FunctionName);
            Assert.True(tool.RequiresScope);
            Assert.Equal("api/ports?url=x", tool.Http!.Resolve(new Dictionary<string, string> { ["url"] = "x" }));
        }

        [Fact]
        public void Every_problem_in_a_tools_file_is_reported_together()
        {
            var json = """
                { "tools": [
                  { "functionName": "bad name", "tool": "agent", "action": "a", "description": "d", "confirmation": "c {missing}",
                    "requiresScope": true, "http": { "method": "PUT", "path": "x" },
                    "classification": "Internal", "outputLabel": "Public", "outputTrust": "Trusted" },
                  { "functionName": "no_http", "tool": "t", "action": "a", "description": "d", "confirmation": "c",
                    "classification": "Internal", "outputLabel": "Public", "outputTrust": "Trusted" }
                ] }
                """;

            var error = Assert.Throws<CatalogException>(() => ToolCatalog.Parse(json, "test.json")).Message;

            Assert.Contains("\"functionName\" is required and may only contain letters, digits and underscores.", error);
            Assert.Contains("\"tool\" can't be \"agent\"; define agents in agents.json.", error);
            Assert.Contains("\"http.method\" must be GET or POST.", error);
            Assert.Contains("\"requiresScope\" needs a url parameter to check.", error);
            Assert.Contains("{missing} doesn't match a parameter.", error);
            Assert.Contains("tool \"no_http\": \"http.path\" is required.", error);
        }

        [Fact]
        public void Every_problem_in_an_agents_file_is_reported_together()
        {
            var json = """
                { "agents": [
                  { "id": "a", "functionName": "attack", "name": "A", "kind": "FindingsAnalyst", "description": "d", "confirmation": "c", "outputTrust": "Untrusted" },
                  { "id": "s", "functionName": "scope", "name": "S", "kind": "Scoping", "description": "d", "confirmation": "c", "outputTrust": "Untrusted",
                    "offeredToOrchestrator": true }
                ] }
                """;

            var error = Assert.Throws<CatalogException>(() => AgentCatalog.Parse(json, "test.json")).Message;

            Assert.Contains("agent \"s\": a Scoping agent talks to customers, so it can't be offered to the orchestrator.", error);
            Assert.Contains("agent \"a\": functionName \"attack\" is already used by a tool in tools.json.", error);
            Assert.Contains("not valid JSON for this file", Assert.Throws<CatalogException>(() => AgentCatalog.Parse("{ \"agents\": [ { \"kind\": \"Wizard\" } ] }")).Message);
        }

        [Fact]
        public void An_agent_without_a_brain_uses_the_rule_based_one_and_its_brain_sets_its_classification()
        {
            var agent = Assert.Single(AgentCatalog.Parse("""
                { "agents": [ { "id": "x", "functionName": "x_agent", "name": "X", "kind": "CostEstimator", "description": "d",
                                "confirmation": "c", "outputTrust": "Untrusted" } ] }
                """));
            var policies = new ComponentPolicyService(llm: Microsoft.Extensions.Options.Options.Create(new ScanHarness.Services.Llm.LlmOptions { BaseUrl = "https://api.example.net/v1" }));

            Assert.Equal(AgentDefinition.RuleBasedBrain, agent.Brain);
            Assert.Equal(Classification.Internal, policies.ForAgent(agent).Classification);

            policies.Update("agent:x", new PolicyOverride(Brain: "hosted-model"), "admin");
            var edited = policies.ForAgent(agent);
            Assert.Equal(Classification.External, edited.Classification);
            Assert.Equal(Sensitivity.Engagement, edited.Clearance);
        }
    }
}
