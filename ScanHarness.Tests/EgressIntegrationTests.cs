using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Agents;
using ScanHarness.Services.Controls;
using ScanHarness.Services.Egress;
using ScanHarness.Services.Engagements;
using ScanHarness.Services.Llm;
using ScanHarness.Services.Persistence;
using Xunit;
using LogLevel = ScanHarness.Models.LogLevel;

namespace ScanHarness.Tests
{
    /// <summary>
    /// Egress control around the policy: tool calls carrying egress tokens, the proxy's policy endpoint, recorded
    /// connections in the store and the log, the access log reader and the settings. Runs against a real SQLite
    /// file in a temp folder.
    /// </summary>
    public sealed class EgressIntegrationTests : IDisposable
    {
        private const string PolicyKey = "test-policy-key-0123456789";

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "scanharness-tests", Guid.NewGuid().ToString("N"));
        private readonly SqliteActivityStore _store;
        private readonly EgressTokens _tokens = new();

        public EgressIntegrationTests()
        {
            _store = new SqliteActivityStore(Path.Combine(_directory, "activity.db"));
            _store.Initialize();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        private static IOptions<EgressOptions> Enforced() =>
            Options.Create(new EgressOptions { Mode = EgressMode.Enforced, PolicyKey = PolicyKey });

        // The session, with an approved engagement for acme.test selected (the window covers today).
        private (SessionState Session, EngagementService Engagements) SessionWithEngagement()
        {
            var session = new SessionState(_store);
            session.SwitchUser("admin");
            var engagements = new EngagementService(session, Options.Create(new EngagementOptions { VerifiedDomains = new() { "acme.test" } }), _store);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var engagement = engagements.Propose(new ScopeProposal("Acme Ltd", new[] { "acme.test" }, today.AddDays(-1), today.AddDays(30), new[] { "none" }));
            engagements.Decide(engagement.Id, approve: true);
            engagements.Select(engagement.Id);
            return (session, engagements);
        }

        // A tool API that behaves like the live scanner behind the proxy: for each host, the proxy asks the policy,
        // which records the decision. Here the "proxy" is the policy plus the authorizer's recording.
        private FakeHttpMessageHandler ToolApiThatConnectsTo(params string[] targets)
        {
            var policy = new EgressPolicy(() => EgressPolicyDefinition.Empty, _tokens,
                (host, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.215.14") }));
            return new FakeHttpMessageHandler(request =>
            {
                var token = request.Headers.TryGetValues(EgressTokens.ToolHeader, out var values) ? values.Single() : null;
                foreach (var target in targets)
                {
                    var egressRequest = new EgressRequest("CONNECT", target, token == null ? null : "Bearer " + token);
                    var decision = policy.DecideAsync(egressRequest).GetAwaiter().GetResult();
                    _store.SaveEgressEvent(EgressAuthorizer.ToEvent(egressRequest, decision));
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"success\",\"links\":[]}") };
            });
        }

        // --- Tool calls ---

        [Fact]
        public async Task Tool_calls_carry_an_egress_token_with_the_engagement_scope()
        {
            var (session, engagements) = SessionWithEngagement();
            var handler = FakeHttpMessageHandler.Returning("{}");
            var service = new ToolCallService(new HttpClient(handler), new LogService(session, _store), session, _store,
                egressTokens: _tokens, egressOptions: Enforced(), engagements: engagements);

            var call = await service.ExecuteToolAsync("scanner", "simple-scanner", new() { ["url"] = "https://shop.acme.test" });

            var token = Assert.Single(handler.Requests[0].Request.Headers.GetValues(EgressTokens.ToolHeader));
            var grant = _tokens.Read(token, DateTime.UtcNow, out _)!;
            Assert.Equal("simple_scanner", grant.Caller);
            Assert.Equal(call.Id, grant.ToolCallId);
            Assert.Equal(new[] { "acme.test" }, grant.EngagementDomains);
            Assert.Equal(engagements.Current!.Label, grant.EngagementLabel);
            Assert.True(grant.ExpiresAt > DateTime.UtcNow.AddMinutes(4));
        }

        [Fact]
        public async Task Tools_without_scope_get_only_their_own_list()
        {
            var (session, engagements) = SessionWithEngagement();
            var handler = FakeHttpMessageHandler.Returning("[]");
            var service = new ToolCallService(new HttpClient(handler), new LogService(session, _store), session, _store,
                egressTokens: _tokens, egressOptions: Enforced(), engagements: engagements);

            await service.ExecuteToolAsync("weather", "forecast", new());

            var grant = _tokens.Read(handler.Requests[0].Request.Headers.GetValues(EgressTokens.ToolHeader).Single(), DateTime.UtcNow, out _)!;
            Assert.Null(grant.EngagementDomains);
            Assert.Equal(new[] { "api.open-meteo.com" }, grant.Domains);
            Assert.Equal(new[] { 443 }, grant.Ports);
        }

        [Fact]
        public async Task No_token_is_sent_when_egress_is_off()
        {
            var handler = FakeHttpMessageHandler.Returning("{}");
            var service = new ToolCallService(new HttpClient(handler), new LogService(), egressTokens: _tokens);

            await service.ExecuteToolAsync("scanner", "simple-scanner", new() { ["url"] = "https://shop.acme.test" });

            Assert.False(handler.Requests[0].Request.Headers.Contains(EgressTokens.ToolHeader));
        }

        [Fact]
        public async Task Connections_are_logged_with_the_call_and_refusals_are_egress_blocks()
        {
            var (session, engagements) = SessionWithEngagement();
            var log = new LogService(session, _store);
            var service = new ToolCallService(new HttpClient(ToolApiThatConnectsTo("shop.acme.test:443", "evil.example:443")), log, session, _store,
                egressTokens: _tokens, egressOptions: Enforced(), engagements: engagements);

            var call = await service.ExecuteToolAsync("scanner", "simple-scanner", new() { ["url"] = "https://shop.acme.test" }, brain: "Keyword brain");

            var entries = log.GetLogs().Where(e => e.ToolCallId == call.Id && ToolCallService.ReadEgress(e) != null).ToList();
            Assert.Equal(2, entries.Count);
            Assert.Equal(LogLevel.Info, entries[0].Level);
            Assert.Contains("Network: allowed CONNECT shop.acme.test:443", entries[0].Message);
            Assert.Equal(LogLevel.Block, entries[1].Level);
            Assert.Contains("Egress control 'Network allow-list' blocked; the connection was refused", entries[1].Message);
            Assert.All(entries, e => Assert.Equal("Keyword brain", e.Brain));

            var violation = Assert.Single(ControlService.FromJson(ChatTranscript.ReadString(entries[1], "controls")));
            Assert.Equal(ControlStage.Egress, violation.Stage);
            Assert.Equal("egress:" + EgressDecision.Rules.NoRule, violation.ControlId);
            Assert.Equal("CONNECT evil.example:443", violation.Text);

            // The reply's pip shows the refusal, and the session report lists both connections.
            var request = new LogEntry { Level = LogLevel.User, Message = "Request: scan", Data = new() { ["prompt"] = "scan" }, Timestamp = call.CreatedAt.AddSeconds(-1) };
            var turnControls = ChatTranscript.Build(new[] { request }
                .Concat(log.GetLogs()), service.GetToolCalls()).Single().ReplyControls;
            Assert.Contains(turnControls, v => v.Stage == ControlStage.Egress);
            var connections = SessionReportBuilder.ListNetworkConnections(log.GetLogs());
            Assert.Equal(new[] { true, false }, connections.Select(c => c.Connection.Allowed));
            Assert.Equal("simple_scanner", connections[0].Tool);
        }

        [Fact]
        public async Task Refused_connections_count_as_blocks_for_the_session()
        {
            var (session, engagements) = SessionWithEngagement();
            var service = new ToolCallService(new HttpClient(ToolApiThatConnectsTo("evil.example:443")), new LogService(session, _store), session, _store,
                egressTokens: _tokens, egressOptions: Enforced(), engagements: engagements);

            await service.ExecuteToolAsync("scanner", "simple-scanner", new() { ["url"] = "https://shop.acme.test" });

            Assert.Equal(1, _store.GetSession(session.Current.Id)!.ControlBlockCount);
        }

        // --- The policy endpoint ---

        private async Task<(IResult Result, HttpContext Context)> Authorize(EgressOptions options, string? key, string target, string? auth = null)
        {
            var authorizer = new EgressAuthorizer(new EgressPolicy(() => EgressPolicyDefinition.Empty, _tokens,
                (host, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.215.14") })), Options.Create(options), _store, logDirectory: _directory);
            var context = new DefaultHttpContext();
            if (key != null) context.Request.Headers[EgressAuthorizer.Headers.Key] = key;
            context.Request.Headers[EgressAuthorizer.Headers.Method] = "CONNECT";
            context.Request.Headers[EgressAuthorizer.Headers.Target] = target;
            context.Request.Headers[EgressAuthorizer.Headers.RequestId] = "req-1";
            if (auth != null) context.Request.Headers[EgressAuthorizer.Headers.Authorization] = auth;
            return (await authorizer.HandleAsync(context.Request, CancellationToken.None), context);
        }

        private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 200;

        [Fact]
        public async Task Endpoint_is_not_found_when_egress_is_off()
        {
            var (result, _) = await Authorize(new EgressOptions(), PolicyKey, "shop.acme.test:443");

            Assert.Equal(404, StatusOf(result));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wrong-key-wrong-key-wrong")]
        [InlineData("test-policy-key-012345678")] // one character short
        public async Task Endpoint_refuses_callers_without_the_key(string? key)
        {
            var (result, _) = await Authorize(Enforced().Value, key, "shop.acme.test:443");

            Assert.Equal(401, StatusOf(result));
            Assert.Empty(_store.QueryEgressEvents(new EgressEventQuery(Challenges: null)));
        }

        [Fact]
        public async Task Endpoint_answers_with_the_checked_upstream_and_records_the_decision()
        {
            var grant = new EgressGrant("simple_scanner", DateTime.UtcNow.AddMinutes(5), ToolCallId: "call-9", SessionId: "s-9", UserName: "chris",
                EngagementId: "e-1", EngagementLabel: "Acme Ltd v1", EngagementDomains: new[] { "acme.test" },
                WindowStart: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), WindowEnd: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1));

            var (result, context) = await Authorize(Enforced().Value, PolicyKey, "shop.acme.test:443", "Bearer " + _tokens.Mint(grant));

            Assert.Equal(200, StatusOf(result));
            Assert.Equal("93.184.215.14:443", context.Response.Headers[EgressAuthorizer.Headers.Upstream].ToString());
            var recorded = Assert.Single(_store.LoadEgressEvents("call-9"));
            Assert.True(recorded.Allowed);
            Assert.Equal("req-1", recorded.RequestId);
            Assert.Equal("chris", recorded.UserName);
            Assert.Equal("e-1", recorded.EngagementId);
            var line = Assert.Single(File.ReadAllLines(Directory.GetFiles(_directory, "egress-*.log").Single()));
            Assert.Contains("\tAllowed\t200\tsimple_scanner\tchris\ts-9\tcall-9\tCONNECT\tshop.acme.test:443\t93.184.215.14:443\tengagement\t", line);
        }

        [Fact]
        public async Task Endpoint_challenges_clients_without_a_token()
        {
            var (result, context) = await Authorize(Enforced().Value, PolicyKey, "shop.acme.test:443");

            Assert.Equal(407, StatusOf(result));
            Assert.Equal(EgressAuthorizer.Challenge, context.Response.Headers.ProxyAuthenticate.ToString());
            Assert.False(context.Response.Headers.ContainsKey(EgressAuthorizer.Headers.Upstream));
        }

        // --- Store ---

        private EgressEvent Saved(string host, bool allowed, int status = 403, DateTime? at = null, string? user = "chris", string caller = "simple_scanner")
        {
            var e = new EgressEvent
            {
                Timestamp = at ?? DateTime.UtcNow, Method = "CONNECT", Target = host + ":443", Host = host, Port = 443, Allowed = allowed,
                Status = allowed ? 200 : status, Rule = "r", Reason = "because", Caller = caller, UserName = user, RequestId = "id-" + host,
            };
            _store.SaveEgressEvent(e);
            return e;
        }

        [Fact]
        public void Queries_filter_by_outcome_user_host_caller_and_time_newest_first()
        {
            var t0 = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
            Saved("shop.acme.test", allowed: true, at: t0);
            Saved("evil.example", allowed: false, at: t0.AddMinutes(1));
            Saved("other.example", allowed: false, at: t0.AddMinutes(2), user: "blob", caller: "weather_forecast");
            Saved("challenge.example", allowed: false, status: 407, at: t0.AddMinutes(3), user: null, caller: EgressCallers.Anonymous);

            Assert.Equal(new[] { "other.example", "evil.example", "shop.acme.test" }, _store.QueryEgressEvents(new()).Select(e => e.Host));
            Assert.Equal(new[] { "other.example", "evil.example" }, _store.QueryEgressEvents(new(Allowed: false)).Select(e => e.Host));
            Assert.Equal(new[] { "other.example" }, _store.QueryEgressEvents(new(UserName: "blob")).Select(e => e.Host));
            Assert.Equal(new[] { "evil.example" }, _store.QueryEgressEvents(new(HostContains: "EVIL")).Select(e => e.Host));
            Assert.Equal(new[] { "other.example" }, _store.QueryEgressEvents(new(Caller: "weather_forecast")).Select(e => e.Host));
            Assert.Equal(new[] { "evil.example" }, _store.QueryEgressEvents(new(FromUtc: t0.AddSeconds(30), ToUtc: t0.AddSeconds(90))).Select(e => e.Host));
            Assert.Equal(new[] { "challenge.example" }, _store.QueryEgressEvents(new(Challenges: true)).Select(e => e.Host));
            Assert.Equal(4, _store.QueryEgressEvents(new(Challenges: null)).Count);
            Assert.Single(_store.QueryEgressEvents(new(Limit: 1)));
        }

        [Fact]
        public void Traffic_figures_are_added_by_request_id()
        {
            var e = Saved("shop.acme.test", allowed: true);

            Assert.True(_store.UpdateEgressTraffic(e.RequestId!, 536, 5762, 1041));
            Assert.False(_store.UpdateEgressTraffic("unknown", 1, 1, 1));

            var stored = _store.QueryEgressEvents(new()).Single();
            Assert.Equal((536L, 5762L, 1041L), (stored.BytesSent!.Value, stored.BytesReceived!.Value, stored.DurationMs!.Value));
        }

        // --- Access log ---

        [Fact]
        public void Access_log_lines_are_parsed_in_either_number_format()
        {
            var numbers = EgressAccessLogLine.Parse("""{"request_id":"abc","bytes_from_client":536,"bytes_to_client":5762,"duration_ms":1041}""")!;
            var strings = EgressAccessLogLine.Parse("""{"request_id":"abc","bytes_from_client":"10","bytes_to_client":"-","duration_ms":"7"}""")!;

            Assert.Equal((536L, 5762L, 1041L), (numbers.BytesSent!.Value, numbers.BytesReceived!.Value, numbers.DurationMs!.Value));
            Assert.Equal(10, strings.BytesSent);
            Assert.Null(strings.BytesReceived);
            Assert.Null(EgressAccessLogLine.Parse("""{"request_id":"-"}"""));
            Assert.Null(EgressAccessLogLine.Parse("not json"));
            Assert.Null(EgressAccessLogLine.Parse(""));
        }

        [Fact]
        public void The_reader_applies_whole_lines_only_and_starts_again_after_truncation()
        {
            var e = Saved("shop.acme.test", allowed: true);
            var path = Path.Combine(_directory, "egress-access.log");
            var reader = new EgressAccessLogReader(_store, Options.Create(new EgressOptions { Mode = EgressMode.Enforced, PolicyKey = PolicyKey, AccessLogPath = path }),
                new TestEnvironment(_directory), Microsoft.Extensions.Logging.Abstractions.NullLogger<EgressAccessLogReader>.Instance);

            File.WriteAllText(path, $"{{\"request_id\":\"{e.RequestId}\",\"bytes_from_client\":1,\"bytes_to_client\":2,\"duration_ms\":3}}\n{{\"request_id\":\"part");
            Assert.Equal(1, reader.ReadNewLines());
            Assert.Equal(0, reader.ReadNewLines()); // the partial line waits

            File.WriteAllText(path, $"{{\"request_id\":\"{e.RequestId}\",\"bytes_from_client\":9,\"bytes_to_client\":9,\"duration_ms\":9}}\n");
            Assert.Equal(1, reader.ReadNewLines()); // shorter than before: read from the top
            Assert.Equal(9, _store.QueryEgressEvents(new()).Single().BytesSent);
        }

        private sealed class TestEnvironment(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
        {
            public string WebRootPath { get; set; } = root;
            public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
            public string ApplicationName { get; set; } = "tests";
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
            public string ContentRootPath { get; set; } = root;
            public string EnvironmentName { get; set; } = "Test";
        }

        // --- Settings and the harness's own traffic ---

        [Fact]
        public void Settings_problems_stop_startup()
        {
            Assert.Empty(new EgressOptions().Problems());
            Assert.Empty(Enforced().Value.Problems());
            Assert.Contains(new EgressOptions { Mode = EgressMode.Advisory, PolicyKey = "short" }.Problems(), p => p.Contains("PolicyKey"));
            Assert.Contains(new EgressOptions { TokenLifetimeSeconds = 1 }.Problems(), p => p.Contains("TokenLifetimeSeconds"));
            Assert.Contains(new EgressOptions { TokenSigningKey = Convert.ToBase64String(new byte[8]) }.Problems(), p => p.Contains("TokenSigningKey"));
            Assert.Contains(new EgressOptions { RouteLlmTraffic = true }.Problems(), p => p.Contains("ProxyUrl"));
        }

        [Fact]
        public void Controls_json_cannot_define_egress_controls()
        {
            var ex = Assert.Throws<ControlCatalogException>(() => ControlCatalog.Parse("""
                { "controls": [ { "id": "x", "stage": "Egress", "name": "n", "defaultMode": "Block", "rule": { "type": "contains", "value": "v" } } ] }
                """));

            Assert.Contains("network rules belong in egress.json", ex.Message);
        }

        [Theory]
        [InlineData("https://api.example-llm.com/v1", true, true)]
        [InlineData("http://localhost:11434/v1", true, false)]   // a local model never goes through the proxy
        [InlineData("https://api.example-llm.com/v1", false, false)]
        public void Llm_traffic_goes_through_the_proxy_only_when_asked_and_not_local(string baseUrl, bool route, bool expected)
        {
            var egress = new EgressOptions { Mode = EgressMode.Enforced, PolicyKey = PolicyKey, RouteLlmTraffic = route, ProxyUrl = "http://localhost:3128" };
            var llm = new LlmOptions { BaseUrl = baseUrl };

            var handler = (SocketsHttpHandler)EgressHandlers.ForLlm(egress, llm, _tokens, TimeSpan.FromMinutes(2));

            Assert.Equal(expected, EgressHandlers.RoutesLlmTraffic(egress, llm));
            Assert.Equal(expected, handler.Proxy != null);
            if (expected)
            {
                var credential = handler.Proxy!.Credentials!.GetCredential(new Uri("http://localhost:3128"), "Basic")!;
                Assert.Equal(EgressCallers.HarnessLlm, _tokens.Read(credential.Password, DateTime.UtcNow, out _)!.Caller);
            }
        }
    }
}
