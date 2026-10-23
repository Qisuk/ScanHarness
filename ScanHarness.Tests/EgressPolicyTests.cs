using System.Net;
using System.Net.Sockets;
using System.Text;
using ScanHarness.Services;
using ScanHarness.Services.Egress;
using Xunit;

namespace ScanHarness.Tests
{
    /// <summary>The egress policy: hosts, rules, tokens and the decision the proxy acts on.</summary>
    public class EgressPolicyTests
    {
        private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

        private sealed class FixedTime(DateTime now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(now);
        }

        // A resolver with fixed answers; any other name fails like an unknown host.
        private static Func<string, CancellationToken, Task<IPAddress[]>> Resolver(Dictionary<string, string[]> answers) =>
            (host, _) => answers.TryGetValue(host, out var addresses)
                ? Task.FromResult(addresses.Select(IPAddress.Parse).ToArray())
                : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));

        private static readonly Dictionary<string, string[]> PublicAnswers = new()
        {
            ["shop.acme.test"] = new[] { "93.184.215.14" },
            ["acme.test"] = new[] { "93.184.215.14" },
            ["evil.example"] = new[] { "93.184.215.99" },
            ["api.open-meteo.com"] = new[] { "93.184.215.50" },
            ["rebind.acme.test"] = new[] { "169.254.169.254" },
            ["internal.acme.test"] = new[] { "10.1.2.3" },
            ["mixed.acme.test"] = new[] { "93.184.215.14", "192.168.1.5" },
            ["v6.acme.test"] = new[] { "2606:2800:21f:cb07:6820:80da:af6b:8b2c", "93.184.215.15" },
            ["pastebin.com"] = new[] { "93.184.215.77" },
            ["lab.acme.test"] = new[] { "10.20.0.5" },
        };

        private readonly EgressTokens _tokens = new();

        private EgressPolicy Policy(EgressPolicyDefinition? definition = null, DateTime? now = null) =>
            new(() => definition ?? EgressPolicyDefinition.Empty, _tokens, Resolver(PublicAnswers), new FixedTime(now ?? Now));

        private static EgressGrant ScannerGrant(DateTime? expires = null) => new(
            "simple_scanner", expires ?? Now.AddMinutes(5), ToolCallId: "call-1", SessionId: "session-1", UserName: "chris",
            EngagementId: "eng-1", EngagementLabel: "Acme Ltd v1", EngagementDomains: new[] { "acme.test" },
            WindowStart: new DateOnly(2026, 10, 1), WindowEnd: new DateOnly(2026, 12, 31));

        private string Basic(EgressGrant grant) =>
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("egress:" + _tokens.Mint(grant)));

        private Task<EgressDecision> Decide(EgressPolicy policy, string target, EgressGrant? grant, string method = "CONNECT") =>
            policy.DecideAsync(new EgressRequest(method, target, grant == null ? null : Basic(grant)));

        // --- Hosts ---

        [Theory]
        [InlineData("Example.COM.", "example.com")]
        [InlineData("  shop.acme.test ", "shop.acme.test")]
        [InlineData("bücher.example", "xn--bcher-kva.example")]
        [InlineData("exаmple.com", "xn--exmple-4nf.com")] // Cyrillic "а": a different host from example.com
        [InlineData("10.0.0.1", "10.0.0.1")]
        [InlineData("[::1]", "::1")]
        [InlineData("::ffff:10.0.0.1", "10.0.0.1")]
        public void Hosts_are_normalised(string raw, string expected)
        {
            Assert.Equal(expected, EgressHost.Normalise(raw));
        }

        [Theory]
        [InlineData("")]
        [InlineData("bad host")]
        [InlineData("127.1")]          // read as an address by resolvers
        [InlineData("2130706433")]     // 127.0.0.1 as a number
        [InlineData("foo.0x7f")]
        [InlineData("under_score.example!")]
        public void Invalid_or_disguised_hosts_are_refused(string raw)
        {
            Assert.Null(EgressHost.Normalise(raw));
        }

        [Theory]
        [InlineData("shop.acme.test:443", null, "shop.acme.test", 443)]
        [InlineData("shop.acme.test", 80, "shop.acme.test", 80)]
        [InlineData("SHOP.acme.test:8080", 80, "shop.acme.test", 8080)]
        [InlineData("[2001:db8::1]:443", null, "2001:db8::1", 443)]
        public void Targets_are_parsed(string authority, int? defaultPort, string host, int port)
        {
            var target = EgressHost.ParseTarget(authority, defaultPort)!;

            Assert.Equal(host, target.Host);
            Assert.Equal(port, target.Port);
        }

        [Theory]
        [InlineData("shop.acme.test", null)]   // CONNECT must name a port
        [InlineData("shop.acme.test:0", null)]
        [InlineData("shop.acme.test:99999", null)]
        [InlineData("shop.acme.test:https", null)]
        [InlineData("[::1", null)]
        public void Bad_targets_are_refused(string authority, int? defaultPort)
        {
            Assert.Null(EgressHost.ParseTarget(authority, defaultPort));
        }

        [Theory]
        [InlineData("10.0.0.1", true)]
        [InlineData("172.16.5.4", true)]
        [InlineData("192.168.1.1", true)]
        [InlineData("127.0.0.1", true)]
        [InlineData("169.254.169.254", true)]
        [InlineData("100.64.0.1", true)]
        [InlineData("0.0.0.0", true)]
        [InlineData("224.0.0.1", true)]
        [InlineData("::1", true)]
        [InlineData("fd00::1", true)]
        [InlineData("fe80::1", true)]
        [InlineData("::ffff:127.0.0.1", true)]
        [InlineData("93.184.215.14", false)]
        [InlineData("2606:2800:21f:cb07:6820:80da:af6b:8b2c", false)]
        public void Non_public_addresses_are_recognised(string address, bool nonPublic)
        {
            Assert.Equal(nonPublic, EgressHost.IsNonPublic(IPAddress.Parse(address)));
        }

        // --- egress.json ---

        [Fact]
        public void Shipped_file_loads()
        {
            var policy = EgressCatalog.Load(Path.Combine(AppContext.BaseDirectory, EgressCatalog.DefaultFileName));

            Assert.Equal(new[] { 80, 443 }, policy.AllowedPorts);
            Assert.True(policy.BlockNonPublicAddresses);
            Assert.Equal(new[] { "deny-cloud-metadata", "deny-paste-and-tunnels" }, policy.Rules.Select(r => r.Id));
            Assert.All(policy.Rules, r => Assert.Equal(EgressAction.Deny, r.Action));
        }

        [Fact]
        public void Every_problem_in_the_file_is_reported_together()
        {
            var ex = Assert.Throws<CatalogException>(() => EgressCatalog.Parse("""
                { "allowedPorts": [443, 70000],
                  "rules": [
                    { "id": "a", "action": "Allow", "match": { "type": "regex", "value": "example\\.com" } },
                    { "id": "a", "action": "Allow", "match": { "type": "regex", "value": "^cdn.acme\\.test$" } },
                    { "id": "c", "action": "Allow", "match": { "type": "regex", "value": "^.*$" } },
                    { "id": "d", "action": "Allow", "match": { "type": "regex", "value": "^(unclosed$" } },
                    { "id": "e", "action": "Allow", "match": { "type": "domain", "value": "*.acme.test" } },
                    { "id": "f", "action": "Allow", "match": { "type": "domain", "value": "test" } },
                    { "id": "g", "action": "Deny", "match": { "type": "cidr", "value": "10.0.0.0/33" } },
                    { "id": "h", "match": { "type": "exact", "value": "10.0.0.1" } },
                    { "action": "Allow", "appliesTo": [""], "match": { "type": "exact" } }
                  ] }
                """));

            Assert.Contains("70000 isn't a valid port", ex.Message);
            Assert.Contains("must start with ^ and end with $", ex.Message);
            Assert.Contains("the id is used more than once", ex.Message);
            Assert.Contains("unescaped \".\"", ex.Message);
            Assert.Contains("matches any host", ex.Message);
            Assert.Contains("not a valid regular expression", ex.Message);
            Assert.Contains("wildcards aren't allowed", ex.Message);
            Assert.Contains("whole top-level domain", ex.Message);
            Assert.Contains("isn't a CIDR range", ex.Message);
            Assert.Contains("\"action\" must be Allow or Deny", ex.Message);
            Assert.Contains("isn't a valid host name", ex.Message);
            Assert.Contains("\"id\" is required", ex.Message);
            Assert.Contains("\"match.value\" is required", ex.Message);
            Assert.Contains("can't contain empty names", ex.Message);
        }

        [Fact]
        public void Anchored_regex_matches_the_whole_host_only()
        {
            var rule = new EgressRule("cdn", EgressAction.Allow, EgressMatchType.Regex, @"^([a-z0-9-]+\.)*cdn\.acme\.test$");

            Assert.True(rule.MatchesHost("cdn.acme.test"));
            Assert.True(rule.MatchesHost("eu.cdn.acme.test"));
            Assert.False(rule.MatchesHost("cdn.acme.test.evil.example"));
            Assert.False(rule.MatchesHost("evilcdn.acme.test"));
        }

        [Fact]
        public void Regex_timeouts_fail_closed_for_both_actions()
        {
            // Catastrophic backtracking on a long host: the rule gives up rather than hanging the proxy.
            const string pattern = @"^(a+)+$";
            var host = new string('a', 40) + "!";

            Assert.False(new EgressRule("allow", EgressAction.Allow, EgressMatchType.Regex, pattern).MatchesHost(host));
            Assert.True(new EgressRule("deny", EgressAction.Deny, EgressMatchType.Regex, pattern).MatchesHost(host));
        }

        [Fact]
        public void Tool_egress_blocks_are_read_from_tools_json()
        {
            var weather = ToolCatalog.FindByFunctionName("weather_forecast")!;
            var scanner = ToolCatalog.FindByFunctionName("simple_scanner")!;

            Assert.Equal(new[] { "api.open-meteo.com" }, weather.EffectiveEgress.Domains);
            Assert.Equal(new[] { 443 }, weather.EffectiveEgress.Ports);
            Assert.False(weather.EffectiveEgress.EngagementScope);
            Assert.Empty(scanner.EffectiveEgress.Domains);
            Assert.True(scanner.EffectiveEgress.EngagementScope); // from requiresScope
        }

        [Fact]
        public void Bad_tool_egress_blocks_stop_the_catalog_loading()
        {
            var ex = Assert.Throws<CatalogException>(() => ToolCatalog.Parse("""
                { "tools": [ { "functionName": "t", "tool": "t", "action": "a", "description": "d", "confirmation": "c",
                  "http": { "method": "GET", "path": "x" }, "classification": "Internal", "outputLabel": "Public", "outputTrust": "Untrusted",
                  "egress": { "domains": [ "*.example.com", "10.0.0.1" ], "ports": [ 0 ] } } ] }
                """));

            Assert.Contains("egress domain wildcards aren't allowed", ex.Message);
            Assert.Contains("egress domain \"10.0.0.1\" isn't a valid host name", ex.Message);
            Assert.Contains("egress port 0 isn't a valid port", ex.Message);
        }

        // --- Tokens ---

        [Fact]
        public void Tokens_round_trip()
        {
            var grant = ScannerGrant();

            var read = _tokens.Read(_tokens.Mint(grant), Now, out var problem);

            Assert.Null(problem);
            Assert.Equal(grant.Caller, read!.Caller);
            Assert.Equal(grant.EngagementDomains, read.EngagementDomains);
            Assert.Equal(grant.WindowEnd, read.WindowEnd);
        }

        [Fact]
        public void Tampered_expired_and_foreign_tokens_are_refused()
        {
            var token = _tokens.Mint(ScannerGrant());
            var parts = token.Split('.');
            var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"caller":"simple_scanner","expiresAt":"2030-01-01T00:00:00Z","engagementDomains":["evil.example"]}"""))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." + parts[1];

            Assert.Null(_tokens.Read(forged, Now, out var tampered));
            Assert.Contains("signature", tampered);
            Assert.Null(_tokens.Read(token, Now.AddMinutes(6), out var expired));
            Assert.Contains("expired", expired);
            Assert.Null(new EgressTokens().Read(token, Now, out var foreign));
            Assert.Contains("signature", foreign);
            Assert.Null(_tokens.Read("not-a-token", Now, out var malformed));
            Assert.Contains("malformed", malformed);
        }

        [Theory]
        [InlineData("Basic ZWdyZXNzOmFiYy5kZWY=", "abc.def")] // egress:abc.def
        [InlineData("Bearer abc.def", "abc.def")]
        [InlineData("Basic ZWdyZXNz", null)]                  // no password
        [InlineData("Digest x", null)]
        [InlineData("", null)]
        public void Tokens_are_read_from_proxy_authorization(string header, string? token)
        {
            Assert.Equal(token, EgressTokens.FromProxyAuthorization(header));
        }

        // --- Decisions ---

        [Fact]
        public async Task Engagement_domains_are_allowed_inside_the_window_and_connect_to_the_checked_address()
        {
            var decision = await Decide(Policy(), "shop.acme.test:443", ScannerGrant());

            Assert.True(decision.Allowed);
            Assert.Equal(200, decision.Status);
            Assert.Equal(EgressDecision.Rules.Engagement, decision.Rule);
            Assert.Equal("93.184.215.14:443", decision.Upstream);
            Assert.Contains("Acme Ltd v1", decision.Reason);
            Assert.Equal("call-1", decision.Grant!.ToolCallId);
        }

        [Fact]
        public async Task Hosts_outside_the_engagement_are_refused()
        {
            var decision = await Decide(Policy(), "evil.example:443", ScannerGrant());

            Assert.False(decision.Allowed);
            Assert.Equal(403, decision.Status);
            Assert.Equal(EgressDecision.Rules.NoRule, decision.Rule);
            Assert.Null(decision.Upstream);
        }

        [Fact]
        public async Task Engagement_domains_are_refused_outside_the_window()
        {
            var decision = await Decide(Policy(now: new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc)), "shop.acme.test:443",
                ScannerGrant(expires: new DateTime(2027, 1, 2, 0, 5, 0, DateTimeKind.Utc)));

            Assert.False(decision.Allowed);
            Assert.Equal(EgressDecision.Rules.Window, decision.Rule);
            Assert.Contains("outside its testing window", decision.Reason);
        }

        [Fact]
        public async Task Plain_http_defaults_to_port_80()
        {
            var decision = await Decide(Policy(), "acme.test", ScannerGrant(), method: "GET");

            Assert.True(decision.Allowed);
            Assert.Equal("93.184.215.14:80", decision.Upstream);
        }

        [Fact]
        public async Task Ports_outside_the_list_are_refused()
        {
            var decision = await Decide(Policy(), "shop.acme.test:25", ScannerGrant());

            Assert.False(decision.Allowed);
            Assert.Equal(EgressDecision.Rules.Port, decision.Rule);
        }

        [Fact]
        public async Task Tools_reach_their_own_domains_on_their_own_ports()
        {
            var grant = new EgressGrant("weather_forecast", Now.AddMinutes(5), Domains: new[] { "api.open-meteo.com" }, Ports: new[] { 443 });

            Assert.True((await Decide(Policy(), "api.open-meteo.com:443", grant)).Allowed);
            Assert.Equal(EgressDecision.Rules.Port, (await Decide(Policy(), "api.open-meteo.com:80", grant)).Rule);
            Assert.False((await Decide(Policy(), "shop.acme.test:443", grant)).Allowed);
        }

        [Fact]
        public async Task Deny_rules_beat_the_engagement()
        {
            var definition = EgressCatalog.Parse("""
                { "rules": [ { "id": "no-shop", "action": "Deny", "match": { "type": "exact", "value": "shop.acme.test" } } ] }
                """);

            var decision = await Decide(Policy(definition), "shop.acme.test:443", ScannerGrant());

            Assert.False(decision.Allowed);
            Assert.Equal("no-shop", decision.Rule);
        }

        [Fact]
        public async Task The_shipped_deny_list_refuses_paste_sites_even_when_in_scope()
        {
            var shipped = EgressCatalog.Load(Path.Combine(AppContext.BaseDirectory, EgressCatalog.DefaultFileName));
            var grant = ScannerGrant() with { EngagementDomains = new[] { "pastebin.com" } };

            var decision = await Decide(Policy(shipped), "pastebin.com:443", grant);

            Assert.Equal("deny-paste-and-tunnels", decision.Rule);
        }

        [Fact]
        public async Task Allow_rules_apply_only_to_their_callers()
        {
            var definition = EgressCatalog.Parse("""
                { "rules": [ { "id": "llm", "action": "Allow", "appliesTo": ["harness:llm"], "match": { "type": "exact", "value": "evil.example" } } ] }
                """);
            var harness = new EgressGrant(EgressCallers.HarnessLlm, Now.AddMinutes(5));

            Assert.Equal("llm", (await Decide(Policy(definition), "evil.example:443", harness)).Rule);
            Assert.False((await Decide(Policy(definition), "evil.example:443", ScannerGrant())).Allowed);
        }

        [Fact]
        public async Task Names_that_resolve_to_non_public_addresses_are_refused()
        {
            var policy = Policy();

            Assert.Equal(EgressDecision.Rules.NonPublic, (await Decide(policy, "rebind.acme.test:443", ScannerGrant())).Rule);
            Assert.Equal(EgressDecision.Rules.NonPublic, (await Decide(policy, "internal.acme.test:443", ScannerGrant())).Rule);
            // One private address among public ones is enough to refuse.
            Assert.Equal(EgressDecision.Rules.NonPublic, (await Decide(policy, "mixed.acme.test:443", ScannerGrant())).Rule);
        }

        [Fact]
        public async Task A_cidr_allow_rule_lets_a_name_reach_a_private_lab_but_metadata_stays_denied()
        {
            var definition = EgressCatalog.Parse("""
                { "rules": [
                  { "id": "lab", "action": "Allow", "match": { "type": "cidr", "value": "10.20.0.0/16" } },
                  { "id": "metadata", "action": "Deny", "match": { "type": "cidr", "value": "169.254.0.0/16" } } ] }
                """);
            var policy = Policy(definition);

            Assert.Equal("10.20.0.5:443", (await Decide(policy, "lab.acme.test:443", ScannerGrant())).Upstream);
            Assert.Equal("metadata", (await Decide(policy, "rebind.acme.test:443", ScannerGrant())).Rule);
            Assert.Equal(EgressDecision.Rules.NonPublic, (await Decide(policy, "internal.acme.test:443", ScannerGrant())).Rule);
        }

        [Fact]
        public async Task Ip_literals_need_a_cidr_rule()
        {
            var lab = EgressCatalog.Parse("""{ "rules": [ { "id": "lab", "action": "Allow", "match": { "type": "cidr", "value": "10.20.0.0/16" } } ] }""");

            Assert.False((await Decide(Policy(), "93.184.215.14:443", ScannerGrant())).Allowed);
            Assert.True((await Decide(Policy(lab), "10.20.0.9:443", ScannerGrant())).Allowed);
            Assert.False((await Decide(Policy(lab), "10.21.0.9:443", ScannerGrant())).Allowed);
        }

        [Fact]
        public async Task Ipv4_is_preferred_when_a_name_has_both()
        {
            var decision = await Decide(Policy(), "v6.acme.test:443", ScannerGrant());

            Assert.Equal("93.184.215.15:443", decision.Upstream);
        }

        [Fact]
        public async Task Unresolvable_hosts_are_refused()
        {
            var decision = await Decide(Policy(), "missing.acme.test:443", ScannerGrant());

            Assert.Equal(EgressDecision.Rules.Resolve, decision.Rule);
        }

        [Fact]
        public async Task No_token_gets_a_challenge_unless_a_rule_lets_everyone_through()
        {
            var open = EgressCatalog.Parse("""{ "rules": [ { "id": "public", "action": "Allow", "match": { "type": "domain", "value": "acme.test" } } ] }""");

            var challenged = await Decide(Policy(), "shop.acme.test:443", grant: null);
            var allowed = await Decide(Policy(open), "shop.acme.test:443", grant: null);

            Assert.Equal(407, challenged.Status);
            Assert.Equal(EgressCallers.Anonymous, challenged.Caller);
            Assert.True(allowed.Allowed);
            Assert.Equal("public", allowed.Rule);
        }

        [Fact]
        public async Task Bad_tokens_are_refused_outright()
        {
            var decision = await Policy().DecideAsync(new EgressRequest("CONNECT", "shop.acme.test:443", "Bearer forged.token"));

            Assert.Equal(403, decision.Status);
            Assert.Equal(EgressDecision.Rules.Token, decision.Rule);
        }

        [Fact]
        public async Task Unparseable_targets_are_refused()
        {
            var decision = await Decide(Policy(), "shop.acme.test", ScannerGrant()); // CONNECT without a port

            Assert.Equal(EgressDecision.Rules.Target, decision.Rule);
        }
    }
}
