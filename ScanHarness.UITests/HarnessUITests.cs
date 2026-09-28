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
    public async Task Request_records_the_brain_that_handled_it()
    {
        await GotoHarnessAsync();

        await PromptInput.FillAsync("scan example.com for links");
        await SendButton.ClickAsync();

        var userItem = Page.Locator(".feed-item", new() { HasText = "Request: scan example.com for links" });
        await Expect(userItem.Locator(".feed-item-brain")).ToHaveTextAsync("Keyword brain (rule-based)", new() { Timeout = 15000 });
    }

    [Test]
    public async Task Weather_request_selects_the_weather_tool_without_approval()
    {
        await GotoHarnessAsync();

        await PromptInput.FillAsync("what's the weather in London?");
        await SendButton.ClickAsync();

        var selected = Page.Locator(".feed-item", new() { HasText = "Agent selected tool: weather/forecast" });
        await Expect(selected).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(Page.Locator(".agent-message")).ToContainTextAsync("weather forecast for London");
        await Expect(Page.Locator(".confirmation-modal")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Finished_action_is_summarised_to_the_user_and_logged()
    {
        await GotoHarnessAsync();

        await PromptInput.FillAsync("what's the weather in London?");
        await SendButton.ClickAsync();

        // The summary always starts with what the user asked for; the result part depends on
        // whether the demo tool API is running, so only the request part is checked here.
        var summary = Page.Locator(".agent-summary");
        await Expect(summary).ToContainTextAsync("The user asked me to get the weather forecast for London.", new() { Timeout = 20000 });

        var logged = Page.Locator(".feed-item", new() { HasText = "The user asked me to get the weather forecast for London." });
        await Expect(logged.Locator(".badge")).ToHaveTextAsync("Summary");
    }

    [Test]
    public async Task Rejected_attack_is_summarised_as_not_run()
    {
        await GotoHarnessAsync();

        await PromptInput.FillAsync("attack example.com");
        await SendButton.ClickAsync();
        await Expect(Page.Locator(".confirmation-modal")).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Page.GetByRole(AriaRole.Button, new() { Name = "Reject" }).ClickAsync();

        await Expect(Page.Locator(".agent-summary")).ToContainTextAsync(
            "The user rejected the request, so the attack tool was not run.", new() { Timeout = 15000 });
    }

    [Test]
    public async Task Summary_brain_picker_defaults_to_the_decision_brain()
    {
        await GotoHarnessAsync();

        await Expect(Page.Locator("#summary-brain-select")).ToHaveValueAsync("same");
    }

    private ILocator SummariseSessionButton => Page.GetByRole(AriaRole.Button, new() { Name = "Summarise session" });

    [Test]
    public async Task Summarise_session_is_disabled_until_a_tool_has_been_called()
    {
        await GotoHarnessAsync();

        await Expect(SummariseSessionButton).ToBeDisabledAsync();
    }

    [Test]
    public async Task Summarise_session_lists_every_tool_called_and_summarises_the_session()
    {
        await GotoHarnessAsync();

        var requests = new[] { "what's the weather in London?", "scan example.com for links" };
        for (var i = 0; i < requests.Length; i++)
        {
            await PromptInput.FillAsync(requests[i]);
            await SendButton.ClickAsync();
            // Each reply in the chat gets its summary; wait for this one so the call has finished before the next.
            await Expect(Page.Locator(".agent-summary")).ToHaveCountAsync(i + 1, new() { Timeout = 20000 });
            await Expect(Page.Locator(".status-state")).ToHaveTextAsync("Ready", new() { Timeout = 20000 });
        }

        await SummariseSessionButton.ClickAsync();

        // Deterministic tool list, in call order.
        var rows = Page.Locator(".session-tools-table tbody tr");
        await Expect(rows).ToHaveCountAsync(2, new() { Timeout = 20000 });
        await Expect(rows.Nth(0)).ToContainTextAsync("weather/forecast");
        await Expect(rows.Nth(0)).ToContainTextAsync("location=London");
        await Expect(rows.Nth(1)).ToContainTextAsync("scanner/simple-scanner");

        // Summary of summaries from the (keyword) summary brain, which is also logged.
        await Expect(Page.Locator(".session-summary")).ToContainTextAsync("2 tool calls this session");
        await Expect(Page.Locator(".feed-item", new() { HasText = "Session summary (" })).ToBeVisibleAsync();
    }

    // --- Chat window ---

    private ILocator UserBubbles => Page.Locator(".chat-row.from-user .chat-bubble");
    private ILocator AgentBubbles => Page.Locator(".chat-row.from-agent .chat-bubble");

    [Test]
    public async Task Question_and_reply_appear_as_chat_bubbles_with_the_summary()
    {
        await GotoHarnessAsync();
        await SendAndWaitAsync("what's the weather in Madrid?");

        await Expect(UserBubbles).ToHaveCountAsync(1);
        await Expect(UserBubbles.First).ToContainTextAsync("what's the weather in Madrid?");
        await Expect(UserBubbles.First).ToContainTextAsync("You");

        var reply = AgentBubbles.First;
        await Expect(reply.Locator(".agent-message")).ToHaveTextAsync("I'll get the weather forecast for Madrid.");
        await Expect(reply.Locator(".chat-tool")).ToContainTextAsync("weather/forecast");
        await Expect(reply.Locator(".agent-summary")).ToContainTextAsync("The user asked me to get the weather forecast for Madrid.");
    }

    [Test]
    public async Task Reply_without_a_tool_is_not_selectable()
    {
        await GotoHarnessAsync();
        await PromptInput.FillAsync("tell me a joke");
        await SendButton.ClickAsync();

        var reply = AgentBubbles.First;
        await Expect(reply.Locator(".agent-message")).ToContainTextAsync("I don't have a suitable tool", new() { Timeout = 15000 });
        await Expect(reply).Not.ToHaveAttributeAsync("role", "button");
    }

    [Test]
    public async Task Selecting_an_earlier_reply_shows_its_tool_output_below()
    {
        await GotoHarnessAsync();
        await SendAndWaitAsync("what's the weather in Rome?");
        await SendAndWaitAsync("scan example.com for links");

        var output = Page.Locator(".output-area");
        await Expect(output.Locator(".output-tool")).ToContainTextAsync("scanner"); // latest by default
        await Expect(AgentBubbles.Nth(1)).ToHaveClassAsync(new Regex("is-selected"));

        await AgentBubbles.Nth(0).ClickAsync();
        await Expect(output.Locator(".output-tool")).ToContainTextAsync("weather");
        await Expect(output).ToContainTextAsync("location");
        await Expect(AgentBubbles.Nth(0)).ToHaveClassAsync(new Regex("is-selected"));
        await Expect(AgentBubbles.Nth(1)).Not.ToHaveClassAsync(new Regex("is-selected"));

        // "Show latest" goes back to following the newest call.
        await output.GetByRole(AriaRole.Button, new() { Name = "Show latest" }).ClickAsync();
        await Expect(output.Locator(".output-tool")).ToContainTextAsync("scanner");
    }

    [Test]
    public async Task Replies_can_be_selected_with_the_keyboard()
    {
        await GotoHarnessAsync();
        await SendAndWaitAsync("what's the weather in Vienna?");
        await SendAndWaitAsync("scan example.com for links");

        await AgentBubbles.Nth(0).FocusAsync();
        await Page.Keyboard.PressAsync("Enter");

        await Expect(Page.Locator(".output-area .output-tool")).ToContainTextAsync("weather");
        await Expect(AgentBubbles.Nth(0)).ToHaveAttributeAsync("aria-pressed", "true");
    }

    [Test]
    public async Task A_new_request_switches_the_output_back_to_the_latest_call()
    {
        await GotoHarnessAsync();
        await SendAndWaitAsync("what's the weather in Athens?");
        await SendAndWaitAsync("scan example.com for links");
        await AgentBubbles.Nth(0).ClickAsync();
        await Expect(Page.Locator(".output-area .output-tool")).ToContainTextAsync("weather");

        await SendAndWaitAsync("scan example.org for links");

        await Expect(Page.Locator(".output-area")).ToContainTextAsync("https://example.org");
        await Expect(AgentBubbles.Nth(2)).ToHaveClassAsync(new Regex("is-selected"));
    }

    // --- Users, sessions and persistence ---

    private ILocator SessionSelect => Page.Locator("#session-select");

    // Sends a request and waits until it has been fully handled (decided, run and summarised).
    private async Task SendAndWaitAsync(string request)
    {
        var questionsBefore = await UserBubbles.CountAsync();
        await PromptInput.FillAsync(request);
        await SendButton.ClickAsync();
        // The new question appearing proves the click was handled, so "Ready" below is this request's.
        await Expect(UserBubbles).ToHaveCountAsync(questionsBefore + 1, new() { Timeout = 20000 });
        await Expect(Page.Locator(".status-state")).ToHaveTextAsync("Ready", new() { Timeout = 20000 });
    }

    private async Task SwitchUserAsync(string user)
    {
        await Page.Locator("#user-select").SelectOptionAsync(user);
        await Expect(Page.Locator("#user-select")).ToHaveValueAsync(user);
    }

    private async Task OpenSessionAsync(string titleText)
    {
        var value = await SessionSelect.Locator("option", new() { HasText = titleText }).GetAttributeAsync("value");
        await SessionSelect.SelectOptionAsync(value!);
    }

    [Test]
    public async Task Offers_the_four_users_and_starts_as_chris()
    {
        await GotoHarnessAsync();

        var users = await Page.Locator("#user-select option").AllInnerTextsAsync();
        Assert.That(users, Is.EqualTo(new[] { "chris", "raccoon", "blob", "admin" }));
        await Expect(Page.Locator("#user-select")).ToHaveValueAsync("chris");
    }

    [Test]
    public async Task Saved_session_survives_a_reload_and_can_be_reopened()
    {
        await GotoHarnessAsync();
        await SendAndWaitAsync("what's the weather in Reykjavik?");

        await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Expect(PromptInput).ToBeEnabledAsync(new() { Timeout = 15000 });

        // The reloaded page starts a new session; the saved one is loaded from the database into the picker.
        await Expect(SessionSelect.Locator("option", new() { HasText = "Reykjavik" })).ToHaveCountAsync(1);
        await OpenSessionAsync("Reykjavik");

        await Expect(Page.Locator(".feed-item", new() { HasText = "Request: what's the weather in Reykjavik?" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".agent-summary")).ToContainTextAsync("weather forecast for Reykjavik");
        await Expect(Page.Locator(".output-area")).ToContainTextAsync("weather");
    }

    [Test]
    public async Task Users_only_see_their_own_sessions()
    {
        await GotoHarnessAsync();
        await SwitchUserAsync("raccoon");
        await SendAndWaitAsync("what's the weather in Oslo?");

        await SwitchUserAsync("blob");
        await Expect(SessionSelect.Locator("option", new() { HasText = "Oslo" })).ToHaveCountAsync(0);

        await SwitchUserAsync("raccoon");
        await Expect(SessionSelect.Locator("option", new() { HasText = "Oslo" })).ToHaveCountAsync(1);
    }

    [Test]
    public async Task Admin_sees_every_users_sessions_with_the_user_on_each_entry()
    {
        await GotoHarnessAsync();
        await SwitchUserAsync("blob");
        await SendAndWaitAsync("what's the weather in Lisbon?");

        await SwitchUserAsync("admin");
        var option = SessionSelect.Locator("option", new() { HasText = "Lisbon" });
        await Expect(option).ToContainTextAsync("blob");
        await OpenSessionAsync("Lisbon");

        // The request is labelled with the user; the entries it triggered carry a user tag.
        var request = Page.Locator(".feed-item", new() { HasText = "Request: what's the weather in Lisbon?" });
        await Expect(request.Locator(".badge")).ToHaveTextAsync("blob");
        var selected = Page.Locator(".feed-item", new() { HasText = "Agent selected tool: weather/forecast" }).Last;
        await Expect(selected.Locator(".feed-item-user")).ToHaveTextAsync("blob");

        // History shows the user column too.
        await Page.GetByRole(AriaRole.Tab, new() { Name = "History" }).ClickAsync();
        await Expect(Page.Locator(".history-user").First).ToHaveTextAsync("blob");
    }

    [Test]
    public async Task Dark_theme_is_the_default()
    {
        await GotoHarnessAsync();

        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark");
        await Expect(Page.Locator("#theme-select")).ToHaveValueAsync("dark");
    }

    [Test]
    public async Task Chosen_theme_applies_immediately_and_survives_a_reload()
    {
        await GotoHarnessAsync();

        await Page.Locator("#theme-select").SelectOptionAsync("light");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");

        await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
        await Expect(Page.Locator("#theme-select")).ToHaveValueAsync("light");
    }

    [Test]
    public async Task System_theme_follows_the_operating_system_setting()
    {
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Light });
        await GotoHarnessAsync();

        await Page.Locator("#theme-select").SelectOptionAsync("system");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");

        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
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
