namespace ScanHarness.Models
{
    /// <summary>A user's working session in the harness: every request, tool call and log entry belongs to one.</summary>
    public class HarnessSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>User who started the session.</summary>
        public string UserName { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Time of the most recent activity in the session.</summary>
        public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

        /// <summary>The session's first request, used to label it in the session picker.</summary>
        public string? Title { get; set; }

        /// <summary>Number of tool calls in the session (filled in when sessions are listed).</summary>
        public int ToolCallCount { get; set; }

        /// <summary>Controls that tripped in Advise mode (warnings) in the session (filled in when listed).</summary>
        public int ControlWarningCount { get; set; }

        /// <summary>Controls that tripped in Block mode in the session (filled in when listed).</summary>
        public int ControlBlockCount { get; set; }

        /// <summary>The most severe control outcome in the session: "none", "warning" or "block".</summary>
        public string ControlSeverity => ControlBlockCount > 0 ? "block" : ControlWarningCount > 0 ? "warning" : "none";
    }

    /// <summary>A user who can be selected in the harness.</summary>
    /// <param name="Name">User name, stored on every session, tool call and log entry.</param>
    /// <param name="IsAdmin">Admins see every user's sessions, with each entry showing who performed it.</param>
    public sealed record HarnessUser(string Name, bool IsAdmin);

    /// <summary>
    /// The fixed set of selectable users. A placeholder until proper authentication replaces it,
    /// at which point the current user comes from the signed-in identity instead.
    /// </summary>
    public static class HarnessUsers
    {
        public static IReadOnlyList<HarnessUser> All { get; } = new[]
        {
            new HarnessUser("chris", IsAdmin: false),
            new HarnessUser("raccoon", IsAdmin: false),
            new HarnessUser("blob", IsAdmin: false),
            new HarnessUser("admin", IsAdmin: true),
        };

        /// <summary>The user selected when the page opens.</summary>
        public static HarnessUser Default => All[0];

        /// <summary>Finds a user by name (case-insensitive), or null.</summary>
        public static HarnessUser? Find(string? name) =>
            All.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
