namespace ScanHarness.Services
{
    /// <summary>How a tool parameter's value is validated.</summary>
    public enum ToolParameterType
    {
        /// <summary>A web address; normalised to an absolute http(s) URL.</summary>
        Url,

        /// <summary>Free text, such as a place name.</summary>
        Text
    }

    /// <summary>A parameter a tool accepts.</summary>
    /// <param name="Name">Parameter name, used in the LLM schema and passed to the tool API.</param>
    /// <param name="Type">How the value is validated.</param>
    /// <param name="Description">Description shown to the LLM.</param>
    /// <param name="Required">Whether the tool can't run without it.</param>
    public sealed record ToolParameter(string Name, ToolParameterType Type, string Description, bool Required);

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
    /// <param name="Parameters">Parameters the tool accepts.</param>
    /// <param name="Confirmation">Builds the message shown to the user when the tool is chosen.</param>
    /// <param name="DefaultClassification">Internal or external unless overridden in config (see <see cref="ToolCatalog.ClassificationOf"/>).</param>
    public sealed record ToolDefinition(
        string FunctionName,
        string Tool,
        string Action,
        string Description,
        bool RequiresApproval,
        IReadOnlyList<ToolParameter> Parameters,
        Func<IReadOnlyDictionary<string, string>, string> Confirmation,
        Classification DefaultClassification = Classification.Internal)
    {
        /// <summary>Formats the confirmation message for the chosen parameters.</summary>
        public string FormatConfirmation(IReadOnlyDictionary<string, string> parameters) => Confirmation(parameters);
    }

    /// <summary>The tools available to the agent.</summary>
    public static class ToolCatalog
    {
        private static readonly ToolParameter TargetUrl = new(
            "url", ToolParameterType.Url, "The target URL or domain, e.g. https://example.com or example.com.", Required: true);

        public static IReadOnlyList<ToolDefinition> All { get; } = new[]
        {
            new ToolDefinition(
                FunctionName: "simple_scanner",
                Tool: "scanner",
                Action: "simple-scanner",
                Description: "Scan a website URL and return the links found on it (link text and URL). " +
                             "Use for requests to scan, crawl, map, or list/enumerate the links on a site.",
                RequiresApproval: false,
                Parameters: new[] { TargetUrl },
                Confirmation: p => $"I'll scan {p["url"]} for links using the SimpleScanner tool."),

            new ToolDefinition(
                FunctionName: "attack",
                Tool: "attack",
                Action: "execute",
                Description: "Run a simulated attack against a website URL and report the outcome. " +
                             "Use only when the user explicitly asks to attack, exploit, hack or breach a site.",
                RequiresApproval: true,
                Parameters: new[] { TargetUrl },
                Confirmation: p => $"This looks like an attack request. I'll run a simulated attack against {p["url"]} once you approve."),

            new ToolDefinition(
                FunctionName: "weather_forecast",
                Tool: "weather",
                Action: "forecast",
                Description: "Get a five-day weather forecast (temperatures and a summary per day). " +
                             "Use for questions about the weather, forecast or temperature.",
                RequiresApproval: false,
                Parameters: new[]
                {
                    new ToolParameter("location", ToolParameterType.Text,
                        "The place the user asked about, e.g. London. Omit it if the user didn't name one.", Required: false),
                },
                Confirmation: p => p.TryGetValue("location", out var location)
                    ? $"I'll get the weather forecast for {location}."
                    : "I'll get the weather forecast.",
                // A real forecast would come from a third-party weather service.
                DefaultClassification: Classification.External),
        };

        private static IReadOnlyDictionary<string, Classification> _classificationOverrides = new Dictionary<string, Classification>();

        /// <summary>Overrides tool classifications by function name (config section "Tools:Classifications").</summary>
        public static void UseClassifications(IReadOnlyDictionary<string, Classification> overrides) =>
            _classificationOverrides = new Dictionary<string, Classification>(overrides, StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether a tool is internal or external: its config override, else its default.</summary>
        public static Classification ClassificationOf(ToolDefinition tool) =>
            _classificationOverrides.TryGetValue(tool.FunctionName, out var classification) ? classification : tool.DefaultClassification;

        /// <summary>Classification of a tool by its identifier (e.g. "scanner"), or null for an unknown tool.</summary>
        public static Classification? ClassificationOf(string? tool) =>
            FindByTool(tool) is ToolDefinition definition ? ClassificationOf(definition) : null;

        /// <summary>Finds a tool by the function name the LLM used (case-insensitive), or null.</summary>
        public static ToolDefinition? FindByFunctionName(string? functionName) =>
            All.FirstOrDefault(t => string.Equals(t.FunctionName, functionName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Finds a tool by its tool identifier (e.g. "scanner"), or null.</summary>
        public static ToolDefinition? FindByTool(string? tool) =>
            All.FirstOrDefault(t => string.Equals(t.Tool, tool, StringComparison.OrdinalIgnoreCase));

        /// <summary>Every distinct parameter across all tools, in first-seen order (used for the JSON-schema decision shape).</summary>
        public static IReadOnlyList<ToolParameter> AllParameters { get; } =
            All.SelectMany(t => t.Parameters).GroupBy(p => p.Name).Select(g => g.First()).ToList();
    }
}
