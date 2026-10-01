using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ScanHarness.Models;
using ScanHarness.Services;
using ScanHarness.Services.Agents;
using ScanHarness.Services.Boundaries;
using ScanHarness.Services.Engagements;
using ScanHarness.Services.Persistence;
using Xunit;

namespace ScanHarness.Tests
{
    /// <summary>Scope proposals, the scope guard, engagement approval, and storing engagements and permission edits.</summary>
    public sealed class EngagementTests : IDisposable
    {
        private static readonly string[] Verified = { "acme.test", "example.com" };
        private static readonly DateOnly Today = new(2026, 10, 5);

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "scanharness-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        private static ScopeProposal Proposal(params string[] domains) =>
            new("Acme Ltd", domains, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 14), new[] { "No testing at weekends." });

        // --- Validation ---

        [Theory]
        [InlineData("https://Shop.Acme.test/login", null)]
        [InlineData("*.acme.test", "wildcards aren't allowed; list each domain")]
        [InlineData("10.0.0.5", "IP addresses aren't accepted; use a domain name")]
        [InlineData("intranet.acme.local", "private or internal names aren't accepted")]
        [InlineData("partner.example.net", "ownership not verified")]
        [InlineData("not a domain", "not a valid domain name")]
        [InlineData("acme.test.evil.example", "ownership not verified")]
        public void Each_domain_is_checked_in_code(string domain, string? reason)
        {
            Assert.Equal(reason, ScopeValidator.Check(domain, Verified, out _));
        }

        [Fact]
        public void Unverified_domains_are_left_out_with_the_reason_and_the_rest_kept()
        {
            var validation = ScopeValidator.Validate(Proposal("acme.test", "https://shop.acme.test/", "partner.example.net"), Verified);

            Assert.True(validation.CanSubmit);
            Assert.Equal(new[] { "acme.test", "shop.acme.test" }, validation.Domains);
            Assert.Equal(new[] { "partner.example.net: ownership not verified" }, validation.Excluded);
        }

        [Fact]
        public void A_proposal_needs_a_customer_a_verified_domain_and_a_window()
        {
            var validation = ScopeValidator.Validate(new ScopeProposal("", new[] { "evil.example" }, new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 1), Array.Empty<string>()), Verified);

