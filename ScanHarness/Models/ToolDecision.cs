namespace ScanHarness.Models
{
    /// <summary>
    /// The result of the agent "brain" interpreting a user prompt: which tool (if any)
    /// should be called, with what parameters, and whether it needs user approval.
    /// </summary>
    public class ToolDecision
    {
        /// <summary>True when the agent found a suitable tool for the request.</summary>
        public bool ToolFound { get; set; }

        /// <summary>Tool identifier, e.g. "scanner" or "attack". Null when no tool was chosen.</summary>
        public string? Tool { get; set; }

        /// <summary>Action on the tool, e.g. "simple-scanner" or "execute".</summary>
        public string? Action { get; set; }

        /// <summary>Parameters to pass to the tool (e.g. the target url).</summary>
        public Dictionary<string, string> Parameters { get; set; } = new();

        /// <summary>Whether the chosen tool requires explicit user approval before running.</summary>
        public bool RequiresApproval { get; set; }

        /// <summary>The agent's message to the user explaining its decision.</summary>
        public string Message { get; set; } = string.Empty;

        public static ToolDecision NoTool(string message) => new() { ToolFound = false, Message = message };
    }
}
