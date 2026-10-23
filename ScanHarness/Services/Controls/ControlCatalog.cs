using System.Text.Json.Serialization;

namespace ScanHarness.Services.Controls
{
    /// <summary>Where in the flow a control is applied.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ControlStage
    {
        /// <summary>The user's message, before it is passed to the brain.</summary>
        ChatIn,

        /// <summary>Output from the brain: its reply and the summaries it writes.</summary>
        ChatOut,

        /// <summary>A tool call the brain chose, before the tool is executed.</summary>
        ToolRule,

        /// <summary>
        /// A tool's or agent's result, before it is passed to any brain. Tool output can be influenced by the
        /// target, so it is untrusted input. Block keeps it away from model brains: the rule-based summary is used.
        /// </summary>
        ToolOut,

        /// <summary>
        /// A network connection a tool tried to make through the egress proxy. Its rules live in egress.json, not
        /// controls.json, and are always Block: a refused connection is reported here so it gets a pip like any control.
        /// </summary>
        Egress
    }

    /// <summary>What a control does when it matches.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ControlMode
    {
        /// <summary>No action.</summary>
        Off,

        /// <summary>Flag the message (orange pip) and log a Warning, but let it through.</summary>
        Advise,

        /// <summary>Flag the message (red pip), log a Block, and stop it: input isn't sent to the
        /// brain, output is withheld, and a tool call isn't executed.</summary>
        Block
    }

    /// <summary>How a control's rule matches text.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ControlRuleType
    {
        /// <summary>The text contains <see cref="ControlRule.Value"/> literally.</summary>
        Contains,

        /// <summary>The text matches the .NET regular expression in <see cref="ControlRule.Value"/>.</summary>
        Regex
    }

    /// <summary>Which brains or tools a control is applied to, by their classification.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ControlScope
    {
        /// <summary>Internal and external alike.</summary>
        Any,

        /// <summary>Only internal brains or tools.</summary>
        Internal,

        /// <summary>Only external brains or tools.</summary>
        External
    }

    /// <summary>What a control looks for.</summary>
    public sealed class ControlRule
    {
        /// <summary>How long a regex may run on one message before the control gives up (and counts it as a match).</summary>
        public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);

        private readonly System.Text.RegularExpressions.Regex? _regex;

