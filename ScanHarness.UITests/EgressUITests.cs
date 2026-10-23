using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace ScanHarness.UITests;

/// <summary>
/// Network egress control in the UI. The UI test server runs with egress Off (no proxy), so these check what is
/// shown without one; the proxy path itself is covered by unit tests and scripts/test-egress.ps1.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.None)]
public class EgressUITests : PageTest
{
    private static string Url(string page) => ServerFixture.BaseUrl + "/" + page;

    [Test]
    public async Task Controls_page_shows_the_egress_rules_and_what_each_tool_may_reach()
    {
        await Page.GotoAsync(Url("controls"), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var egress = Page.GetByTestId("egress-controls");

        await Expect(egress).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(egress).ToContainTextAsync("off: no egress proxy in use");
        await Expect(egress).ToContainTextAsync("Ports: 80, 443");
        await Expect(egress.Locator("[data-egress-tool='simple_scanner']")).ToHaveTextAsync("engagement domains");
        await Expect(egress.Locator("[data-egress-tool='weather_forecast']")).ToHaveTextAsync("api.open-meteo.com");
        await Expect(egress.Locator("[data-egress-rule-id='deny-cloud-metadata']")).ToContainTextAsync("Deny addresses in 169.254.0.0/16");
        await Expect(egress.Locator("[data-egress-rule-id='deny-paste-and-tunnels'] .egress-action")).ToHaveTextAsync("Deny");
        // Egress rules aren't per-user settings, so there's no Off/Advise/Block switch for them.
        await Expect(egress.Locator(".mode-switch")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Admin_has_a_network_tab_and_other_users_do_not()
    {
        await Page.GotoAsync(Url("harness"), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Expect(Page.Locator("#prompt-input")).ToBeEnabledAsync(new() { Timeout = 15000 });
        await Expect(Page.GetByRole(AriaRole.Tab, new() { Name = "Network" })).ToHaveCountAsync(0);

        await Page.Locator("#user-select").SelectOptionAsync("admin");
        await Expect(Page.Locator("#user-select")).ToHaveValueAsync("admin");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Network" }).ClickAsync();

        var results = Page.GetByTestId("network-results");
        await Expect(results).ToContainTextAsync("No connections match these filters.");
        await Expect(Page.Locator("#network-outcome option")).ToHaveTextAsync(new[] { "Any", "Refused", "Allowed", "No token (challenges)" });

        await Page.Locator("#user-select").SelectOptionAsync("chris");
    }
}
