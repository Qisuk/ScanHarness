using Microsoft.Extensions.Options;
using ScanHarness.Services.Llm;

namespace ScanHarness.Services
{
    /// <summary>A brain the user can pick in the UI.</summary>
    /// <param name="Id">Stable identifier: <see cref="AgentBrainSelector.KeywordBrainId"/> or the model id.</param>
    /// <param name="DisplayName">Label shown in the picker.</param>
    /// <param name="IsLlm">True for brains backed by the LLM endpoint.</param>
    public sealed record BrainOption(string Id, string DisplayName, bool IsLlm);

    /// <summary>
    /// Holds the per-user choice of agent brain. Offers the rule-based keyword brain plus one
    /// brain per model served by the configured OpenAI-compatible endpoint, and falls back to
    /// the keyword brain when the endpoint is disabled or unreachable.
    /// </summary>
    public class AgentBrainSelector
    {
        public const string KeywordBrainId = "keyword";

        private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(5);

        private readonly KeywordAgentBrain _keywordBrain;
        private readonly OpenAiCompatibleClient _llmClient;
        private readonly LlmOptions _options;
        private readonly ILogger<AgentBrainSelector> _logger;
        private readonly Dictionary<string, IAgentBrain> _brains = new();
        private List<BrainOption> _choices = new();
        private bool _initialized;

        public AgentBrainSelector(
            KeywordAgentBrain keywordBrain,
            OpenAiCompatibleClient llmClient,
            IOptions<LlmOptions> options,
            ILogger<AgentBrainSelector> logger)
        {
            _keywordBrain = keywordBrain;
            _llmClient = llmClient;
            _options = options.Value;
            _logger = logger;

            _brains[KeywordBrainId] = keywordBrain;
            _choices.Add(KeywordOption);
            Current = keywordBrain;
        }

        /// <summary>The brain that handles the next prompt.</summary>
        public IAgentBrain Current { get; private set; }

        /// <summary>Id of <see cref="Current"/>.</summary>
        public string CurrentId { get; private set; } = KeywordBrainId;

        /// <summary>Brains the user can choose from; the keyword brain is always first.</summary>
        public IReadOnlyList<BrainOption> Options => _choices;

        /// <summary>
        /// Describes a problem with the LLM endpoint for the UI: null when models were listed
        /// successfully (or LLMs are disabled), otherwise why no LLM brains are offered.
        /// </summary>
        public string? LlmStatus { get; private set; }

        /// <summary>True when at least one LLM brain is available.</summary>
        public bool HasLlmBrains => _choices.Any(o => o.IsLlm);

        private BrainOption KeywordOption => new(KeywordBrainId, _keywordBrain.Name, IsLlm: false);

        /// <summary>
        /// Lists the endpoint's models and selects the configured default. Safe to call more than once;
        /// only the first call queries the endpoint.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            if (!_options.Enabled)
            {
                // Deliberately keyword-only; nothing to warn about.
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(ModelListTimeout);
                var models = await _llmClient.ListModelsAsync(timeout.Token);

                var usable = models
                    .Where(m => !_options.ExcludeModelPatterns.Any(p => m.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                _choices = new List<BrainOption> { KeywordOption };
                _choices.AddRange(usable.Select(m => new BrainOption(m, m, IsLlm: true)));

                LlmStatus = usable.Count == 0 ? $"No models found at {_llmClient.BaseAddress}." : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmException or System.Text.Json.JsonException)
            {
                LlmStatus = $"LLM endpoint {_llmClient.BaseAddress} is unreachable.";
                _logger.LogWarning(ex, "Could not list models from {BaseUrl}; only the keyword brain is available.", _llmClient.BaseAddress);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_options.DefaultModel))
            {
                if (_choices.Any(o => o.Id == _options.DefaultModel))
                {
                    Select(_options.DefaultModel);
                }
                else
                {
                    _logger.LogWarning("Default model {Model} is not served by {BaseUrl}; starting with the keyword brain.",
                        _options.DefaultModel, _llmClient.BaseAddress);
                }
            }
        }

        /// <summary>Switches to the brain with the given id. Unknown ids are ignored.</summary>
        /// <returns>True when the selection changed.</returns>
        public bool Select(string id)
        {
            if (id == CurrentId || !_choices.Any(o => o.Id == id))
            {
                return false;
            }

            if (!_brains.TryGetValue(id, out var brain))
            {
                brain = new OpenAiCompatibleAgentBrain(_llmClient, id, _options.DecisionMode, _options.Temperature);
                _brains[id] = brain;
            }

            Current = brain;
            CurrentId = id;
            return true;
        }
    }
}
