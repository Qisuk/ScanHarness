using Microsoft.Extensions.Options;
using ScanHarness.Models;
using ScanHarness.Services.Agents;
using ScanHarness.Services.Persistence;

namespace ScanHarness.Services.Engagements
{
    /// <summary>
    /// Engagements for one Blazor circuit: proposing a validated scope, approving or rejecting it, and which
    /// approved engagement the current session works under (the scope check uses it).
    /// </summary>
    public class EngagementService
    {
        private readonly IActivityStore? _store;
        private readonly SessionState _session;
        private readonly List<Engagement> _memory = new();
        private readonly List<string> _verifiedInMemory = new();

        public EngagementService(SessionState session, IOptions<EngagementOptions>? options = null, IActivityStore? store = null)
        {
            _session = session;
            _store = store;
            Options = options?.Value ?? new EngagementOptions();
        }

        public EngagementOptions Options { get; }

        /// <summary>Raised when an engagement is proposed, decided or selected.</summary>
        public event Action? OnChanged;

        /// <summary>The engagement the current session works under, or null.</summary>
        public Engagement? Current { get; private set; }

        /// <summary>Every engagement, newest first.</summary>
        public IReadOnlyList<Engagement> All() => _store?.ListEngagements() ?? _memory.OrderByDescending(e => e.ProposedAt).ToList();

        /// <summary>Engagements that may be selected for a session.</summary>
        public IReadOnlyList<Engagement> Approved() => All().Where(e => e.Status == EngagementStatus.Approved).ToList();

        public Engagement? Find(string? id) => id == null ? null : All().FirstOrDefault(e => e.Id == id);

        /// <summary>Selects an approved engagement for the session (null clears it). Other ids are ignored.</summary>
        /// <returns>True when the selection changed.</returns>
        public bool Select(string? id)
        {
            var engagement = string.IsNullOrEmpty(id) ? null : Approved().FirstOrDefault(e => e.Id == id);
            if ((!string.IsNullOrEmpty(id) && engagement == null) || engagement?.Id == Current?.Id)
            {
                return false;
            }

            Current = engagement;
            OnChanged?.Invoke();
            return true;
        }

        /// <summary>Checks a proposal against the verified domains (see <see cref="ScopeValidator"/>).</summary>
        public ScopeValidation Validate(ScopeProposal proposal) => ScopeValidator.Validate(proposal, VerifiedDomains());

        /// <summary>Domains whose ownership counts as verified: those in config plus those verified on the Scoping page.</summary>
        public IReadOnlyList<string> VerifiedDomains() =>
            Options.VerifiedDomains.Concat(_store?.ListVerifiedDomains() ?? _verifiedInMemory)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// The DNS TXT record a customer would publish on <paramref name="domain"/> to prove they own it.
        /// Stable per domain, so the page can show it before the check.
        /// </summary>
        public static string OwnershipRecord(string domain)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("scanharness:" + domain.ToLowerInvariant()));
            return $"scanharness-verification={Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
        }

        /// <summary>
        /// Simulates checking <see cref="OwnershipRecord"/> on the domain and records it as verified. Like the demo
        /// tools, the check always passes; a real deployment would look the record up in DNS here. Names that fail
        /// the other scope checks (wildcards, IP addresses, private names) can't be verified.
        /// </summary>
        /// <returns>The normalised domain now verified, or null if it can't be verified.</returns>
        public string? VerifyOwnership(string raw)
        {
            var reason = ScopeValidator.Check(raw, VerifiedDomains(), out var domain);
            if (reason != null && reason != ScopeValidator.NotVerified)
            {
                return null;
            }

            if (reason == ScopeValidator.NotVerified)
            {
                if (_store != null)
                    _store.SaveVerifiedDomain(domain, _session.CurrentUser.Name, DateTime.UtcNow);
                else
                    _verifiedInMemory.Add(domain);
                OnChanged?.Invoke();
            }

            return domain;
        }

        /// <summary>Records a validated proposal as a Proposed engagement, waiting for approval.</summary>
        /// <exception cref="InvalidOperationException">The proposal doesn't pass validation.</exception>
        public Engagement Propose(ScopeProposal proposal)
        {
            var validation = Validate(proposal);
            if (!validation.CanSubmit)
            {
                throw new InvalidOperationException("The proposal can't be submitted: " + string.Join(" ", validation.Problems));
            }

            var customer = proposal.Customer.Trim();
            var engagement = new Engagement
            {
                Customer = customer,
                Domains = validation.Domains.ToList(),
                WindowStart = proposal.WindowStart,
                WindowEnd = proposal.WindowEnd,
                Rules = proposal.Rules.ToList(),
                Excluded = validation.Excluded.ToList(),
                ProposedBy = _session.CurrentUser.Name,
                Version = All().Count(e => string.Equals(e.Customer, customer, StringComparison.OrdinalIgnoreCase)) + 1,
            };
            Save(engagement);
            OnChanged?.Invoke();
            return engagement;
        }

        /// <summary>Approves or rejects a proposed engagement. Only admin may decide, and decided engagements never change.</summary>
        /// <returns>The decided engagement, or null if it wasn't found, was already decided, or the user isn't admin.</returns>
        public Engagement? Decide(string id, bool approve)
        {
            var engagement = Find(id);
            if (engagement == null || engagement.Status != EngagementStatus.Proposed || !_session.CurrentUser.IsAdmin)
            {
                return null;
            }

            engagement.Status = approve ? EngagementStatus.Approved : EngagementStatus.Rejected;
            engagement.DecidedBy = _session.CurrentUser.Name;
            engagement.DecidedAt = DateTime.UtcNow;
            Save(engagement);
            OnChanged?.Invoke();
            return engagement;
        }

        /// <summary>Why a scan or attack of <paramref name="url"/> may not run now, or null if it may (always null when scope isn't enforced).</summary>
        public string? ScopeViolation(string? url) =>
            Options.EnforceScope ? ScopeGuard.Violation(Current, url, DateOnly.FromDateTime(DateTime.UtcNow)) : null;

        private void Save(Engagement engagement)
        {
            if (_store != null)
            {
                _store.SaveEngagement(engagement);
            }
            else if (!_memory.Contains(engagement))
            {
                _memory.Add(engagement);
            }
        }
    }
}
