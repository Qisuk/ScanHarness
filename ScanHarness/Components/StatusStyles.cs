namespace ScanHarness.Components
{
    // Imported inside the namespace so Models.LogLevel wins over the implicit
    // Microsoft.Extensions.Logging.LogLevel.
    using ScanHarness.Models;

    /// <summary>
    /// Maps tool-call statuses and log levels to the shared pill classes defined in
    /// <c>wwwroot/app.css</c>, so every component colors statuses the same way.
    /// </summary>
    public static class StatusStyles
    {
        /// <summary>Returns the CSS classes for a status pill representing a tool call status.</summary>
        public static string PillClass(ToolCallStatus status) => "sh-pill " + status switch
        {
            ToolCallStatus.Completed => "sh-pill-success",
            ToolCallStatus.Failed => "sh-pill-danger",
            ToolCallStatus.WaitingForApproval => "sh-pill-warning",
            ToolCallStatus.Running => "sh-pill-info",
            ToolCallStatus.Approved => "sh-pill-accent",
            _ => ""
        };

        /// <summary>Returns the CSS classes for a status pill representing a log level.</summary>
        public static string PillClass(LogLevel level) => "sh-pill " + level switch
        {
            LogLevel.Error => "sh-pill-danger",
            LogLevel.Warning => "sh-pill-warning",
            LogLevel.Success => "sh-pill-success",
            LogLevel.User => "sh-pill-user",
            _ => "sh-pill-accent"
        };

        /// <summary>Returns the modifier class used to tint a log entry by its level.</summary>
        public static string LevelClass(LogLevel level) => "level-" + level.ToString().ToLowerInvariant();
    }
}
