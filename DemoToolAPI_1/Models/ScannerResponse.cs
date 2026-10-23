namespace DemoToolAPI_1.Models
{
    /// <summary>
    /// Response model for SimpleScanner endpoint
    /// </summary>
    public class ScannerResponse
    {
        /// <summary>
        /// Status of the scan operation
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Message describing the result
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// List of links found during the scan
        /// </summary>
        public List<LinkInfo> Links { get; set; } = new();
    }

    /// <summary>
    /// Represents a link found during scanning
    /// </summary>
    public class LinkInfo
    {
        /// <summary>
        /// The display text of the link
        /// </summary>
        public string LinkText { get; set; } = string.Empty;

        /// <summary>
        /// The URL of the link
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Live mode only: what happened when the scanner followed the link, e.g. "reachable (200)" or
        /// "refused by the egress proxy". Left out of canned responses.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; }
    }
}