        /// <param name="type">Plain-text contains, or a regular expression.</param>
        /// <param name="value">The text or pattern to look for.</param>
        /// <param name="caseSensitive">Whether letter case must match (default: no).</param>
        /// <param name="regexTimeout">Limit for one regex match; defaults to <see cref="DefaultRegexTimeout"/>.</param>
        /// <exception cref="ArgumentException">The value is empty or the pattern isn't a valid regex.</exception>
        public ControlRule(ControlRuleType type, string value, bool caseSensitive = false, TimeSpan? regexTimeout = null)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("A control rule needs a value to look for.", nameof(value));
            }

            Type = type;
            Value = value;
            CaseSensitive = caseSensitive;

            if (type == ControlRuleType.Regex)
            {
                var options = System.Text.RegularExpressions.RegexOptions.CultureInvariant
                    | (caseSensitive ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                try
                {
                    _regex = new System.Text.RegularExpressions.Regex(value, options, regexTimeout ?? DefaultRegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"\"{value}\" is not a valid regular expression: {ex.Message}", nameof(value), ex);
                }
            }
        }

        public ControlRuleType Type { get; }

        public string Value { get; }

        public bool CaseSensitive { get; }

        /// <summary>Describes what matched (e.g. Found "&lt;script&gt;" at character 17), or null if the text passes.</summary>
        public string? Match(string text)
        {
            if (_regex == null)
            {
                var index = text.IndexOf(Value, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                return index < 0 ? null : $"Found \"{text.Substring(index, Value.Length)}\" at character {index + 1}.";
            }

            try
            {
                var match = _regex.Match(text);
                return match.Success ? $"Matched \"{match.Value}\" at character {match.Index + 1}." : null;
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // A guardrail must not let text through just because it was expensive to check: fail closed.
                return $"The rule took too long to check this text (over {_regex.MatchTimeout.TotalMilliseconds:0} ms), so it counts as a match.";
            }
        }
    }

    /// <summary>A guardrail that checks text at one stage of the flow.</summary>
    /// <param name="Id">Stable identifier, used to store each user's setting.</param>
    /// <param name="Stage">Where the control is applied.</param>
    /// <param name="Name">Short name shown on the Controls page and in pips and the log.</param>
    /// <param name="Description">What the control checks, shown on the Controls page.</param>
    /// <param name="DefaultMode">Mode used until a user changes it.</param>
    /// <param name="Rule">What the control looks for.</param>
    /// <param name="AppliesTo">
    /// Which subjects the control checks: for Chat In, the brain the message is sent to; for Chat Out,
    /// the brain that produced the output; for ToolRule, the tool about to run.
    /// </param>
    public sealed record ControlDefinition(
        string Id,
        ControlStage Stage,
        string Name,
        string Description,
        ControlMode DefaultMode,
        ControlRule Rule,
        ControlScope AppliesTo = ControlScope.Any)
    {
        /// <summary>Describes what matched, or null if the text passes.</summary>
        public string? Match(string text) => Rule.Match(text);

        /// <summary>
        /// Whether the control applies to a subject with this classification. An unknown classification
        /// (null) is always checked, so a scoped guardrail fails closed rather than being skipped.
        /// </summary>
        public bool AppliesToSubject(Classification? subject) => AppliesTo switch
        {
            ControlScope.Internal => subject != Classification.External,
            ControlScope.External => subject != Classification.Internal,
            _ => true
        };
    }

    /// <summary>
    /// Every control the harness offers, read from a JSON file (<c>controls.json</c> by default; see
    /// <see cref="Load"/> for the format). Add a control to the file and it appears on the Controls page
    /// and is applied at its stage. The app loads the configured file at startup with <see cref="Use"/>;
    /// until then (e.g. in unit tests) the <c>controls.json</c> next to the app is used.
    /// </summary>
    public static class ControlCatalog
    {
        /// <summary>The file name used when no path is configured.</summary>
        public const string DefaultFileName = "controls.json";

        private static IReadOnlyList<ControlDefinition>? _all;

        public static IReadOnlyList<ControlDefinition> All =>
            _all ??= Load(Path.Combine(AppContext.BaseDirectory, DefaultFileName));

        /// <summary>Replaces the catalog, e.g. with the file configured for the app.</summary>
        public static void Use(IReadOnlyList<ControlDefinition> controls) => _all = controls;

        /// <summary>Controls applied at <paramref name="stage"/>, in file order.</summary>
        public static IEnumerable<ControlDefinition> ForStage(ControlStage stage) => All.Where(c => c.Stage == stage);

        /// <summary>Finds a control by id, or null.</summary>
        public static ControlDefinition? Find(string id) => All.FirstOrDefault(c => c.Id == id);

        /// <summary>Stages controls.json may use (Egress rules are defined in egress.json).</summary>
        public static IReadOnlyList<ControlStage> ConfigurableStages { get; } =
            Enum.GetValues<ControlStage>().Where(s => s != ControlStage.Egress).ToList();

        /// <summary>Display name of a stage, e.g. "Chat In".</summary>
        public static string StageName(ControlStage stage) => stage switch
        {
            ControlStage.ChatIn => "Chat In",
            ControlStage.ChatOut => "Chat Out",
            ControlStage.ToolRule => "ToolRule",
            ControlStage.ToolOut => "ToolOut",
            ControlStage.Egress => "Egress",
            _ => stage.ToString()
        };

        /// <summary>What a control's scope covers at its stage, e.g. "external brains" or "all tools".</summary>
        public static string ScopeLabel(ControlDefinition control)
        {
            var subject = control.Stage == ControlStage.ToolRule ? "tools" : "brains";
            return control.AppliesTo switch
            {
                ControlScope.Internal => $"internal {subject}",
                ControlScope.External => $"external {subject}",
                _ => $"all {subject}"
            };
        }

        /// <summary>Reads control definitions from a JSON file.</summary>
        /// <exception cref="ControlCatalogException">The file is missing or invalid; the message says what to fix.</exception>
        public static IReadOnlyList<ControlDefinition> Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new ControlCatalogException($"Control definitions file not found: {path}");
            }

            return Parse(File.ReadAllText(path), path);
        }

        /// <summary>
        /// Parses control definitions:
        /// <code>
        /// { "controls": [ { "id": "...", "stage": "ChatIn|ChatOut|ToolRule", "name": "...", "description": "...",
        ///                   "defaultMode": "Off|Advise|Block", "appliesTo": "Any|Internal|External" (optional, default Any),
        ///                   "rule": { "type": "contains|regex", "value": "...", "caseSensitive": false } } ] }
        /// </code>
        /// Every problem found is reported together, so a broken file can be fixed in one go.
        /// </summary>
        /// <param name="source">Where the JSON came from, for error messages.</param>
        public static IReadOnlyList<ControlDefinition> Parse(string json, string source = "controls")
        {
            ControlFile? file;
            try
            {
                file = System.Text.Json.JsonSerializer.Deserialize<ControlFile>(json, JsonOptions);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ControlCatalogException($"{source} is not valid control JSON: {ex.Message}");
            }

            var errors = new List<string>();
            var controls = new List<ControlDefinition>();
            var entries = file?.Controls ?? new List<ControlEntry>();

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var label = string.IsNullOrWhiteSpace(entry.Id) ? $"control #{i + 1}" : $"control \"{entry.Id}\"";
                var before = errors.Count;

                if (string.IsNullOrWhiteSpace(entry.Id)) errors.Add($"{label}: \"id\" is required.");
                else if (controls.Any(c => c.Id == entry.Id) || entries.Take(i).Any(e => e.Id == entry.Id)) errors.Add($"{label}: the id is used more than once.");
                if (string.IsNullOrWhiteSpace(entry.Name)) errors.Add($"{label}: \"name\" is required.");
                if (entry.Stage == null || entry.Stage == ControlStage.Egress)
                    errors.Add($"{label}: \"stage\" must be one of {string.Join(", ", ConfigurableStages)}" +
                               (entry.Stage == ControlStage.Egress ? " (network rules belong in egress.json)." : "."));
                if (entry.DefaultMode == null) errors.Add($"{label}: \"defaultMode\" must be one of {string.Join(", ", Enum.GetNames<ControlMode>())}.");
                var scope = ControlScope.Any;
                if (entry.AppliesTo != null && (!Enum.TryParse(entry.AppliesTo, ignoreCase: true, out scope) || !Enum.IsDefined(scope)))
                    errors.Add($"{label}: \"appliesTo\" must be one of {string.Join(", ", Enum.GetNames<ControlScope>())}.");
                if (entry.Rule?.Type == null) errors.Add($"{label}: \"rule.type\" must be \"contains\" or \"regex\".");

                ControlRule? rule = null;
                if (entry.Rule?.Type != null)
                {
                    try
                    {
                        rule = new ControlRule(entry.Rule.Type.Value, entry.Rule.Value ?? "", entry.Rule.CaseSensitive ?? false);
                    }
                    catch (ArgumentException ex)
                    {
                        errors.Add($"{label}: {ex.Message.Split(" (Parameter")[0]}");
                    }
                }

                if (errors.Count == before)
                {
                    controls.Add(new ControlDefinition(entry.Id!, entry.Stage!.Value, entry.Name!, entry.Description ?? "", entry.DefaultMode!.Value, rule!, scope));
                }
            }

            if (errors.Count > 0)
            {
                throw new ControlCatalogException($"{source} has {errors.Count} problem{(errors.Count == 1 ? "" : "s")}:\n- {string.Join("\n- ", errors)}");
            }

            return controls;
        }

        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        };

        // The file's shape. Everything is nullable so missing values are reported, not defaulted.
        private sealed class ControlFile
        {
            public List<ControlEntry>? Controls { get; set; }
        }

        private sealed class ControlEntry
        {
            public string? Id { get; set; }
            public ControlStage? Stage { get; set; }
            public string? Name { get; set; }
            public string? Description { get; set; }
            public ControlMode? DefaultMode { get; set; }

            // A string, so a missing value defaults to Any while a bad one is reported rather than failing the whole file.
            public string? AppliesTo { get; set; }
            public RuleEntry? Rule { get; set; }
        }

        private sealed class RuleEntry
        {
            public ControlRuleType? Type { get; set; }
            public string? Value { get; set; }
            public bool? CaseSensitive { get; set; }
        }
    }

    /// <summary>The control definitions file is missing or invalid.</summary>
    public sealed class ControlCatalogException : Exception
    {
        public ControlCatalogException(string message) : base(message) { }
    }
}
