using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace ScanHarness.UITests;

/// <summary>The Permissions tab, agents as tools, and scoping an engagement.</summary>
[TestFixture]
[Parallelizable(ParallelScope.None)]
public class BoundaryUITests : PageTest
{
    private static string Url(string page) => ServerFixture.BaseUrl + "/" + page;

    private ILocator PromptInput => Page.Locator("#prompt-input");
    private ILocator AgentBubbles => Page.Locator(".chat-row.from-agent .chat-bubble");
    private ILocator Row(string policyId) => Page.Locator($"tr[data-policy-id='{policyId}']");

    private async Task GotoHarnessAsync()
    {
        await Page.GotoAsync(Url("harness"), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Expect(PromptInput).ToBeEnabledAsync(new() { Timeout = 15000 });
    }

    private async Task SendAndWaitAsync(string request)
    {
        var before = await Page.Locator(".chat-row.from-user").CountAsync();
        await PromptInput.FillAsync(request);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();
        await Expect(Page.Locator(".chat-row.from-user")).ToHaveCountAsync(before + 1, new() { Timeout = 20000 });
        await Expect(Page.Locator(".status-state")).ToHaveTextAsync("Ready", new() { Timeout = 20000 });
    }

    private async Task GotoPermissionsAsync()
    {
        await Page.GotoAsync(Url("controls"), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Permissions" }).ClickAsync();
        await Expect(Row("tool:simple_scanner")).ToBeVisibleAsync(new() { Timeout = 15000 });
    }

    // Permission edits are shared by everyone, so every test that makes one puts the defaults back.
    private async Task ResetPermissionsAsync()
    {
        await GotoPermissionsAsync();
        var resetAll = Page.GetByRole(AriaRole.Button, new() { Name = "Reset all" });
        if (await resetAll.IsEnabledAsync())
        {
            await resetAll.ClickAsync();
            await Expect(resetAll).ToBeDisabledAsync();
        }
    }

    [Test]
    public async Task Permissions_tab_lists_brains_tools_and_agents_with_their_trust_settings()
    {
        await GotoPermissionsAsync();

        await Expect(Row("brain:keyword").GetByLabel("Keyword brain (rule-based) hosting")).ToBeDisabledAsync();
        await Expect(Row("brain:keyword").GetByLabel("Keyword brain (rule-based) output trust")).ToHaveValueAsync("Trusted");

        var scanner = Row("tool:simple_scanner");
        await Expect(scanner.GetByLabel("simple_scanner hosting")).ToHaveValueAsync("Internal");
        await Expect(scanner.GetByLabel("simple_scanner clearance")).ToHaveValueAsync("CustomerConfidential");
        await Expect(scanner.GetByLabel("simple_scanner output trust")).ToHaveValueAsync("Untrusted");
        await Expect(scanner).ToContainTextAsync("needs engagement scope");
        // ServerFixture overrides the attack tool to External through config.
        await Expect(Row("tool:attack").GetByLabel("attack hosting")).ToHaveValueAsync("External");
        await Expect(Row("tool:attack")).ToContainTextAsync("needs approval · needs engagement scope");

        await Expect(Row("agent:findings-analyst").GetByLabel("Findings analyst brain")).ToHaveValueAsync("rule-based");
        await Expect(Row("agent:cost-estimator").GetByLabel("Cost estimator clearance")).ToHaveValueAsync("Engagement");
        await Expect(Row("agent:scoping-agent")).ToContainTextAsync("own page only");
    }

    [Test]
    public async Task Asking_for_an_analysis_runs_the_findings_analyst_as_a_tool()
    {
        await GotoHarnessAsync();
        await SendAndWaitAsync("please analyse the findings");

        var reply = AgentBubbles.Last;
        await Expect(reply.Locator(".chat-tool-name")).ToHaveTextAsync("agent/findings-analyst");
        await Expect(reply.Locator(".chat-tool .sh-class-tag")).ToHaveTextAsync(new[] { "internal", "untrusted output" });
        await Expect(reply).ToContainTextAsync("findings analyst (rule-based) reported: There are no completed scan or attack results");
        await Expect(Page.Locator(".feed-item", new() { HasText = "Handoff: FindingSet (customer confidential) → Findings analyst" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Permission_edits_apply_straight_away_and_can_be_reset()
    {
        try
        {
            await GotoPermissionsAsync();
            await Row("agent:findings-analyst").GetByLabel("Findings analyst hosting").SelectOptionAsync("External");
            await Expect(Row("agent:findings-analyst").GetByRole(AriaRole.Button, new() { Name = "Reset" })).ToBeVisibleAsync();

            await GotoHarnessAsync();
            await SendAndWaitAsync("recommend fixes");
            await Expect(AgentBubbles.Last.Locator(".chat-tool .sh-class-tag").First).ToHaveTextAsync("external");

            await GotoPermissionsAsync();
            await Row("agent:findings-analyst").GetByRole(AriaRole.Button, new() { Name = "Reset" }).ClickAsync();
            await Expect(Row("agent:findings-analyst").GetByLabel("Findings analyst hosting")).ToHaveValueAsync("Internal");
        }
        finally
        {
            await ResetPermissionsAsync();
        }
    }

    [Test]
    public async Task A_disabled_tool_is_blocked_before_it_runs()
    {
        try
        {
            await GotoPermissionsAsync();
            await Row("tool:weather_forecast").GetByLabel("weather_forecast enabled").UncheckAsync();

            await GotoHarnessAsync();
            await SendAndWaitAsync("what's the weather in Rome?");

            var reply = AgentBubbles.Last;
            await Expect(reply).ToContainTextAsync("Tool call blocked by a ToolRule control");
            await Expect(reply.Locator(".chat-blocked-reason")).ToHaveTextAsync("Component disabled: weather_forecast is disabled on the Permissions tab.");
            await reply.Locator(".control-pip").ClickAsync();
            await Expect(reply.Locator(".control-pip-popover")).ToContainTextAsync("weather_forecast is disabled on the Permissions tab.");
        }
        finally
        {
            await ResetPermissionsAsync();
        }
    }

    private async Task SayToScopingAgentAsync(string message)
    {
        var replies = await Page.Locator(".scoping-message.from-agent").CountAsync();
        await Page.Locator("#scoping-input").FillAsync(message);
        await Page.Locator("#scoping-send").ClickAsync();
        await Expect(Page.Locator(".scoping-message.from-agent")).ToHaveCountAsync(replies + 1, new() { Timeout = 15000 });
    }

    [Test]
    public async Task An_unverified_domain_can_be_verified_and_then_submitted()
    {
        await Page.GotoAsync(Url("scoping"), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Expect(Page.Locator("#scoping-input")).ToBeEnabledAsync(new() { Timeout = 15000 });

        await SayToScopingAgentAsync("We are Initech. Please test initech.example.net");
        await SayToScopingAgentAsync("2026-10-01 to 2026-10-20");
        await SayToScopingAgentAsync("do not touch any servers which contain 'critical' in the name");

        // The agent thinks it's done, but the checks haven't passed, and the harness says so.
        await Expect(Page.Locator(".scoping-notice")).ToContainTextAsync("Not ready to submit yet: No domain passed the checks");
        await Expect(Page.Locator("#scoping-submit")).ToBeDisabledAsync();

        var verify = Page.Locator(".scoping-verify-row[data-domain='initech.example.net']");
        await Expect(verify.Locator(".scoping-record")).ToContainTextAsync("TXT scanharness-verification=");
        await verify.GetByRole(AriaRole.Button, new() { Name = "Check record" }).ClickAsync();

        await Expect(Page.Locator("dd[data-field='domains'] .scoping-domain")).ToHaveTextAsync(new[] { "initech.example.net" });
        await Expect(Page.Locator(".scoping-verify")).ToHaveCountAsync(0);
        await Expect(Page.Locator(".scoping-notice")).ToHaveCountAsync(0);
        await Page.Locator("#scoping-submit").ClickAsync();
        await Expect(Page.Locator("tr", new() { HasText = "Initech v1" })).ToContainTextAsync("Proposed");
    }

    [Test]
    public async Task A_customer_scope_is_validated_and_approved_before_it_can_be_selected()
    {
        await Page.GotoAsync(Url("scoping"), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Expect(Page.Locator("#scoping-input")).ToBeEnabledAsync(new() { Timeout = 15000 });

        await Page.Locator("#scoping-input").FillAsync(
            "We are Globex Corp. Please test acme.test and partner.example.net from 2026-10-01 to 2026-12-31. No testing at weekends.");
        await Page.Locator("#scoping-send").ClickAsync();

        await Expect(Page.Locator(".scoping-message.from-agent")).ToContainTextAsync("I have what I need", new() { Timeout = 15000 });
        await Expect(Page.Locator("dd[data-field='customer']")).ToHaveTextAsync("Globex Corp");
        await Expect(Page.Locator("dd[data-field='domains'] .scoping-domain")).ToHaveTextAsync(new[] { "acme.test" });
        await Expect(Page.Locator(".scoping-checks")).ToContainTextAsync("partner.example.net: ownership not verified");

        await Page.Locator("#scoping-submit").ClickAsync();
        var row = Page.Locator("tr", new() { HasText = "Globex Corp v1" });
        await Expect(row).ToContainTextAsync("Proposed");
        await Expect(row.GetByRole(AriaRole.Button, new() { Name = "Approve" })).ToHaveCountAsync(0);   // chris can't approve

        await Page.Locator("#scoping-user-select").SelectOptionAsync("admin");
        await row.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
        await Expect(row).ToContainTextAsync("Approved");
        await Expect(row).ToContainTextAsync("admin");

        await GotoHarnessAsync();
        await Page.Locator("#engagement-select").SelectOptionAsync(new SelectOptionValue { Label = "Globex Corp v1" });
        await Expect(Page.Locator(".feed-item", new() { HasText = "Selected engagement Globex Corp v1: acme.test" })).ToBeVisibleAsync();
    }
}
