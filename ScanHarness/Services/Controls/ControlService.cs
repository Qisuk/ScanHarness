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
    public sealed record ControlViolation(string ControlId, string Name, ControlStage Stage, ControlMode Mode, string Target, string Detail)
    {
        public string StageName => ControlCatalog.StageName(Stage);
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
        private readonly Dictionary<string, Dictionary<string, ControlMode>> _settingsByUser = new();

        /// <param name="store">Where settings are saved; null keeps them in memory (e.g. in tests).</param>
        public ControlService(SessionState session, IActivityStore? store = null)
        {
            _session = session;
            _store = store;
        }

        /// <summary>Raised when a setting changes.</summary>
        public event Action? OnChanged;

        /// <summary>The current user's mode for a control.</summary>
        public ControlMode GetMode(ControlDefinition control) =>
            Settings().TryGetValue(control.Id, out var mode) ? mode : control.DefaultMode;

        /// <summary>Sets and saves the current user's mode for a control.</summary>
        /// <returns>True when the mode changed.</returns>
        public bool SetMode(string controlId, ControlMode mode)
        {
            var control = ControlCatalog.Find(controlId);
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
        public ControlCheck Check(ControlStage stage, string target, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return ControlCheck.Passed;
            }

            var violations = new List<ControlViolation>();
            foreach (var control in ControlCatalog.ForStage(stage))
            {
                var mode = GetMode(control);
                if (mode == ControlMode.Off)
                {
                    continue;
                }

                var detail = control.Match(text);
                if (detail != null)
                {
                    violations.Add(new ControlViolation(control.Id, control.Name, stage, mode, target, detail));
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
