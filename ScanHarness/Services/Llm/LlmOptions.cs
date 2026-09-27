namespace ScanHarness.Services.Llm
{
    /// <summary>How an LLM brain is asked to express its decision.</summary>
    public enum LlmDecisionMode
    {
        /// <summary>OpenAI function/tool calling: the model returns <c>tool_calls</c>.</summary>
        Tools,

        /// <summary>Structured output: the model returns a JSON object matching a schema.</summary>
        JsonSchema
    }

    /// <summary>
    /// Settings for an OpenAI-compatible chat completions endpoint (the "Llm" config section).
    /// Works with Ollama (<c>http://localhost:11434/v1</c>), LM Studio, llama.cpp server, vLLM,
    /// Docker Model Runner, or a hosted OpenAI-compatible API — only the base URL and key change.
    /// </summary>
    public class LlmOptions
    {
        public const string SectionName = "Llm";

        /// <summary>When false, only the rule-based keyword brain is offered.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Base URL of the OpenAI-compatible API, including the <c>/v1</c> segment.</summary>
        public string BaseUrl { get; set; } = "http://localhost:11434/v1";

        /// <summary>API key sent as a bearer token. Local servers such as Ollama ignore it.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Model selected when the page opens. Empty means start with the keyword brain.</summary>
        public string? DefaultModel { get; set; }

        /// <summary>Whether to use tool calling or JSON-schema structured output.</summary>
        public LlmDecisionMode DecisionMode { get; set; } = LlmDecisionMode.JsonSchema;

        /// <summary>Sampling temperature; 0 keeps decisions as repeatable as possible.</summary>
        public double Temperature { get; set; } = 0;

        /// <summary>Timeout for a single decision. CPU-only models can take a while to load.</summary>
        public int TimeoutSeconds { get; set; } = 180;

        /// <summary>Case-insensitive substrings of model ids to hide (e.g. embedding models).</summary>
        public List<string> ExcludeModelPatterns { get; set; } = new();
    }
}
