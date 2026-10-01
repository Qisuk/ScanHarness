namespace ScanHarness.Models
{
    public enum EngagementStatus
    {
        /// <summary>Validated and waiting for a person to approve it.</summary>
        Proposed,

        /// <summary>Signed off: scans and attacks may run against its domains inside its window.</summary>
        Approved,

        /// <summary>Turned down; kept for the record.</summary>
        Rejected
    }

    /// <summary>
    /// An agreed piece of work for a customer: which domains may be tested, when, and under which rules.
    /// Created from a scope proposal once code has validated it; only an approved engagement lets the
    /// scanner and attack tools run, and only against its domains. Approved engagements are never edited:
    /// a change of scope is a new proposal.
    /// </summary>
    public class Engagement
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Customer { get; set; } = string.Empty;

        /// <summary>Domains in scope; subdomains of each are in scope too.</summary>
        public List<string> Domains { get; set; } = new();

        /// <summary>First and last day testing is allowed (inclusive, UTC); null means not agreed, which blocks testing.</summary>
        public DateOnly? WindowStart { get; set; }

        public DateOnly? WindowEnd { get; set; }

        /// <summary>Rules of engagement agreed with the customer.</summary>
        public List<string> Rules { get; set; } = new();

        /// <summary>Why domains the customer asked for were left out (e.g. ownership not verified).</summary>
        public List<string> Excluded { get; set; } = new();

        public EngagementStatus Status { get; set; } = EngagementStatus.Proposed;

        public string ProposedBy { get; set; } = string.Empty;

        public DateTime ProposedAt { get; set; } = DateTime.UtcNow;

        public string? DecidedBy { get; set; }

        public DateTime? DecidedAt { get; set; }

        /// <summary>1 for a customer's first engagement, then 2, 3… as scopes are re-proposed.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Short label, e.g. "Acme v2 (approved)".</summary>
        public string Label => $"{Customer} v{Version}";
    }
}
