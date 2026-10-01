using System.Text.Json.Serialization;

namespace ScanHarness.Services.Boundaries
{
    /// <summary>
    /// How sensitive a piece of data is, lowest first. Data may only be passed to a brain, tool or agent
    /// whose clearance is at least the data's label (see docs/design/multi-agent-boundaries.md).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Sensitivity
    {
        /// <summary>Nothing about the customer: tool names, generic advice, weather.</summary>
        Public,

        /// <summary>About the engagement but not identifying: counts, severities, durations, pseudonymised hosts.</summary>
        Engagement,

        /// <summary>Identifies the customer or their systems: hostnames, URLs, findings, scope, prices.</summary>
        CustomerConfidential,

        /// <summary>Credentials, tokens and similar; never placed in a prompt.</summary>
        Secret
    }

    /// <summary>How far a component's output is believed.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Trust
    {
        /// <summary>Deterministic code whose output can drive actions directly.</summary>
        Trusted,

        /// <summary>
        /// Model output, customer input or anything a scanned target controls. It can be shown, but can't
        /// cause an action on its own: it must pass validation, deterministic checks and, where it matters, approval.
        /// </summary>
        Untrusted
    }

    public static class SensitivityExtensions
    {
        /// <summary>Display name, e.g. "customer confidential".</summary>
        public static string Label(this Sensitivity sensitivity) => sensitivity switch
        {
            Sensitivity.CustomerConfidential => "customer confidential",
            _ => sensitivity.ToString().ToLowerInvariant()
        };

        public static string Label(this Trust trust) => trust.ToString().ToLowerInvariant();

        /// <summary>The clearance a component gets when nothing more specific is configured.</summary>
        public static Sensitivity DefaultClearance(this Classification classification) =>
            classification == Classification.Internal ? Sensitivity.CustomerConfidential : Sensitivity.Engagement;
    }
}
