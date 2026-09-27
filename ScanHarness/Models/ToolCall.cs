namespace ScanHarness.Models
{
    public class ToolCall
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Tool { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public ToolCallStatus Status { get; set; } = ToolCallStatus.Pending;
        public string? Result { get; set; }
        public bool RequiresApproval { get; set; }
        public bool IsApproved { get; set; }
    }

    public class LogEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? ToolCallId { get; set; }
        public Dictionary<string, object>? Data { get; set; }
    }

    public class ToolResponse
    {
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, object>? Data { get; set; }
    }

    public enum ToolCallStatus
    {
        Pending,
        WaitingForApproval,
        Approved,
        Running,
        Completed,
        Failed,
        Cancelled
    }

    public enum LogLevel
    {
        Info,
        Warning,
        Error,
        Success,
        User
    }
}
