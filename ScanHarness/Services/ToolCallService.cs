using ScanHarness.Models;
using ScanHarness.Services.Persistence;
using System.Text.Json;

namespace ScanHarness.Services
{
    /// <summary>
    /// Runs tool calls against the tool APIs, handles approval, and keeps the current session's calls.
    /// Every call is saved to the activity store at each change of status, including the approval
    /// decision and who made it, so a session can be recreated from the store.
    /// </summary>
    public class ToolCallService
    {
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private readonly SessionState? _session;
        private readonly IActivityStore? _store;
        private readonly Agents.AgentRunner? _agents;
        private readonly ToolOptions _options;
        private readonly Egress.EgressTokens? _egressTokens;
        private readonly Egress.EgressOptions _egressOptions;
        private readonly Engagements.EngagementService? _engagements;
        private readonly List<ToolCall> _toolCalls = new();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _approvalWaiters = new();
        private readonly object _lockObject = new();

        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

        public event Action? OnToolCallsChanged;

        /// <param name="session">Current user and session; calls are stamped with them. Optional for tests.</param>
        /// <param name="store">Where calls are saved; null keeps them in memory only.</param>
        /// <param name="agents">Runs agent tool calls; null means agents can't be called (e.g. in some tests).</param>
        /// <param name="options">Where the tool API is; defaults to the local DemoToolAPI_1.</param>
        /// <param name="egressTokens">Signs each tool API call's egress token; null sends none.</param>
        /// <param name="egressOptions">Whether the egress proxy is in use (Egress:Mode); defaults to Off.</param>
        /// <param name="engagements">The session's engagement, whose scope goes into the egress token of scoped tools.</param>
        public ToolCallService(HttpClient httpClient, LogService logService, SessionState? session = null, IActivityStore? store = null,
            Agents.AgentRunner? agents = null, Microsoft.Extensions.Options.IOptions<ToolOptions>? options = null,
            Egress.EgressTokens? egressTokens = null, Microsoft.Extensions.Options.IOptions<Egress.EgressOptions>? egressOptions = null,
            Engagements.EngagementService? engagements = null)
        {
            _httpClient = httpClient;
            _logService = logService;
            _session = session;
            _store = store;
            _agents = agents;
            _options = options?.Value ?? new ToolOptions();
            _egressTokens = egressTokens;
            _egressOptions = egressOptions?.Value ?? new Egress.EgressOptions();
            _engagements = engagements;
        }

        private bool EgressInUse => _egressOptions.Mode != Egress.EgressMode.Off && _egressTokens != null;

        /// <summary>Replaces the in-memory calls with a stored session's, e.g. when a session is opened.</summary>
        public void LoadSession(IEnumerable<ToolCall> calls)
        {
            lock (_lockObject)
            {
                _toolCalls.Clear();
                _toolCalls.AddRange(calls);
            }

            OnToolCallsChanged?.Invoke();
        }

        /// <summary>Records the brain's summary of a finished call and saves it.</summary>
        public void RecordSummary(ToolCall toolCall, string summary, string summaryBrain)
        {
            toolCall.Summary = summary;
            toolCall.SummaryBrain = summaryBrain;
            Persist(toolCall);
            OnToolCallsChanged?.Invoke();
        }

        /// <param name="brain">Name of the agent brain that chose this call; recorded on the call and every log entry.</param>
        public async Task<ToolCall> ExecuteToolAsync(string tool, string action, Dictionary<string, string> parameters, bool requiresApproval = false, string? brain = null)
        {
            var toolCall = new ToolCall
            {
                Tool = tool,
                Action = action,
                Parameters = parameters,
                RequiresApproval = requiresApproval,
                Brain = brain,
                Status = requiresApproval ? ToolCallStatus.WaitingForApproval : ToolCallStatus.Running,
                SessionId = _session?.Current.Id,
                UserName = _session?.CurrentUser.Name,
            };

            lock (_lockObject)
            {
                _toolCalls.Add(toolCall);
            }

            Persist(toolCall);
            _logService.LogInfo($"Tool call initiated: {tool}/{action}", toolCall.Id, brain: brain);

            if (requiresApproval)
            {
                _logService.LogWarning($"Tool call awaiting user approval: {tool}/{action}", toolCall.Id, brain: brain);
                OnToolCallsChanged?.Invoke();

                // Wait for approval
                var approvalSource = new TaskCompletionSource<bool>();
                _approvalWaiters[toolCall.Id] = approvalSource;

                var approved = await approvalSource.Task;

                if (!approved)
                {
                    toolCall.Status = ToolCallStatus.Cancelled;
                    toolCall.CompletedAt = DateTime.UtcNow;
                    Persist(toolCall);
                    _logService.LogError($"Tool call cancelled by user: {tool}/{action}", toolCall.Id, brain: brain);
                    OnToolCallsChanged?.Invoke();
                    return toolCall;
                }

                toolCall.IsApproved = true;
                toolCall.Status = ToolCallStatus.Running;
                Persist(toolCall);
            }

            try
            {
                // Call the actual tool API
                var result = await CallToolApiAsync(toolCall);

                toolCall.Result = JsonSerializer.Serialize(result);
                toolCall.Status = ToolCallStatus.Completed;
                toolCall.CompletedAt = DateTime.UtcNow;
                Persist(toolCall);
                _logService.LogSuccess($"Tool call completed: {tool}/{action}", toolCall.Id, brain: brain);
            }
            catch (Exception ex)
            {
                toolCall.Status = ToolCallStatus.Failed;
                toolCall.Error = ex.Message;
                toolCall.CompletedAt = DateTime.UtcNow;
                Persist(toolCall);
                _logService.LogError($"Tool call failed: {tool}/{action} - {ex.Message}", toolCall.Id, brain: brain);
            }

            ReportEgress(toolCall);
            OnToolCallsChanged?.Invoke();
            return toolCall;
        }

