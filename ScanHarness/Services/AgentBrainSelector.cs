using Microsoft.Extensions.Options;
using ScanHarness.Services.Llm;

namespace ScanHarness.Services
{
    /// <summary>A brain the user can pick in the UI.</summary>
    /// <param name="Id">Stable identifier: <see cref="AgentBrainSelector.KeywordBrainId"/> or the model id.</param>
    /// <param name="DisplayName">Label shown in the picker.</param>
    /// <param name="IsLlm">True for brains backed by the LLM endpoint.</param>
    /// <param name="Classification">Internal or external; null for the "Same as decision" summary choice.</param>
    public sealed record BrainOption(string Id, string DisplayName, bool IsLlm, Classification? Classification = null);

    /// <summary>
    /// Holds the per-user choice of agent brains: one that decides which tool to call and one that
    /// summarises each finished action (by default the same brain). Offers the rule-based keyword
    /// brain plus one brain per model served by the configured OpenAI-compatible endpoint, and falls
    /// back to the keyword brain when the endpoint is disabled or unreachable.
    /// </summary>
    public class AgentBrainSelector
    {
        public const string KeywordBrainId = "keyword";

        /// <summary>Summary-brain id meaning "use whichever brain made the decision".</summary>
        public const string SameAsDecisionId = "same";

        private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(5);

        private readonly KeywordAgentBrain _keywordBrain;
        private readonly OpenAiCompatibleClient _llmClient;
        private readonly LlmOptions _options;
        private readonly ILogger<AgentBrainSelector> _logger;
        private readonly Boundaries.ComponentPolicyService? _policies;
        private readonly Dictionary<string, IAgentBrain> _brains = new();
        private List<BrainOption> _choices = new();
        private bool _initialized;

        /// <param name="policies">Supplies each brain's classification, including Permissions tab edits; null uses the Llm config only.</param>
        public AgentBrainSelector(
            KeywordAgentBrain keywordBrain,
            OpenAiCompatibleClient llmClient,
            IOptions<LlmOptions> options,
            ILogger<AgentBrainSelector> logger,
            Boundaries.ComponentPolicyService? policies = null)
        {
            _keywordBrain = keywordBrain;
            _llmClient = llmClient;
            _options = options.Value;
            _logger = logger;
            _policies = policies;

            _brains[KeywordBrainId] = keywordBrain;
            _choices.Add(KeywordOption);
            Current = keywordBrain;
        }

        /// <summary>The brain that decides which tool to call for the next prompt.</summary>
        public IAgentBrain Current { get; private set; }

        /// <summary>Id of <see cref="Current"/>.</summary>
        public string CurrentId { get; private set; } = KeywordBrainId;

        /// <summary>Brains the user can choose from; the keyword brain is always first. Labels show each brain's current classification.</summary>
        public IReadOnlyList<BrainOption> Options => _choices.Select(Labelled).ToList();

        /// <summary>Id of <see cref="SummaryBrain"/> itself (never <see cref="SameAsDecisionId"/>).</summary>
        public string SummaryBrainKey => SummaryBrainId == SameAsDecisionId ? CurrentId : SummaryBrainId;

        /// <summary>Where a brain runs: its Permissions tab edit, else the Llm config.</summary>
        public Classification ClassificationOf(string id) =>
            id == KeywordBrainId ? _keywordBrain.Classification : _policies?.ClassificationOfBrain(id) ?? _options.ClassificationFor(id);

        private BrainOption Labelled(BrainOption option)
        {
            var classification = ClassificationOf(option.Id);
            var name = option.Id == KeywordBrainId ? _keywordBrain.Name : option.Id;
            return option with { DisplayName = $"{name} · {classification.Label()}", Classification = classification };
        }

        /// <summary>Id of the summary brain, or <see cref="SameAsDecisionId"/> to follow the decision brain.</summary>
        public string SummaryBrainId { get; private set; } = SameAsDecisionId;

        /// <summary>The brain that summarises finished actions.</summary>
        public IAgentBrain SummaryBrain => SummaryBrainId == SameAsDecisionId ? Current : GetOrCreateBrain(SummaryBrainId);

        /// <summary>Choices for the summary brain: "Same as decision" followed by every brain.</summary>
        public IReadOnlyList<BrainOption> SummaryOptions =>
            new[] { new BrainOption(SameAsDecisionId, "Same as decision", IsLlm: false) }.Concat(Options).ToList();

        /// <summary>
        /// Describes a problem with the LLM endpoint for the UI: null when models were listed
        /// successfully (or LLMs are disabled), otherwise why no LLM brains are offered.
        /// </summary>
        public string? LlmStatus { get; private set; }

        /// <summary>True when at least one LLM brain is available.</summary>
        public bool HasLlmBrains => _choices.Any(o => o.IsLlm);

        private BrainOption KeywordOption =>
            new(KeywordBrainId, $"{_keywordBrain.Name} · {_keywordBrain.Classification.Label()}", IsLlm: false, _keywordBrain.Classification);

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
                _choices.AddRange(usable.Select(m =>
                {
                    var classification = _options.ClassificationFor(m);
                    return new BrainOption(m, $"{m} · {classification.Label()}", IsLlm: true, classification);
                }));

                LlmStatus = usable.Count == 0 ? $"No models found at {_llmClient.BaseAddress}." : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmException or System.Text.Json.JsonException)
            {
                LlmStatus = $"LLM endpoint {_llmClient.BaseAddress} is unreachable.";
                _logger.LogWarning(ex, "Could not list models from {BaseUrl}; only the keyword brain is available.", _llmClient.BaseAddress);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_options.DefaultModel) && !Select(_options.DefaultModel) && CurrentId != _options.DefaultModel)
            {
                _logger.LogWarning("Default model {Model} is not served by {BaseUrl}; starting with the keyword brain.",
                    _options.DefaultModel, _llmClient.BaseAddress);
            }

            if (!string.IsNullOrWhiteSpace(_options.DefaultSummaryModel) && !SelectSummary(_options.DefaultSummaryModel)
                && SummaryBrainId != _options.DefaultSummaryModel)
            {
                _logger.LogWarning("Default summary model {Model} is not served by {BaseUrl}; summaries use the decision brain.",
                    _options.DefaultSummaryModel, _llmClient.BaseAddress);
            }
        }

        /// <summary>Switches the decision brain to the brain with the given id. Unknown ids are ignored.</summary>
        /// <returns>True when the selection changed.</returns>
        public bool Select(string id)
        {
            if (id == CurrentId || !_choices.Any(o => o.Id == id))
            {
                return false;
            }

            Current = GetOrCreateBrain(id);
            CurrentId = id;
            return true;
        }

        /// <summary>
        /// Switches the summary brain to the brain with the given id, or to <see cref="SameAsDecisionId"/>.
        /// Unknown ids are ignored.
        /// </summary>
        /// <returns>True when the selection changed.</returns>
        public bool SelectSummary(string id)
        {
            if (id == SummaryBrainId || (id != SameAsDecisionId && !_choices.Any(o => o.Id == id)))
            {
                return false;
            }

            SummaryBrainId = id;
            return true;
        }

        // One instance per brain, shared by the decision and summary roles.
        private IAgentBrain GetOrCreateBrain(string id)
        {
            if (!_brains.TryGetValue(id, out var brain))
            {
                brain = new OpenAiCompatibleAgentBrain(_llmClient, id, _options.DecisionMode, _options.Temperature,
                    classification: _options.ClassificationFor(id), classificationProvider: () => ClassificationOf(id));
                _brains[id] = brain;
            }

            return brain;
        }
    }
}
