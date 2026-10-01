using System.Globalization;
using System.Net;
using ScanHarness.Models;
using ScanHarness.Services.Agents;

namespace ScanHarness.Services.Engagements
{
    /// <summary>Settings for engagements and scope (config section "Engagements").</summary>
    public class EngagementOptions
    {
        public const string SectionName = "Engagements";

        /// <summary>
        /// When true (the default), tools marked requiresScope only run against a domain in the session's
        /// approved, in-window engagement. Turn off only for demos and tests.
        /// </summary>
        public bool EnforceScope { get; set; } = true;

        /// <summary>
        /// Domains whose ownership counts as verified. Simulated for the demo, like the tools: a real
        /// deployment would check a DNS TXT record or a well-known file instead. Subdomains count too.
        /// </summary>
        public List<string> VerifiedDomains { get; set; } = new();
    }

    /// <summary>The result of checking a scope proposal.</summary>
    /// <param name="Domains">Domains that passed every check.</param>
    /// <param name="Excluded">Domains left out, each with the reason.</param>
    /// <param name="Problems">Anything else that stops the proposal being submitted.</param>
    /// <param name="Warnings">Things the approver should know but that don't stop submission.</param>
    /// <param name="Unverified">Domains left out only because their ownership isn't verified yet (normalised).</param>
    public sealed record ScopeValidation(IReadOnlyList<string> Domains, IReadOnlyList<string> Excluded, IReadOnlyList<string> Problems,
        IReadOnlyList<string> Warnings, IReadOnlyList<string>? Unverified = null)
    {
        public bool CanSubmit => Problems.Count == 0;

        public IReadOnlyList<string> UnverifiedDomains => Unverified ?? Array.Empty<string>();
    }

    /// <summary>
    /// Checks a scope proposal in code before a person sees it. The scoping agent's output is untrusted (it
    /// came from a customer conversation), so nothing it proposes is accepted on its say-so.
    /// </summary>
    public static class ScopeValidator
    {
        /// <summary>The reason given for a domain whose ownership hasn't been verified.</summary>
        public const string NotVerified = "ownership not verified";

        private static readonly string[] PrivateSuffixes = { ".local", ".internal", ".lan", ".localhost", ".home", ".corp" };

        public static ScopeValidation Validate(ScopeProposal proposal, IReadOnlyCollection<string> verifiedDomains)
        {
            var domains = new List<string>();
            var excluded = new List<string>();
            var problems = new List<string>();
            var warnings = new List<string>();
            var unverified = new List<string>();

            if (string.IsNullOrWhiteSpace(proposal.Customer))
                problems.Add("The customer's name hasn't been given.");

            foreach (var raw in proposal.Domains.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var reason = Check(raw, verifiedDomains, out var domain);
                if (reason == NotVerified && !unverified.Contains(domain, StringComparer.OrdinalIgnoreCase))
                    unverified.Add(domain);
                if (reason != null)
                    excluded.Add($"{raw}: {reason}");
                else if (!domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                    domains.Add(domain);
            }

            if (domains.Count == 0)
                problems.Add("No domain passed the checks, so there is nothing to test.");

            if (proposal.WindowStart == null || proposal.WindowEnd == null)
                problems.Add("The testing window (start and end dates) hasn't been agreed.");
            else if (proposal.WindowEnd < proposal.WindowStart)
                problems.Add("The testing window ends before it starts.");
            else if (proposal.WindowEnd.Value.DayNumber - proposal.WindowStart.Value.DayNumber > 90)
                warnings.Add("The testing window is longer than 90 days.");

            if (proposal.Rules.Count == 0)
                warnings.Add("No rules of engagement were stated.");

            return new ScopeValidation(domains, excluded, problems, warnings, unverified);
        }

        /// <summary>Normalises a domain and returns why it can't be in scope, or null if it can.</summary>
        internal static string? Check(string raw, IReadOnlyCollection<string> verifiedDomains, out string domain)
        {
            domain = Normalise(raw);
            if (domain.Contains('*'))
                return "wildcards aren't allowed; list each domain";
            if (IPAddress.TryParse(domain, out _))
                return "IP addresses aren't accepted; use a domain name";
            if (Uri.CheckHostName(domain) != UriHostNameType.Dns || !domain.Contains('.'))
                return "not a valid domain name";
            var candidate = domain;
            if (PrivateSuffixes.Any(s => candidate.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                return "private or internal names aren't accepted";
            if (!verifiedDomains.Any(v => ScopeGuard.IsWithin(candidate, v)))
                return NotVerified;
            return null;
        }

        /// <summary>"https://www.Example.com/path" → "www.example.com".</summary>
        public static string Normalise(string raw)
        {
            var text = raw.Trim().TrimEnd('.', ',', ';');
            if (Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out var uri) && !text.Contains('*'))
                text = uri.Host;
            return text.ToLowerInvariant();
        }
    }

    /// <summary>
    /// The deterministic scope check that runs before a tool marked requiresScope. It lives in code, not a
    /// prompt, so no model output can widen what may be tested.
    /// </summary>
    public static class ScopeGuard
    {
        /// <summary>Why <paramref name="url"/> may not be tested under <paramref name="engagement"/> today, or null if it may.</summary>
        public static string? Violation(Engagement? engagement, string? url, DateOnly today)
        {
            if (engagement == null)
                return "No engagement is selected for this session, so nothing is in scope. Choose an approved engagement.";
            if (engagement.Status != EngagementStatus.Approved)
                return $"Engagement {engagement.Label} is {engagement.Status.ToString().ToLowerInvariant()}, not approved.";
            if (engagement.WindowStart == null || engagement.WindowEnd == null || today < engagement.WindowStart || today > engagement.WindowEnd)
                return $"Today ({today.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}) is outside engagement {engagement.Label}'s testing window ({Window(engagement)}).";
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "The target isn't a valid URL.";
            if (!engagement.Domains.Any(d => IsWithin(uri.Host, d)))
                return $"{uri.Host} isn't in engagement {engagement.Label}'s scope ({string.Join(", ", engagement.Domains)}).";
            return null;
        }

        /// <summary>True when <paramref name="host"/> is <paramref name="domain"/> or one of its subdomains.</summary>
        public static bool IsWithin(string host, string domain)
        {
            host = host.TrimEnd('.').ToLowerInvariant();
            domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
            return domain.Length > 0 && (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
        }

        public static string Window(Engagement engagement) =>
            engagement.WindowStart == null || engagement.WindowEnd == null
                ? "not agreed"
                : $"{engagement.WindowStart.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} to {engagement.WindowEnd.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
    }
}
