using System.Text.Json.Serialization;

namespace ScanHarness.Services
{
    /// <summary>
    /// Whether a brain or tool is internal (runs on infrastructure you control, e.g. a local model or
    /// the demo tool API) or external (a third-party service data is sent to). Controls can be scoped
    /// to one or the other.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Classification
    {
        Internal,
        External
    }

    public static class ClassificationExtensions
    {
        /// <summary>"internal" or "external", for labels and messages.</summary>
        public static string Label(this Classification classification) =>
            classification == Classification.Internal ? "internal" : "external";

        /// <summary>
        /// Classifies an endpoint by its address: loopback hosts (localhost, 127.0.0.1, ::1) are internal,
        /// anything else external.
        /// </summary>
        public static Classification ForEndpoint(Uri? endpoint) =>
            endpoint != null && endpoint.IsLoopback ? Classification.Internal : Classification.External;
    }
}
