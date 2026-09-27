namespace ScanHarness.Services
{
    /// <summary>
    /// Describes a tool the agent brain may choose. The catalog is the single source of truth
    /// for what an LLM brain is offered and for whether a tool needs user approval, so an LLM
    /// can never talk its way out of the attack confirmation.
    /// </summary>
    /// <param name="FunctionName">Name exposed to the LLM (OpenAI function name).</param>
    /// <param name="Tool">Tool identifier used by <see cref="ToolCallService"/>, e.g. "scanner".</param>
    /// <param name="Action">Action on the tool, e.g. "simple-scanner".</param>
    /// <param name="Description">Description shown to the LLM so it can decide when the tool fits.</param>
    /// <param name="RequiresApproval">Whether the user must approve the call before it runs.</param>
    /// <param name="ConfirmationTemplate">Message shown to the user when the tool is chosen; <c>{url}</c> is replaced with the target.</param>
    public sealed record ToolDefinition(
        string FunctionName,
        string Tool,
        string Action,
        string Description,
        bool RequiresApproval,
        string ConfirmationTemplate)
    {
        /// <summary>Formats <see cref="ConfirmationTemplate"/> for the given target URL.</summary>
        public string FormatConfirmation(string url) => ConfirmationTemplate.Replace("{url}", url);
    }

    /// <summary>The tools available to the agent. Every tool takes a single <c>url</c> parameter.</summary>
    public static class ToolCatalog
    {
        public static IReadOnlyList<ToolDefinition> All { get; } = new[]
        {
            new ToolDefinition(
                FunctionName: "simple_scanner",
                Tool: "scanner",
                Action: "simple-scanner",
                Description: "Scan a website URL and return the links found on it (link text and URL). " +
                             "Use for requests to scan, crawl, map, or list/enumerate the links on a site.",
                RequiresApproval: false,
                ConfirmationTemplate: "I'll scan {url} for links using the SimpleScanner tool."),

            new ToolDefinition(
                FunctionName: "attack",
                Tool: "attack",
                Action: "execute",
                Description: "Run a simulated attack against a website URL and report the outcome. " +
                             "Use only when the user explicitly asks to attack, exploit, hack or breach a site.",
                RequiresApproval: true,
                ConfirmationTemplate: "This looks like an attack request. I'll run a simulated attack against {url} once you approve."),
        };

        /// <summary>Finds a tool by the function name the LLM used (case-insensitive), or null.</summary>
        public static ToolDefinition? FindByFunctionName(string? functionName) =>
            All.FirstOrDefault(t => string.Equals(t.FunctionName, functionName, StringComparison.OrdinalIgnoreCase));
    }
}
