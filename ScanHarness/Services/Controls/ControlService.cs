using System.Text.Json;
using ScanHarness.Services.Persistence;

namespace ScanHarness.Services.Controls
{
    /// <summary>A control that matched some text.</summary>
    /// <param name="ControlId">Which control matched.</param>
    /// <param name="Name">The control's name.</param>
    /// <param name="Stage">Where it was applied.</param>
    /// <param name="Mode">Advise or Block (a control that is Off never matches).</param>
    /// <param name="Target">What was checked, e.g. "request", "reply", "summary", "session summary" or "tool call".</param>
    /// <param name="Detail">What matched, e.g. Found "&lt;script&gt;" at character 17.</param>
    /// <param name="Text">The text that was checked (truncated), so reports can show the message that tripped the
    /// control. Null for hits recorded before this was stored.</param>
    /// <param name="Subject">Classification of the brain or tool the check was for (see <see cref="ControlDefinition.AppliesTo"/>);
    /// null when unknown, including hits recorded before this was stored.</param>
    public sealed record ControlViolation(string ControlId, string Name, ControlStage Stage, ControlMode Mode, string Target, string Detail,
        string? Text = null, Classification? Subject = null)
    {
        public string StageName => ControlCatalog.StageName(Stage);

        /// <summary>Describes what was checked, e.g. "request to an external brain" or "tool call to an internal tool".</summary>
        public string TargetDescription
        {
            get
            {
                if (Subject is not Classification subject)
                {
                    return Target;
                }

                var noun = Stage == ControlStage.ToolRule ? "tool" : "brain";
                var preposition = Stage switch
                {
                    ControlStage.ChatIn or ControlStage.ToolOut => "to",
                    ControlStage.ChatOut => "from",
                    _ => "for"
                };
                var article = subject == Classification.Internal ? "an internal" : "an external";
                return $"{Target} {preposition} {article} {noun}";
            }
        }

        /// <summary>Longest checked text kept with a violation.</summary>
        public const int MaxTextLength = 500;
    }

    /// <summary>The outcome of checking some text at one stage.</summary>
    public sealed record ControlCheck(IReadOnlyList<ControlViolation> Violations)
    {
        public static ControlCheck Passed { get; } = new(Array.Empty<ControlViolation>());

        /// <summary>At least one control matched.</summary>
        public bool IsFlagged => Violations.Count > 0;

        /// <summary>At least one matching control is set to Block.</summary>
        public bool IsBlocked => Violations.Any(v => v.Mode == ControlMode.Block);
    }

    /// <summary>
    /// Applies the controls in <see cref="ControlCatalog"/> using the current user's settings, and
    /// changes and saves those settings. Settings are per user; controls a user hasn't changed use
    /// their default mode.
    /// </summary>
    public class ControlService
    {
        private readonly SessionState _session;
        private readonly IActivityStore? _store;
        private readonly IReadOnlyList<ControlDefinition>? _controls;
        private readonly Dictionary<string, Dictionary<string, ControlMode>> _settingsByUser = new();

        /// <param name="store">Where settings are saved; null keeps them in memory (e.g. in tests).</param>
        /// <param name="controls">Controls to apply; null uses <see cref="ControlCatalog.All"/> (tests pass their own).</param>
        public ControlService(SessionState session, IActivityStore? store = null, IReadOnlyList<ControlDefinition>? controls = null)
        {
            _session = session;
            _store = store;
            _controls = controls;
        }

        private IReadOnlyList<ControlDefinition> Controls => _controls ?? ControlCatalog.All;

        /// <summary>Raised when a setting changes.</summary>
        public event Action? OnChanged;

        /// <summary>The current user's mode for a control.</summary>
        public ControlMode GetMode(ControlDefinition control) =>
            Settings().TryGetValue(control.Id, out var mode) ? mode : control.DefaultMode;

        /// <summary>Sets and saves the current user's mode for a control.</summary>
        /// <returns>True when the mode changed.</returns>
        public bool SetMode(string controlId, ControlMode mode)
        {
            var control = Controls.FirstOrDefault(c => c.Id == controlId);
            if (control == null || GetMode(control) == mode)
            {
                return false;
            }

            Settings()[controlId] = mode;
            _store?.SaveControlSetting(_session.CurrentUser.Name, controlId, mode, DateTime.UtcNow);
            OnChanged?.Invoke();
            return true;
        }

        /// <summary>Checks <paramref name="text"/> against every enabled control at <paramref name="stage"/>.</summary>
        /// <param name="target">What the text is, for the log and pips (e.g. "request").</param>
        /// <param name="subject">
        /// Classification of the brain the text is sent to (Chat In), the brain that wrote it (Chat Out) or the
        /// tool about to run (ToolRule). Controls scoped to the other classification are skipped; null checks every control.
        /// </param>
        public ControlCheck Check(ControlStage stage, string target, string? text, Classification? subject = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return ControlCheck.Passed;
            }

            var violations = new List<ControlViolation>();
            foreach (var control in Controls.Where(c => c.Stage == stage))
            {
                var mode = GetMode(control);
                if (mode == ControlMode.Off || !control.AppliesToSubject(subject))
                {
                    continue;
                }

                var detail = control.Match(text);
                if (detail != null)
                {
                    var checkedText = text.Length <= ControlViolation.MaxTextLength ? text : text[..ControlViolation.MaxTextLength] + "…";
                    violations.Add(new ControlViolation(control.Id, control.Name, stage, mode, target, detail, checkedText, subject));
                }
            }

            return violations.Count == 0 ? ControlCheck.Passed : new ControlCheck(violations);
        }

        // Loaded from the store the first time each user's settings are needed.
        private Dictionary<string, ControlMode> Settings()
        {
            var user = _session.CurrentUser.Name;
            if (!_settingsByUser.TryGetValue(user, out var settings))
            {
                settings = _store?.LoadControlSettings(user).ToDictionary(kv => kv.Key, kv => kv.Value) ?? new();
                _settingsByUser[user] = settings;
            }

            return settings;
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>Serialises violations for a log entry's data (read back by the chat transcript).</summary>
        public static string ToJson(IEnumerable<ControlViolation> violations) => JsonSerializer.Serialize(violations, JsonOptions);

        /// <summary>Reads violations written by <see cref="ToJson"/>; malformed data gives an empty list.</summary>
        public static IReadOnlyList<ControlViolation> FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<ControlViolation>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<ControlViolation>>(json, JsonOptions) ?? new List<ControlViolation>();
            }
            catch (JsonException)
            {
                return Array.Empty<ControlViolation>();
            }
        }
    }
}
