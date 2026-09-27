namespace DemoToolAPI_1.Models
{
    /// <summary>
    /// Response model for Attack endpoint
    /// </summary>
    public class AttackResponse
    {
        /// <summary>
        /// Status of the attack simulation
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Message describing the attack result
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Severity level of findings (if any)
        /// </summary>
        public string Severity { get; set; } = string.Empty;

        /// <summary>
        /// Number of vulnerabilities found
        /// </summary>
        public int VulnerabilitiesFound { get; set; }

        /// <summary>
        /// Additional details about the attack
        /// </summary>
        public Dictionary<string, object> Details { get; set; } = new();
    }
}