            Assert.False(validation.CanSubmit);
            Assert.Contains("The customer's name hasn't been given.", validation.Problems);
            Assert.Contains("No domain passed the checks, so there is nothing to test.", validation.Problems);
            Assert.Contains("The testing window ends before it starts.", validation.Problems);
            Assert.Contains("No rules of engagement were stated.", validation.Warnings);
        }

        // --- Guard ---

        private static Engagement Approved() => new()
        {
            Customer = "Acme Ltd", Domains = new() { "acme.test" }, Status = EngagementStatus.Approved,
            WindowStart = new DateOnly(2026, 10, 1), WindowEnd = new DateOnly(2026, 10, 14),
        };

        [Theory]
        [InlineData("https://acme.test/", null)]
        [InlineData("https://www.shop.acme.test/x", null)]
        [InlineData("https://notacme.test/", "notacme.test isn't in engagement Acme Ltd v1's scope (acme.test).")]
        [InlineData("https://acme.test.evil.example/", "acme.test.evil.example isn't in engagement Acme Ltd v1's scope (acme.test).")]
        [InlineData("not a url", "The target isn't a valid URL.")]
        public void Only_hosts_inside_the_scope_may_be_tested(string url, string? violation)
        {
            Assert.Equal(violation, ScopeGuard.Violation(Approved(), url, Today));
        }

        [Fact]
        public void Nothing_is_in_scope_without_an_approved_in_window_engagement()
        {
            Assert.StartsWith("No engagement is selected", ScopeGuard.Violation(null, "https://acme.test", Today));

            var proposed = Approved();
            proposed.Status = EngagementStatus.Proposed;
            Assert.Equal("Engagement Acme Ltd v1 is proposed, not approved.", ScopeGuard.Violation(proposed, "https://acme.test", Today));

            Assert.StartsWith("Today (15 Oct 2026) is outside", ScopeGuard.Violation(Approved(), "https://acme.test", new DateOnly(2026, 10, 15)));
        }

        // --- Service ---

        private (EngagementService Service, SessionState Session) Service(IActivityStore? store = null)
        {
            var session = new SessionState(store);
            return (new EngagementService(session, Options.Create(new EngagementOptions { VerifiedDomains = Verified.ToList() }), store), session);
        }

        [Fact]
        public void Only_admin_can_approve_and_only_approved_engagements_can_be_selected()
        {
            var (service, session) = Service();
            var engagement = service.Propose(Proposal("acme.test"));

            Assert.Equal(EngagementStatus.Proposed, engagement.Status);
            Assert.Equal("chris", engagement.ProposedBy);
            Assert.False(service.Select(engagement.Id));
            Assert.Null(service.Decide(engagement.Id, approve: true));

            session.SwitchUser("admin");
            var approved = service.Decide(engagement.Id, approve: true)!;
            Assert.Equal("admin", approved.DecidedBy);
            Assert.Null(service.Decide(engagement.Id, approve: false));   // decided engagements never change

            Assert.True(service.Select(engagement.Id));
            Assert.Equal(engagement.Id, service.Current!.Id);
        }

        [Fact]
        public void A_proposal_that_fails_validation_cant_be_submitted_and_versions_count_up()
        {
            var (service, _) = Service();

            Assert.Throws<InvalidOperationException>(() => service.Propose(Proposal("evil.example")));
            Assert.Equal(1, service.Propose(Proposal("acme.test")).Version);
            Assert.Equal(2, service.Propose(Proposal("acme.test", "example.com")).Version);
        }

        [Fact]
        public void Verifying_ownership_lets_a_left_out_domain_into_the_scope()
        {
            var (service, _) = Service();
            var proposal = Proposal("testtest.com", "https://www.Test.com/");

            var before = service.Validate(proposal);
            Assert.False(before.CanSubmit);
            Assert.Equal(new[] { "testtest.com", "www.test.com" }, before.UnverifiedDomains);

            Assert.Equal("testtest.com", service.VerifyOwnership("testtest.com"));
            var after = service.Validate(proposal);
            Assert.True(after.CanSubmit);
            Assert.Equal(new[] { "testtest.com" }, after.Domains);
            Assert.Equal(new[] { "www.test.com" }, after.UnverifiedDomains);
        }

        [Theory]
        [InlineData("*.test.com")]
        [InlineData("10.0.0.5")]
        [InlineData("intranet.acme.local")]
        public void Only_names_that_pass_the_other_checks_can_be_verified(string domain)
        {
            var (service, _) = Service();

            Assert.Null(service.VerifyOwnership(domain));
            Assert.Equal(Verified, service.VerifiedDomains());
        }

        [Fact]
        public void The_ownership_record_is_stable_per_domain()
        {
            Assert.Equal(EngagementService.OwnershipRecord("TestTest.com"), EngagementService.OwnershipRecord("testtest.com"));
            Assert.NotEqual(EngagementService.OwnershipRecord("testtest.com"), EngagementService.OwnershipRecord("test.com"));
            Assert.Matches("^scanharness-verification=[0-9a-f]{16}$", EngagementService.OwnershipRecord("testtest.com"));
        }

        [Fact]
        public void Engagements_and_permission_edits_survive_a_restart()
        {
            var path = Path.Combine(_directory, "activity.db");
            var store = new SqliteActivityStore(path);
            store.Initialize();
            var (service, session) = Service(store);
            session.SwitchUser("admin");
            var engagement = service.Propose(Proposal("acme.test", "partner.example.net"));
            service.Decide(engagement.Id, approve: true);
            new ComponentPolicyService(store).Update("tool:attack", new PolicyOverride(Enabled: false, Clearance: Sensitivity.Engagement), "admin");
            service.VerifyOwnership("testtest.com");

            var restarted = new SqliteActivityStore(path);
            restarted.Initialize();
            var loaded = Assert.Single(restarted.ListEngagements());
            Assert.Equal(EngagementStatus.Approved, loaded.Status);
            Assert.Equal(new[] { "acme.test" }, loaded.Domains);
            Assert.Equal(new[] { "partner.example.net: ownership not verified" }, loaded.Excluded);
            Assert.Equal(new DateOnly(2026, 10, 14), loaded.WindowEnd);
            Assert.Equal("admin", loaded.DecidedBy);
            Assert.Equal(new[] { "testtest.com" }, restarted.ListVerifiedDomains());

            var attack = new ComponentPolicyService(restarted).ForTool(ToolCatalog.FindByTool("attack")!);
            Assert.False(attack.Enabled);
            Assert.Equal(Sensitivity.Engagement, attack.Clearance);
            Assert.True(attack.IsEdited);
        }
    }
}
