using ScanHarness.Services;
using Xunit;

namespace ScanHarness.Tests
{
    public class KeywordAgentBrainTests
    {
        private readonly KeywordAgentBrain _brain = new();

        [Theory]
        [InlineData("scan example.com for links")]
        [InlineData("crawl https://example.com")]
        [InlineData("please enumerate the links on test.co.uk")]
        public async Task Scan_requests_select_the_scanner_tool_without_approval(string prompt)
        {
            var decision = await _brain.DecideAsync(prompt);

            Assert.True(decision.ToolFound);
            Assert.Equal("scanner", decision.Tool);
            Assert.Equal("simple-scanner", decision.Action);
            Assert.False(decision.RequiresApproval);
            Assert.True(decision.Parameters.ContainsKey("url"));
        }

        [Theory]
        [InlineData("attack example.com")]
        [InlineData("try to exploit https://example.com")]
        [InlineData("hack test.org for me")]
        public async Task Attack_requests_select_the_attack_tool_and_require_approval(string prompt)
        {
            var decision = await _brain.DecideAsync(prompt);

            Assert.True(decision.ToolFound);
            Assert.Equal("attack", decision.Tool);
            Assert.Equal("execute", decision.Action);
            Assert.True(decision.RequiresApproval);
            Assert.True(decision.Parameters.ContainsKey("url"));
        }

        [Fact]
        public async Task Attack_wins_when_both_attack_and_scan_words_appear()
        {
            var decision = await _brain.DecideAsync("scan and then attack example.com");

            Assert.True(decision.ToolFound);
            Assert.Equal("attack", decision.Tool);
            Assert.True(decision.RequiresApproval);
        }

        [Theory]
        [InlineData("scan for links")]
        [InlineData("please attack the site")]
        public async Task Tool_intent_without_a_url_asks_for_one_and_selects_no_tool(string prompt)
        {
            var decision = await _brain.DecideAsync(prompt);

            Assert.False(decision.ToolFound);
            Assert.False(string.IsNullOrWhiteSpace(decision.Message));
        }

        [Theory]
        [InlineData("what's the weather today?")]
        [InlineData("tell me a joke")]
        [InlineData("")]
        public async Task Unrelated_or_empty_prompts_select_no_tool(string prompt)
        {
            var decision = await _brain.DecideAsync(prompt);

            Assert.False(decision.ToolFound);
            Assert.Null(decision.Tool);
            Assert.False(string.IsNullOrWhiteSpace(decision.Message));
        }

        [Fact]
        public async Task Bare_host_is_normalised_to_an_https_url()
        {
            var decision = await _brain.DecideAsync("scan example.com");

            Assert.Equal("https://example.com", decision.Parameters["url"]);
        }

        [Fact]
        public async Task Existing_scheme_is_preserved()
        {
            var decision = await _brain.DecideAsync("scan http://example.com/path");

            Assert.Equal("http://example.com/path", decision.Parameters["url"]);
        }
    }
}
