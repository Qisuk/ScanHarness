using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace ScanHarness.UITests;

/// <summary>Drilling into a reasoning model's thinking behind an action (using <see cref="FakeLlmServer"/>).</summary>
[TestFixture]
[Parallelizable(ParallelScope.None)]
public class ReasoningUITests : PageTest
{
    private static string HarnessUrl => ServerFixture.BaseUrl + "/harness";

    private ILocator PromptInput => Page.Locator("#prompt-input");
    private ILocator LastReply => Page.Locator(".chat-row.from-agent .chat-bubble").Last;

    private async Task GotoHarnessAsync()
    {
        await Page.GotoAsync(HarnessUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Expect(PromptInput).ToBeEnabledAsync(new() { Timeout = 15000 });
    }

    [Test]
    public async Task A_reasoning_models_thinking_can_be_drilled_into_from_the_reply_and_the_tool_output()
    {
        await GotoHarnessAsync();
        await Page.Locator("#brain-select").SelectOptionAsync(FakeLlmServer.Model);
        await Expect(Page.Locator("#brain-select")).ToHaveValueAsync(FakeLlmServer.Model);

        await PromptInput.FillAsync("what's the weather in Lisbon");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();
        await Expect(LastReply.Locator(".chat-summary")).ToContainTextAsync("no forecast", new() { Timeout = 20000 });

        // In the chat: collapsed under the reply, one step for the decision and one for the summary.
        var reasoning = LastReply.Locator("details.chat-reasoning");
        await Expect(reasoning.Locator("summary")).ToHaveTextAsync("Reasoning (2 steps)");
        await reasoning.Locator("summary").ClickAsync();
        await Expect(reasoning.Locator(".reasoning-step[data-stage='decision'] .reasoning-text")).ToHaveTextAsync(FakeLlmServer.DecisionReasoning);
        await Expect(reasoning.Locator(".reasoning-step[data-stage='summary'] .reasoning-text")).ToHaveTextAsync(FakeLlmServer.SummaryReasoning);
        await Expect(reasoning.Locator(".reasoning-brain").First).ToHaveTextAsync($"{FakeLlmServer.Model} (LLM)");

        // The think block was taken out of the summary itself.
        await Expect(LastReply.Locator(".chat-summary")).Not.ToContainTextAsync("<think>");

        // In the tool output for that action, and in the activity feed.
        await Expect(Page.Locator(".output-reasoning .reasoning-step")).ToHaveCountAsync(2);
        await Expect(Page.Locator(".feed-item", new() { HasText = "Reasoning (decision): " + FakeLlmServer.DecisionReasoning })).ToBeVisibleAsync();

        // It's saved: reopening the session shows it again.
        await GotoHarnessAsync();
        var session = Page.Locator("#session-select");
        var value = await session.Locator("option", new() { HasText = "Lisbon" }).GetAttributeAsync("value");
        await session.SelectOptionAsync(value!);
        await Expect(LastReply.Locator("details.chat-reasoning summary")).ToHaveTextAsync("Reasoning (2 steps)");
    }

    [Test]
    public async Task Replies_from_the_keyword_brain_have_no_reasoning_section()
    {
        await GotoHarnessAsync();
        await PromptInput.FillAsync("what's the weather in Porto?");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();
        await Expect(Page.Locator(".status-state")).ToHaveTextAsync("Ready", new() { Timeout = 20000 });
        await Expect(LastReply.Locator(".chat-summary")).ToBeVisibleAsync();

        await Expect(LastReply.Locator("details.chat-reasoning")).ToHaveCountAsync(0);
    }
}
