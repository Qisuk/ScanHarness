using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace ScanHarness.UITests;

[TestFixture]
[Parallelizable(ParallelScope.None)]
public class HarnessUITests : PageTest
{
    private string HarnessUrl => ServerFixture.BaseUrl + "/harness";

    private ILocator PromptInput => Page.Locator("input[placeholder*='scan example.com']");
    private ILocator SendButton => Page.GetByRole(AriaRole.Button, new() { Name = "Send" });

    [TearDown]
    public async Task DumpOnFailure()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed)
            return;

        try
        {
            var feed = await Page.Locator(".activity-feed").InnerTextAsync();
            TestContext.Out.WriteLine("=== ACTIVITY FEED ===\n" + feed);
            var modalCount = await Page.Locator(".confirmation-modal").CountAsync();
            TestContext.Out.WriteLine($"=== modal count: {modalCount} ===");
            var shot = Path.Combine(Path.GetTempPath(), $"uifail-{TestContext.CurrentContext.Test.MethodName}.png");
            await Page.ScreenshotAsync(new() { Path = shot, FullPage = true });
            TestContext.Out.WriteLine("screenshot: " + shot);
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine("dump failed: " + ex.Message);
        }
    }

    private async Task GotoHarnessAsync()
    {
        await Page.GotoAsync(HarnessUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        // Wait for the interactive circuit: the prompt input must be present and enabled.
        await Expect(PromptInput).ToBeEnabledAsync(new() { Timeout = 15000 });
    }

    [Test]
    public async Task Prompt_section_renders_above_the_tool_output()
    {
        await GotoHarnessAsync();

        var inputBox = await Page.Locator(".input-area").BoundingBoxAsync();
        var outputBox = await Page.Locator(".output-area").BoundingBoxAsync();

        Assert.That(inputBox, Is.Not.Null);
        Assert.That(outputBox, Is.Not.Null);
        Assert.That(inputBox!.Y, Is.LessThan(outputBox!.Y),
            "The 'Ask the agent' input section should render above the Tool Output area.");
    }

    [Test]
    public async Task User_request_appears_in_the_activity_feed_with_a_timestamp()
    {
        await GotoHarnessAsync();

        await PromptInput.FillAsync("scan example.com for links");
        await SendButton.ClickAsync();

        var userItem = Page.Locator(".feed-item", new() { HasText = "Request: scan example.com for links" });
        await Expect(userItem).ToBeVisibleAsync(new() { Timeout = 15000 });

        // The item carries a HH:mm:ss timestamp.
        await Expect(userItem.Locator("small")).ToContainTextAsync(new Regex(@"\d{2}:\d{2}:\d{2}"));

        // It is tagged as a user ("You") action.
        await Expect(userItem.Locator(".badge")).ToContainTextAsync("You");
    }

    [Test]
    public async Task Attack_request_prompts_for_approval_and_logs_the_decision()
    {
        await GotoHarnessAsync();

        await PromptInput.FillAsync("attack example.com");
        await SendButton.ClickAsync();

        // Approval prompt appears only because the agent chose the attack tool.
        var modal = Page.Locator(".confirmation-modal");
        await Expect(modal).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(modal).ToContainTextAsync("Authorization Required");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Reject" }).ClickAsync();

        await Expect(modal).ToBeHiddenAsync();
        var rejectItem = Page.Locator(".feed-item", new() { HasText = "Rejected tool call: attack/execute" });
        await Expect(rejectItem).ToBeVisibleAsync(new() { Timeout = 15000 });
    }

    [Test]
    public async Task Approving_an_attack_logs_the_approval_in_the_activity_feed()
    {
        await GotoHarnessAsync();

        await PromptInput.FillAsync("attack example.com");
        await SendButton.ClickAsync();

        var modal = Page.Locator(".confirmation-modal");
        await Expect(modal).ToBeVisibleAsync(new() { Timeout = 15000 });

        await Page.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();

        await Expect(modal).ToBeHiddenAsync();
        var approveItem = Page.Locator(".feed-item", new() { HasText = "Approved tool call: attack/execute" });
        await Expect(approveItem).ToBeVisibleAsync(new() { Timeout = 15000 });
    }

    [Test]
    public async Task Scan_request_does_not_prompt_for_approval()
    {
        await GotoHarnessAsync();

        await PromptInput.FillAsync("scan example.com for links");
        await SendButton.ClickAsync();

        // The scanner tool does not require approval, so no modal should appear.
        var agentSelected = Page.Locator(".feed-item", new() { HasText = "Agent selected tool: scanner/simple-scanner" });
        await Expect(agentSelected).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(Page.Locator(".confirmation-modal")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task User_actions_are_offset_left_of_the_actions_they_trigger()
    {
        await GotoHarnessAsync();

        await PromptInput.FillAsync("scan example.com for links");
        await SendButton.ClickAsync();

        var userItem = Page.Locator(".feed-item", new() { HasText = "Request: scan example.com for links" });
        var triggeredItem = Page.Locator(".feed-item", new() { HasText = "Agent selected tool: scanner/simple-scanner" });
        await Expect(userItem).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(triggeredItem).ToBeVisibleAsync(new() { Timeout = 15000 });

        var userBox = await userItem.BoundingBoxAsync();
        var triggeredBox = await triggeredItem.BoundingBoxAsync();

        Assert.That(userBox, Is.Not.Null);
        Assert.That(triggeredBox, Is.Not.Null);
        Assert.That(userBox!.X, Is.LessThan(triggeredBox!.X),
            "The user's request should be offset to the left of the actions it triggered.");
    }

    [Test]
    public async Task Arrow_up_recalls_the_previous_prompt()
    {
        await GotoHarnessAsync();

        // Use a prompt with no matching tool so nothing external is called; it is still recorded in history.
        await PromptInput.FillAsync("hello there");
        await SendButton.ClickAsync();

        // The input clears after sending.
        await Expect(PromptInput).ToHaveValueAsync("");

        await PromptInput.FocusAsync();
        await PromptInput.PressAsync("ArrowUp");

        await Expect(PromptInput).ToHaveValueAsync("hello there");
    }
}
