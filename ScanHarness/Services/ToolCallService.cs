using ScanHarness.Models;
using System.Text.Json;

namespace ScanHarness.Services
{
    public class ToolCallService
    {
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private readonly List<ToolCall> _toolCalls = new();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _approvalWaiters = new();
        private readonly object _lockObject = new();

        public event Action? OnToolCallsChanged;

        public ToolCallService(HttpClient httpClient, LogService logService)
        {
            _httpClient = httpClient;
            _logService = logService;
        }

        public async Task<ToolCall> ExecuteToolAsync(string tool, string action, Dictionary<string, string> parameters, bool requiresApproval = false)
        {
            var toolCall = new ToolCall
            {
                Tool = tool,
                Action = action,
                Parameters = parameters,
                RequiresApproval = requiresApproval,
                Status = requiresApproval ? ToolCallStatus.WaitingForApproval : ToolCallStatus.Running
            };

            lock (_lockObject)
            {
                _toolCalls.Add(toolCall);
            }

            _logService.LogInfo($"Tool call initiated: {tool}/{action}", toolCall.Id);

            if (requiresApproval)
            {
                _logService.LogWarning($"Tool call awaiting user approval: {tool}/{action}", toolCall.Id);
                OnToolCallsChanged?.Invoke();

                // Wait for approval
                var approvalSource = new TaskCompletionSource<bool>();
                _approvalWaiters[toolCall.Id] = approvalSource;

                var approved = await approvalSource.Task;

                if (!approved)
                {
                    toolCall.Status = ToolCallStatus.Cancelled;
                    _logService.LogError($"Tool call cancelled by user: {tool}/{action}", toolCall.Id);
                    OnToolCallsChanged?.Invoke();
                    return toolCall;
                }

                toolCall.IsApproved = true;
                toolCall.Status = ToolCallStatus.Running;
            }

            try
            {
                // Call the actual tool API
                var result = await CallToolApiAsync(tool, action, parameters);

                toolCall.Result = JsonSerializer.Serialize(result);
                toolCall.Status = ToolCallStatus.Completed;
                _logService.LogSuccess($"Tool call completed: {tool}/{action}", toolCall.Id);
            }
            catch (Exception ex)
            {
                toolCall.Status = ToolCallStatus.Failed;
                _logService.LogError($"Tool call failed: {tool}/{action} - {ex.Message}", toolCall.Id);
            }

            OnToolCallsChanged?.Invoke();
            return toolCall;
        }

        private async Task<object> CallToolApiAsync(string tool, string action, Dictionary<string, string> parameters)
        {
            // Build the API call based on tool and action
            // For now, this is a placeholder - will call DemoToolAPI_1

            var baseUrl = "https://localhost:7031"; // DemoToolAPI_1 port

            if (tool == "scanner" && action == "simple-scanner")
            {
                var url = parameters.ContainsKey("url") ? parameters["url"] : "";
                var response = await _httpClient.GetAsync($"{baseUrl}/api/tools/simple-scanner?url={Uri.EscapeDataString(url)}");
                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<object>(content) ?? new { };
            }
            else if (tool == "attack" && action == "execute")
            {
                var url = parameters.ContainsKey("url") ? parameters["url"] : "";
                var response = await _httpClient.PostAsync($"{baseUrl}/api/tools/attack?url={Uri.EscapeDataString(url)}", null);
                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<object>(content) ?? new { };
            }

            throw new NotImplementedException($"Tool {tool}/{action} not implemented");
        }

        public void ApproveToolCall(string toolCallId)
        {
            if (_approvalWaiters.ContainsKey(toolCallId))
            {
                var toolCall = GetToolCall(toolCallId);
                _logService.LogUser($"Approved tool call: {Describe(toolCall)}", toolCallId);
                _approvalWaiters[toolCallId].SetResult(true);
                _approvalWaiters.Remove(toolCallId);
            }
        }

        public void RejectToolCall(string toolCallId)
        {
            if (_approvalWaiters.ContainsKey(toolCallId))
            {
                var toolCall = GetToolCall(toolCallId);
                _logService.LogUser($"Rejected tool call: {Describe(toolCall)}", toolCallId);
                _approvalWaiters[toolCallId].SetResult(false);
                _approvalWaiters.Remove(toolCallId);
            }
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
}