        /// <summary>
        /// Logs every connection the call's tool made through the egress proxy. The proxy checks each one with the
        /// harness before connecting, so by the time the tool answers they're all recorded. Allowed connections are
        /// Info entries; refused ones are Egress blocks, so they get a red pip and count towards the session's colour.
        /// Each entry carries the connection in its "egress" data.
        /// </summary>
        private void ReportEgress(ToolCall toolCall)
        {
            if (!EgressInUse || _store == null || ToolCatalog.Find(toolCall.Tool, toolCall.Action)?.IsAgent != false)
            {
                return;
            }

            IReadOnlyList<EgressEvent> events;
            try
            {
                events = _store.LoadEgressEvents(toolCall.Id);
            }
            catch (Exception ex)
            {
                _logService.LogError($"Could not read the network connections of {toolCall.Tool}/{toolCall.Action}: {ex.Message}", toolCall.Id, brain: toolCall.Brain);
                return;
            }

            foreach (var connection in events)
            {
                var data = new Dictionary<string, object> { [EgressDataKey] = JsonSerializer.Serialize(connection, WebJson) };
                if (connection.Allowed)
                {
                    _logService.LogInfo($"Network: allowed {connection.Describe()}. {connection.Reason}", toolCall.Id, data, toolCall.Brain);
                    continue;
                }

                var violation = new Controls.ControlViolation($"egress:{connection.Rule}", NetworkControlName, Controls.ControlStage.Egress,
                    Controls.ControlMode.Block, ChatTranscript.Targets.Network, connection.Reason, connection.Describe());
                Controls.ControlReporter.Report(_logService, new Controls.ControlCheck(new[] { violation }), toolCall.Brain, toolCall.Id, data);
            }
        }

        /// <summary>Log data key holding a connection the egress proxy was asked to make (an <see cref="EgressEvent"/> as JSON).</summary>
        public const string EgressDataKey = "egress";

        /// <summary>Name shown on pips and in the log for refused connections.</summary>
        public const string NetworkControlName = "Network allow-list";

