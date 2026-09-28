using ScanHarness.Models;
using ScanHarness.Services;
using Xunit;

namespace ScanHarness.Tests
{
    public class ToolResultSummarizerTests
    {
        private static ToolCall Call(string tool, string action, Dictionary<string, string> parameters,
            ToolCallStatus status = ToolCallStatus.Completed, string? result = null, string? error = null) => new()
        {
            Tool = tool,
            Action = action,
            Parameters = parameters,
            Status = status,
            Result = result,
            Error = error,
        };

        private static readonly Dictionary<string, string> Target = new() { ["url"] = "https://test.example.com" };

        [Fact]
        public void Attack_with_findings_names_the_count_severity_and_findings()
        {
            var result = "{\"status\":\"success\",\"message\":\"Attack simulation completed. Medium severity vulnerabilities detected.\"," +
                         "\"severity\":\"medium\",\"vulnerabilitiesFound\":5,\"details\":{\"duration_ms\":3500,\"tests_run\":150," +
                         "\"findings\":[\"SQL injection risk\",\"Cross-site scripting vulnerability\",\"Insecure cookies\"]}}";

            var summary = ToolResultSummarizer.Summarize(Call("attack", "execute", Target, result: result));

            Assert.Equal(
                "The user asked me to run a simulated attack against https://test.example.com. " +
                "The attack tool reported \"success\": Attack simulation completed. Medium severity vulnerabilities detected. " +
                "It found 5 medium-severity vulnerabilities across 150 tests, including SQL injection risk, " +
                "Cross-site scripting vulnerability and Insecure cookies.",
                summary);
        }

        [Fact]
        public void Attack_without_findings_says_none_were_found()
        {
            var result = "{\"status\":\"success\",\"message\":\"No vulnerabilities found.\",\"severity\":\"none\"," +
                         "\"vulnerabilitiesFound\":0,\"details\":{\"tests_run\":150}}";

            var summary = ToolResultSummarizer.Summarize(Call("attack", "execute", Target, result: result));

            Assert.EndsWith("It found no vulnerabilities across 150 tests.", summary);
        }

        [Fact]
        public void Scan_lists_the_links_it_returned()
        {
            var result = "{\"status\":\"success\",\"message\":\"Scan completed successfully. Found 3 links.\",\"links\":[" +
                         "{\"linkText\":\"Home\",\"url\":\"https://test.example.com/\"}," +
                         "{\"linkText\":\"About\",\"url\":\"https://test.example.com/about\"}," +
                         "{\"linkText\":\"Contact\",\"url\":\"https://test.example.com/contact\"}]}";

            var summary = ToolResultSummarizer.Summarize(Call("scanner", "simple-scanner", Target, result: result));

            Assert.Equal(
                "The user asked me to scan https://test.example.com for links. " +
                "The scan tool reported \"success\": Scan completed successfully. Found 3 links. " +
                "It returned 3 links: Home, About and Contact.",
                summary);
        }

        [Fact]
        public void Long_link_lists_are_shortened()
        {
            var links = string.Join(",", Enumerable.Range(1, 8).Select(i => $"{{\"linkText\":\"Page {i}\",\"url\":\"https://x.com/{i}\"}}"));
            var result = $"{{\"status\":\"success\",\"message\":\"Found 8 links\",\"links\":[{links}]}}";

            var summary = ToolResultSummarizer.Summarize(Call("scanner", "simple-scanner", Target, result: result));

            Assert.EndsWith("It returned 8 links: Page 1, Page 2, Page 3, Page 4, Page 5 and 3 more.", summary);
        }

        [Fact]
        public void Scan_error_reports_the_status_and_no_links()
        {
            var result = "{\"status\":\"error\",\"message\":\"Invalid URL format\",\"links\":[]}";

            var summary = ToolResultSummarizer.Summarize(Call("scanner", "simple-scanner", Target, result: result));

            Assert.EndsWith("The scan tool reported \"error\": Invalid URL format. No links were returned.", summary);
        }

        [Fact]
        public void Weather_reports_days_location_range_and_first_day()
        {
            var result = "[" +
                         "{\"date\":\"2026-09-29\",\"location\":\"London\",\"temperatureC\":12,\"temperatureF\":53,\"summary\":\"Chilly\"}," +
                         "{\"date\":\"2026-09-30\",\"location\":\"London\",\"temperatureC\":18,\"temperatureF\":64,\"summary\":\"Mild\"}," +
                         "{\"date\":\"2026-10-01\",\"location\":\"London\",\"temperatureC\":-2,\"temperatureF\":29,\"summary\":\"Freezing\"}]";

            var summary = ToolResultSummarizer.Summarize(Call("weather", "forecast", new() { ["location"] = "London" }, result: result));

            Assert.Equal(
                "The user asked me to get the weather forecast for London. " +
                "The weather tool returned a 3-day forecast for London with temperatures from -2°C to 18°C; " +
                "Tue 29 Sep is 12°C and Chilly.",
                summary);
        }

        [Fact]
        public void Rejected_call_says_the_tool_was_not_run()
        {
            var summary = ToolResultSummarizer.Summarize(Call("attack", "execute", Target, ToolCallStatus.Cancelled));

            Assert.Equal(
                "The user asked me to run a simulated attack against https://test.example.com. " +
                "The user rejected the request, so the attack tool was not run.",
                summary);
        }

        [Fact]
        public void Failed_call_includes_the_error()
        {
            var summary = ToolResultSummarizer.Summarize(Call("weather", "forecast", new(), ToolCallStatus.Failed,
                error: "No connection could be made because the target machine actively refused it."));

            Assert.Equal(
                "The user asked me to get the weather forecast. The weather tool failed " +
                "(No connection could be made because the target machine actively refused it), so there is no result.",
                summary);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("")]
        public void Unreadable_or_empty_result_is_reported(string result)
        {
            var summary = ToolResultSummarizer.Summarize(Call("scanner", "simple-scanner", Target, result: result));

            Assert.Matches("The scan tool returned (a response I couldn't read|an empty response)\\.$", summary);
        }

        [Fact]
        public void Unexpected_shape_falls_back_to_the_raw_result()
        {
            var summary = ToolResultSummarizer.Summarize(Call("scanner", "simple-scanner", Target, result: "{\"status\":42}"));

            Assert.EndsWith("The scan tool returned: {\"status\":42}", summary);
        }

        [Fact]
        public async Task Keyword_brain_uses_the_rule_based_summary()
        {
            var call = Call("attack", "execute", Target, ToolCallStatus.Cancelled);

            var summary = await new KeywordAgentBrain().SummarizeAsync("attack test.example.com", call);

            Assert.Equal(ToolResultSummarizer.Summarize(call), summary.Text);
            Assert.Null(summary.FallbackReason);
        }
    }
}