        /// <summary>Reads the connection from a log entry's "egress" data, or null.</summary>
        public static EgressEvent? ReadEgress(LogEntry entry)
        {
            var json = ChatTranscript.ReadString(entry, EgressDataKey);
            if (json == null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<EgressEvent>(json, WebJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// The egress token for a tool API call: what this call may reach, signed so the tool can't widen it. Scoped
        /// tools get the session's engagement (already checked by ScopeGuard before the call), and every tool gets its
        /// own egress list. Null when the egress proxy isn't in use.
        /// </summary>
        private string? MintEgressToken(ToolDefinition definition, ToolCall toolCall)
        {
            if (!EgressInUse)
            {
                return null;
            }

            var egress = definition.EffectiveEgress;
            var engagement = egress.EngagementScope ? _engagements?.Current : null;
            var grant = new Egress.EgressGrant(
                definition.FunctionName,
                DateTime.UtcNow.AddSeconds(_egressOptions.TokenLifetimeSeconds),
                toolCall.Id,
                toolCall.SessionId,
                toolCall.UserName,
                engagement?.Id,
                engagement?.Label,
                engagement?.Domains,
                engagement?.WindowStart,
                engagement?.WindowEnd,
                egress.Domains,
                egress.Ports);
            return _egressTokens!.Mint(grant);
        }

        // Saves the call's current state. Best-effort: a storage failure is logged, not thrown, so the call itself isn't affected.
        private void Persist(ToolCall toolCall)
        {
            if (_store == null || _session == null)
            {
                return;
            }

            try
            {
                _session.EnsureSaved();
                _store.SaveToolCall(toolCall);
            }
            catch (Exception ex)
            {
                _logService.LogError($"Could not save tool call {toolCall.Tool}/{toolCall.Action}: {ex.Message}", toolCall.Id);
            }
        }

        // Agents run in the harness; everything else is an HTTP call described in tools.json.
        private async Task<object> CallToolApiAsync(ToolCall toolCall)
        {
            var definition = ToolCatalog.Find(toolCall.Tool, toolCall.Action)
                ?? throw new NotImplementedException($"Tool {toolCall.Tool}/{toolCall.Action} is not in the catalog");

            if (definition.IsAgent)
            {
                if (_agents == null)
                    throw new InvalidOperationException("Agents aren't available here.");
                var json = await _agents.RunAsync(definition.Action, GetToolCalls(), toolCall.Id);
                return JsonSerializer.Deserialize<object>(json) ?? new { };
            }

            var http = definition.Http ?? throw new NotImplementedException($"Tool {definition.FunctionName} has no HTTP binding");
            var url = $"{_options.BaseUrl.TrimEnd('/')}/{http.Resolve(toolCall.Parameters)}";
            using var request = new HttpRequestMessage(http.Method == "POST" ? HttpMethod.Post : HttpMethod.Get, url);
            if (MintEgressToken(definition, toolCall) is string token)
            {
                // The tool presents this to the egress proxy as its proxy credential for every connection it makes.
                request.Headers.Add(Egress.EgressTokens.ToolHeader, token);
            }

            using var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(content) ?? new { };
        }

        public void ApproveToolCall(string toolCallId) => Decide(toolCallId, approved: true);

        public void RejectToolCall(string toolCallId) => Decide(toolCallId, approved: false);

        // Records the decision (what, when and by whom), logs it, then lets the waiting call continue.
        private void Decide(string toolCallId, bool approved)
        {
            if (!_approvalWaiters.TryGetValue(toolCallId, out var waiter))
            {
                return;
            }

            var toolCall = GetToolCall(toolCallId);
            if (toolCall != null)
            {
                toolCall.ApprovalDecision = approved ? "approved" : "rejected";
                toolCall.ApprovalDecidedAt = DateTime.UtcNow;
                toolCall.ApprovalDecidedBy = _session?.CurrentUser.Name;
                Persist(toolCall);
            }

            _logService.LogUser($"{(approved ? "Approved" : "Rejected")} tool call: {Describe(toolCall)}", toolCallId, brain: toolCall?.Brain);
            waiter.SetResult(approved);
            _approvalWaiters.Remove(toolCallId);
        }

        private static string Describe(ToolCall? toolCall)
        {
            if (toolCall == null)
            {
                return "(unknown)";
            }

            var call = $"{toolCall.Tool}/{toolCall.Action}";
            if (toolCall.Parameters.Count == 0)
            {
                return call;
            }

            var parameters = string.Join(", ", toolCall.Parameters.Select(p => $"{p.Key}={p.Value}"));
            return $"{call} ({parameters})";
        }

        public List<ToolCall> GetToolCalls()
        {
            lock (_lockObject)
            {
                return _toolCalls.OrderBy(t => t.CreatedAt).ToList();
            }
        }

        public List<ToolCall> GetRecentToolCalls(int count = 20)
        {
            lock (_lockObject)
            {
                return _toolCalls.OrderByDescending(t => t.CreatedAt).Take(count).Reverse().ToList();
            }
        }

        public ToolCall? GetToolCall(string id)
        {
            lock (_lockObject)
            {
                return _toolCalls.FirstOrDefault(t => t.Id == id);
            }
        }

        public void Clear()
        {
            lock (_lockObject)
            {
                _toolCalls.Clear();
            }
            OnToolCallsChanged?.Invoke();
        }
    }

    /// <summary>Settings for tools (config section "Tools").</summary>
    public class ToolOptions
    {
        public const string SectionName = "Tools";

        /// <summary>Base URL of the tool API; each tool's http.path in tools.json is relative to it.</summary>
        public string BaseUrl { get; set; } = "https://localhost:7031";

        /// <summary>Tool definitions file, relative to the content root.</summary>
        public string DefinitionsPath { get; set; } = ToolCatalog.DefaultFileName;

        /// <summary>Overrides of tool classifications by function name (applied under any Permissions tab edit).</summary>
        public Dictionary<string, Classification> Classifications { get; set; } = new();
    }
}
